using System.Collections.Concurrent;
using System.IO;

namespace CloudClient.Sync;

public enum ActivityKind
{
    Uploaded,
    Downloaded,
    CreatedFolder,
    Deleted,
    Moved,
    NewFromServer,
    ChangedOnServer,
    RemovedOnServer,
    FreedUp,
}

/// <summary>One finished sync action, as the Activity page lists it.</summary>
public sealed record ActivityEntry(DateTime At, ActivityKind Kind, string Path, long? Size = null, string? Detail = null);

public enum Direction
{
    Up,
    Down,
}

/// <summary>A file going up or down right now; Done grows as bytes move.</summary>
public sealed class Transfer
{
    private long _done;

    public required string Path { get; init; }
    public required Direction Direction { get; init; }
    public required long Total { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public long Done => Interlocked.Read(ref _done);

    internal void Add(long bytes) => Interlocked.Add(ref _done, bytes);
}

/// <summary>
/// What the sync engine is doing and has done in this session, for the Home
/// and Activity pages: transfers in progress, transfer speeds, recently
/// synced items and session totals. Kept in memory only; the log file has the
/// full history.
/// </summary>
public static class Activity
{
    private const int MaxRecent = 300;
    /// <summary>Speeds are averaged over this window, so they read steadily.</summary>
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);

    private static readonly object Gate = new();
    private static readonly LinkedList<ActivityEntry> RecentList = new();
    private static readonly ConcurrentDictionary<Transfer, byte> Active = new();
    private static readonly ConcurrentQueue<(DateTime At, Direction Direction, long Bytes)> Samples = new();
    private static long _bytesUp;
    private static long _bytesDown;
    private static int _filesUp;
    private static int _filesDown;
    private static int _version;

    /// <summary>Goes up on every change, so a page redraws only when something happened.</summary>
    public static int Version => Volatile.Read(ref _version);

    public static long BytesUploaded => Interlocked.Read(ref _bytesUp);
    public static long BytesDownloaded => Interlocked.Read(ref _bytesDown);
    public static int FilesUploaded => Volatile.Read(ref _filesUp);
    public static int FilesDownloaded => Volatile.Read(ref _filesDown);

    public static void Record(ActivityKind kind, string path, long? size = null, string? detail = null)
    {
        lock (Gate)
        {
            RecentList.AddFirst(new ActivityEntry(DateTime.Now, kind, path, size, detail));
            while (RecentList.Count > MaxRecent) RecentList.RemoveLast();
        }
        if (kind == ActivityKind.Uploaded) Interlocked.Increment(ref _filesUp);
        if (kind == ActivityKind.Downloaded) Interlocked.Increment(ref _filesDown);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<ActivityEntry> Recent(int max = MaxRecent)
    {
        lock (Gate) return RecentList.Take(max).ToList();
    }

    public static Transfer Begin(string path, Direction direction, long total)
    {
        var transfer = new Transfer { Path = path, Direction = direction, Total = total };
        Active[transfer] = 0;
        Interlocked.Increment(ref _version);
        return transfer;
    }

    public static void Progress(Transfer transfer, long bytes)
    {
        transfer.Add(bytes);
        if (transfer.Direction == Direction.Up) Interlocked.Add(ref _bytesUp, bytes);
        else Interlocked.Add(ref _bytesDown, bytes);
        Samples.Enqueue((DateTime.UtcNow, transfer.Direction, bytes));
        Interlocked.Increment(ref _version);
    }

    public static void End(Transfer transfer)
    {
        Active.TryRemove(transfer, out _);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Oldest first.</summary>
    public static IReadOnlyList<Transfer> Transfers() => Active.Keys.OrderBy(t => t.StartedAt).ToList();

    /// <summary>Bytes per second over the last few seconds.</summary>
    public static double Speed(Direction direction)
    {
        var since = DateTime.UtcNow - SpeedWindow;
        while (Samples.TryPeek(out var oldest) && oldest.At < since) Samples.TryDequeue(out _);
        return Samples.Where(s => s.Direction == direction && s.At >= since).Sum(s => s.Bytes) / SpeedWindow.TotalSeconds;
    }
}

/// <summary>Reports bytes as they are read, so an upload's progress can be shown.</summary>
public sealed class ProgressStream(Stream inner, Action<long> report) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0) report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        if (read > 0) report(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
