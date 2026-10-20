using System.Windows;
using System.Windows.Threading;
using CloudClient.Api;
using CloudClient.Auth;
using CloudClient.Core;
using CloudClient.Sync;
using CloudClient.UI;
using CloudClient.Updates;
using CloudClient.Views;

namespace CloudClient;

/// <summary>
/// Starts once per user (a second launch just brings the window forward),
/// lives in the tray, and switches between the setup window and the main
/// window as the device signs in and out.
/// </summary>
public sealed class App : Application
{
    /// <summary>Signalled by the installer and uninstaller to make a running copy quit.</summary>
    public static readonly string QuitEventName = $@"Local\{Brand.Id}.Quit";
    /// <summary>The first check waits a little, so starting with Windows stays light.</summary>
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(6);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly bool _background;
    private TrayIcon? _tray;
    private ApiClient? _api;
    private ServerEvents? _events;
    private SyncEngine? _sync;
    private Updater? _updater;
    private DispatcherTimer? _updateTimer;
    private MainWindow? _main;
    private SetupWindow? _setup;

    private App(bool background)
    {
        _background = background;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled error", e.Exception);
            e.Handled = true;
        };
    }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains(Installer.UninstallArgument))
        {
            Installer.Uninstall();
            return;
        }
        if (Installer.ShouldInstall(args))
        {
            Installer.Install();
            return;
        }
        Updater.FinishPrevious(args);
        // The app is in English throughout, dates and numbers included, whatever the Windows language.
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = english;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
        Thread.CurrentThread.CurrentCulture = english;
        Thread.CurrentThread.CurrentUICulture = english;

        using var single = new Mutex(true, $@"Local\{Brand.Id}.App", out var first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Brand.Id}.Show");
        if (!first)
        {
            showSignal.Set();
            return;
        }

        // Started by Windows to serve File Explorer's thumbnails: stay in the tray.
        var background = args.Contains(AutoStart.BackgroundArgument) ||
                         args.Any(a => a.Equals(ThumbnailServer.Embedding, StringComparison.OrdinalIgnoreCase) || a.Equals("/Embedding", StringComparison.OrdinalIgnoreCase));
        var app = new App(background);
        Theme.Apply(app);

        // A second launch signals this event; bring the window forward.
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => app.Dispatcher.BeginInvoke(app.ShowWindow), null, -1, false);
        using var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => app.Dispatcher.BeginInvoke(app.Quit), null, -1, true);
        app.Startup += (_, _) => app.Begin();
        app.Run();
    }

    private void Begin()
    {
        Log.Info($"{Brand.Name} {Updater.Current} starting from {Environment.ProcessPath}");
        _tray = new TrayIcon(ShowWindow, TrayItems);
        ThumbnailServer.Start();
        Installer.Refresh();
        // Points the start-with-Windows entry at this exe, wherever it now lives.
        if (_settings.IsSignedIn) AutoStart.Apply(_settings.StartWithWindows);

        var tokens = _settings.IsSignedIn ? SafeLoadTokens(_settings.ServerUrl!) : null;
        if (tokens is null) ShowSetup();
        else StartSignedIn(tokens, show: !_background);
    }

    private static TokenSet? SafeLoadTokens(string serverUrl)
    {
        try
        {
            return TokenStore.Load(serverUrl);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the saved sign-in", ex);
            return null;
        }
    }

    private void StartSignedIn(TokenSet tokens, bool show)
    {
        _api = new ApiClient(_settings.ServerUrl!, tokens);
        _api.SignedOut += () => Dispatcher.BeginInvoke(() => EndSession(serverSide: false));
        _sync = new SyncEngine(_settings, _api);
        _updater = new Updater(_api);
        _updater.Changed += () => Dispatcher.BeginInvoke(() => _tray?.SetUpdate(_updater?.Available?.Version));
        _updateTimer = new DispatcherTimer { Interval = FirstUpdateCheck };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer!.Interval = UpdateCheckEvery;
            _ = _updater?.CheckAsync();
        };
        _updateTimer.Start();
        _main = new MainWindow(_settings, _api, _sync, _updater, InstallUpdate, () => EndSession(serverSide: true));
        _tray?.SetStatus("Connecting…");
        _events = new ServerEvents(_api);
        _events.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => _tray?.SetStatus(connected ? "Connected" : "Offline, retrying"));
        _sync.Attach(_events);
        // The drive first, so the catch-up on connecting finds it ready.
        _ = _sync.StartAsync().ContinueWith(_ => _events?.Start());
        if (show) _main.Show();
    }

    private void ShowSetup()
    {
        _tray?.SetStatus("Not signed in");
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        _setup = new SetupWindow(_settings, result =>
        {
            AutoStart.Apply(_settings.StartWithWindows);
            StartSignedIn(result.Tokens, show: true);
        });
        _setup.Closed += (_, _) => _setup = null;
        _setup.Show();
    }

    /// <summary>Signs this device out (telling the server when asked) and goes back to setup.</summary>
    private async void EndSession(bool serverSide)
    {
        var api = _api;
        _api = null;
        _updateTimer?.Stop();
        _updateTimer = null;
        _updater = null;
        _tray?.SetUpdate(null);
        _events?.Dispose();
        _events = null;
        _sync?.Remove();
        _sync = null;
        if (_main is not null)
        {
            _main.ExitRequested = true;
            _main.Close();
            _main = null;
        }
        if (api is not null)
        {
            if (serverSide) await api.SignOutAsync();
            else TokenStore.Delete(api.ServerUrl);
            api.Dispose();
        }
        _settings.DeviceId = null;
        _settings.Username = null;
        _settings.Save();
        Log.Info("Signed out");
        ShowSetup();
    }

    private void ShowWindow()
    {
        if (_main is null)
        {
            ShowSetup();
            return;
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _ = _main.RefreshAsync();
    }

    private IReadOnlyList<TrayItem> TrayItems()
    {
        var items = new List<TrayItem> { new($"Open {Brand.Name}", "cloud", ShowWindow) };
        if (_updater?.Available is { } update && Updater.Supported) items.Add(new($"Update to version {update.Version}", "download", InstallUpdate));
        if (_api is not null) items.Add(new("Open website", "external-link", () => Views.MainWindow.OpenWebsite(_api.ServerUrl)));
        items.Add(new("Quit", "power", Quit));
        return items;
    }

    /// <summary>Downloads and installs the update; the new version starts as this one quits.</summary>
    private async void InstallUpdate()
    {
        if (_updater is null) return;
        if (await _updater.InstallAsync()) Quit();
    }

    private void Quit()
    {
        if (_main is not null) _main.ExitRequested = true;
        _tray?.Dispose();
        _events?.Dispose();
        _sync?.Stop();
        ThumbnailServer.Stop();
        _api?.Dispose();
        Log.Info($"{Brand.Name} closing");
        Shutdown();
    }
}
