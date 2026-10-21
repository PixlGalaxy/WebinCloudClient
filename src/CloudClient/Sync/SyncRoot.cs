using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CloudClient.Core;
using Microsoft.Win32;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace CloudClient.Sync;

/// <summary>
/// Registers the sync folder with Windows as a cloud drive: it then shows in
/// File Explorer's navigation pane with the brand's name and icon (like
/// OneDrive or iCloud Drive), and its files carry the cloud status icons.
/// </summary>
public static class SyncRoot
{
    /// <summary>"{provider}!{Windows user SID}!{account}", the form Windows expects.</summary>
    public static string IdFor(string serverUrl, string username)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "user";
        // The account part must not contain "!"; a short hash keeps it stable and safe.
        var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{serverUrl}|{username}")))[..16];
        return $"{Brand.Id}!{sid}!{account}";
    }

    public static async Task RegisterAsync(string id, string folder, string serverUrl)
    {
        Directory.CreateDirectory(folder);
        var info = new StorageProviderSyncRootInfo
        {
            Id = id,
            Path = await StorageFolder.GetFolderFromPathAsync(folder),
            DisplayNameResource = Brand.Name,
            // The exe carries the brand icon (ApplicationIcon), so Explorer shows the logo.
            IconResource = $"{Environment.ProcessPath},0",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            // Folders are listed from the server the first time they are opened.
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.Default,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            Version = "1",
            AllowPinning = true,
            ProtectionMode = StorageProviderProtectionMode.Personal,
            Context = CryptographicBuffer.ConvertStringToBinary(serverUrl, BinaryStringEncoding.Utf8),
        };
        StorageProviderSyncRootManager.Register(info);
        Log.Info($"Registered the sync folder {folder}");
    }

    /// <summary>
    /// Removes every cloud drive this brand registered for the Windows user
    /// except `keep`: ones left behind when the account or folder changed
    /// without signing out, each showing as another entry in File Explorer.
    /// </summary>
    public static void UnregisterAll(string? keep = null)
    {
        // Read from the registry: GetCurrentSyncRoots leaves out roots like these, registered by an unpackaged app.
        string[] ids;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegisteredRoots);
            ids = key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list the sync folders: {ex.Message}");
            return;
        }
        var mine = $"{Brand.Id}!{WindowsIdentity.GetCurrent().User?.Value ?? "user"}!";
        foreach (var id in ids)
            if (id.StartsWith(mine, StringComparison.OrdinalIgnoreCase) && !string.Equals(id, keep, StringComparison.OrdinalIgnoreCase)) Unregister(id);
    }

    /// <summary>Where Windows keeps every registered sync root, one subkey per id.</summary>
    private const string RegisteredRoots = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>Removes the cloud drive. The folder itself and any downloaded files stay.</summary>
    public static void Unregister(string id)
    {
        try
        {
            StorageProviderSyncRootManager.Unregister(id);
            Log.Info("Unregistered the sync folder");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not unregister the sync folder: {ex.Message}");
        }
    }
}
