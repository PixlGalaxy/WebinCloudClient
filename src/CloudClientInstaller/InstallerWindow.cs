using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CloudClient.UI.Controls;
using Path = System.Windows.Shapes.Path;

namespace CloudClient.Installer;

/// <summary>Lucide icon outlines (24×24, stroke 2), the same set the app draws.</summary>
internal static class Icons
{
    private const string Circle = "M2,12 A10,10 0 1 1 22,12 A10,10 0 1 1 2,12";
    public const string Check = Circle + " M16,9 L10.5,14.5 L8,12";
    public const string Alert = Circle + " M12,8 L12,12 M12,16 L12.01,16";
    public const string Pending = Circle;
    public const string Loader = "M21,12 A9,9 0 1 1 14.781,3.44";
    public const string Close = "M18,6 L6,18 M6,6 L18,18";
    public const string Minimize = "M5,12 L19,12";
}

/// <summary>
/// The installer's one window, in the app's style (dark panels with a cut
/// corner, Rajdhani headings, the brand's accent). Not installed: Welcome →
/// Checking the release → Installing → Done. Installed: Repair or Uninstall.
/// </summary>
internal sealed class InstallerWindow : Window
{
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private CancellationTokenSource? _work;
    private bool _busy; // files are being replaced or removed: closing now could leave half an install

    public InstallerWindow()
    {
        Title = $"{Brand.Name} Installer";
        Width = 680;
        Height = 580;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B("WindowBg");
        Foreground = B("Text");
        FontFamily = (FontFamily)FindResource("BodyFont");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Brand/app.ico"));
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 48,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(TitleBar());
        _body.Margin = new Thickness(40, 26, 40, 0);
        Grid.SetRow(_body, 1);
        grid.Children.Add(_body);
        _footer.Margin = new Thickness(40, 0, 40, 30);
        Grid.SetRow(_footer, 2);
        grid.Children.Add(_footer);
        Content = new Border { BorderBrush = B("PanelBorder"), BorderThickness = new Thickness(1), Child = grid };

        Closing += (_, e) =>
        {
            if (_busy)
                e.Cancel = true; // a few seconds: the old copy is going and the new one is not in place yet
            else
                _work?.Cancel();
        };
        ShowStart();
    }

    /// <summary>Nothing installed: the install flow. The app in its folder: Repair or Uninstall.</summary>
    private void ShowStart()
    {
        if (Setup.IsInstalled)
            ShowInstalled();
        else
            ShowWelcome();
    }

    // ── Pages ───────────────────────────────────────────────────────────────────────────

    private void ShowWelcome()
    {
        var location = Card(Stack(
            Text("INSTALL LOCATION", "CardLabel"),
            Mono(Setup.InstallDir, 6),
            Spaced(Text($"Desktop and Start menu shortcuts are added. After you sign in, your files appear in File Explorer under {Brand.Name}.", "Body"), 10)));
        Show(Stack(Logo(),
                Spaced(Text($"{Brand.Name} Installer", "PageTitle"), 16),
                Spaced(Text($"Gets {Brand.Name} for Windows from the publisher's server, makes sure it's the signed release and installs it.", "Body"), 6),
                Spaced(location, 22)),
            Button("CANCEL", "SecondaryButton", Close),
            Button("NEXT", "PrimaryButton", () => _ = CheckAsync()));
    }

    private void ShowInstalled()
    {
        var version = Setup.InstalledVersion is { } v ? $"Version {v}, installed in" : "Installed in";
        Show(Stack(Logo(),
                Spaced(Text($"{Brand.Name} is installed", "PageTitle"), 16),
                Spaced(Text($"Repair downloads {Brand.Name} again and puts it back in place, keeping your sign-in and settings. "
                    + "Uninstall removes it from this PC.", "Body"), 6),
                Spaced(Card(Stack(Text(version.ToUpperInvariant(), "CardLabel"), Mono(Setup.InstallDir, 6))), 22)),
            Button("CANCEL", "SecondaryButton", Close),
            Button("UNINSTALL", "SecondaryButton", ShowConfirmUninstall),
            Button("REPAIR", "PrimaryButton", () => _ = CheckAsync()));
    }

