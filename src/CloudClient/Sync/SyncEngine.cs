using CloudClient.Api;
using CloudClient.Core;

namespace CloudClient.Sync;

/// <summary>Brings the cloud drive up after sign-in and takes it down on sign-out.</summary>
public sealed class SyncEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ApiClient _api;
    private CloudProvider? _provider;

    /// <summary>Why the drive is not available, in words; null while it works.</summary>
    public string? Problem { get; private set; }

    public bool IsRunning => _provider is not null;

    /// <summary>Raised when the drive comes up or fails, from a background thread.</summary>
    public event Action? StateChanged;

    public SyncEngine(AppSettings settings, ApiClient api)
    {
        _settings = settings;
        _api = api;
    }

    /// <summary>
    /// Follows the server: a changed folder is refreshed, and every (re)connect
    /// catches up on what changed while the app was away.
    /// </summary>
    public void Attach(ServerEvents events)
    {
        events.FolderChanged += folder => _ = _provider?.ReconcileAsync(folder);
        events.ConnectionChanged += connected =>
        {
            if (connected) _ = _provider?.ReconcileAllAsync();
        };
    }

    public async Task StartAsync()
    {
        try
        {
            var id = SyncRoot.IdFor(_api.ServerUrl, _settings.Username ?? "");
            await SyncRoot.RegisterAsync(id, _settings.SyncFolder, _api.ServerUrl);
            _settings.SyncRootId = id;
            _settings.Save();

            _provider = new CloudProvider(_api, _settings.SyncFolder);
            _provider.Connect();
            Problem = null;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the cloud drive", ex);
            _provider = null;
            Problem = "The folder could not be set up in File Explorer. Details are in the log.";
        }
        StateChanged?.Invoke();
    }

    /// <summary>Stops answering Windows; the drive stays registered, as when the app quits.</summary>
    public void Stop()
    {
        _provider?.Dispose();
        _provider = null;
    }

    /// <summary>Signing out: stop, and remove the drive from File Explorer.</summary>
    public void Remove()
    {
        Stop();
        if (_settings.SyncRootId is not null)
        {
            SyncRoot.Unregister(_settings.SyncRootId);
            _settings.SyncRootId = null;
            _settings.Save();
        }
    }

    public void Dispose() => Stop();
}
