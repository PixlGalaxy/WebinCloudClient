using System.Windows;
using System.Windows.Media;

namespace CloudClient.UI;

/// <summary>
/// Puts the brand's palette and the fonts into the application resources,
/// then loads Theme.xaml, whose styles refer to them by key. Code reads them
/// with Ui.B("Key"); never hard-code a colour that exists here.
/// </summary>
public static class Theme
{
    public static readonly Color Success = Hex("#22C55E");
    public static readonly Color Warning = Hex("#F97316");
    public static readonly Color Danger = Hex("#EF4444");
    public static readonly Color Info = Hex("#38BDF8");

    public static void Apply(Application app)
    {
        var p = Brand.Colors;
        var resources = app.Resources;

        void Brush(string key, string hex) => resources[key] = Frozen(Hex(hex));
        Brush("WindowBg", p.WindowBg);
        Brush("SidebarBg", p.SidebarBg);
        Brush("PanelBg", p.PanelBg);
        Brush("PanelBorder", p.PanelBorder);
        Brush("Divider", p.Divider);
        Brush("HoverBg", p.HoverBg);
        Brush("SelectedBg", p.SelectedBg);
        Brush("Text", p.Text);
        Brush("TextMuted", p.TextMuted);
        Brush("TextDim", p.TextDim);
        Brush("Accent", p.Accent);
        Brush("AccentText", p.AccentText);
        Brush("AccentAlt", p.AccentAlt);
        Brush("Highlight", p.Highlight);
        resources["Success"] = Frozen(Success);
        resources["Warning"] = Frozen(Warning);
        resources["Danger"] = Frozen(Danger);
        resources["Info"] = Frozen(Info);
        resources["AccentChip"] = Frozen(Hex(p.Accent), 0x26);
        resources["SuccessChip"] = Frozen(Success, 0x26);
        resources["WarningChip"] = Frozen(Warning, 0x26);
        resources["DangerChip"] = Frozen(Danger, 0x26);
        resources["InfoChip"] = Frozen(Info, 0x26);

        resources["HeadingFont"] = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Rajdhani");
        resources["BodyFont"] = new FontFamily("Segoe UI");
        resources["MonoFont"] = new FontFamily("Cascadia Mono, Consolas");

        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UI/Theme.xaml", UriKind.Relative) });
    }

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush Frozen(Color color, byte? alpha = null)
    {
        var brush = new SolidColorBrush(alpha is null ? color : Color.FromArgb(alpha.Value, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