    private void ShowConfirmUninstall()
    {
        var removed = Card(Stack(
            Text("THIS REMOVES", "CardLabel"),
            Spaced(Text($"•  {Brand.Name} and its shortcuts", "Body"), 8),
            Text($"•  Its folder in File Explorer, and this PC from your account's devices", "Body"),
            Text("•  Its settings and log", "Body"),
            Spaced(Text("Files you downloaded stay in their folder, and everything stays on the server.", "CardHint"), 10)));
        Show(Stack(Glyph(Icons.Alert, B("Warning"), 58),
                Spaced(Text($"Uninstall {Brand.Name}?", "PageTitle"), 16),
                Spaced(removed, 22)),
            Button("CANCEL", "SecondaryButton", ShowInstalled),
            Button("UNINSTALL", "DangerButton", () => _ = UninstallAsync()));
    }

    private async Task UninstallAsync()
    {
        var close = new StatusRow($"Close {Brand.Name}", "Waiting…");
        var account = new StatusRow("Sign out", "Waiting…");
        var explorer = new StatusRow("File Explorer", "Waiting…");
        var links = new StatusRow("Shortcuts", "Waiting…");
        var files = new StatusRow("Files", "Waiting…");
        Show(Stack(Text($"Uninstalling {Brand.Name}", "PageTitle"),
                Spaced(Card(Stack(close, Spaced(account, 12), Spaced(explorer, 12), Spaced(links, 12), Spaced(files, 12))), 22)));

        StatusRow current = close;
        try
        {
            _busy = true;
            close.Busy("Closing the app…");
            await Task.Run(Setup.CloseApp);
            close.Done("Closed");

            current = account;
            account.Busy("Disconnecting this PC from your account…");
            await Task.Run(Setup.SignOutAsync);
            account.Done("Signed out");

            current = explorer;
            explorer.Busy("Removing the folder from File Explorer…");
            await Task.Run(Setup.RemoveFromExplorer);
            explorer.Done("Removed, with its thumbnails and start with Windows");

            current = links;
            links.Busy("Removing…");
            await Task.Run(Setup.RemoveShortcuts);
            links.Done("Desktop, Start menu and Settings → Apps");

            current = files;
            files.Busy("Deleting the app, its settings and log…");
            await Task.Run(Setup.DeleteFiles);
            files.Done("Deleted");
            _busy = false;

            await Task.Delay(400);
            Show(Stack(Glyph(Icons.Check, B("Success"), 58),
                    Spaced(Text($"{Brand.Name} was uninstalled", "PageTitle"), 16),
                    Spaced(Text("Everything it installed is gone. Your files are still on the server, and the ones you downloaded are still in their folder.", "Body"), 6)),
                Button("CLOSE", "PrimaryButton", Close));
        }
        catch (Exception ex)
        {
            _busy = false;
            current.Failed(ex is InstallException ? ex.Message : $"Unexpected error: {ex.Message}");
            SetFooter(Button("CLOSE", "SecondaryButton", Close), Button("TRY AGAIN", "PrimaryButton", () => _ = UninstallAsync()));
        }
    }

