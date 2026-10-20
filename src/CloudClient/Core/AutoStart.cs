using Microsoft.Win32;

namespace CloudClient.Core;

/// <summary>Starts the app (in the tray, without a window) when the user signs in to Windows.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string BackgroundArgument = "--background";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(Brand.Id, $"\"{Environment.ProcessPath}\" {BackgroundArgument}");
            else key.DeleteValue(Brand.Id, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the start-with-Windows setting", ex);
        }
    }
}
