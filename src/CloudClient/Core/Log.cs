using System.IO;

namespace CloudClient.Core;

/// <summary>
/// A plain text log in the app's data folder, for diagnosing problems. Never
/// write tokens or passwords to it.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    /// <summary>Above this the log starts over, keeping the previous one as client.log.1.</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(file.FullName, file.FullName + ".1", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
