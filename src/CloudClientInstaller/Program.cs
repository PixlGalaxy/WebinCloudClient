using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace CloudClient.Installer;

/// <summary>
/// The brand's installer: gets the signed app from the publisher's server and
/// installs it for this user. A copy of it in the install folder is the
/// uninstaller (Settings → Apps, Start menu): opened with the app installed,
/// it offers Repair and Uninstall.
/// --headless installs (or repairs) with no window; --headless --uninstall
/// uninstalls. Both exit with a code (see Headless).
/// --open starts the installed app and exits; with nothing installed it shows
/// the install window instead.
/// </summary>
internal static class Program
{
    private const string FromTempFlag = "--from-temp";
    private const string OpenFlag = "--open";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains(Headless.Flag))
            return Headless.Run(uninstall: args.Contains(Headless.UninstallFlag));

        // For shortcuts and scripts that only have the installer at hand.
        if (args.Contains(OpenFlag) && Setup.IsInstalled)
        {
            Setup.Launch();
            return 0;
        }

        // The uninstaller lives in the folder it deletes: run a copy from %TEMP% instead, which removes itself at exit.
        var self = Process.GetCurrentProcess().MainModule!.FileName;
        var fromTemp = args.Contains(FromTempFlag);
        if (!fromTemp && string.Equals(self, Setup.InstallerCopyPath, StringComparison.OrdinalIgnoreCase))
        {
            var copy = Path.Combine(Path.GetTempPath(), $"{Brand.Id}Installer-{Guid.NewGuid():N}.exe");
            File.Copy(self, copy);
            var forwarded = string.Join(" ", args.Select(a => $"\"{a}\"").Concat([FromTempFlag]));
            Process.Start(new ProcessStartInfo(copy, forwarded) { UseShellExecute = false })?.Dispose();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        ApplyTheme(app);
        var code = app.Run(new InstallerWindow());

        if (fromTemp)
            DeleteLater(self);
        return code;
    }

    /// <summary>The brand's palette and the fonts, then the styles that use them (as Theme.Apply does in the app).</summary>
    private static void ApplyTheme(Application app)
    {
        var p = Brand.Colors;
        var resources = app.Resources;
        void Set(string key, string hex, byte? alpha = null)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            if (alpha is not null)
                color.A = alpha.Value;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
        Set("WindowBg", p.WindowBg);
        Set("PanelBg", p.PanelBg);
        Set("PanelBorder", p.PanelBorder);
        Set("Divider", p.Divider);
        Set("HoverBg", p.HoverBg);
        Set("SelectedBg", p.SelectedBg);
        Set("Text", p.Text);
        Set("TextMuted", p.TextMuted);
        Set("TextDim", p.TextDim);
        Set("Accent", p.Accent);
        Set("AccentText", p.AccentText);
        Set("Success", "#22C55E");
        Set("Warning", "#F97316");
        Set("WarningChip", "#F97316", 0x26);
        Set("Danger", "#EF4444");

        resources["HeadingFont"] = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Rajdhani");
        resources["BodyFont"] = new FontFamily("Segoe UI");
        resources["MonoFont"] = new FontFamily("Cascadia Mono, Consolas");
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Theme.xaml", UriKind.Relative) });
    }

    /// <summary>Deletes a file a few seconds after this process has exited.</summary>
    public static void DeleteLater(string path) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & del /f /q \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
}
