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
        _timer = new Timer(_ => _ = DrainAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Queues a path to be looked at once it has been quiet.</summary>
    public void Touch(string path) => _due[path] = DateTime.UtcNow + Quiet;

    /// <summary>Sync now: every change waiting, failed ones included, is tried at the next pass.</summary>
    public void RetryNow()
    {
        foreach (var path in _due.Keys) _due[path] = DateTime.UtcNow;
    }

    /// <summary>Changes made here and not on the server yet, including the ones waiting for another try.</summary>
    public int Pending => _due.Count;

    /// <summary>Changes that failed and wait for another try (the server refused or could not be reached).</summary>
    public int Failing => _failing.Count;

    private void Later(string path, TimeSpan delay) => _due.TryAdd(path, DateTime.UtcNow + delay);

    /// <summary>5 s, 10 s, 20 s... up to 5 minutes: a change that cannot go through must not retry every few seconds for days.</summary>
    private static TimeSpan Backoff(int tries) =>
        TimeSpan.FromSeconds(Math.Min(LongestRetry.TotalSeconds, Retry.TotalSeconds * Math.Pow(2, Math.Min(tries - 1, 10))));

    private async Task DrainAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;
            // Parents first, so a new folder exists on the server before its files go up.
            var ready = _due.Where(p => p.Value <= now).Select(p => p.Key)
                .OrderBy(p => p.Count(c => c == '\\')).ToList();
            foreach (var path in ready)
            {
                if (!_due.TryGetValue(path, out var due) || due > DateTime.UtcNow) continue;
                _due.TryRemove(path, out _);
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
                    continue;
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
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _timer.Dispose();
    }
}