    private async Task CheckAsync()
    {
        var server = new StatusRow("Publisher's server", "Connecting…");
        var signed = new StatusRow("Signed release", "Waiting…");
        var file = new StatusRow($"{Brand.Name} download", "Waiting…");
        var rows = Card(Stack(server, Spaced(signed, 16), Spaced(file, 16)));
        var note = new ContentControl { Focusable = false };
        var cancel = Button("CANCEL", "SecondaryButton", Close);
        Show(Stack(Logo(),
                Spaced(Text("Checking the release", "PageTitle"), 14),
                Spaced(Text($"Making sure the server is online and {Brand.Name} is there and signed before anything is installed.", "Body"), 6),
                Spaced(rows, 22), note),
            cancel);

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        StatusRow current = server;
        try
        {
            server.Busy();
            signed.Busy("Reading the version and its signature…");
            var release = await Setup.FindReleaseAsync(ct);
            server.Done("Online");
            current = signed;
            signed.Done($"{Brand.Name} {release.Version}, signed by the publisher");

            current = file;
            file.Busy($"Looking for {Brand.Id}.exe…");
            var size = await Setup.CheckExeAsync(ct);
            file.Done($"{Brand.Id}.exe is available" + (size is { } bytes ? $"  ·  {Megabytes(bytes)}" : ""));

            var running = Setup.AppIsRunning();
            if (running)
                note.Content = Spaced(Chip($"{Brand.Name} is running. It's closed while the new copy goes in, and you can open it again at the end.", "Warning"), 14);
            SetFooter(cancel, Button(running ? "CLOSE AND INSTALL" : "INSTALL", "PrimaryButton", () => _ = InstallAsync(release)));
        }
        catch (OperationCanceledException)
        {
            // the window is closing
        }
        catch (Exception ex)
        {
            // The release is read in one go: a server that answered but published no valid release fails on the second row.
            if (current == server && ex is InstallException { Message: var m } && !m.Contains("reached") && !m.Contains("in time"))
            {
                server.Done("Online");
                current = signed;
            }
            current.Failed(ex is InstallException ? ex.Message : $"Unexpected error: {ex.Message}");
            SetFooter(cancel, Button("RETRY", "PrimaryButton", () => _ = CheckAsync()));
        }
    }

    private async Task InstallAsync(Release release)
    {
        var bar = new ProgressLine();
        var download = new StatusRow("Download", "Waiting…");
        var verify = new StatusRow("SHA-256 check", "Waiting…");
        var copy = new StatusRow("Install", "Waiting…");
        var shortcuts = new StatusRow("Shortcuts", "Waiting…");
        var cancel = Button("CANCEL", "SecondaryButton", () => _work?.Cancel());
        Show(Stack(Text($"Installing {Brand.Name}", "PageTitle"),
                Spaced(bar, 22),
                Spaced(Card(Stack(download, Spaced(verify, 12), Spaced(copy, 12), Spaced(shortcuts, 12))), 18)),
            cancel);

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        StatusRow current = download;
        try
        {
            download.Busy("Starting…");
            bar.Set(0, "Downloading…");
            var file = await Setup.DownloadAsync(release, new Progress<(long Got, long Total)>(p =>
            {
                var fraction = p.Total > 0 ? (double)p.Got / p.Total : 0;
                var text = p.Total > 0 ? $"{Megabytes(p.Got)} of {Megabytes(p.Total)}" : Megabytes(p.Got);
                download.Busy(text);
                bar.Set(fraction * 0.9, $"Downloading…  {fraction * 100:0}%  ·  {text}");
            }), ct);
            download.Done(Megabytes(new FileInfo(file).Length));

            // DownloadAsync already compared the SHA-256 with the signed release while the bytes came in.
            current = verify;
            verify.Done("Matches the signed release");
            cancel.IsEnabled = false;

            current = copy;
            copy.Busy("Copying to the install folder…");
            bar.Set(0.93, "Installing…");
            _busy = true;
            await Task.Run(() => Setup.CopyIntoPlace(file));
            copy.Done(Setup.ExePath);

            current = shortcuts;
            shortcuts.Busy("Desktop and Start menu…");
            bar.Set(0.97, "Creating shortcuts…");
            await Task.Run(() => Setup.Register(release));
            _busy = false;
            shortcuts.Done("Desktop, Start menu and Settings → Apps");
            bar.Set(1, "Done");

            await Task.Delay(400);
            ShowDone();
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
                ShowStart(); // cancelled while downloading: nothing was changed
        }
        catch (Exception ex)
        {
            _busy = false;
            current.Failed(ex.Message);
            ShowProblem(ex is InstallException ? ex.Message : $"Unexpected error: {ex.Message}");
        }
    }

