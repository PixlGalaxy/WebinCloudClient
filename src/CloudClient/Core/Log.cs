using System.IO;

namespace CloudClient.Core;

/// <summary>
/// A plain text log in the app's data folder, for diagnosing problems. Never
/// write tokens or passwords to it.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    /// <summary>Above this the log starts over; the previous ones are kept as client.log.1 to .4 (25 MB in all).</summary>
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int KeptFiles = 4;

    private const int MaxProblems = 200;
    private static readonly LinkedList<LogLine> ProblemList = new();
    private static int _problemCount;

    /// <summary>Detail for diagnosing: timings, requests that failed or were slow, each local change. Never shown as a problem.</summary>
    public static void Debug(string message) => Write("DEBUG", message);
    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>The problem list shows the short message; the file gets the whole exception, stack trace and inner ones included.</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}", ex?.ToString());

    /// <summary>Warnings and errors of this session, newest first, for the Activity page.</summary>
    public static IReadOnlyList<LogLine> Problems()
    {
        lock (Gate) return ProblemList.ToList();
    }

    /// <summary>Goes up with every warning or error, so a page redraws only when there is a new one.</summary>
    public static int ProblemCount => Volatile.Read(ref _problemCount);

    /// <summary>The end of the log file, for the Activity page.</summary>
    public static IReadOnlyList<string> Tail(int lines)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(AppPaths.LogFile)) return [];
                using var stream = new FileStream(AppPaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // The log is at most a couple of MB; read the last part only.
                stream.Seek(Math.Max(0, stream.Length - 256 * 1024), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return all.TakeLast(lines).Select(l => l.TrimEnd('\r')).ToList();
            }
        }
        catch
        {
            return [];
        }
    }

    private static void Write(string level, string message, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                if (level is "WARN " or "ERROR")
                {
                    ProblemList.AddFirst(new LogLine(DateTime.Now, level.Trim(), message));
                    while (ProblemList.Count > MaxProblems) ProblemList.RemoveLast();
                    Interlocked.Increment(ref _problemCount);
                }
                Directory.CreateDirectory(AppPaths.DataDir);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    for (var i = KeptFiles - 1; i >= 1; i--)
                        if (File.Exists($"{file.FullName}.{i}")) File.Move($"{file.FullName}.{i}", $"{file.FullName}.{i + 1}", overwrite: true);
                    File.Move(file.FullName, file.FullName + ".1", overwrite: true);
                }
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
                if (detail is not null) line += "    " + detail.Replace(Environment.NewLine, Environment.NewLine + "    ") + Environment.NewLine;
                File.AppendAllText(AppPaths.LogFile, line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

public sealed record LogLine(DateTime At, string Level, string Message);
