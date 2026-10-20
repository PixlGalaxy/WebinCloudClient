using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CloudClient.Core;
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