    private void ShowDone()
    {
        Show(Stack(Glyph(Icons.Check, B("Success"), 58),
                Spaced(Text($"{Brand.Name} is installed", "PageTitle"), 16),
                Spaced(Text($"Open it to sign in to your server; your files then appear in File Explorer. It lives in the tray next to the clock.", "Body"), 6),
                Spaced(Card(Stack(Text("INSTALLED IN", "CardLabel"), Mono(Setup.InstallDir, 6))), 22)),
            Button("FINISH", "SecondaryButton", Close),
            Button($"OPEN {Brand.Name.ToUpperInvariant()}", "PrimaryButton", LaunchAndClose));
    }

    private void ShowProblem(string message)
    {
        Show(Stack(Glyph(Icons.Alert, B("Danger"), 58),
                Spaced(Text("The installation didn't finish", "PageTitle"), 16),
                Spaced(Text(message, "Body"), 6),
                Spaced(Text("Nothing was changed if the download failed. Try again, or close the installer and try later.", "Body"), 10)),
            Button("CLOSE", "SecondaryButton", Close),
            Button("TRY AGAIN", "PrimaryButton", () => _ = CheckAsync()));
    }

    private void LaunchAndClose()
    {
        try
        {
            Setup.Launch();
        }
        catch (Exception ex)
        {
            ShowProblem($"{Brand.Name} is installed but did not start: {ex.Message}");
            return;
        }
        Close();
    }

    // ── Building blocks ─────────────────────────────────────────────────────────────────

    private void Show(UIElement body, params Button[] buttons)
    {
        _body.Content = body;
        SetFooter(buttons);
    }

