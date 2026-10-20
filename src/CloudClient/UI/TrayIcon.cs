using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CloudClient.UI.Controls;
using Forms = System.Windows.Forms;

namespace CloudClient.UI;

public sealed record TrayItem(string Text, string Icon, Action OnClick);

/// <summary>
/// The notification-area icon. Left click opens the app; right click shows a
/// dark menu drawn in WPF (the stock context menu is light and clashes).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Func<IReadOnlyList<TrayItem>> _items;

    public TrayIcon(Action open, Func<IReadOnlyList<TrayItem>> items)
    {
        _items = items;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Brand/app.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = Brand.Name,
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_announced is not null) UpdateClicked?.Invoke();
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
    }

    public void SetStatus(string status) => _icon.Text = Truncate($"{Brand.Name} - {status}", 63);

    private Version? _announced;

    /// <summary>Clicking the update notification: restart into the new version.</summary>
    public event Action? UpdateClicked;

    /// <summary>Mentions a downloaded version once, with a notification; the menu then offers to install it.</summary>
    public void SetUpdate(Version? version)
    {
        if (version is null || version == _announced) return;
        _announced = version;
        _icon.ShowBalloonTip(10000, $"{Brand.Name} {version} is ready",
            "Click to restart and update now. Otherwise it updates the next time the app starts.", Forms.ToolTipIcon.Info);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private void ShowMenu()
    {
        var list = new StackPanel { Margin = new Thickness(4) };
        var menu = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
        };
        var card = Ui.Card(list, padding: 6);
        card.Chamfer = 10;
        menu.Content = card;

        foreach (var item in _items())
        {
            var row = new Button { Content = Ui.Row(Ui.Icon(item.Icon, 15), Spaced(Ui.Text(item.Text, "Body"))), Padding = new Thickness(12, 8, 24, 8) };
            row.Template = MenuRowTemplate();
            row.SetResourceReference(Control.ForegroundProperty, "Text");
            AutomationProperties.SetName(row, item.Text);
            row.Click += (_, _) =>
            {
                menu.Close();
                item.OnClick();
            };
            list.Children.Add(row);
        }

        menu.Deactivated += (_, _) => menu.Close();
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) menu.Close();
        };

        // Open just above the cursor, kept on screen.
        menu.Loaded += (_, _) =>
        {
            var source = PresentationSource.FromVisual(menu);
            var scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
            var cursor = Forms.Cursor.Position;
            var area = Forms.Screen.FromPoint(cursor).WorkingArea;
            var x = Math.Min(cursor.X * scale, area.Right * scale - menu.ActualWidth);
            var y = Math.Min(cursor.Y * scale, area.Bottom * scale) - menu.ActualHeight;
            menu.Left = Math.Max(area.Left * scale, x);
            menu.Top = Math.Max(area.Top * scale, y);
        };
        menu.Show();
        menu.Activate();
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(10, 0, 0, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private static ControlTemplate MenuRowTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bg");
        border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBg"), "Bg"));
        template.Triggers.Add(hover);
        return template;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
