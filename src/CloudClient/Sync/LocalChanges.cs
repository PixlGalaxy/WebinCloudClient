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

    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _due = new(StringComparer.OrdinalIgnoreCase);
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

    private void Later(string path, TimeSpan delay) => _due.TryAdd(path, DateTime.UtcNow + delay);

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
                try
                {
                    done = await _process(path);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not sync \"{path}\": {ex.Message}");
                    done = false;
                }
                if (!done) Later(path, Retry);
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
