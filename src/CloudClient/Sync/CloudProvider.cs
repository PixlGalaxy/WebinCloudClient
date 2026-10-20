using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudClient.Api;
using CloudClient.Core;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudClient.Sync;

/// <summary>
/// Answers Windows' Cloud Files callbacks for the sync folder and keeps it in
/// step with the server:
/// - a folder's entries are listed from the server when it is opened, as
///   placeholders that take no space;
/// - a file's bytes are downloaded (with HTTP ranges) when something opens it;
/// - when the server reports a folder changed, the folder is compared with
///   the server: edited files become fresh placeholders (downloaded again on
///   the next open, or at once if kept on this device), new entries appear and
///   deleted ones go;
/// - files and folders created or edited here are uploaded (see LocalChanges),
///   and deleting, renaming or moving a synced item here is done on the server
///   first; if the server refuses, Windows cancels it here too.
/// Temporary files that editors write while saving are never uploaded.
/// </summary>
public sealed class CloudProvider : IDisposable
{
    /// <summary>Data is handed to Windows in pieces this size (a multiple of 4 KB, as required).</summary>
    private const int ChunkSize = 4 * 1024 * 1024;
    /// <summary>"Always keep on this device": Windows downloads a pinned placeholder by itself.</summary>
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    /// <summary>On a placeholder folder: not listed on this computer yet; opening it lists it from the server.</summary>
    private const FileAttributes NotListed = FileAttributes.Offline;
    /// <summary>Times within this of each other count as equal (FAT-style rounding, clock skew in transfers).</summary>
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);
    private static readonly NTStatus StatusSuccess = 0;
    private static readonly NTStatus StatusUnsuccessful = unchecked((int)0xC0000001);
    private static readonly NTStatus StatusAccessDenied = unchecked((int)0xC0000022);

    private readonly ApiClient _api;
    private readonly string _root;
    /// <summary>The root as Windows reports paths in callbacks: without the drive letter.</summary>
    private readonly string _rootNoDrive;
    private readonly ConcurrentDictionary<CF_TRANSFER_KEY, CancellationTokenSource> _transfers = new();
    /// <summary>Folders being removed locally: listing them again would only fetch what is about to go.</summary>
    private readonly ConcurrentDictionary<string, byte> _removing = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>One comparison at a time, so two notices for the same folder never race.</summary>
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    // Kept as fields so the GC never collects delegates Windows still calls.
    private readonly CF_CALLBACK _fetchPlaceholders;
    private readonly CF_CALLBACK _fetchData;
    private readonly CF_CALLBACK _cancelFetchData;
    private readonly CF_CALLBACK _notifyDelete;
    private readonly CF_CALLBACK _notifyRename;
    private LocalChanges? _local;
    private CF_CONNECTION_KEY _connection;
    private bool _connected;

    public CloudProvider(ApiClient api, string root)
    {
        _api = api;
        _root = Path.GetFullPath(root).TrimEnd('\\');
        _rootNoDrive = _root[Path.GetPathRoot(_root)!.TrimEnd('\\').Length..];
        _fetchPlaceholders = OnFetchPlaceholders;
        _fetchData = OnFetchData;
        _cancelFetchData = OnCancelFetchData;
        _notifyDelete = OnNotifyDelete;
        _notifyRename = OnNotifyRename;
    }

    public void Connect()
    {
        var callbacks = new[]
        {
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = _fetchPlaceholders },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = _fetchData },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = _cancelFetchData },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = _notifyDelete },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = _notifyRename },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END,
        };
        // Process info tells our own deletes (refreshing from the server) apart from the user's.
        CfConnectSyncRoot(_root, callbacks, IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out _connection).ThrowIfFailed();
        _connected = true;
        _local = new LocalChanges(_root, ProcessLocalAsync);
        Log.Info($"Connected to the sync folder {_root}");
    }

    // --- listing folders ----------------------------------------------------------

    private void OnFetchPlaceholders(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var connection = info.ConnectionKey;
        var transfer = info.TransferKey;
        var relative = RelativePath(info.NormalizedPath);
        if (IsBeingRemoved(relative))
        {
            TransferPlaceholders(connection, transfer, [], StatusSuccess);
            return;
        }
        _ = Task.Run(() => PopulateAsync(connection, transfer, relative));
    }

    private async Task PopulateAsync(CF_CONNECTION_KEY connection, CF_TRANSFER_KEY transfer, string relative)
    {
        List<RemoteEntry> entries;
        try
        {
            entries = await ListAsync(relative);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list \"{relative}\": {ex.Message}");
            TransferPlaceholders(connection, transfer, [], StatusUnsuccessful);
            return;
        }
        TransferPlaceholders(connection, transfer, entries, StatusSuccess);
    }

    private async Task<List<RemoteEntry>> ListAsync(string relative) =>
        (await _api.GetJsonAsync<RemoteListing>($"files?path={Uri.EscapeDataString(relative)}")).Entries;

    private static void TransferPlaceholders(CF_CONNECTION_KEY connection, CF_TRANSFER_KEY transfer, List<RemoteEntry> entries, NTStatus status)
    {
        using var batch = new PlaceholderBatch(entries);
        var operation = Operation(connection, transfer, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS);
        // No "fully populated" flag: Windows asks again next time the folder is opened,
        // which picks up entries added on the server since. Names already here are skipped.
        var parameters = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
        {
            Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
            CompletionStatus = status,
            PlaceholderTotalCount = entries.Count,
            PlaceholderArray = batch.Array,
            PlaceholderCount = (uint)entries.Count,
        });
        var result = CfExecute(operation, ref parameters);
        if (result.Failed) Log.Warn($"Listing a folder failed: {result}");
    }

    // --- following the server -----------------------------------------------------------

    /// <summary>
    /// Brings one local folder in line with the server. Only folders already
    /// listed on this computer: the rest are listed fresh when first opened.
    /// Windows does not ask for a listed folder again, so this is how entries
    /// added on the server later reach it.
    /// </summary>
    public async Task ReconcileAsync(string relative)
    {
        var local = LocalPath(relative);
        if (!IsListed(local, relative)) return;

        await _reconcileGate.WaitAsync();
        try
        {
            List<RemoteEntry> remote;
            try
            {
                remote = await ListAsync(relative);
            }
            catch (ApiException ex) when (ex.Status is 403 or 404)
            {
                // The folder itself is gone or out of reach; its parent's comparison removes it.
                return;
            }

            var remoteByName = remote.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            var localEntries = new DirectoryInfo(local).GetFileSystemInfos();
            var toCreate = new List<RemoteEntry>();
            var pinnedAgain = new List<string>();

            foreach (var item in localEntries)
            {
                // Created or edited here and not uploaded yet: the upload decides, not the server.
                // Queued too, for changes made while the app was closed, which no watcher saw.
                if (IsIgnored(item.Name) || !IsSynced(item.FullName))
                {
                    if (!IsIgnored(item.Name)) _local?.Touch(item.FullName);
                    remoteByName.Remove(item.Name);
                    continue;
                }
                if (!remoteByName.TryGetValue(item.Name, out var entry))
                {
                    Remove(item, $"{Join(relative, item.Name)} is gone from the server");
                    continue;
                }

                var isFolder = item is DirectoryInfo;
                if (isFolder != (entry.Type == "folder"))
                {
                    Remove(item, $"{entry.Path} changed between file and folder");
                    toCreate.Add(entry);
                }
                else if (!isFolder && Changed((FileInfo)item, entry))
                {
                    var wasPinned = item.Attributes.HasFlag(Pinned);
                    if (Remove(item, $"{entry.Path} changed on the server"))
                    {
                        toCreate.Add(entry);
                        if (wasPinned) pinnedAgain.Add(item.FullName);
                    }
                }
                remoteByName.Remove(item.Name);
            }
            toCreate.AddRange(remoteByName.Values);

            if (toCreate.Count > 0) CreatePlaceholders(local, toCreate);
            // Kept on this device: pin the fresh placeholder again and download it now,
            // since Windows only downloads pinned files when the pin is set by the user.
            foreach (var path in pinnedAgain)
            {
                try
                {
                    File.SetAttributes(path, File.GetAttributes(path) | Pinned);
                    _ = Task.Run(() => Hydrate(path));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not keep \"{path}\" on this device: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not refresh \"{relative}\": {ex.Message}");
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>
    /// After (re)connecting: every folder listed on this computer, top down,
    /// to catch up on what changed while the app was away.
    /// </summary>
    public async Task ReconcileAllAsync()
    {
        var queue = new Queue<string>([""]);
        while (queue.Count > 0)
        {
            var relative = queue.Dequeue();
            await ReconcileAsync(relative);
            var local = LocalPath(relative);
            if (!Directory.Exists(local)) continue;
            foreach (var child in new DirectoryInfo(local).GetDirectories())
            {
                var childRelative = Join(relative, child.Name);
                if (IsListed(child.FullName, childRelative)) queue.Enqueue(childRelative);
            }
        }
    }

    /// <summary>The root always counts; any other folder once Windows has listed it.</summary>
    private static bool IsListed(string local, string relative)
    {
        if (!Directory.Exists(local)) return false;
        return relative == "" || !File.GetAttributes(local).HasFlag(NotListed);
    }

    /// <summary>Reading a placeholder through makes Windows fetch all of it (through FETCH_DATA).</summary>
    private static void Hydrate(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            while (stream.Read(buffer, 0, buffer.Length) > 0) { }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not download \"{path}\" to keep it on this device: {ex.Message}");
        }
    }

    private static bool Changed(FileInfo local, RemoteEntry remote) =>
        local.Length != remote.Size || (local.LastWriteTimeUtc - remote.ModifiedAt.ToUniversalTime()).Duration() > TimeTolerance;

    /// <summary>Deletes a local entry; false if it is in use (it is retried on the next notice).</summary>
    private bool Remove(FileSystemInfo item, string reason)
    {
        var relative = RelativeOf(item.FullName);
        try
        {
            if (item is DirectoryInfo)
            {
                _removing[relative] = 0;
                try
                {
                    Directory.Delete(item.FullName, recursive: true);
                }
                finally
                {
                    _removing.TryRemove(relative, out _);
                }
            }
            else
            {
                File.Delete(item.FullName);
            }
            Log.Info($"Refreshed: {reason}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not update \"{relative}\" ({reason}): {ex.Message}");
            return false;
        }
    }

    private static void CreatePlaceholders(string folder, List<RemoteEntry> entries)
    {
        using var batch = new PlaceholderBatch(entries);
        var result = CfCreatePlaceholders(folder, batch.Infos, (uint)entries.Count, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _);
        if (result.Failed) Log.Warn($"Could not add {entries.Count} item(s) in \"{folder}\": {result}");
    }

    private bool IsBeingRemoved(string relative) =>
        _removing.Keys.Any(r => relative.Equals(r, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase));

    // --- changes made on this computer -------------------------------------------------

    /// <summary>
    /// Names editors and Windows write next to real files while saving
    /// (Office's ~$ owner files and ~WRL*.tmp backups, .tmp, desktop.ini...).
    /// They are never uploaded, and renaming to or deleting them never touches
    /// the server, so saving a document only replaces it there.
    /// </summary>
    public static bool IsIgnored(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("~$") || lower.StartsWith(".~lock") || lower.StartsWith("~wr") ||
               lower.EndsWith(".tmp") || lower.EndsWith(".temp") || lower.EndsWith(".partial") || lower.EndsWith(".crdownload") ||
               lower is "desktop.ini" or "thumbs.db" or ".ds_store";
    }

    private static bool IsOwnProcess(in CF_CALLBACK_INFO info)
    {
        if (info.ProcessInfo.IsNull) return false;
        return info.ProcessInfo.Value?.ProcessId == Environment.ProcessId;
    }

    private void OnNotifyDelete(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var connection = info.ConnectionKey;
        var transfer = info.TransferKey;
        var relative = RelativePath(info.NormalizedPath);
        // Our own refresh from the server, or a temporary file: nothing to do there.
        if (IsOwnProcess(info) || relative == "" || IsIgnored(Path.GetFileName(relative)))
        {
            Ack(connection, transfer, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE, StatusSuccess);
            return;
        }
        _ = Task.Run(async () =>
        {
            var status = await RemoteAsync(() => _api.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, ServerAddress.Api(_api.ServerUrl, $"files?path={Uri.EscapeDataString(relative)}")),
                CancellationToken.None), $"delete \"{relative}\"");
            Ack(connection, transfer, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE, status);
        });
    }

    private void OnNotifyRename(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var connection = info.ConnectionKey;
        var transfer = info.TransferKey;
        var source = RelativePath(info.NormalizedPath);
        var targetPath = parameters.Rename.TargetPath;
        var targetInScope = InScope(targetPath);
        var target = targetInScope ? RelativePath(targetPath) : "";
        var sourceIgnored = IsIgnored(Path.GetFileName(source));
        var targetIgnored = targetInScope && IsIgnored(Path.GetFileName(target));

        // Ours, or an editor's save dance: renaming to a temporary name leaves the server copy
        // in place, to be replaced by the upload of the new version.
        if (IsOwnProcess(info) || source == "" || sourceIgnored || targetIgnored)
        {
            Ack(connection, transfer, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME, StatusSuccess);
            if (targetInScope && !targetIgnored && sourceIgnored) _local?.Touch(LocalPath(target));
            return;
        }

        _ = Task.Run(async () =>
        {
            NTStatus status;
            if (!targetInScope)
            {
                // Moved out of the sync folder: it leaves the server, as with OneDrive.
                status = await RemoteAsync(() => _api.SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Delete, ServerAddress.Api(_api.ServerUrl, $"files?path={Uri.EscapeDataString(source)}")),
                    CancellationToken.None), $"remove \"{source}\" (moved out of the folder)");
            }
            else
            {
                status = await RemoteAsync(() => RenameOrMoveAsync(source, target), $"move \"{source}\" to \"{target}\"");
            }
            Ack(connection, transfer, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME, status);
        });
    }

    /// <summary>Same folder: a rename. Otherwise a move, then a rename if the name changed too.</summary>
    private async Task<HttpResponseMessage> RenameOrMoveAsync(string source, string target)
    {
        var sourceParent = ParentOf(source);
        var targetParent = ParentOf(target);
        var sourceName = NameOf(source);
        var targetName = NameOf(target);

        if (sourceParent.Equals(targetParent, StringComparison.Ordinal))
            return await PatchAsync("files/rename", new { path = source, newName = targetName });

        var moved = await PatchAsync("files/move", new { paths = new[] { source }, destination = targetParent, onConflict = "fail" });
        if (sourceName == targetName) return moved;
        moved.Dispose();
        return await PatchAsync("files/rename", new { path = Join(targetParent, sourceName), newName = targetName });
    }

    private Task<HttpResponseMessage> PatchAsync(string path, object body) => _api.SendAsync(
        () => new HttpRequestMessage(HttpMethod.Patch, ServerAddress.Api(_api.ServerUrl, path)) { Content = JsonContent(body) },
        CancellationToken.None);

    private static HttpContent JsonContent(object body) =>
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>Runs a server change and turns the outcome into the answer Windows expects.</summary>
    private static async Task<NTStatus> RemoteAsync(Func<Task<HttpResponseMessage>> call, string what)
    {
        try
        {
            using var response = await call();
            Log.Info($"Synced: {what}");
            return StatusSuccess;
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            // Already gone there; nothing to undo here.
            return StatusSuccess;
        }
        catch (ApiException ex) when (ex.Status == 403)
        {
            Log.Warn($"The server refused to {what}: {ex.Message}");
            return StatusAccessDenied;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not {what}: {ex.Message}");
            return StatusUnsuccessful;
        }
    }

    private static void Ack(CF_CONNECTION_KEY connection, CF_TRANSFER_KEY transfer, CF_OPERATION_TYPE type, NTStatus status)
    {
        var operation = Operation(connection, transfer, type);
        var parameters = type == CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE
            ? CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.ACKDELETE { CompletionStatus = status })
            : CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.ACKRENAME { CompletionStatus = status });
        var result = CfExecute(operation, ref parameters);
        if (result.Failed) Log.Warn($"Windows did not take the answer to a {type}: {result}");
    }

    /// <summary>
    /// One path from LocalChanges: a new folder is created on the server, a
    /// new or edited file uploaded, then marked as in sync. Returns false to be
    /// retried (the file is still open elsewhere, or the server is unreachable).
    /// </summary>
    private async Task<bool> ProcessLocalAsync(string fullPath)
    {
        var relative = RelativeOf(fullPath);
        if (relative.StartsWith("..") || relative == "." || relative.Split('/').Any(IsIgnored)) return true;
        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath)) return true;
        if (IsSynced(fullPath)) return true;

        try
        {
            if (isDirectory)
            {
                await CreateRemoteFolderAsync(relative);
                MarkSynced(fullPath, relative);
                // A folder moved in from elsewhere arrives in one event: its contents follow.
                foreach (var child in Directory.EnumerateFileSystemEntries(fullPath)) _local?.Touch(child);
                Log.Info($"Synced: created folder \"{relative}\"");
            }
            else
            {
                await UploadAsync(fullPath, relative);
                MarkSynced(fullPath, relative);
                Log.Info($"Synced: uploaded \"{relative}\"");
            }
            return true;
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            // Still being written or held open by another program.
            return false;
        }
        catch (ApiException ex) when (ex.Status is 403 or 413 or 400)
        {
            // Will not change by trying again: read-only folder, too large, invalid name.
            Log.Warn($"The server did not take \"{relative}\": {ex.Message}");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException)
        {
            Log.Warn($"Could not sync \"{relative}\" yet: {ex.Message}");
            return false;
        }
    }

    private async Task CreateRemoteFolderAsync(string relative)
    {
        try
        {
            using var _ = await _api.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, ServerAddress.Api(_api.ServerUrl, "files/mkdir"))
                {
                    Content = JsonContent(new { path = ParentOf(relative), name = NameOf(relative) }),
                },
                CancellationToken.None);
        }
        catch (ApiException ex) when (ex.Status == 409)
        {
            // Already there.
        }
    }

    /// <summary>Uploads the file over whatever is on the server (the last change wins), keeping its modification time.</summary>
    private async Task UploadAsync(string fullPath, string relative)
    {
        var modified = File.GetLastWriteTimeUtc(fullPath).ToString("O");
        var url = $"files/upload?path={Uri.EscapeDataString(ParentOf(relative))}&onConflict=overwrite&modifiedAt={Uri.EscapeDataString(modified)}";
        var opened = new List<FileStream>();
        try
        {
            using var _ = await _api.SendAsync(() =>
            {
                // A fresh stream per attempt: a retried request cannot reuse a read one.
                var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true);
                opened.Add(stream);
                var file = new StreamContent(stream);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var form = new MultipartFormDataContent { { file, "file", NameOf(relative) } };
                return new HttpRequestMessage(HttpMethod.Post, ServerAddress.Api(_api.ServerUrl, url)) { Content = form };
            }, CancellationToken.None, stream: true);
        }
        finally
        {
            foreach (var stream in opened) await stream.DisposeAsync();
        }
    }

    /// <summary>Turns a local item into a synced placeholder (or marks an edited one synced again).</summary>
    private static void MarkSynced(string fullPath, string relative)
    {
        CfOpenFileWithOplock(fullPath, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS, out var protectedHandle).ThrowIfFailed();
        using (protectedHandle)
        {
            var handle = CfGetWin32HandleFromProtectedHandle(protectedHandle);
            var identity = Encoding.UTF8.GetBytes(relative);
            HRESULT result;
            if (State(fullPath).HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER))
            {
                CF_FS_METADATA? metadata = null;
                long? usn = null;
                System.Threading.NativeOverlapped? overlapped = null;
                result = CfUpdatePlaceholder(handle, in metadata, identity, null, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, ref usn, ref overlapped);
            }
            else
            {
                System.Threading.NativeOverlapped? overlapped = null;
                result = CfConvertToPlaceholder(handle, identity, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC, out _, ref overlapped);
            }
            if (result.Failed) throw new IOException($"Could not mark \"{relative}\" as synced: {result}");
        }
    }

    private static CF_PLACEHOLDER_STATE State(string fullPath)
    {
        using var search = Kernel32.FindFirstFile(fullPath, out var data);
        return search.IsInvalid ? CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_INVALID : CfGetPlaceholderStateFromFindData(data);
    }

    /// <summary>A placeholder that matches the server; anything else is local work still to upload.</summary>
    private static bool IsSynced(string fullPath)
    {
        var state = State(fullPath);
        return state.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER) && state.HasFlag(CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC);
    }

    private static string ParentOf(string relative)
    {
        var at = relative.LastIndexOf('/');
        return at < 0 ? "" : relative[..at];
    }

    private static string NameOf(string relative) => relative[(relative.LastIndexOf('/') + 1)..];

    // --- downloading files ----------------------------------------------------------

    private void OnFetchData(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var connection = info.ConnectionKey;
        var transfer = info.TransferKey;
        // Where the file is now (it may have been renamed here since); failing
        // that, the server path stored in the placeholder when it was created.
        var relative = RelativePath(info.NormalizedPath);
        if (relative == "" && info.FileIdentity != IntPtr.Zero && info.FileIdentityLength > 0)
            relative = Marshal.PtrToStringUTF8(info.FileIdentity, (int)info.FileIdentityLength);
        var fetch = parameters.FetchData;
        var offset = fetch.RequiredFileOffset;
        var length = fetch.RequiredLength;
        var fileSize = info.FileSize;

        var cancel = new CancellationTokenSource();
        _transfers[transfer] = cancel;
        _ = Task.Run(() => DownloadAsync(connection, transfer, relative, offset, length, fileSize, cancel.Token));
    }

    private void OnCancelFetchData(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        if (_transfers.TryRemove(info.TransferKey, out var cancel)) cancel.Cancel();
    }

    private async Task DownloadAsync(
        CF_CONNECTION_KEY connection, CF_TRANSFER_KEY transfer, string relative, long offset, long length, long fileSize, CancellationToken cancel)
    {
        var position = offset;
        var end = Math.Min(offset + length, fileSize);
        try
        {
            using var response = await _api.SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, ServerAddress.Api(_api.ServerUrl, $"files/download?path={Uri.EscapeDataString(relative)}"));
                request.Headers.Range = new RangeHeaderValue(offset, end - 1);
                return request;
            }, cancel, stream: true);
            await using var body = await response.Content.ReadAsStreamAsync(cancel);

            var buffer = new byte[ChunkSize];
            while (position < end)
            {
                var wanted = (int)Math.Min(ChunkSize, end - position);
                var filled = 0;
                while (filled < wanted)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(filled, wanted - filled), cancel);
                    if (read == 0) throw new IOException("The server ended the download early.");
                    filled += read;
                }
                TransferData(connection, transfer, buffer, position, filled, StatusSuccess);
                position += filled;
            }
        }
        catch (Exception ex)
        {
            if (!cancel.IsCancellationRequested) Log.Warn($"Could not download \"{relative}\": {ex.Message}");
            // Tell Windows the rest failed, so the app that opened the file gets an error instead of hanging.
            if (position < end) TransferData(connection, transfer, null, position, end - position, StatusUnsuccessful);
        }
        finally
        {
            if (_transfers.TryRemove(transfer, out var source)) source.Dispose();
        }
    }

    private static void TransferData(CF_CONNECTION_KEY connection, CF_TRANSFER_KEY transfer, byte[]? data, long offset, long length, NTStatus status)
    {
        var handle = data is null ? default : GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var operation = Operation(connection, transfer, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA);
            var parameters = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                CompletionStatus = status,
                Buffer = data is null ? IntPtr.Zero : handle.AddrOfPinnedObject(),
                Offset = offset,
                Length = length,
            });
            var result = CfExecute(operation, ref parameters);
            if (result.Failed && status == StatusSuccess) throw new IOException($"Windows refused the data: {result}");
        }
        finally
        {
            if (data is not null) handle.Free();
        }
    }

    // --- helpers ------------------------------------------------------------------------

    private static CF_OPERATION_INFO Operation(CF_CONNECTION_KEY connection, CF_TRANSFER_KEY transfer, CF_OPERATION_TYPE type) => new()
    {
        StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
        Type = type,
        ConnectionKey = connection,
        TransferKey = transfer,
    };

    /// <summary>"\Users\me\Webin Cloud\Docs\a.txt" → "Docs/a.txt", the server's form.</summary>
    private string RelativePath(string? normalizedPath)
    {
        var path = (normalizedPath ?? "").TrimEnd('\\');
        var at = path.IndexOf(_rootNoDrive, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        path = path[(at + _rootNoDrive.Length)..];
        if (path.Length > 0 && path[0] != '\\') return "";
        return path.Trim('\\').Replace('\\', '/');
    }

    /// <summary>Whether a Windows path (as callbacks give it) is inside the sync folder.</summary>
    private bool InScope(string? path)
    {
        var p = (path ?? "").TrimEnd('\\');
        var at = p.IndexOf(_rootNoDrive, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && (p.Length == at + _rootNoDrive.Length || p[at + _rootNoDrive.Length] == '\\');
    }

    private string RelativeOf(string fullPath) => Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

    private string LocalPath(string relative) => relative == "" ? _root : Path.Combine(_root, relative.Replace('/', '\\'));

    private static string Join(string folder, string name) => folder == "" ? name : $"{folder}/{name}";

    public void Dispose()
    {
        _local?.Dispose();
        _local = null;
        foreach (var cancel in _transfers.Values) cancel.Cancel();
        if (_connected)
        {
            CfDisconnectSyncRoot(_connection);
            _connected = false;
        }
    }

    /// <summary>
    /// Placeholder descriptions in unmanaged memory, as Windows reads them:
    /// each one's identity is its server path (UTF-8), so a download can find
    /// the file even when Windows does not pass the path.
    /// </summary>
    private sealed class PlaceholderBatch : IDisposable
    {
        private static readonly int Size = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
        private readonly List<IntPtr> _identities = [];
        private readonly int _count;

        public IntPtr Array { get; }
        public CF_PLACEHOLDER_CREATE_INFO[] Infos { get; }

        public PlaceholderBatch(List<RemoteEntry> entries)
        {
            _count = entries.Count;
            Infos = new CF_PLACEHOLDER_CREATE_INFO[entries.Count];
            Array = entries.Count == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(Size * entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                Infos[i] = Describe(entries[i]);
                Marshal.StructureToPtr(Infos[i], Array + i * Size, false);
            }
        }

        private CF_PLACEHOLDER_CREATE_INFO Describe(RemoteEntry entry)
        {
            var identity = Encoding.UTF8.GetBytes(entry.Path);
            var identityPtr = Marshal.AllocHGlobal(identity.Length);
            Marshal.Copy(identity, 0, identityPtr, identity.Length);
            _identities.Add(identityPtr);

            var modified = FileTime(entry.ModifiedAt.ToFileTimeUtc());
            return new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = entry.Name,
                FileIdentity = identityPtr,
                FileIdentityLength = (uint)identity.Length,
                // Already matching the server; folders stay on-demand, so opening one lists it.
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                FsMetadata = new CF_FS_METADATA
                {
                    FileSize = entry.Type == "folder" ? 0 : entry.Size,
                    BasicInfo = new Kernel32.FILE_BASIC_INFO
                    {
                        CreationTime = modified,
                        LastWriteTime = modified,
                        LastAccessTime = modified,
                        ChangeTime = modified,
                        FileAttributes = entry.Type == "folder"
                            ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY
                            : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                    },
                },
            };
        }

        private static System.Runtime.InteropServices.ComTypes.FILETIME FileTime(long value) => new()
        {
            dwLowDateTime = unchecked((int)(value & 0xFFFFFFFF)),
            dwHighDateTime = unchecked((int)(value >> 32)),
        };

        public void Dispose()
        {
            if (Array != IntPtr.Zero)
            {
                for (var i = 0; i < _count; i++) Marshal.DestroyStructure<CF_PLACEHOLDER_CREATE_INFO>(Array + i * Size);
                Marshal.FreeHGlobal(Array);
            }
            foreach (var pointer in _identities) Marshal.FreeHGlobal(pointer);
        }
    }

    private sealed class RemoteListing
    {
        [JsonPropertyName("entries")] public List<RemoteEntry> Entries { get; set; } = [];
    }

    private sealed class RemoteEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "file";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("modifiedAt")] public DateTime ModifiedAt { get; set; }
    }
}
