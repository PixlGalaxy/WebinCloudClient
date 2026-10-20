using System.Diagnostics;
using System.IO;
using System.Windows;
using CloudClient.Api;
using CloudClient.Auth;
using CloudClient.Sync;
using CloudClient.Updates;
using Microsoft.Win32;

namespace CloudClient.Core;

/// <summary>
/// The downloaded exe installs itself, per user and without admin rights, in
/// %LOCALAPPDATA%\Programs\&lt;Brand&gt;, with a Start menu shortcut and an
/// entry in Settings → Apps to uninstall it. Every path and registry key is
/// named after the brand, so Webin Cloud and Ryzhell Cloud install side by
/// side without touching each other.
/// </summary>
public static class Installer
{
    public const string UninstallArgument = "--uninstall";
    /// <summary>Runs the exe where it is, without installing (for testing a build).</summary>
    public const string PortableArgument = "--portable";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Brand.Id);

    public static string InstalledExe { get; } = Path.Combine(InstallDir, $"{Brand.Id}.exe");

    private static string Shortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{Brand.Name}.lnk");

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Brand.Id;

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>A release build started from anywhere but its install folder (a fresh download) installs itself.</summary>
    public static bool ShouldInstall(string[] args)
    {
#if DEBUG
        return false;
#else
        return !IsRunningInstalled && !args.Contains(PortableArgument) && !args.Contains(UninstallArgument);
#endif
    }

    /// <summary>Copies this exe into place (replacing a running copy), registers it and starts it.</summary>
    public static void Install()
    {
        try
        {
            StopRunningCopy();
            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
            WriteShortcut();
            WriteUninstallEntry();
            Log.Info($"Installed version {Updater.Current} in {InstallDir}");
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Could not install", ex);
            MessageBox.Show($"{Brand.Name} could not be installed: {ex.Message}", Brand.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Keeps the Settings → Apps entry and shortcut current, for instance after an update.</summary>
    public static void Refresh()
    {
        if (!IsRunningInstalled) return;
        try
        {
            WriteUninstallEntry();
            if (!File.Exists(Shortcut)) WriteShortcut();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not refresh the install entry: {ex.Message}");
        }
    }

    /// <summary>
    /// Started from Settings → Apps: signs this computer out, removes the cloud
    /// drive from File Explorer and everything the app registered, then
    /// deletes the install folder once this process has exited. Files already
    /// downloaded to the sync folder stay.
    /// </summary>
    public static void Uninstall()
    {
        var answer = MessageBox.Show(
            $"Remove {Brand.Name} from this computer? It signs out and removes the folder from File Explorer. Files you downloaded stay in the folder; everything stays on the server.",
            $"Uninstall {Brand.Name}", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        StopRunningCopy();
        var settings = AppSettings.Load();
        if (settings.SyncRootId is not null) SyncRoot.Unregister(settings.SyncRootId);
        if (settings.ServerUrl is not null) SignOut(settings.ServerUrl);

        AutoStart.Apply(false);
        ThumbnailServer.Unregister();
        TryRun(() => File.Delete(Shortcut));
        TryRun(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        TryRun(() => Directory.Delete(AppPaths.DataDir, recursive: true));

        // This exe is still running from the folder: remove it a moment after it exits.
        if (IsRunningInstalled)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        MessageBox.Show($"{Brand.Name} was removed.", Brand.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Disconnects the device on the server too, so it leaves the user's device list.</summary>
    private static void SignOut(string serverUrl)
    {
        try
        {
            var tokens = TokenStore.Load(serverUrl);
            if (tokens is not null)
            {
                using var api = new ApiClient(serverUrl, tokens);
                api.SignOutAsync().Wait(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not sign out on the server while uninstalling: {ex.Message}");
        }
        TryRun(() => TokenStore.Delete(serverUrl));
    }

    /// <summary>Asks the running app (if any) to quit and waits for it, so its exe can be replaced or removed.</summary>
    private static void StopRunningCopy()
    {
        if (EventWaitHandle.TryOpenExisting(App.QuitEventName, out var quit))
        {
            using (quit) quit.Set();
        }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        foreach (var process in Process.GetProcessesByName(Brand.Id))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero) process.WaitForExit(left);
            }
        }
    }

    private static void WriteShortcut()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        var link = shell.CreateShortcut(Shortcut);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallDir;
        link.IconLocation = $"{InstalledExe},0";
        link.Description = Brand.Name;
        link.Save();
    }

    private static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", Brand.Name);
        key.SetValue("DisplayVersion", Updater.Current.ToString());
        key.SetValue("Publisher", Brand.Name);
        key.SetValue("DisplayIcon", $"{InstalledExe},0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" {UninstallArgument}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall step skipped: {ex.Message}");
        }
    }
}
