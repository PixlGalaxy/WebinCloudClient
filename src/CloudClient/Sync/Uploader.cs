using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using CloudClient.Api;
using CloudClient.Core;
using Microsoft.Win32.SafeHandles;

namespace CloudClient.Sync;

/// <summary>
/// Sends files to the server as the web interface does: larger files in
/// chunks over upload sockets, or as parallel HTTP chunks where sockets cannot
/// be opened; small files in one request. Every upload replaces what is on the
/// server (the last change wins) and keeps the file's modification time.
/// </summary>
public sealed class Uploader(ApiClient api) : IDisposable
{
    /// <summary>Files larger than this stream over upload sockets: a small file goes faster in one request.</summary>
    private const long SocketsAbove = 1024 * 1024;
    /// <summary>Over HTTP, files up to this size go in one request; larger ones in the server's 8 MiB chunks.</summary>
    private const long ChunkedAbove = 8 * 1024 * 1024;
    /// <summary>HTTP chunks of one file in flight at once.</summary>
    private const int ParallelHttpChunks = 6;
    /// <summary>Chunks of one file handed to the socket pool at once (1 MiB each); the pool spreads them over its sockets.</summary>
    private const int QueuedSocketChunks = 64;
    /// <summary>Times an HTTP chunk is sent before its file fails.</summary>
    private const int HttpAttempts = 4;

    private readonly UploadSocketPool _sockets = new(api);

    /// <param name="progress">Called with each batch of bytes the server confirmed.</param>
    /// <returns>How it went, in words, for the log.</returns>
    public async Task<string> UploadAsync(string fullPath, string folder, string name, long size, Action<long> progress)
    {
        var modified = File.GetLastWriteTimeUtc(fullPath);
        if (size > SocketsAbove && !_sockets.Broken) return await OverSocketsAsync(fullPath, folder, name, size, modified, progress);
        if (size <= ChunkedAbove)
        {
            await InOneRequestAsync(fullPath, folder, name, modified, progress);
            return "in one request";
        }
        await OverHttpAsync(fullPath, folder, name, size, modified, progress);
        return "in HTTP chunks";
    }

