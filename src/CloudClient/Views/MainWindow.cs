using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CloudClient.Api;
using CloudClient.Core;
using CloudClient.Sync;
using CloudClient.UI;
using CloudClient.Updates;
using Activity = CloudClient.Sync.Activity;
using ActivityKind = CloudClient.Sync.ActivityKind;

namespace CloudClient.Views;

/// <summary>
/// The app's window: a sidebar with Home, Activity and Settings. Home and
/// Activity follow the sync engine live while the window is open. Closing it
/// keeps the app in the tray.
/// </summary>
public sealed class MainWindow : AppWindow
{
    private const int HomeTransfers = 5;
    private const int HomeRecent = 6;
    private const int HomeProblems = 3;
    private const int LogLines = 400;

    private readonly AppSettings _settings;
    private readonly ApiClient _api;
    private readonly Action _signOut;
    private readonly SyncEngine _sync;
    private readonly Updater _updater;
    private readonly Action _installUpdate;
    private readonly ContentControl _page = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RadioButton _navActivity;
    private UserInfo? _user;
    private string? _connectionError;
    private string _activityTab = "recent";

    // Parts of the current page the ticker refreshes in place; null when that page is not shown.
    private TextBlock? _upSpeed, _downSpeed, _upTotal, _downTotal, _upFiles, _downFiles;
    private StackPanel? _transfersBox, _recentBox, _problemsBox, _activityList;
    private ContentControl? _updateBox;
    private int _seenActivity = -1;
    private int _seenProblems = -1;
    private bool _hadTransfers = true;
    /// <summary>Sync now is running; kept here so a Home redrawn meanwhile still shows it.</summary>
    private bool _syncingNow;
    /// <summary>The Files card's state badge, redrawn by the ticker when the state changes.</summary>
    private ContentControl? _filesBadge;
    private string? _filesState;

    public bool ExitRequested { get; set; }

