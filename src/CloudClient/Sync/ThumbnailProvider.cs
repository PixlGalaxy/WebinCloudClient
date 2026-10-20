using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using CloudClient.Api;
using CloudClient.Core;
using Microsoft.Win32;

namespace CloudClient.Sync;

/// <summary>
/// Gives File Explorer the thumbnail of a file that is only in the cloud, by
/// asking the server for it (the same one the website shows) instead of
/// downloading the file. Explorer finds it through the sync root's
/// "ThumbnailProvider" value and calls it out of process over COM; if the app
/// is not running, Windows starts it in the background.
/// </summary>
public static class ThumbnailServer
{
    private static uint _cookie;
    private static volatile ApiClient? _api;
    private static volatile string? _root;

    /// <summary>The account and folder thumbnails are served for; null while signed out.</summary>
    public static void Serve(ApiClient? api, string? root)
    {
        _api = api;
        _root = root is null ? null : Path.GetFullPath(root).TrimEnd('\\');
    }

    /// <summary>
    /// Makes the provider reachable: the COM class (per user, pointing at this
    /// exe) and the class object, registered from a thread-pool (MTA) thread so
    /// Explorer's calls never wait on the UI thread.
    /// </summary>
    public static void Start()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{{{Brand.ThumbnailProviderClsid}}}"))
            {
                key.SetValue("", $"{Brand.Name} thumbnails");
                using var server = key.CreateSubKey("LocalServer32");
                server.SetValue("", $"\"{Environment.ProcessPath}\" {Embedding}");
            }

            Task.Run(() =>
            {
                var clsid = new Guid(Brand.ThumbnailProviderClsid);
                var hr = CoRegisterClassObject(ref clsid, new ClassFactory(), ClsctxLocalServer, RegclsMultipleUse, out _cookie);
                if (hr != 0) Log.Warn($"Could not offer thumbnails to File Explorer: 0x{hr:X8}");
            }).Wait();
        }
        catch (Exception ex)
        {
            Log.Error("Could not set up thumbnails for File Explorer", ex);
        }
    }

    /// <summary>Uninstalling: removes the COM class registered by Start.</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{Brand.ThumbnailProviderClsid}}}", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove the thumbnail provider: {ex.Message}");
        }
    }

    public static void Stop()
    {
        if (_cookie != 0) CoRevokeClassObject(_cookie);
        _cookie = 0;
    }

    /// <summary>The argument COM adds when it starts the app to serve a call.</summary>
    public const string Embedding = "-Embedding";

    /// <summary>Points the sync root at the provider, so Explorer asks it for cloud files' thumbnails.</summary>
    public static void AttachTo(string syncRootId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\{syncRootId}", writable: true);
            key?.SetValue("ThumbnailProvider", $"{{{Brand.ThumbnailProviderClsid}}}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not attach thumbnails to the sync folder: {ex.Message}");
        }
    }

    /// <summary>The image and video types the server makes thumbnails of (see its mime.ts).</summary>
    private static readonly HashSet<string> Previewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".mp4", ".webm", ".ogv", ".mov",
    };

    /// <summary>Called by Explorer through the provider: a bitmap no larger than <paramref name="size"/>, or null.</summary>
    internal static IntPtr Render(string path, int size)
    {
        var api = _api;
        var root = _root;
        if (api is null || root is null || !path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;

        // Only what the server makes thumbnails of; anything else gets its file-type icon at once.
        if (!Previewable.Contains(Path.GetExtension(path))) return IntPtr.Zero;

        var relative = path[(root.Length + 1)..].Replace('\\', '/');
        var bytes = Task.Run(async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await api.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get,
                    ServerAddress.Api(api.ServerUrl, $"files/thumbnail?path={Uri.EscapeDataString(relative)}&size=lg&format=png")),
                cancel.Token);
            return await response.Content.ReadAsByteArrayAsync(cancel.Token);
        }).GetAwaiter().GetResult();

        using var source = new Bitmap(new MemoryStream(bytes));
        var scale = Math.Min(1.0, (double)size / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, width, height);
        }
        return ToDib(scaled);
    }

    /// <summary>A top-down 32-bit premultiplied DIB section, the form IThumbnailProvider hands back (WTSAT_ARGB).</summary>
    private static IntPtr ToDib(Bitmap bitmap)
    {
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = bitmap.Width,
            biHeight = -bitmap.Height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) return IntPtr.Zero;

        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = bitmap.Width * 4;
            var buffer = new byte[row];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, buffer, 0, row);
                Marshal.Copy(buffer, 0, bits + y * row, row);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return dib;
    }

    // --- COM plumbing -----------------------------------------------------------------

    private const uint ClsctxLocalServer = 4;
    private const uint RegclsMultipleUse = 1;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    [ComVisible(true)]
    private sealed class ClassFactory : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            var unknown = Marshal.GetIUnknownForObject(new CloudThumbnailProvider());
            try
            {
                return Marshal.QueryInterface(unknown, in riid, out ppv);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool fLock) => 0;
    }
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithItem
{
    void Initialize(IShellItem psi, uint grfMode);
}

[ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IThumbnailProvider
{
    void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha);
}

/// <summary>One per thumbnail Explorer asks for: told the file, then asked for the bitmap.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class CloudThumbnailProvider : IInitializeWithItem, IThumbnailProvider
{
    private const uint SigdnFileSysPath = 0x80058000;
    private const uint WtsatArgb = 2;
    private string? _path;

    public void Initialize(IShellItem psi, uint grfMode) => psi.GetDisplayName(SigdnFileSysPath, out _path);

    public void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha)
    {
        pdwAlpha = WtsatArgb;
        phbmp = IntPtr.Zero;
        try
        {
            if (_path is not null) phbmp = ThumbnailServer.Render(_path, (int)Math.Clamp(cx, 16, 1024));
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            // The server has none for this file (an unreadable image, say).
        }
        catch (Exception ex)
        {
            Log.Warn($"No thumbnail for \"{_path}\": {ex.Message}");
        }
        // No thumbnail (not an image, server unreachable): Explorer falls back to the file type's icon.
        if (phbmp == IntPtr.Zero) Marshal.ThrowExceptionForHR(unchecked((int)0x80004005));
    }
}
