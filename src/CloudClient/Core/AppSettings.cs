using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudClient.Core;

/// <summary>Per-user folders, all named after the brand so two brands never share state.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.Id);

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "client.log");

    /// <summary>Where files appear in File Explorer, like %USERPROFILE%\OneDrive.</summary>
    public static string DefaultSyncFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Brand.Name);
}

/// <summary>
/// What the app remembers between runs. Tokens are not here: they live in the
/// Windows Credential Manager (see TokenStore).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Root of the server, e.g. https://cloud.example.com (no /backend/api).</summary>
    public string? ServerUrl { get; set; }
    public string? Username { get; set; }
    public string? DeviceId { get; set; }
    public string DeviceName { get; set; } = Environment.MachineName;
    public string SyncFolder { get; set; } = AppPaths.DefaultSyncFolder;
    public bool StartWithWindows { get; set; } = true;
    /// <summary>The cloud drive registered with Windows for this account, so it can be removed on sign-out.</summary>
    public string? SyncRootId { get; set; }

    [JsonIgnore]
    public bool IsSignedIn => ServerUrl is not null && DeviceId is not null;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings, starting fresh", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        // Write beside and swap, so a crash mid-write never leaves half a file.
        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, AppPaths.SettingsFile, overwrite: true);
    }
}