    public MainWindow(AppSettings settings, ApiClient api, SyncEngine sync, Updater updater, Action installUpdate, Action signOut)
    {
        _settings = settings;
        _api = api;
        _signOut = signOut;
        _sync = sync;
        _updater = updater;
        _installUpdate = installUpdate;
        // The drive starts in the background; refresh the card once it is up.
        sync.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (_page.Tag as string == "home") ShowHome();
        });
        updater.Changed += () => Dispatcher.BeginInvoke(FillUpdate);
        Title = Brand.Name;
        // Tall enough for all of Home, but never larger than the screen.
        var screen = SystemParameters.WorkArea;
        Width = Math.Min(1120, screen.Width - 40);
        Height = Math.Min(940, screen.Height - 40);
        MinWidth = Math.Min(900, Width);
        MinHeight = Math.Min(600, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(Sidebar(out _navActivity));
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Padding = new Thickness(40, 32, 40, 36), Child = _page } };
        Grid.SetColumn(scroller, 1);
        root.Children.Add(scroller);
        Content = root;

        _ticker.Tick += (_, _) => Tick();
        // Live parts only update while someone can see them.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _ticker.Start();
            else _ticker.Stop();
        };

        ShowHome();
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) =>
        {
            // The window only hides; the app keeps running in the tray.
            if (ExitRequested) return;
            e.Cancel = true;
            Hide();
        };
    }

    public async Task RefreshAsync()
    {
        try
        {
            _user = (await _api.GetMeAsync()).User;
            _connectionError = null;
        }
        catch (SignedOutException)
        {
            return;
        }
        catch (ApiException ex)
        {
            _connectionError = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _connectionError = "Can't reach the server right now.";
        }
        if (_page.Tag as string == "home") ShowHome();
    }

    private UIElement Sidebar(out RadioButton activity)
    {
        var panel = new DockPanel();
        var brand = Ui.Row(Ui.BrandImage("icon.png", 38), Spaced(Ui.Text(Brand.Name, "PanelTitleText"), 12));
        brand.Margin = new Thickness(24, 26, 16, 30);
        DockPanel.SetDock(brand, Dock.Top);
        panel.Children.Add(brand);

        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(26, 14, 20, 16),
            Child = Ui.Stack(
                FooterLine($"2026 {Brand.Id}Client", "TextMuted"),
                FooterLine("ItzGalaxy Web Services", "TextDim"),
                FooterLine($"Version: {Updater.Current}", "TextDim")),
        };
        footer.SetResourceReference(Border.BorderBrushProperty, "Divider");
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        var nav = new StackPanel();
        nav.Children.Add(NavItem("Home", "house", true, ShowHome));
        activity = NavItem("Activity", "activity", false, () => ShowActivity(_activityTab));
        nav.Children.Add(activity);
        nav.Children.Add(NavItem("Settings", "settings", false, ShowSettings));
        panel.Children.Add(nav);

        var sidebar = new Border { Child = panel, BorderThickness = new Thickness(0, 0, 1, 0) };
        sidebar.SetResourceReference(Border.BackgroundProperty, "SidebarBg");
        sidebar.SetResourceReference(Border.BorderBrushProperty, "Divider");
        return sidebar;
    }

    private static TextBlock FooterLine(string text, string brushKey)
    {
        var line = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 1, 0, 0) };
        line.SetResourceReference(TextBlock.FontFamilyProperty, "HeadingFont");
        line.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return line;
    }

    private static RadioButton NavItem(string text, string icon, bool selected, Action show)
    {
        var item = new RadioButton
        {
            GroupName = "nav",
            IsChecked = selected,
            Content = Ui.Row(Ui.Icon(icon, 18), Spaced(new TextBlock { Text = text }, 12)),
        };
        item.SetResourceReference(FrameworkElement.StyleProperty, "NavItem");
        AutomationProperties.SetName(item, text);
        item.Checked += (_, _) => show();
        return item;
    }

    private void OpenActivity(string tab)
    {
        _activityTab = tab;
        if (_navActivity.IsChecked == true) ShowActivity(tab);
        else _navActivity.IsChecked = true;
    }

    /// <summary>Forgets the previous page's live parts, so the ticker never updates a page no longer shown.</summary>
    private void SetPage(string tag, UIElement content)
    {
        _page.Tag = tag;
        _page.Content = content;
    }

    private void ResetLiveParts()
    {
        _upSpeed = _downSpeed = _upTotal = _downTotal = _upFiles = _downFiles = null;
        _filesBadge = null;
        _filesState = null;
        _transfersBox = _recentBox = _problemsBox = _activityList = null;
        _updateBox = null;
        _seenActivity = _seenProblems = -1;
        _hadTransfers = true;
    }

    // --- Home ------------------------------------------------------------------------

    private void ShowHome()
    {
        ResetLiveParts();
        var name = _user?.DisplayName ?? _user?.Username ?? _settings.Username ?? "";
        var header = Header("Home", "house", _connectionError is null ? $"Signed in as {name}" : _connectionError);

        var status = _connectionError is null
            ? Badge("Connected", "Success", "SuccessChip")
            : Badge("Offline", "Warning", "WarningChip");
        var account = Ui.Card(Ui.Stack(
            Ui.Row(Ui.PanelTitle("Account"), Spaced(status, 12)),
            Gap(Label("User"), 16),
            Gap(Ui.Text(name, "CardValue"), 2),
            Gap(Ui.Text(_user?.Email ?? "", "CardHint"), 2),
            Gap(Label("Server"), 14),
            Gap(Ui.Text(_api.ServerUrl, "Mono"), 4),
            Gap(Label("This computer"), 14),
            Gap(Ui.Text(_settings.DeviceName, "Body"), 4)));

        var ready = _sync.IsRunning;
        var openFolder = Ui.Button("Open folder", "folder-open", "SecondaryButton", (_, _) =>
            Process.Start(new ProcessStartInfo(_settings.SyncFolder) { UseShellExecute = true }));
        openFolder.HorizontalAlignment = HorizontalAlignment.Left;
        openFolder.IsEnabled = ready;
        var files = Ui.Stack(
            Ui.Row(Ui.PanelTitle("Files"), Spaced(_filesBadge = new ContentControl { Focusable = false }, 12)),
            Gap(Label("Folder"), 16),
            Gap(Ui.Text(_settings.SyncFolder, "Mono"), 4));
        if (_sync.Problem is not null) files.Children.Add(Gap(Ui.Sentence(_sync.Problem, "Warning"), 14));
        var syncNow = Ui.Button(_syncingNow ? "Syncing…" : "Sync now", "folder-sync", "SecondaryButton");
        syncNow.IsEnabled = ready && !_syncingNow;
        syncNow.Margin = new Thickness(10, 0, 0, 0);
        syncNow.Click += async (_, _) => await SyncNowAsync();
        files.Children.Add(Gap(Ui.Row(openFolder, syncNow), 18));
        var sync = Ui.Card(files);

        _updateBox = new ContentControl();
        // FillUpdate draws Home's banner or Settings' rows by the page shown, which is set further down: say it now.
        _page.Tag = "home";
        FillUpdate();

        _upSpeed = Ui.Text("", "CardValue");
        _downSpeed = Ui.Text("", "CardValue");
        _upTotal = Ui.Text("", "CardValue");
        _downTotal = Ui.Text("", "CardValue");
        _upFiles = Ui.Text("", "CardHint");
        _downFiles = Ui.Text("", "CardHint");
        var stats = new UniformGrid { Columns = 4, Rows = 1 };
        stats.Children.Add(Stat("arrow-up", "Accent", "Uploading", _upSpeed));
        stats.Children.Add(Stat("arrow-down", "Success", "Downloading", _downSpeed));
        stats.Children.Add(Stat("upload", "Accent", "Uploaded", _upTotal, _upFiles));
        stats.Children.Add(Stat("download", "Success", "Downloaded", _downTotal, _downFiles));
        _transfersBox = new StackPanel();
        var transfers = Ui.Card(Ui.Stack(
            Ui.PanelTitle("Transfers"),
            Gap(stats, 18), Gap(_transfersBox, 14)));

        _recentBox = new StackPanel();
        var recent = Ui.Card(Ui.Stack(
            TitleWithLink("Recent activity", "View all", () => OpenActivity("recent")),
            Gap(_recentBox, 6)));
        _problemsBox = new StackPanel();
        var problems = Ui.Card(Ui.Stack(
            TitleWithLink("Problems", "View all", () => OpenActivity("problems")),
            Gap(_problemsBox, 6)));

        SetPage("home", Ui.Stack(header, _updateBox,
            Gap(TwoColumns(account, sync, 1, 1), 26),
            Gap(transfers, 20),
            Gap(TwoColumns(recent, problems, 3, 2), 20)));
        Tick();
    }

    /// <summary>
    /// Icon, label and value, with an optional smaller line under the value.
    /// Kept inside its column: a long value is cut with "…" instead of running
    /// under the next one.
    /// </summary>
    /// <summary>
    /// What the Files card says about syncing, re-read every second: syncing
    /// while Sync now runs or anything goes up, comes down or waits to go up;
    /// unsynced when offline or a change keeps failing; synced otherwise.
    /// </summary>
    private void FillFilesBadge()
    {
        if (_filesBadge is null) return;
        var transfers = Activity.Transfers().Count > 0;
        var (state, tone, chip, tip) =
            _sync.Problem is not null ? ("Not available", "Warning", "WarningChip", _sync.Problem)
            : !_sync.IsRunning ? ("Starting…", "Accent", "AccentChip", "The folder is being set up in File Explorer.")
            : _sync.Online == false ? ("Unsynced", "Warning", "WarningChip", "Can't reach the server. Changes sync once it is back.")
            : _syncingNow || transfers || _sync.PendingChanges > _sync.FailingChanges
                ? ("Syncing", "Accent", "AccentChip", "Files are going up or coming down.")
            : _sync.FailingChanges > 0
                ? ("Unsynced", "Warning", "WarningChip", $"{_sync.FailingChanges} {(_sync.FailingChanges == 1 ? "change waits" : "changes wait")} for another try. See Problems.")
            : ("Synced", "Success", "SuccessChip", "Everything here matches the server.");
        if (state == _filesState) return;
        _filesState = state;
        var badge = Badge(state, tone, chip);
        badge.ToolTip = tip;
        _filesBadge.Content = badge;
    }

    /// <summary>Runs Sync now, showing it for at least a second so the press is seen to do something.</summary>
    private async Task SyncNowAsync()
    {
        if (_syncingNow) return;
        _syncingNow = true;
        if (_page.Tag as string == "home") ShowHome();
        try
        {
            await Task.WhenAll(_sync.SyncNowAsync(), Task.Delay(TimeSpan.FromSeconds(1)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Sync now failed: {ex.Message}");
        }
        finally
        {
            _syncingNow = false;
            if (_page.Tag as string == "home") ShowHome();
        }
    }

    private static UIElement Stat(string icon, string tone, string label, TextBlock value, TextBlock? detail = null)
    {
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = Ui.Stack(Ui.Text(label.ToUpperInvariant(), "CardLabel"), Gap(value, 2));
        if (detail is not null) text.Children.Add(detail);
        text.Margin = new Thickness(12, 0, 8, 0);
        text.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chip = Ui.IconChip(icon, tone, $"{tone}Chip", 36, 18);
        chip.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(chip);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>The update banner on Home, or the update card's body in Settings.</summary>
    private void FillUpdate()
    {
        if (_updateBox is null) return;
        var onHome = _page.Tag as string == "home";
        var update = _updater.Ready;
        if (onHome && update is null && _updater.Status is null)
        {
            _updateBox.Content = null;
            return;
        }

        var install = Ui.Button("Restart and update", "download", "PrimaryButton", (_, _) => _installUpdate());
        install.IsEnabled = !_updater.IsBusy;
        install.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        var check = Ui.Button("Check for updates", "refresh-cw", "SecondaryButton", (_, _) => _ = _updater.CheckAsync());
        check.IsEnabled = !_updater.IsBusy;

        var summary = update is not null
            ? $"Version {update.Version} is downloaded and ready. It installs by itself the next time the app starts, or restart now."
            : _updater.LastChecked is { } at ? $"You have the latest version, {Updater.Current}. Checked at {When(at)}." : $"You have version {Updater.Current}.";
        var lines = Ui.Stack(Ui.Text(summary, "Body"));
        if (_updater.Status is { } status) lines.Children.Add(Gap(Ui.Sentence(status, status.StartsWith("Could") || status.Contains("not") ? "Warning" : "TextMuted"), 6));
        if (!Updater.Supported) lines.Children.Add(Gap(Ui.Sentence("This is a development build: updates install in the installed app only.", "TextDim"), 6));

        if (onHome)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.IconChip("download", "Accent", "AccentChip", 40, 20));
            lines.Margin = new Thickness(14, 0, 14, 0);
            lines.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lines, 1);
            row.Children.Add(lines);
            install.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(install, 2);
            row.Children.Add(install);
            var banner = Ui.Card(row, padding: 18);
            banner.Margin = new Thickness(0, 26, 0, 0);
            _updateBox.Content = banner;
        }
        else
        {
            var apply = Ui.Button("Apply update", "download", "PrimaryButton", (_, _) => _installUpdate());
            apply.IsEnabled = update is not null && !_updater.IsBusy;
            apply.Margin = new Thickness(10, 0, 0, 0);
            var rows = Ui.Stack(
                SettingRow("Installed", Ui.Text($"{Brand.Name} {Updater.Current}", "Body")),
                SettingRow("Latest", Ui.Text(LatestText(), "Body")),
                SettingRow("Connection", ServerStatus()));
            // Something the rows do not say: a release that is not signed, say.
            if (_updater.Status is { } problem && _updater.Reachable == true && !_updater.IsBusy)
                rows.Children.Add(Gap(Ui.Sentence(problem, "Warning"), 14));
            rows.Children.Add(Gap(Ui.Row(check, apply), 22));
            _updateBox.Content = rows;
        }
    }

    private string LatestText()
    {
        if (_updater.Ready is { } ready) return $"{ready.Version}  (downloaded, installs on next start)";
        // The result shows once the whole check is over, not halfway through it.
        if (_updater.IsBusy) return "Checking…";
        if (_updater.Latest is not { } latest) return "Not checked yet";
        return latest > Updater.Current ? $"{latest}  (update available)" : $"{latest}  (up to date)";
    }

    /// <summary>Webin Cloud Server's logo and whether it answered the last update check.</summary>
    private UIElement ServerStatus()
    {
        var checkedAt = _updater.LastChecked is { } at ? $"  ·  checked {at:HH:mm}" : "";
        var (text, brush) = _updater.IsBusy ? ("Checking…", "Warning")
            : _updater.Reachable switch
            {
                true => ($"Online (Webin Cloud Server){checkedAt}", "Success"),
                false => ($"Webin Cloud Server unreachable{checkedAt}", "Danger"),
                null => ("Not checked yet", "TextMuted"),
            };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/webincloud.png")), Width = 22, Height = 22 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        return Ui.Row(logo, Spaced(Ui.Sentence(text, brush), 10));
    }

    /// <summary>A label on the left, its value on the right (as Durazzno App lays out its settings).</summary>
    private static UIElement SettingRow(string label, UIElement value)
    {
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = Label(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }


    // --- Activity --------------------------------------------------------------------

    private void ShowActivity(string tab)
    {
        ResetLiveParts();
        _activityTab = tab;
        var header = Header("Activity", "activity", "What was synced in this session, and anything that went wrong.");

        var tabs = Ui.Row(
            Tab("Recent", "recent", tab),
            Spaced(Tab($"Problems{(Log.ProblemCount > 0 ? $" ({Log.Problems().Count})" : "")}", "problems", tab), 26),
            Spaced(Tab("Log", "log", tab), 26));
        var tabsLine = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs };
        tabsLine.SetResourceReference(Border.BorderBrushProperty, "Divider");

        _activityList = new StackPanel();
        FrameworkElement body = tab switch
        {
            "log" => LogView(),
            _ => Ui.Card(_activityList),
        };
        SetPage("activity", Ui.Stack(header, Gap(tabsLine, 26), Gap(body, 18)));
        Tick();
    }

    private RadioButton Tab(string text, string key, string current)
    {
        var tab = new RadioButton { GroupName = "activityTabs", IsChecked = key == current, Content = text.ToUpperInvariant() };
        tab.SetResourceReference(FrameworkElement.StyleProperty, "TabItem");
        AutomationProperties.SetName(tab, text);
        tab.Checked += (_, _) => ShowActivity(key);
        return tab;
    }

    private FrameworkElement LogView()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(FrameworkElement.StyleProperty, "Mono");
        var scroller = new ScrollViewer
        {
            Content = text,
            Height = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        void Load()
        {
            var lines = Log.Tail(LogLines);
            text.Text = lines.Count == 0 ? "The log is empty." : string.Join(Environment.NewLine, lines);
            scroller.ScrollToEnd();
        }
        Load();

        var refresh = Ui.Button("Refresh", "refresh-cw", "SecondaryButton", (_, _) => Load());
        var openFile = Ui.Button("Open log file", "file-text", "SecondaryButton", (_, _) =>
        {
            if (File.Exists(AppPaths.LogFile)) Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
        });
        openFile.Margin = new Thickness(10, 0, 0, 0);
        var openFolder = Ui.Button("Open log folder", "folder-open", "SecondaryButton", (_, _) =>
            Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }));
        openFolder.Margin = new Thickness(10, 0, 0, 0);
        return Ui.Card(Ui.Stack(
            Ui.Sentence($"The last {LogLines} lines of {AppPaths.LogFile}.", "TextMuted"),
            Gap(scroller, 14),
            Gap(Ui.Row(refresh, openFile, openFolder), 16)));
    }

    // --- live refresh ----------------------------------------------------------------

    private void Tick()
    {
        FillFilesBadge();
        if (_upSpeed is not null)
        {
            _upSpeed.Text = Speed(Activity.Speed(Direction.Up));
            _downSpeed!.Text = Speed(Activity.Speed(Direction.Down));
            _upTotal!.Text = Size(Activity.BytesUploaded);
            _downTotal!.Text = Size(Activity.BytesDownloaded);
            _upFiles!.Text = Files(Activity.FilesUploaded);
            _downFiles!.Text = Files(Activity.FilesDownloaded);
        }
        if (_transfersBox is not null) FillTransfers(_transfersBox);

        var version = Activity.Version;
        var problems = Log.ProblemCount;
        if (_recentBox is not null && version != _seenActivity)
        {
            FillEntries(_recentBox, Activity.Recent(HomeRecent));
        }
        if (_problemsBox is not null && problems != _seenProblems)
        {
            FillProblems(_problemsBox, Log.Problems().Take(HomeProblems).ToList(), compact: true);
        }
        if (_activityList is not null)
        {
            if (_activityTab == "recent" && version != _seenActivity) FillEntries(_activityList, Activity.Recent());
            if (_activityTab == "problems" && problems != _seenProblems) FillProblems(_activityList, Log.Problems(), compact: false);
        }
        _seenActivity = version;
        _seenProblems = problems;
    }

    private void FillTransfers(StackPanel box)
    {
        var transfers = Activity.Transfers();
        // Nothing moving and nothing was: leave the empty message alone.
        if (transfers.Count == 0 && !_hadTransfers) return;
        _hadTransfers = transfers.Count > 0;
        box.Children.Clear();
        if (transfers.Count == 0)
        {
            box.Children.Add(Ui.Sentence("Nothing is uploading or downloading right now.", "TextDim"));
            return;
        }
        foreach (var transfer in transfers.Take(HomeTransfers))
        {
            var up = transfer.Direction == Direction.Up;
            var fraction = transfer.Total <= 0 ? 0 : Math.Clamp((double)transfer.Done / transfer.Total, 0, 1);
            var row = EntryRow(up ? "arrow-up" : "arrow-down", up ? "Accent" : "Success",
                NameOf(transfer.Path), FolderOf(transfer.Path),
                $"{(int)(fraction * 100)}%", $"{Size(transfer.Done)} of {Size(transfer.Total)}");
            box.Children.Add(Ui.Stack(row, Gap(Bar(fraction, up ? "Accent" : "Success"), 6)));
        }
        if (transfers.Count > HomeTransfers)
            box.Children.Add(Gap(Ui.Sentence($"And {transfers.Count - HomeTransfers} more…", "TextDim"), 10));
    }

    private static void FillEntries(StackPanel box, IReadOnlyList<ActivityEntry> entries)
    {
        box.Children.Clear();
        if (entries.Count == 0)
        {
            box.Children.Add(Gap(Ui.Sentence("Nothing has synced yet in this session. Changes on this computer or on the server show up here.", "TextDim"), 8));
            return;
        }
        foreach (var entry in entries)
        {
            var (icon, tone, label) = Describe(entry.Kind);
            var folder = entry.Detail ?? FolderOf(entry.Path);
            box.Children.Add(EntryRow(icon, tone, NameOf(entry.Path), folder, label,
                entry.Size is { } size ? $"{Size(size)} · {When(entry.At)}" : When(entry.At)));
        }
    }

    private static void FillProblems(StackPanel box, IReadOnlyList<LogLine> problems, bool compact)
    {
        box.Children.Clear();
        if (problems.Count == 0)
        {
            box.Children.Add(Gap(Ui.Row(Ui.Icon("circle-check", 16, "Success"), Spaced(Ui.Sentence("No problems in this session.", "TextMuted"), 10)), 10));
            return;
        }
        foreach (var problem in problems)
        {
            var error = problem.Level == "ERROR";
            var message = Ui.Text(problem.Message, "Body");
            message.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
            message.TextTrimming = TextTrimming.CharacterEllipsis;
            message.ToolTip = problem.Message;
            var text = Ui.Stack(message, Ui.Text($"{(error ? "Error" : "Warning")} · {When(problem.At)}", "CardHint"));
            box.Children.Add(Row(Ui.IconChip(error ? "circle-alert" : "triangle-alert", error ? "Danger" : "Warning", error ? "DangerChip" : "WarningChip", 28, 15), text, null));
        }
    }

    private static (string Icon, string Tone, string Label) Describe(ActivityKind kind) => kind switch
    {
        ActivityKind.Uploaded => ("upload", "Accent", "Uploaded"),
        ActivityKind.Downloaded => ("download", "Success", "Downloaded"),
        ActivityKind.CreatedFolder => ("folder-plus", "Accent", "Folder created"),
        ActivityKind.Deleted => ("trash", "Danger", "Deleted"),
        ActivityKind.Moved => ("arrow-right-left", "Info", "Moved or renamed"),
        ActivityKind.NewFromServer => ("cloud", "Info", "New on the server"),
        ActivityKind.ChangedOnServer => ("refresh-cw", "Info", "Changed on the server"),
        ActivityKind.RemovedOnServer => ("trash", "Warning", "Removed on the server"),
        ActivityKind.FreedUp => ("cloud", "Info", "Freed up space"),
        _ => ("info", "Info", kind.ToString()),
    };

    // --- Settings --------------------------------------------------------------------

    private void ShowSettings()
    {
        ResetLiveParts();
        var header = Header("Settings", "settings", "How the app behaves on this computer.");

        var startup = new CheckBox { Content = "Start when I sign in to Windows", IsChecked = _settings.StartWithWindows };
        startup.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(startup, "Start with Windows");
        startup.Click += (_, _) =>
        {
            _settings.StartWithWindows = startup.IsChecked == true;
            _settings.Save();
            AutoStart.Apply(_settings.StartWithWindows);
        };
        var general = Ui.Card(Ui.Stack(Ui.PanelTitle("General"), Gap(startup, 18)));

        _updateBox = new ContentControl { Margin = new Thickness(0, 14, 0, 0) };
        var updates = Ui.Card(Ui.Stack(Ui.PanelTitle("Updates"), _updateBox));

        var signOut = Ui.Button("Sign out", "log-out", "DangerButton", (_, _) =>
        {
            if (AppDialog.Confirm(this, "Sign out?",
                    $"This computer is disconnected from {_api.ServerUrl}. Your files stay on the server; sign in again to use them here.",
                    "Sign out", "log-out", "Danger", destructive: true))
                _signOut();
        });
        signOut.HorizontalAlignment = HorizontalAlignment.Left;
        var openWeb = Ui.Button("Open website", "external-link", "SecondaryButton", (_, _) => OpenWebsite(_api.ServerUrl));
        openWeb.Margin = new Thickness(10, 0, 0, 0);
        var account = Ui.Card(Ui.Stack(
            Ui.PanelTitle("Account"),
            Gap(Ui.Text($"Signed in as {_settings.Username} on {_api.ServerUrl}, as \"{_settings.DeviceName}\".", "Body"), 14),
            Gap(Ui.Row(signOut, openWeb), 18)));

        var openLogs = Ui.Link("Open the log folder", (_, _) => Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }));
        var about = Ui.Card(Ui.Stack(
            Ui.PanelTitle("About"),
            Gap(Ui.Text($"{Brand.Id}Client For Windows | Version: {Updater.Current}", "Body"), 14),
            Gap(openLogs, 10)));

        SetPage("settings", Ui.Stack(header, Gap(general, 26), Gap(updates, 20), Gap(account, 20), Gap(about, 20)));
        FillUpdate();
        // The first scheduled check waits a couple of minutes after start; opening Settings does not wait.
        if (_updater.LastChecked is null) _ = _updater.CheckAsync();
    }

    public static void OpenWebsite(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // --- helpers ---------------------------------------------------------------------

    private static UIElement Header(string title, string icon, string subtitle)
    {
        var text = Ui.Stack(Ui.Text(title, "PageTitle"), Ui.Text(subtitle, "PageSubtitle"));
        text.Margin = new Thickness(18, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row(Ui.IconChip(icon, "Accent", "AccentChip", 64, 34), text);
    }

    private static Grid TwoColumns(UIElement left, UIElement right, double leftWeight, double rightWeight)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftWeight, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWeight, GridUnitType.Star) });
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A card title with a link (or, given a note instead, a dim note) on the right.</summary>
    private static UIElement TitleWithLink(string title, string link, Action onClick, string? note = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.PanelTitle(title));
        FrameworkElement right = note is not null ? Ui.Text(note, "CardHint") : Ui.Link(link, (_, _) => onClick());
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A list row: tinted icon, a title over a dim line, and two right-aligned lines.</summary>
    private static UIElement EntryRow(string icon, string tone, string title, string subtitle, string right, string rightSub)
    {
        var titleText = Ui.Text(title, "Body");
        titleText.FontWeight = FontWeights.SemiBold;
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleText.ToolTip = title;
        var subtitleText = Ui.Text(subtitle, "CardHint");
        subtitleText.TextTrimming = TextTrimming.CharacterEllipsis;
        var rightTop = Ui.Text(right, "CardHint");
        rightTop.HorizontalAlignment = HorizontalAlignment.Right;
        rightTop.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var rightBottom = Ui.Text(rightSub, "CardHint");
        rightBottom.HorizontalAlignment = HorizontalAlignment.Right;
        return Row(Ui.IconChip(icon, tone, $"{tone}Chip", 28, 15), Ui.Stack(titleText, subtitleText), Ui.Stack(rightTop, rightBottom));
    }

    private static Grid Row(UIElement icon, FrameworkElement middle, FrameworkElement? right)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ((FrameworkElement)icon).VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        middle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);
        if (right is not null)
        {
            right.Margin = new Thickness(16, 0, 0, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>A thin progress bar: the filled part in the tone colour over the divider colour.</summary>
    private static FrameworkElement Bar(double fraction, string tone)
    {
        var bar = new Grid { Height = 4, Margin = new Thickness(40, 0, 0, 0) };
        bar.SetResourceReference(Panel.BackgroundProperty, "Divider");
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        var fill = new Border();
        fill.SetResourceReference(Border.BackgroundProperty, tone);
        bar.Children.Add(fill);
        return bar;
    }

    private static string NameOf(string path)
    {
        var at = path.LastIndexOf('/');
        return at < 0 ? path : path[(at + 1)..];
    }

    private static string FolderOf(string path)
    {
        var at = path.LastIndexOf('/');
        return at < 0 ? Brand.Name : $"{Brand.Name}/{path[..at]}";
    }

    private static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Since the app started, e.g. "60 MB · 1 file".</summary>
    /// <summary>"300 files", "1 K files", "2.7 K files", "1.2 M files": rounded down, so it never says more than there are.</summary>
    private static string Files(int files)
    {
        static string Short(int n, double unit, string suffix) => $"{Math.Floor(n / unit * 10) / 10:0.#} {suffix}";
        var count = files switch
        {
            >= 1_000_000 => Short(files, 1_000_000, "M"),
            >= 1_000 => Short(files, 1_000, "K"),
            _ => files.ToString(),
        };
        return $"{count} {(files == 1 ? "file" : "files")}";
    }

    private static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "—" : $"{Size((long)bytesPerSecond)}/s";

    private static string When(DateTime at) => at.Date == DateTime.Today ? at.ToString("HH:mm:ss") : at.ToString("MMM d, HH:mm");

    private static TextBlock Label(string text) => Ui.Text(text.ToUpperInvariant(), "CardLabel");

    /// <summary>Status badge: tinted background, bold uppercase text in the tone colour.</summary>
    private static Border Badge(string text, string toneKey, string chipKey)
    {
        var label = new TextBlock { Text = text.ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, toneKey);
        var badge = new Border { Padding = new Thickness(8, 3, 8, 3), Child = label, VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, chipKey);
        return badge;
    }

    private static FrameworkElement Spaced(FrameworkElement element, double left)
    {
        element.Margin = new Thickness(left, 0, 0, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private static FrameworkElement Gap(FrameworkElement element, double top)
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }
}
