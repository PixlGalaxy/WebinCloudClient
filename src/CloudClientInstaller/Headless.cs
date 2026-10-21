using System.Diagnostics;

namespace CloudClient.Installer;

/// <summary>
/// --headless: the whole install (or repair) with no window, for scripts. It
/// checks the signed release, downloads and verifies the exe, installs it and
/// adds the shortcuts and the uninstaller, without starting the app.
/// --headless --uninstall removes it as the window's Uninstall does.
/// Progress goes to %TEMP%\&lt;Brand&gt;Installer.log, and to standard output when
/// the caller captures it.
/// </summary>
internal static class Headless
{
    public const string Flag = "--headless";
    public const string UninstallFlag = "--uninstall";

    // Exit codes, for the caller.
    public const int Done = 0;
    public const int UnexpectedError = 1;
    public const int ServerUnavailable = 2;
    public const int NotTrusted = 3;
    public const int Failed = 4;

    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), $"{Brand.Id}Installer.log");

    public static int Run(bool uninstall)
    {
        try
        {
            File.WriteAllText(LogFile, "");
        }
        catch
        {
            // logging is best effort
        }
        try
        {
            return uninstall ? Uninstall() : InstallAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Write($"Unexpected error: {ex}");
            return UnexpectedError;
        }
    }

    private static async Task<int> InstallAsync()
    {
        Write($"{Brand.Name} installer (headless) installing to {Setup.InstallDir}");
        var ct = CancellationToken.None;

        Release release;
        try
        {
            release = await Setup.FindReleaseAsync(ct);
            Write($"Release: {Brand.Name} {release.Version}, signed by the publisher");
            var size = await Setup.CheckExeAsync(ct);
            Write($"{Brand.Id}.exe is available ({size?.ToString() ?? "unknown"} bytes)");
        }
        catch (InstallException ex)
        {
            Write($"Server check failed: {ex.Message}");
            return ex.Message.Contains("signed") ? NotTrusted : ServerUnavailable;
        }

        string download;
        try
        {
            var lastTenth = -1;
            download = await Setup.DownloadAsync(release, new SyncProgress<(long Got, long Total)>(p =>
            {
                var tenth = p.Total > 0 ? (int)(p.Got * 10 / p.Total) : -1;
                if (tenth != lastTenth)
                {
                    lastTenth = tenth;
                    Write($"Downloading: {Math.Max(0, tenth) * 10}%");
                }
            }), ct);
            Write("Download matches the signed release (SHA-256)");
        }
        catch (InstallException ex)
        {
            Write($"Download failed: {ex.Message}");
            // A connection problem can be tried again later; a file that does not match the signed release cannot.
            return ex.Message.Contains("SHA-256") || ex.Message.Contains("incomplete") ? NotTrusted : ServerUnavailable;
        }

        try
        {
            Setup.CopyIntoPlace(download);
            Write($"Installed: {Setup.ExePath}");
            Setup.Register(release);
            Write("Shortcuts and uninstaller added");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallException)
        {
            Write($"Install failed: {ex.Message}");
            return Failed;
        }

        Write("Done");
        return Done;
    }

    private static int Uninstall()
    {
        Write($"{Brand.Name} installer (headless) uninstalling from {Setup.InstallDir}");
        try
        {
            Setup.CloseApp();
            Write($"{Brand.Name} closed");
            Setup.SignOutAsync().GetAwaiter().GetResult();
            Write("Signed out");
            Setup.RemoveFromExplorer();
            Write("Removed from File Explorer, thumbnails and start with Windows");
            Setup.RemoveShortcuts();
            Write("Shortcuts and the Apps entry removed");
            Setup.DeleteFiles();
            Write("Program, settings and log deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallException)
        {
            Write($"Uninstall failed: {ex.Message}");
            return Failed;
        }

        // Started from the install folder: that folder goes once this exe has exited.
        var self = Process.GetCurrentProcess().MainModule!.FileName;
        if (Directory.Exists(Setup.InstallDir) && Path.GetDirectoryName(self)!.Equals(Setup.InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{Setup.InstallDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        Write("Done");
        return Done;
    }

    private static void Write(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        try
        {
            File.AppendAllText(LogFile, stamped + Environment.NewLine);
        }
        catch
        {
            // logging is best effort
        }
        try
        {
            Console.WriteLine(stamped); // reaches the caller when it redirects output
        }
        catch
        {
            // no console
        }
    }

    /// <summary>Progress reported on the calling thread (there is no UI thread to post to).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
