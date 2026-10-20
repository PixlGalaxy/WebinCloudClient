using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CloudClient.Api;
using CloudClient.Core;
using CloudClient.Sync;
using CloudClient.UI;

namespace CloudClient.Views;

/// <summary>The app's window: a sidebar with Home and Settings. Closing it keeps the app in the tray.</summary>
public sealed class MainWindow : AppWindow
{
    private readonly AppSettings _settings;
    private readonly ApiClient _api;
    private readonly Action _signOut;
    private readonly SyncEngine _sync;
    private readonly ContentControl _page = new();
    private UserInfo? _user;
    private string? _connectionError;

    public bool ExitRequested { get; set; }

    public MainWindow(AppSettings settings, ApiClient api, SyncEngine sync, Action signOut)
    {
        _settings = settings;
        _api = api;
        _signOut = signOut;
        _sync = sync;
        // The drive starts in the background; refresh the card once it is up.
        sync.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (_page.Tag as string == "home") ShowHome();
        });
        Title = Brand.Name;
        Width = 980;
        Height = 660;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(Sidebar());
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Padding = new Thickness(40, 32, 40, 36), Child = _page } };
        Grid.SetColumn(scroller, 1);
        root.Children.Add(scroller);
        Content = root;

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

    private UIElement Sidebar()
    {
        var panel = new DockPanel();
        var brand = Ui.Row(Ui.BrandImage("icon.png", 38), Spaced(Ui.Text(Brand.Name, "PanelTitleText"), 12));
        brand.Margin = new Thickness(24, 26, 16, 30);
        DockPanel.SetDock(brand, Dock.Top);
        panel.Children.Add(brand);

        var version = Ui.Text($"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}", "CardHint");
        version.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        version.Margin = new Thickness(30, 0, 0, 20);
        DockPanel.SetDock(version, Dock.Bottom);
        panel.Children.Add(version);

        var nav = new StackPanel();
        nav.Children.Add(NavItem("Home", "house", true, ShowHome));
        nav.Children.Add(NavItem("Settings", "settings", false, ShowSettings));
        panel.Children.Add(nav);

        var sidebar = new Border { Child = panel, BorderThickness = new Thickness(0, 0, 1, 0) };
        sidebar.SetResourceReference(Border.BackgroundProperty, "SidebarBg");
        sidebar.SetResourceReference(Border.BorderBrushProperty, "Divider");
        return sidebar;
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

    // --- pages -----------------------------------------------------------------------

    private void ShowHome()
    {
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
        var (chipIcon, tone, chip, state) = _sync.Problem is not null
            ? ("cloud-off", "Warning", "WarningChip", "Not available")
            : ready ? ("cloud-check", "Success", "SuccessChip", "On") : ("cloud", "Accent", "AccentChip", "Starting…");
        var openFolder = Ui.Button("Open folder", "folder-open", "SecondaryButton", (_, _) =>
            Process.Start(new ProcessStartInfo(_settings.SyncFolder) { UseShellExecute = true }));
        openFolder.HorizontalAlignment = HorizontalAlignment.Left;
        openFolder.IsEnabled = ready;
        var sync = Ui.Card(Ui.Stack(
            Ui.PanelTitle("Files"),
            Gap(Ui.Row(Ui.IconChip(chipIcon, tone, chip, 44, 22),
                Spaced(Ui.Stack(Ui.Text("Files On-Demand", "PanelTitleText"), Ui.Text(state, "CardHint")), 14)), 16),
            Gap(Ui.Text(_sync.Problem ?? $"Your files are in File Explorer under {Brand.Name}, in {_settings.SyncFolder}. They take no space until you open them.", "Body"), 14),
            Gap(openFolder, 16)));

        var grid = new Grid { Margin = new Thickness(0, 26, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(sync, 2);
        grid.Children.Add(account);
        grid.Children.Add(sync);

        _page.Tag = "home";
        _page.Content = Ui.Stack(header, grid);
    }

    private void ShowSettings()
    {
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
            Gap(Ui.Text($"{Brand.Name} for Windows, version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}", "Body"), 14),
            Gap(openLogs, 10)));

        _page.Tag = "settings";
        _page.Content = Ui.Stack(header, Gap(general, 26), Gap(account, 20), Gap(about, 20));
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
