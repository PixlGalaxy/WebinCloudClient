using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CloudClient.Core;

namespace CloudClient.Api;

/// <summary>Upload sockets cannot be opened here (a proxy without WebSocket support, say): send over HTTP instead.</summary>
public sealed class SocketsUnavailableException() : Exception("Upload sockets are unavailable.");

/// <summary>
/// Upload sockets to the server (docs/desktop-api.md in the server repository).
/// One connection across a long distance carries only so much; chunks spread
/// over many sockets, with a window of bytes in flight on each, keep a fast
/// line full. A socket only carries chunk data: uploads are opened and
/// completed over HTTP. A chunk on a socket that drops is sent again on another.
/// </summary>
public sealed class UploadSocketPool : IDisposable
{
    /// <summary>Sockets open at once, as many as the web interface opens (the server allows 64 per user).</summary>
    private const int Sockets = 32;
    /// <summary>
    /// Bytes sent and not yet acknowledged per socket. Keeps a long-distance
    /// connection busy and rides out the second-long pauses some servers' disks
    /// make while committing, during which the server reads but cannot acknowledge.
    /// </summary>
    private const long WindowBytes = 8 * 1024 * 1024;
    /// <summary>Session id (16 bytes) and chunk index (4 bytes, big-endian) ahead of each chunk's data.</summary>
    private const int HeaderBytes = 20;
    /// <summary>Times a chunk is sent before its file fails.</summary>
    private const int Attempts = 5;
    /// <summary>Sockets in a row that failed to open, with none open, before giving up.</summary>
    private const int FailedOpens = 3;
    /// <summary>The server closes with this once the device is signed out or loses access.</summary>
    private const int ClosedNotAllowed = 4403;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Sockets with nothing to send are closed after this long.</summary>
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(30);
    /// <summary>A dropped socket is opened again after this, rather than hammering a server that just dropped us.</summary>
    private static readonly TimeSpan Reopen = TimeSpan.FromSeconds(1);
    /// <summary>Sockets that never opened are tried again after this; the app runs for days, across networks.</summary>
    private static readonly TimeSpan RetrySocketsAfter = TimeSpan.FromMinutes(10);

    private readonly ApiClient _api;
    private readonly Uri _url;
    private readonly object _gate = new();
    private readonly LinkedList<Job> _queue = new();
    private readonly HashSet<Lane> _lanes = [];
    private readonly Timer _idle;
    private bool _everOpened;
    private int _failedOpens;
    private DateTime _brokenUntil;
    private bool _disposed;
    /// <summary>Why the last socket failed to open, for the one log line when they never do.</summary>
    private string? _openError;

    public UploadSocketPool(ApiClient api)
    {
        _api = api;
        _url = ServerAddress.Socket(api.ServerUrl, "uploads/socket");
        _idle = new Timer(_ => CloseIdle());
    }

    /// <summary>True while sockets could not be opened here: send over HTTP.</summary>
    public bool Broken
    {
        get
        {
            lock (_gate) return DateTime.UtcNow < _brokenUntil;
        }
    }

