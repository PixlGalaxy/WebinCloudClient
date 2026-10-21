using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CloudClient.Updates;
using Microsoft.Win32;

namespace CloudClient.Installer;

/// <summary>The published release: what the app's updater reads, from the same links.</summary>
internal sealed record Release(Version Version, string Sha256);

/// <summary>Something went wrong, with a short reason to show.</summary>
internal sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What the installer does. Installing reads the version, SHA-256 and
/// signature the publisher put next to the exe (the files the app updates
/// from), checks the signature against the key built into the app, downloads
/// the exe and checks its SHA-256, then puts it in %LOCALAPPDATA%\Programs\&lt;Brand&gt;
/// with Desktop and Start menu shortcuts and an entry in Settings → Apps that
/// opens a copy of this installer. Uninstalling closes the app, signs the
/// device out on the server, removes its folders from File Explorer, its
/// thumbnails and start-with-Windows entries, the shortcuts, the entry, the
/// program and its settings. Files in the sync folder stay. Per user, no
/// admin rights.
/// </summary>
internal static class Setup
{
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Brand.Id);

    /// <summary>The app's settings and log (AppPaths.DataDir in the app).</summary>
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.Id);

    public static string ExePath => Path.Combine(InstallDir, $"{Brand.Id}.exe");

    /// <summary>A copy of this installer in the install folder: Settings → Apps and the Start menu open it to repair or uninstall.</summary>
    public static string InstallerCopyPath => Path.Combine(InstallDir, $"{Brand.Id}Installer.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>The installed app's version, from its file; null if unreadable.</summary>
    public static string? InstalledVersion
    {
        get
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(ExePath);
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
            }
            catch
            {
                return null;
            }
        }
    }

    private static string Shortcut(Environment.SpecialFolder folder, string name) =>
        Path.Combine(Environment.GetFolderPath(folder), name + ".lnk");

    private static string DesktopShortcut => Shortcut(Environment.SpecialFolder.DesktopDirectory, Brand.Name);
    private static string StartShortcut => Shortcut(Environment.SpecialFolder.Programs, Brand.Name);
    private static string UninstallShortcut => Shortcut(Environment.SpecialFolder.Programs, $"Uninstall {Brand.Name}");

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Brand.Id;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SyncRootsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    /// <summary>The app quits when this is signalled (App.QuitEventName).</summary>
    private const string QuitEvent = @"Local\" + Brand.Id + ".Quit";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        // Redirects off: the sign-out request carries the device's token.
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{Brand.Id}Installer/1.0");
        return client;
    }

    // ── Check ───────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the published version, SHA-256 and signature, and checks the signature.</summary>
    public static async Task<Release> FindReleaseAsync(CancellationToken ct)
    {
        var versionText = await GetTextAsync(UpdateSource.VersionUrl, "the version", ct);
        if (!Version.TryParse(versionText, out var parsed))
            throw new InstallException($"The version published on the server (\"{versionText}\") is not valid.");
        var version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));

        var sha256 = (await GetTextAsync(UpdateSource.Sha256Url, "the SHA-256", ct)).ToLowerInvariant();
        if (!Regex.IsMatch(sha256, "^[0-9a-f]{64}$"))
            throw new InstallException("The SHA-256 published on the server is not valid.");

        var signature = await GetTextAsync(UpdateSource.SignatureUrl, "the signature", ct);
        if (!IsSigned(version, sha256, signature))
            throw new InstallException($"{Brand.Name} {version} on the server is not signed by the publisher, so it was not trusted.");
        return new Release(version, sha256);
    }

    /// <summary>The message scripts/build.ps1 signs: brand, version and SHA-256, one per line (as the app's updater checks it).</summary>
    private static bool IsSigned(Version version, string sha256, string signature)
    {
        try
        {
            using var key = CngKey.Import(Convert.FromBase64String(UpdateKey.Public), CngKeyBlobFormat.EccPublicBlob);
            using var ecdsa = new ECDsaCng(key);
            var message = Encoding.UTF8.GetBytes($"{Brand.Id}\n{version}\n{sha256}");
            return ecdsa.VerifyData(message, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Makes sure the exe is on the server without downloading it; returns its size when the server says.</summary>
    public static Task<long?> CheckExeAsync(CancellationToken ct) => WithTimeout(ct, async token =>
    {
        using var response = await GetAsync(UpdateSource.ExeUrl, $"{Brand.Id}.exe", token);
        return response.Content.Headers.ContentLength;
    });

    // ── Install ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the exe to a temporary file and checks its SHA-256 against the
    /// signed release. Returns the file; it is deleted when it does not match.
    /// </summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<(long Got, long Total)> progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"{Brand.Id}-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await GetAsync(UpdateSource.ExeUrl, $"{Brand.Id}.exe", ct);
            var total = response.Content.Headers.ContentLength ?? 0;
            using var sha = SHA256.Create();
            long got = 0;
            using (var source = await response.Content.ReadAsStreamAsync())
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer, 0, read, ct);
                    got += read;
                    progress.Report((got, total));
                }
            }
            sha.TransformFinalBlock([], 0, 0);

            if (total > 0 && got != total)
                throw new InstallException("The download is incomplete. Check your connection and try again.");
            var hash = BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
            if (hash != release.Sha256)
                throw new InstallException("The download does not match the signed release (SHA-256 check failed), so it was not installed.");
            return temp;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>True while the app runs from the install folder (it is closed before its exe is replaced).</summary>
    public static bool AppIsRunning() => ProcessesInside().Any();

    /// <summary>Closes a running app, then puts the verified download in the install folder.</summary>
    public static void CopyIntoPlace(string download)
    {
        CloseApp();
        Directory.CreateDirectory(InstallDir);
        Retry(() => File.Copy(download, ExePath, overwrite: true));
        // Left by the app's own updates.
        TryDelete(ExePath + ".old");
        TryDelete(download);
    }

    /// <summary>
    /// Desktop and Start menu shortcuts, a copy of this installer in the
    /// install folder, and the Settings → Apps entry that opens it.
    /// </summary>
    public static void Register(Release release)
    {
        var self = Process.GetCurrentProcess().MainModule!.FileName;
        if (!string.Equals(self, InstallerCopyPath, StringComparison.OrdinalIgnoreCase))
            Retry(() => File.Copy(self, InstallerCopyPath, overwrite: true));

        SaveShortcut(DesktopShortcut, ExePath, Brand.Name);
        SaveShortcut(StartShortcut, ExePath, Brand.Name);
        SaveShortcut(UninstallShortcut, InstallerCopyPath, $"Repair or uninstall {Brand.Name}");

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", Brand.Name);
        key.SetValue("DisplayVersion", release.Version.ToString());
        key.SetValue("DisplayIcon", ExePath + ",0");
        key.SetValue("Publisher", Brand.Name);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstallerCopyPath}\"");
        key.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void SaveShortcut(string path, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InstallException("Windows could not create the shortcuts.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = InstallDir;
        link.IconLocation = ExePath + ",0";
        link.Description = description;
        link.Save();
    }

    /// <summary>Starts the installed app with its window open.</summary>
    public static void Launch() =>
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = InstallDir })?.Dispose();

    // ── Uninstall ───────────────────────────────────────────────────────────────────────

    /// <summary>Asks the app to quit, as its tray menu does, then closes whatever still runs from the install folder.</summary>
    public static void CloseApp()
    {
        if (EventWaitHandle.TryOpenExisting(QuitEvent, out var quit))
        {
            using (quit)
                quit.Set();
        }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && AppIsRunning())
            Thread.Sleep(250);

        foreach (var process in ProcessesInside())
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(10_000);
                }
                catch
                {
                    // gone already
                }
            }
        }
    }

    /// <summary>
    /// Disconnects this device on the server with the sign-in the app saved, so
    /// it leaves the user's device list. Best effort: offline, or signed out
    /// already, the saved sign-in is removed all the same.
    /// </summary>
    public static async Task SignOutAsync()
    {
        var server = ReadSetting("ServerUrl");
        if (server is null)
            return;
        var target = $"{Brand.Id}:{server}";
        try
        {
            if (ReadCredential(target) is { } saved)
            {
                var access = Field(saved, "AccessToken");
                var refresh = Field(saved, "RefreshToken");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var status = await DeleteDeviceAsync(server, access, timeout.Token);
                // The access token lasts an hour: renew it once, then try again.
                if (status == HttpStatusCode.Unauthorized && refresh is not null && await RenewAsync(server, refresh, timeout.Token) is { } renewed)
                    await DeleteDeviceAsync(server, renewed, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // Offline or the server is gone: the device can be disconnected from the website.
        }
        CredDelete(target, CredTypeGeneric, 0);
    }

    private static async Task<HttpStatusCode> DeleteDeviceAsync(string server, string? access, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{server}/backend/api/devices/current");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        using var response = await Http.SendAsync(request, ct);
        return response.StatusCode;
    }

    private static async Task<string?> RenewAsync(string server, string refresh, CancellationToken ct)
    {
        var body = $"{{\"grantType\":\"refresh_token\",\"refreshToken\":\"{refresh}\"}}";
        using var response = await Http.PostAsync($"{server}/backend/api/devices/token", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        return response.IsSuccessStatusCode ? Field(await response.Content.ReadAsStringAsync(), "accessToken") : null;
    }

    /// <summary>
    /// Removes every folder of this brand from File Explorer (the sync roots
    /// Windows keeps for this user), the thumbnail provider and the
    /// start-with-Windows entries. The folders and their files stay.
    /// </summary>
    public static void RemoveFromExplorer()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        using (var roots = Registry.LocalMachine.OpenSubKey(SyncRootsKey))
        {
            foreach (var id in roots?.GetSubKeyNames() ?? [])
            {
                if (!id.StartsWith($"{Brand.Id}!{sid}!", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    // Also takes its entry out of File Explorer's navigation pane, which CfUnregisterSyncRoot leaves.
                    Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(id);
                }
                catch (Exception ex)
                {
                    throw new InstallException($"Windows did not remove a {Brand.Name} folder from File Explorer: {ex.Message}", ex);
                }
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{Brand.ThumbnailProviderClsid}}}", throwOnMissingSubKey: false);
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        foreach (var name in run?.GetValueNames() ?? [])
        {
            if (name == Brand.Id || run!.GetValue(name) is string command && command.IndexOf(InstallDir, StringComparison.OrdinalIgnoreCase) >= 0)
                run!.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>The shortcuts that open the app or this installer, and the Settings → Apps entry.</summary>
    public static void RemoveShortcuts()
    {
        foreach (var path in new[] { DesktopShortcut, StartShortcut, UninstallShortcut })
            TryDelete(path);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
    }

    /// <summary>Deletes the program and the app's settings and log. The sync folder is left alone.</summary>
    public static void DeleteFiles()
    {
        var self = Process.GetCurrentProcess().MainModule!.FileName;
        foreach (var folder in new[] { InstallDir, DataDir })
        {
            for (var attempt = 1; Directory.Exists(folder); attempt++)
            {
                try
                {
                    // Running from the install folder (only with --headless): everything but this exe.
                    if (IsInside(self) && folder == InstallDir)
                    {
                        foreach (var file in Directory.GetFiles(folder))
                            if (!string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
                                File.Delete(file);
                        break;
                    }
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 20)
                        throw new InstallException($"Some files in {folder} are still in use. Close {Brand.Name} and try again.", ex);
                    Thread.Sleep(500); // the closed app may still be letting go of its files
                }
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>Processes running from the install folder, except this one.</summary>
    private static List<Process> ProcessesInside()
    {
        var found = new List<Process>();
        var self = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcesses())
        {
            if (process.Id != self && IsInside(ImagePath(process.Id)))
                found.Add(process);
            else
                process.Dispose();
        }
        return found;
    }

    private static bool IsInside(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(InstallDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>A process's exe path, the quick way Task Manager reads it. Null for processes this user cannot query.</summary>
    private static string? ImagePath(int pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>One value from the app's settings.json (a flat file the app writes); null if missing.</summary>
    private static string? ReadSetting(string name)
    {
        try
        {
            return Field(File.ReadAllText(Path.Combine(DataDir, "settings.json")), name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A string field of a small JSON object, without a JSON library on .NET Framework.</summary>
    private static string? Field(string json, string name)
    {
        var match = Regex.Match(json, $"\"{Regex.Escape(name)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return match.Success ? Regex.Unescape(match.Groups[1].Value) : null;
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(500); // the closed app may still be letting go of its exe
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────────────

    private static Task<string> GetTextAsync(string url, string what, CancellationToken ct) => WithTimeout(ct, async token =>
    {
        using var response = await GetAsync(url, what, token);
        return (await response.Content.ReadAsStringAsync()).Trim();
    });

    /// <summary>Runs a server call with a 20 s limit; running out of time is an error, not a cancel.</summary>
    private static async Task<T> WithTimeout<T>(CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await call(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InstallException("The server did not answer in time. Try again in a moment.");
        }
    }

    /// <summary>GET with only the headers read; throws an <see cref="InstallException"/> unless the answer is 200.</summary>
    private static async Task<HttpResponseMessage> GetAsync(string url, string what, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException("The server can't be reached. Check your internet connection.", ex);
        }
        if (response.StatusCode == HttpStatusCode.OK)
            return response;
        var code = (int)response.StatusCode;
        response.Dispose();
        throw new InstallException(code == 404
            ? $"The server is online, but {what} is not published there (404)."
            : $"The server answered with an error ({code}) for {what}.");
    }

    // ── Windows ─────────────────────────────────────────────────────────────────────────

    private const int CredTypeGeneric = 1;

    /// <summary>The app's saved sign-in (TokenStore in the app): UTF-8 JSON in a generic credential.</summary>
    private static string? ReadCredential(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
