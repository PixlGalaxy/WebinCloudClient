using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CloudClient.Api;
using CloudClient.Core;

namespace CloudClient.Updates;

/// <summary>A newer build the publisher offers, already checked against the publisher's signature.</summary>
public sealed record UpdateInfo(Version Version, string Sha256);

/// <summary>
/// Finds and installs newer builds of this app from the publisher's server
/// (UpdateSource), the same for everyone whatever server they sign in to.
/// It offers, as plain text, the latest version (e.g. 1.0.0),
/// the exe's SHA-256 and the publisher's signature over both. Nothing is
/// installed unless the signature matches the publisher's key and the
/// downloaded exe matches that SHA-256, so neither a compromised server nor
/// anyone on the network can slip in a different program. Updating swaps the exe in place (a running exe can be
/// renamed), starts the new one and quits.
/// </summary>
public sealed class Updater
{
    /// <summary>Larger than any real build: a download growing past it is stopped.</summary>
    private const long MaxSize = 500L * 1024 * 1024;
    public const string WaitForArgument = "--wait-for";
    public const string UpdatedArgument = "--updated";

    public static Version Current { get; } = Trim(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Debug builds run from the build folder and are never replaced.</summary>
#if DEBUG
    public static bool Supported => false;
#else
    public static bool Supported => true;
#endif

    private int _busy;

    public UpdateInfo? Available { get; private set; }
    public DateTime? LastChecked { get; private set; }
    /// <summary>What the updater is doing or what went wrong, in words; null when idle.</summary>
    public string? Status { get; private set; }
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>Raised from background threads whenever the above change.</summary>
    public event Action? Changed;

    public async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Set("Checking for updates…");
        try
        {
            Available = await FindAsync();
            LastChecked = DateTime.Now;
            Set(null);
            if (Available is not null) Log.Info($"Update available: version {Available.Version}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check for updates: {ex.Message}");
            Set("Could not check for updates. Details are in the log.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    private async Task<UpdateInfo?> FindAsync()
    {
        var text = await GetTextAsync(UpdateSource.VersionUrl);
        // The server offers no build of this app.
        if (text is null) return null;
        if (!Version.TryParse(text, out var version)) throw new InvalidDataException("The published update has no valid version.");
        version = Trim(version);
        if (version <= Current) return null;

        var sha256 = (await GetTextAsync(UpdateSource.Sha256Url))?.ToLowerInvariant();
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The published update has no valid SHA-256.");
        var signature = await GetTextAsync(UpdateSource.SignatureUrl);
        if (signature is null || !IsSigned(version, sha256, signature))
            throw new InvalidDataException("The published update is not signed by the publisher; it was not installed.");
        return new UpdateInfo(version, sha256);
    }

    /// <summary>One of the plain-text update files (version, SHA-256, signature); null if there is none.</summary>
    private static async Task<string?> GetTextAsync(string url)
    {
        using var response = await GetAsync(url, stream: false);
        if (response is null) return null;
        var text = (await response.Content.ReadAsStringAsync()).Trim();
        return text.Length is > 0 and < 512 ? text : null;
    }

    /// <summary>GETs an update file from the publisher's server; null on 404. No sign-in is ever sent there.</summary>
    private static async Task<HttpResponseMessage?> GetAsync(string url, bool stream)
    {
        var response = await Http.GetAsync(url, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = ApiClient.CreateHttp(followRedirects: true);
        // The exe is tens of MB; give slow connections time.
        http.Timeout = TimeSpan.FromMinutes(30);
        return http;
    }

    /// <summary>The same message scripts/build.ps1 signs: brand, version and SHA-256, one per line.</summary>
    private static bool IsSigned(Version version, string sha256, string signature)
    {
        try
        {
            using var key = CngKey.Import(Convert.FromBase64String(UpdateKey.Public), CngKeyBlobFormat.EccPublicBlob);
            using var ecdsa = new ECDsaCng(key);
            var message = Encoding.UTF8.GetBytes($"{Brand.Id}\n{version}\n{sha256}");
            return ecdsa.VerifyData(message, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads and checks the update, puts it in place of this exe and
    /// starts it. Returns true when the caller should quit so the new
    /// version can take over.
    /// </summary>
    public async Task<bool> InstallAsync()
    {
        var update = Available;
        if (update is null || !Supported) return false;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;

        var target = Environment.ProcessPath!;
        var download = target + ".new";
        try
        {
            Set("Downloading the update…");
            await DownloadAsync(update, download);

            // A running exe cannot be overwritten, but it can be renamed out of the way.
            var old = target + ".old";
            File.Delete(old);
            File.Move(target, old);
            try
            {
                File.Move(download, target);
            }
            catch
            {
                File.Move(old, target);
                throw;
            }

            Log.Info($"Updated to version {update.Version}; restarting");
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = false,
                ArgumentList = { UpdatedArgument, WaitForArgument, Environment.ProcessId.ToString() },
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not install the update", ex);
            Set(ex is InvalidDataException ? ex.Message : "The update could not be installed. Details are in the log.");
            TryDelete(download);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    private async Task DownloadAsync(UpdateInfo update, string path)
    {
        using var response = await GetAsync(UpdateSource.ExeUrl, stream: true)
            ?? throw new InvalidDataException("The update is no longer published.");
        var length = response.Content.Headers.ContentLength;
        await using var body = await response.Content.ReadAsStreamAsync();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var lastPercent = -1;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (total > MaxSize) throw new InvalidDataException("The downloaded update is far too large; it was not installed.");
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read));
                var percent = length is > 0 ? (int)(total * 100 / length.Value) : -1;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    Set(percent >= 0 ? $"Downloading the update… {percent}%" : "Downloading the update…");
                }
            }
        }
        // The published SHA-256 (which the publisher signed) must match before anything is replaced.
        if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != update.Sha256)
            throw new InvalidDataException("The downloaded update's SHA-256 does not match the published one; it was not installed.");
    }

    /// <summary>On start after an update: waits for the previous version to quit, then removes it.</summary>
    public static void FinishPrevious(string[] args)
    {
        var at = Array.IndexOf(args, WaitForArgument);
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(30_000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        if (Environment.ProcessPath is { } exe)
        {
            TryDelete(exe + ".old");
            TryDelete(exe + ".new");
        }
    }

    private void Set(string? status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked; the next start removes it.
        }
    }

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

}