    /// <summary>
    /// Sends chunk `index` of upload `id`; completes once the server has it on
    /// disk. Fails with SocketsUnavailableException when sockets cannot be
    /// opened here, and with ApiException when the server refuses the chunk.
    /// </summary>
    public Task SendAsync(string id, int index, ReadOnlySpan<byte> data)
    {
        var frame = new byte[HeaderBytes + data.Length];
        Convert.FromHexString(id.Replace("-", "")).CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16), (uint)index);
        data.CopyTo(frame.AsSpan(HeaderBytes));
        var job = new Job(id.ToLowerInvariant(), index, frame);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (DateTime.UtcNow < _brokenUntil) return Task.FromException(new SocketsUnavailableException());
            _queue.AddLast(job);
            Wake();
        }
        return job.Done.Task;
    }

    /// <summary>Drops the chunks of `id` still waiting; those already sent finish on their own.</summary>
    public void Forget(string id)
    {
        id = id.ToLowerInvariant();
        lock (_gate)
        {
            for (var node = _queue.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Id == id)
                {
                    node.Value.Done.TrySetCanceled();
                    _queue.Remove(node);
                }
                node = next;
            }
        }
    }

    /// <summary>Under the gate: opens sockets for what is waiting, then hands it out.</summary>
    private void Wake()
    {
        _idle.Change(Timeout.Infinite, Timeout.Infinite);
        var wanted = Math.Min(Sockets, _queue.Count + _lanes.Sum(lane => lane.Jobs.Count));
        while (_lanes.Count < wanted && !_disposed && DateTime.UtcNow >= _brokenUntil)
        {
            var lane = new Lane();
            _lanes.Add(lane);
            _ = RunAsync(lane);
        }
        Pump();
    }

    /// <summary>Under the gate: fills each open socket's window from the queue.</summary>
    private void Pump()
    {
        foreach (var lane in _lanes)
        {
            while (lane.Open && !lane.Retiring && lane.Bytes < WindowBytes && _queue.First is { } node)
            {
                _queue.RemoveFirst();
                var job = node.Value;
                if (job.Done.Task.IsCompleted) continue;
                lane.Jobs[(job.Id, job.Index)] = job;
                lane.Bytes += job.Length;
                lane.Outgoing.Writer.TryWrite(job);
            }
        }

        var busy = _queue.Count > 0 || _lanes.Any(lane => lane.Jobs.Count > 0);
        if (!busy && _lanes.Count > 0) _idle.Change(IdleClose, Timeout.InfiniteTimeSpan);
    }

    private void CloseIdle()
    {
        lock (_gate)
        {
            if (_queue.Count > 0 || _lanes.Any(lane => lane.Jobs.Count > 0)) return;
            foreach (var lane in _lanes) Retire(lane);
        }
    }

    /// <summary>Closes a socket on purpose: its end is not counted as a failure.</summary>
    private static void Retire(Lane lane)
    {
        lane.Retiring = true;
        // Callbacks run elsewhere, never inline under the gate.
        _ = lane.Stop.CancelAsync();
    }

    private async Task RunAsync(Lane lane)
    {
        int? closeCode = null;
        try
        {
            await ConnectAsync(lane);
            lock (_gate)
            {
                lane.Open = true;
                _everOpened = true;
                _failedOpens = 0;
                Pump();
            }
            var sending = SendLoopAsync(lane);
            closeCode = await ReceiveLoopAsync(lane);
            await sending;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException or ApiException or SignedOutException or ObjectDisposedException)
        {
            if (!lane.Open && !lane.Retiring)
            {
                // Logged here only once sockets have worked; until then, one line sums it up below.
                lock (_gate)
                {
                    _openError = ex.Message;
                    if (_everOpened) Log.Warn($"Could not open an upload socket: {ex.Message}");
                }
            }
        }
        finally
        {
            lane.Outgoing.Writer.TryComplete();
            _ = lane.Stop.CancelAsync();
            lane.Ws.Dispose();
        }
        Closed(lane, closeCode);
    }

    private async Task ConnectAsync(Lane lane)
    {
        var token = await _api.AccessTokenAsync(null, lane.Stop.Token);
        lane.Ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        lane.Ws.Options.CollectHttpResponseDetails = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lane.Stop.Token);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await lane.Ws.ConnectAsync(_url, timeout.Token);
        }
        catch (WebSocketException) when (lane.Ws.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            // Renewed for the next socket; this one counts as failed to open.
            await _api.AccessTokenAsync(token, CancellationToken.None);
            throw;
        }
    }

    /// <summary>One message at a time per socket, as WebSockets require.</summary>
    private static async Task SendLoopAsync(Lane lane)
    {
        try
        {
            await foreach (var job in lane.Outgoing.Reader.ReadAllAsync(lane.Stop.Token))
                await lane.Ws.SendAsync(job.Frame, WebSocketMessageType.Binary, true, lane.Stop.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The receive loop then ends too, and the socket's chunks go to another.
            lane.Ws.Abort();
        }
    }

    /// <summary>Reads acknowledgements until the socket closes; returns the server's close code.</summary>
    private async Task<int?> ReceiveLoopAsync(Lane lane)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await lane.Ws.ReceiveAsync(buffer, lane.Stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) return (int?)result.CloseStatus;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            if (result.MessageType == WebSocketMessageType.Text) Acknowledge(lane, message.GetBuffer().AsSpan(0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void Acknowledge(Lane lane, ReadOnlySpan<byte> json)
    {
        Reply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<Reply>(json);
        }
        catch (JsonException)
        {
            return;
        }
        if (reply?.Id is null) return;

        lock (_gate)
        {
            if (!lane.Jobs.Remove((reply.Id.ToLowerInvariant(), reply.Index), out var job)) return;
            lane.Bytes -= job.Length;
            if (reply.Ok) job.Done.TrySetResult();
            else Retry(job, new ApiException(reply.Status, reply.Error ?? "The server did not take part of the upload."), Retryable(reply.Status));
            Pump();
        }
    }

    /// <summary>Still writing an earlier copy, throttled, or a server fault: worth sending again.</summary>
    private static bool Retryable(int status) => status is 0 or 409 or 429 or >= 500;

    /// <summary>Under the gate.</summary>
    private void Retry(Job job, Exception error, bool worthIt)
    {
        if (job.Done.Task.IsCompleted) return;
        job.Attempts++;
        if (worthIt && job.Attempts < Attempts) _queue.AddFirst(job);
        else job.Done.TrySetException(error);
    }

    private void Closed(Lane lane, int? closeCode)
    {
        var reopen = false;
        lock (_gate)
        {
            // Already let go of by FailAll.
            if (!_lanes.Remove(lane)) return;
            if (_disposed) return;

            if (!lane.Open && !lane.Retiring && ++_failedOpens >= FailedOpens && !_lanes.Any(other => other.Open))
            {
                if (!_everOpened)
                {
                    // Never worked here: the uploads waiting go over HTTP instead.
                    Log.Warn($"Upload sockets cannot be opened at {_url} ({_openError}); sending over HTTP for {RetrySocketsAfter.TotalMinutes:0} minutes");
                    _brokenUntil = DateTime.UtcNow + RetrySocketsAfter;
                    FailAll(new SocketsUnavailableException());
                }
                else
                {
                    // Worked before (offline now, or the server went away): the uploads fail and are retried later.
                    FailAll(new ApiException(0, "The upload connection was lost."));
                }
                return;
            }

            var denied = closeCode == ClosedNotAllowed;
            foreach (var job in lane.Jobs.Values)
                Retry(job, new ApiException(denied ? 403 : 0, "The upload connection was lost."), !denied);
            lane.Jobs.Clear();
            if (denied)
            {
                foreach (var job in _queue) job.Done.TrySetException(new ApiException(403, "This device can no longer upload."));
                _queue.Clear();
                return;
            }
            reopen = _queue.Count > 0;
            if (!reopen) Pump();
        }
        if (reopen)
            _ = Task.Delay(Reopen).ContinueWith(_ =>
            {
                lock (_gate)
                    if (!_disposed) Wake();
            });
    }

    /// <summary>Under the gate: fails everything waiting or in flight and lets every socket go.</summary>
    private void FailAll(Exception error)
    {
        _failedOpens = 0;
        foreach (var lane in _lanes)
        {
            foreach (var job in lane.Jobs.Values) job.Done.TrySetException(error);
            lane.Jobs.Clear();
            Retire(lane);
        }
        _lanes.Clear();
        foreach (var job in _queue) job.Done.TrySetException(error);
        _queue.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            FailAll(new ObjectDisposedException(nameof(UploadSocketPool)));
        }
        _idle.Dispose();
    }

    private sealed class Job(string id, int index, byte[] frame)
    {
        public string Id { get; } = id;
        public int Index { get; } = index;
        public byte[] Frame { get; } = frame;
        public int Length => Frame.Length - HeaderBytes;
        public int Attempts { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lane
    {
        public ClientWebSocket Ws { get; } = new();
        public CancellationTokenSource Stop { get; } = new();
        public Channel<Job> Outgoing { get; } = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
        public Dictionary<(string Id, int Index), Job> Jobs { get; } = [];
        public bool Open { get; set; }
        public bool Retiring { get; set; }
        /// <summary>Bytes sent on this socket and not yet acknowledged.</summary>
        public long Bytes { get; set; }
    }

    private sealed class Reply
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