    private void SetFooter(params Button[] buttons)
    {
        _footer.Children.Clear();
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0);
            _footer.Children.Add(buttons[i]);
        }
    }

    private UIElement TitleBar()
    {
        var logo = new Image { Source = BrandIcon(), Width = 22, Height = 22 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var brand = new TextBlock { Text = Brand.Name.ToUpperInvariant(), FontFamily = (FontFamily)FindResource("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 17, Foreground = B("Text"), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = "INSTALLER", FontFamily = (FontFamily)FindResource("HeadingFont"), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = B("TextDim"), Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(logo);
        left.Children.Add(brand);
        left.Children.Add(label);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(WindowButton(Icons.Minimize, () => WindowState = WindowState.Minimized));
        right.Children.Add(WindowButton(Icons.Close, Close));

        var bar = new Grid();
        bar.Children.Add(left);
        bar.Children.Add(right);
        return new Border { Background = B("WindowBg"), BorderBrush = B("Divider"), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
    }

    private Button WindowButton(string icon, Action click)
    {
        var button = new Button { Style = (Style)FindResource("WindowButton") };
        button.Content = Glyph(icon, null, 15, 1.6, button);
        button.Click += (_, _) => click();
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private Button Button(string text, string style, Action click)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(style) };
        button.Click += (_, _) => click();
        return button;
    }

    private static Image Logo()
    {
        var logo = new Image { Source = BrandIcon(), Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        return logo;
    }

    private static BitmapImage BrandIcon() => new(new Uri("pack://application:,,,/Brand/icon.png"));

    private FrameworkElement Card(UIElement child) =>
        new ChamferBorder { Background = B("PanelBg"), BorderBrush = B("PanelBorder"), Padding = new Thickness(20, 18, 20, 18), Chamfer = 14, Child = child };

    private FrameworkElement Chip(string text, string tone) =>
        new Border { Background = B(tone + "Chip"), Padding = new Thickness(12, 9, 12, 9), Child = new TextBlock { Text = text, Foreground = B(tone), FontSize = 13, TextWrapping = TextWrapping.Wrap } };

    private TextBlock Text(string text, string style) => new() { Text = text, Style = (Style)FindResource(style) };

    private TextBlock Mono(string text, double top) => new()
    {
        Text = text, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 13, Foreground = B("Text"),
        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, top, 0, 0),
    };

    private static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children)
            stack.Children.Add(child);
        return stack;
    }

    private static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    /// <summary>A Lucide outline. With no brush it follows <paramref name="foregroundOf"/>'s Foreground (window buttons).</summary>
    internal static Viewbox Glyph(string data, Brush? brush, double size, double stroke = 2, Control? foregroundOf = null)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data), StrokeThickness = stroke, Stroke = brush,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        if (brush is null && foregroundOf is not null)
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = foregroundOf });
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, HorizontalAlignment = HorizontalAlignment.Left };
    }

    internal static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    // ── Pieces with state ───────────────────────────────────────────────────────────────

    /// <summary>One line of a checklist: icon (waiting, spinning, done, failed), title and detail.</summary>
    private sealed class StatusRow : Grid
    {
        private readonly ContentControl _icon = new() { Focusable = false, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        private readonly TextBlock _detail;

        public StatusRow(string title, string detail)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Children.Add(_icon);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("RowTitle") });
            _detail = new TextBlock { Text = detail, Style = (Style)Application.Current.FindResource("CardHint"), Margin = new Thickness(0, 2, 0, 0) };
            text.Children.Add(_detail);
            SetColumn(text, 1);
            Children.Add(text);
            _icon.Content = Glyph(Icons.Pending, B("TextDim"), 22);
        }

        public void Busy(string? detail = null)
        {
            if (detail is not null)
                _detail.Text = detail;
            _detail.Foreground = B("TextMuted");
            if (_icon.Content is Viewbox { Tag: "spin" })
                return;
            var spinner = Glyph(Icons.Loader, B("Accent"), 22);
            spinner.Tag = "spin";
            spinner.RenderTransformOrigin = new Point(0.5, 0.5);
            var rotate = new RotateTransform();
            spinner.RenderTransform = rotate;
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            _icon.Content = spinner;
        }

        public void Done(string detail)
        {
            _detail.Text = detail;
            _detail.Foreground = B("TextMuted");
            _icon.Content = Glyph(Icons.Check, B("Success"), 22);
        }

        public void Failed(string detail)
        {
            _detail.Text = detail;
            _detail.Foreground = B("Danger");
            _detail.TextTrimming = TextTrimming.None;
            _detail.TextWrapping = TextWrapping.Wrap;
            _icon.Content = Glyph(Icons.Alert, B("Danger"), 22);
        }
    }

    /// <summary>A flat progress bar in the accent colour with a caption under it.</summary>
    private sealed class ProgressLine : StackPanel
    {
        private readonly ColumnDefinition _done = new() { Width = new GridLength(0, GridUnitType.Star) };
        private readonly ColumnDefinition _left = new() { Width = new GridLength(1, GridUnitType.Star) };
        private readonly TextBlock _caption;

        public ProgressLine()
        {
            var track = new Grid { Height = 8, Background = B("HoverBg") };
            track.ColumnDefinitions.Add(_done);
            track.ColumnDefinitions.Add(_left);
            track.Children.Add(new Border { Background = B("Accent") });
            Children.Add(track);
            _caption = new TextBlock { Style = (Style)Application.Current.FindResource("CardHint"), Margin = new Thickness(0, 8, 0, 0) };
            Children.Add(_caption);
        }

        public void Set(double fraction, string caption)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            _done.Width = new GridLength(fraction, GridUnitType.Star);
            _left.Width = new GridLength(1 - fraction, GridUnitType.Star);
            _caption.Text = caption;
        }
    }
}
