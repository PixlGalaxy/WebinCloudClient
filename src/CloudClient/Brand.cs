namespace CloudClient;

/// <summary>
/// Everything that differs between the products built from this code, chosen
/// at build time with -p:Brand=... (see Directory.Build.props). The rest of the
/// app reads names, colours and identifiers from here, never literals.
/// </summary>
public static class Brand
{
#if BRAND_RYZHELLCLOUD
    /// <summary>Squashed form, for identifiers, folders and credentials.</summary>
    public const string Id = "RyzhellCloud";
    /// <summary>Shown to people.</summary>
    public const string Name = "Ryzhell Cloud";
    /// <summary>COM class of the File Explorer thumbnail provider; one per brand so both can be installed.</summary>
    public const string ThumbnailProviderClsid = "CA19E048-C5D3-405C-B513-A1E2D55CC8DE";

    public static readonly Palette Colors = new(
        WindowBg: "#181D31",
        SidebarBg: "#1D2339",
        PanelBg: "#232A43",
        PanelBorder: "#363F61",
        Divider: "#2B3352",
        HoverBg: "#29314E",
        SelectedBg: "#2F385A",
        Text: "#F1F3FA",
        TextMuted: "#AAB1CE",
        TextDim: "#7A82A2",
        Accent: "#00D4FF",
        AccentText: "#071018",
        AccentAlt: "#F0623A",
        Highlight: "#6B7493");
#else
    /// <summary>Squashed form, for identifiers, folders and credentials.</summary>
    public const string Id = "WebinCloud";
    /// <summary>Shown to people.</summary>
    public const string Name = "Webin Cloud";
    /// <summary>COM class of the File Explorer thumbnail provider; one per brand so both can be installed.</summary>
    public const string ThumbnailProviderClsid = "8ADA4C5C-EF07-41AC-93B1-B85BC2D75ACD";

    public static readonly Palette Colors = new(
        WindowBg: "#0B0B0D",
        SidebarBg: "#111113",
        PanelBg: "#151518",
        PanelBorder: "#26262C",
        Divider: "#1F1F24",
        HoverBg: "#1A1A1F",
        SelectedBg: "#1E1E24",
        Text: "#F2F2F3",
        TextMuted: "#9A9AA3",
        TextDim: "#62626B",
        Accent: "#FFA200",
        AccentText: "#0B0B0D",
        AccentAlt: "#FF5F00",
        Highlight: "#00E02A");
#endif

    /// <summary>Platform name the server shows in Settings → Devices.</summary>
    public const string Platform = "windows";
}

/// <summary>
/// The brand's colours. Accent is the main action colour; AccentAlt the second
/// colour of the logo; Highlight an extra tone for small details.
/// </summary>
public sealed record Palette(
    string WindowBg,
    string SidebarBg,
    string PanelBg,
    string PanelBorder,
    string Divider,
    string HoverBg,
    string SelectedBg,
    string Text,
    string TextMuted,
    string TextDim,
    string Accent,
    string AccentText,
    string AccentAlt,
    string Highlight);
