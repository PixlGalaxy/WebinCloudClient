using System.Collections.Concurrent;
using System.IO;
using CloudClient.Core;

namespace CloudClient.Sync;

/// <summary>
/// Watches the sync folder for files and folders created or edited on this
/// computer and hands each one to the provider once it has been quiet for a
/// moment, so a file still being written is not uploaded half-way. Deletes
/// and renames of synced items are not seen here: Windows reports those to
/// the provider before they happen, so the server can refuse them.
/// </summary>
public sealed class LocalChanges : IDisposable
{
    /// <summary>How long a path must stay unchanged before it is uploaded.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    /// <summary>A file another program still holds open is tried again after this.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);
    /// <summary>A change that keeps failing waits longer each time, up to this.</summary>
    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _due = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paths that could not be synced yet and wait for another try: how many tries failed, and the last error logged.</summary>
    private readonly ConcurrentDictionary<string, (int Tries, string? Logged)> _failing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, Task<bool>> _process;
    private readonly Timer _timer;
    private int _running;
    private int _drainAgain;
    /// <summary>
    /// Changes sent at once. One at a time, a large upload, or a folder of
    /// thousands of files, held back a file just copied until all of it was
    /// done; now the newest goes as soon as one of these is free.
    /// </summary>
    private const int Parallel = 4;
    private readonly SemaphoreSlim _slots = new(Parallel, Parallel);
    /// <summary>Paths being sent right now.</summary>
    private readonly ConcurrentDictionary<string, byte> _working = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="process">Handles one path; returns false to be tried again later.</param>
    public LocalChanges(string root, Func<string, Task<bool>> process)
    {
        _process = process;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            // Attributes: File Explorer's "Always keep on this device" and "Free up space" only set them.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Touch(e.FullPath);
        _watcher.Changed += (_, e) => Touch(e.FullPath);
        _watcher.Renamed += (_, e) => Touch(e.FullPath);
        _watcher.Error += (_, e) => Log.Warn($"Watching the sync folder: {e.GetException().Message}");
        _watcher.EnableRaisingEvents = true;
        _timer = new Timer(_ => Drain(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Queues a path to be looked at once it has been quiet.</summary>
    public void Touch(string path)
    {
        _due[path] = DateTime.UtcNow + Quiet;
        // A new change starts afresh: it is not held back by how often the previous one failed.
        if (_failing.TryGetValue(path, out var failing)) _failing[path] = (0, failing.Logged);
    }

    /// <summary>
    /// Queues a path found unsynced (a change made while the app was closed,
    /// say) unless it is already waiting or being sent: its quiet time is not
    /// restarted, as a fresh change from the watcher would.
    /// </summary>
    public void Queue(string path)
    {
        if (!_working.ContainsKey(path)) _due.TryAdd(path, DateTime.UtcNow + Quiet);
    }

    /// <summary>Sync now: changes waiting for another try go at the next pass. Ones still being written keep their quiet time.</summary>
    public void RetryNow()
    {
        foreach (var path in _failing.Keys)
            if (_due.TryGetValue(path, out var due)) _due.TryUpdate(path, DateTime.UtcNow, due);
    }

    /// <summary>Changes made here and not on the server yet: waiting, being sent, or waiting for another try.</summary>
    public int Pending => _due.Count + _working.Count;

    /// <summary>Changes that failed and wait for another try (the server refused or could not be reached).</summary>
    public int Failing => _failing.Count;

    private void Later(string path, TimeSpan delay) => _due.TryAdd(path, DateTime.UtcNow + delay);

    /// <summary>
    /// Every 5 s for the first minute (a file another program has open is
    /// usually let go soon), then 10 s, 20 s... up to 5 minutes: a change
    /// that cannot go through must not retry every few seconds for days.
    /// </summary>
    private static TimeSpan Backoff(int tries)
    {
        const int Patient = 12;
        if (tries <= Patient) return Retry;
        return TimeSpan.FromSeconds(Math.Min(LongestRetry.TotalSeconds, Retry.TotalSeconds * Math.Pow(2, Math.Min(tries - Patient, 10))));
    }

    /// <summary>
    /// Starts what is due, up to <see cref="Parallel"/> at once, without
    /// waiting for any of it to finish. Folders go first, parents before their
    /// contents; then the newest files, so the one just copied is not stuck
    /// behind a long batch. Nothing inside a folder starts while that folder
    /// is being created on the server.
    /// </summary>
    private void Drain()
    {
        // Asked while running: that pass goes round once more, so a slot freed meanwhile is not left idle.
        Interlocked.Exchange(ref _drainAgain, 1);
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            while (Interlocked.Exchange(ref _drainAgain, 0) == 1) StartDue();
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
        // A request that came in just as the loop ended.
        if (Volatile.Read(ref _drainAgain) == 1) Drain();
    }

    private void StartDue()
    {
        {
            var now = DateTime.UtcNow;
            var ready = _due.Where(p => p.Value <= now && !_working.ContainsKey(p.Key))
                .Select(p => (Path: p.Key, Due: p.Value, Folder: Directory.Exists(p.Key)))
                .OrderByDescending(p => p.Folder)
                .ThenBy(p => p.Folder ? p.Path.Count(c => c == '\\') : 0)
                .ThenByDescending(p => p.Due)
                .ToList();
            foreach (var (path, _, _) in ready)
            {
                if (_working.Keys.Any(busy => path.StartsWith(busy + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                if (!_slots.Wait(0)) break;
                if (!_due.TryGetValue(path, out var due) || due > DateTime.UtcNow || !_working.TryAdd(path, 0))
                {
                    _slots.Release();
                    continue;
                }
                _due.TryRemove(path, out _);
                _ = Task.Run(() => ProcessOneAsync(path));
            }
        }
    }

    private async Task ProcessOneAsync(string path)
    {
        try
        {
            bool done;
            string? error = null;
            try
            {
                done = await _process(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                done = false;
            }
            if (done)
            {
                if (_failing.TryRemove(path, out var past) && past.Logged is not null) Log.Info($"Synced after {past.Tries} failed tries: \"{path}\"");
                return;
            }
            var (tries, logged) = _failing.TryGetValue(path, out var before) ? before : (0, null);
            tries++;
            // Logged once per error, not on every try: the same failure for days filled the log.
            if (error is not null && error != logged)
            {
                Log.Warn($"Could not sync \"{path}\": {error} (tried again later, waiting longer each time)");
                logged = error;
            }
            _failing[path] = (tries, logged);
            Later(path, Backoff(tries));
        }
        finally
        {
            _working.TryRemove(path, out _);
            _slots.Release();
            // The next one starts now, not at the next tick: with a tick a second, small files went four a second.
            Drain();
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _timer.Dispose();
    }
}