    private async Task InOneRequestAsync(string fullPath, string folder, string name, DateTime modified, Action<long> progress)
    {
        var url = $"files/upload?path={Uri.EscapeDataString(folder)}&onConflict=overwrite&modifiedAt={Uri.EscapeDataString(modified.ToString("O"))}";
        var opened = new List<FileStream>();
        try
        {
            using var _ = await api.SendAsync(() =>
            {
                // A fresh stream per attempt: a retried request cannot reuse a read one.
                var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true);
                opened.Add(stream);
                var file = new StreamContent(new ProgressStream(stream, progress));
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var form = new MultipartFormDataContent { { file, "file", name } };
                return new HttpRequestMessage(HttpMethod.Post, ServerAddress.Api(api.ServerUrl, url)) { Content = form };
            }, CancellationToken.None, stream: true);
        }
        finally
        {
            foreach (var stream in opened) await stream.DisposeAsync();
        }
    }

    private async Task OverHttpAsync(string fullPath, string folder, string name, long size, DateTime modified, Action<long> progress)
    {
        using var file = OpenFile(fullPath);
        var session = await OpenSessionAsync(folder, name, size, modified, "http");
        try
        {
            var indices = Enumerable.Range(0, ChunkCount(session, size)).ToList();
            await SendChunksAsync(indices, ParallelHttpChunks, (index, cancel) => PutChunkAsync(file, session, size, index, progress, cancel));
            await CompleteAsync(fullPath, session, size, modified);
        }
        catch
        {
            await DropAsync(session);
            throw;
        }
    }

    private async Task<string> OverSocketsAsync(string fullPath, string folder, string name, long size, DateTime modified, Action<long> progress)
    {
        using var file = OpenFile(fullPath);
        var session = await OpenSessionAsync(folder, name, size, modified, "socket");
        var count = ChunkCount(session, size);
        var pending = new HashSet<int>(Enumerable.Range(0, count));
        var how = "over upload sockets";
        try
        {
            try
            {
                await SendChunksAsync(Enumerable.Range(0, count).ToList(), QueuedSocketChunks, async (index, cancel) =>
                {
                    var data = await ReadChunkAsync(file, session, size, index, cancel);
                    await _sockets.SendAsync(session.Id, index, data).WaitAsync(cancel);
                    lock (pending) pending.Remove(index);
                    progress(data.Length);
                });
            }
            catch (SocketsUnavailableException)
            {
                // The same session takes the rest of its chunks over HTTP.
                List<int> rest;
                lock (pending) rest = pending.Order().ToList();
                how = "in HTTP chunks (upload sockets unavailable)";
                await SendChunksAsync(rest, ParallelHttpChunks, (index, cancel) => PutChunkAsync(file, session, size, index, progress, cancel));
            }
            await CompleteAsync(fullPath, session, size, modified);
            return how;
        }
        catch
        {
            _sockets.Forget(session.Id);
            await DropAsync(session);
            throw;
        }
    }

    private static SafeFileHandle OpenFile(string fullPath) =>
        File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous);

    private static int ChunkCount(UploadSession session, long size) => (int)Math.Max(1, (size + session.ChunkSize - 1) / session.ChunkSize);

    private async Task<UploadSession> OpenSessionAsync(string folder, string name, long size, DateTime modified, string transport)
    {
        using var response = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ServerAddress.Api(api.ServerUrl, "files/upload/sessions"))
        {
            Content = JsonContent.Create(new { path = folder, name, size, onConflict = "overwrite", modifiedAt = modified.ToString("O"), transport }),
        }, CancellationToken.None);
        return (await response.Content.ReadFromJsonAsync<UploadSession>())!;
    }

    /// <summary>
    /// Moves the file into place on the server, unless it changed while being
    /// sent: then it is left to be sent again, rather than marked synced with
    /// an older copy on the server.
    /// </summary>
    private async Task CompleteAsync(string fullPath, UploadSession session, long size, DateTime modified)
    {
        var now = new FileInfo(fullPath);
        if (now.Length != size || now.LastWriteTimeUtc != modified) throw new IOException("The file changed while it was being uploaded.");
        using var _ = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ServerAddress.Api(api.ServerUrl, $"files/upload/sessions/{Uri.EscapeDataString(session.Id)}/complete")), CancellationToken.None);
    }

    /// <summary>Frees the partial file on the server now rather than when it times out. Best effort.</summary>
    private async Task DropAsync(UploadSession session)
    {
        try
        {
            using var _ = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ServerAddress.Api(api.ServerUrl, $"files/upload/sessions/{Uri.EscapeDataString(session.Id)}")), CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException or SignedOutException)
        {
            // The server drops it on its own after 10 minutes.
        }
    }

    private static async Task<byte[]> ReadChunkAsync(SafeFileHandle file, UploadSession session, long size, int index, CancellationToken cancel)
    {
        var start = (long)index * session.ChunkSize;
        var data = new byte[(int)Math.Min(session.ChunkSize, size - start)];
        var filled = 0;
        while (filled < data.Length)
        {
            var read = await RandomAccess.ReadAsync(file, data.AsMemory(filled), start + filled, cancel);
            if (read == 0) throw new IOException("The file changed while it was being uploaded.");
            filled += read;
        }
        return data;
    }

    /// <summary>One chunk as an HTTP PUT, sent again after a dropped connection or a server fault.</summary>
    private async Task PutChunkAsync(SafeFileHandle file, UploadSession session, long size, int index, Action<long> progress, CancellationToken cancel)
    {
        var data = await ReadChunkAsync(file, session, size, index, cancel);
        var url = ServerAddress.Api(api.ServerUrl, $"files/upload/sessions/{Uri.EscapeDataString(session.Id)}/chunks/{index}");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var _ = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
                }, cancel);
                progress(data.Length);
                return;
            }
            catch (Exception ex) when (attempt < HttpAttempts && !cancel.IsCancellationRequested && Retryable(ex))
            {
                // 409: the server is still writing the copy whose connection dropped; give it a moment.
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancel);
            }
        }
    }

    private static bool Retryable(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException || ex is ApiException { Status: 0 or 408 or 409 or 429 or >= 500 };

    /// <summary>
    /// Runs `send` for each index with up to `parallel` at once. Stops at the
    /// first that fails for good and throws its error, not the cancellations it
    /// then causes in the others.
    /// </summary>
    private static async Task SendChunksAsync(List<int> indices, int parallel, Func<int, CancellationToken, Task> send)
    {
        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        var next = -1;

        async Task Worker()
        {
            try
            {
                int at;
                while (!stop.IsCancellationRequested && (at = Interlocked.Increment(ref next)) < indices.Count)
                    await send(indices[at], stop.Token);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                stop.Cancel();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(parallel, indices.Count)).Select(_ => Worker()));
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    public void Dispose() => _sockets.Dispose();
}
