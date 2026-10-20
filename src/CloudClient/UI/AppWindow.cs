using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace CloudClient.UI;

/// <summary>
/// Base for every window: brand icon, theme background and our own dark title
/// bar (AppWindowTemplate in Theme.xaml), the same on Windows 10 and 11.
/// </summary>
public class AppWindow : Window
{
    public AppWindow()
    {
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(FontFamilyProperty, "BodyFont");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Brand/app.ico"));
        UseLayoutRounding = true;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 34,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        SetResourceReference(TemplateProperty, "AppWindowTemplate");

        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => Close()));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => WindowState = WindowState.Minimized));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        // Keeps the taskbar thumbnail and Alt+Tab preview dark too.
        SourceInitialized += (_, _) => DarkTitleBar(this);
    }

    /// <summary>Asks Windows for the dark caption; 20 on Windows 10 20H1+ and 11, 19 before that.</summary>
    public static void DarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var on = 1;
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
