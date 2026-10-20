using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CloudClient.Api;
using CloudClient.Core;

namespace CloudClient.Updates;

/// <summary>A newer build, downloaded and checked against the publisher's signature and SHA-256.</summary>
public sealed record UpdateInfo(Version Version, string Sha256, string Signature);

/// <summary>
/// Keeps the app up to date from the publisher's server (UpdateSource), the
/// same for everyone whatever server they sign in to. The server offers, as
/// plain text, the latest version (e.g. 1.0.0), the exe's SHA-256 and the
/// publisher's signature over both.
///
/// A newer version is downloaded quietly in the background and kept beside
/// the app until it is installed. Only once the signature matches the
/// publisher's key and the download matches the SHA-256 is the user told it
/// is ready. It installs when the user chooses "Restart and update", or by
/// itself the next time the app starts. Neither a compromised server nor
/// anyone on the network can slip in a different program.
/// </summary>
public sealed class Updater
{
    /// <summary>Larger than any real build: a download growing past it is stopped.</summary>
    private const long MaxSize = 500L * 1024 * 1024;
    public const string WaitForArgument = "--wait-for";

    private static readonly string StagingDir = Path.Combine(AppPaths.DataDir, "update");
    private static readonly string StagedExe = Path.Combine(StagingDir, $"{Brand.Id}.exe");
    /// <summary>The staged build's version, SHA-256 and signature, one per line.</summary>
    private static readonly string StagedInfo = Path.Combine(StagingDir, "update.txt");

    public static Version Current { get; } = Trim(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Debug builds run from the build folder and are never replaced.</summary>
#if DEBUG
    public static bool Supported => false;
#else
    public static bool Supported => true;
#endif

    private int _busy;
    private string? _lastProblem;

    /// <summary>Downloaded, checked and waiting to be installed; null if there is none.</summary>
    public UpdateInfo? Ready { get; private set; }
    public DateTime? LastChecked { get; private set; }
    /// <summary>What the updater is doing or what went wrong, in words; null when idle.</summary>
    public string? Status { get; private set; }
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>Raised from background threads whenever the above change.</summary>
    public event Action? Changed;
    /// <summary>Raised once per version, when it has been downloaded and checked.</summary>
    public event Action<Version>? ReadyToInstall;

    public Updater()
    {
        // Downloaded in an earlier run but not installed yet (it installs on the next start).
        Ready = Supported ? ReadStaged() : null;
    }

    /// <summary>Looks for a newer version and, if there is one, downloads and checks it.</summary>
    public async Task CheckAsync()
    {
        if (!Supported) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Set("Checking for updates…");
        try
        {
            var found = await FindAsync();
            LastChecked = DateTime.Now;
            if (found is null)
            {
                Set(null);
                return;
            }
            if (Ready?.Version == found.Version && Ready.Sha256 == found.Sha256)
            {
                Set(null);
                return;
            }

            Log.Info($"Version {found.Version} is available; downloading it");
            await DownloadAsync(found);
            Ready = found;
            _lastProblem = null;
            Set(null);
            Log.Info($"Version {found.Version} is downloaded and checked, ready to install");
            ReadyToInstall?.Invoke(found.Version);
        }
        catch (Exception ex)
        {
            // Checked every few minutes: log a problem once, not on every check.
            if (ex.Message != _lastProblem) Log.Warn($"Could not update: {ex.Message}");
            _lastProblem = ex.Message;
            Set(ex is InvalidDataException ? ex.Message : "Could not check for updates. Details are in the log.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    private static async Task<UpdateInfo?> FindAsync()
    {
        var text = await GetTextAsync(UpdateSource.VersionUrl);
        // Nothing published for this app.
        if (text is null) return null;
        if (!Version.TryParse(text, out var version)) throw new InvalidDataException("The published update has no valid version.");
        version = Trim(version);
        if (version <= Current) return null;

        var sha256 = (await GetTextAsync(UpdateSource.Sha256Url))?.ToLowerInvariant();
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The published update has no valid SHA-256.");
        var signature = await GetTextAsync(UpdateSource.SignatureUrl);
        if (signature is null || !IsSigned(version, sha256, signature))
            throw new InvalidDataException($"Version {version} is not signed by the publisher, so it was not downloaded.");
        return new UpdateInfo(version, sha256, signature);
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

    /// <summary>Downloads the exe beside the app, checks its SHA-256, and records it as staged.</summary>
    private async Task DownloadAsync(UpdateInfo update)
    {
        Directory.CreateDirectory(StagingDir);
        TryDelete(StagedInfo);
        var partial = StagedExe + ".part";
        try
        {
            using var response = await GetAsync(UpdateSource.ExeUrl, stream: true)
                ?? throw new InvalidDataException("The update is no longer published.");
            var length = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var lastPercent = -1;
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await body.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxSize) throw new InvalidDataException("The downloaded update is far too large; it was discarded.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    var percent = length is > 0 ? (int)(total * 100 / length.Value) : -1;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        Set(percent >= 0 ? $"Downloading version {update.Version}… {percent}%" : $"Downloading version {update.Version}…");
                    }
                }
            }
            // The published SHA-256 (which the publisher signed) must match, or the file is thrown away.
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != update.Sha256)
                throw new InvalidDataException($"The download of version {update.Version} does not match its SHA-256; it was discarded.");
            File.Move(partial, StagedExe, overwrite: true);
            File.WriteAllLines(StagedInfo, [update.Version.ToString(), update.Sha256, update.Signature]);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>The staged update, if it is complete, newer than this version and still signed; anything else is removed.</summary>
    private static UpdateInfo? ReadStaged()
    {
        try
        {
            if (!File.Exists(StagedInfo) || !File.Exists(StagedExe)) return null;
            var lines = File.ReadAllLines(StagedInfo);
            if (lines.Length >= 3 && Version.TryParse(lines[0], out var version) && Trim(version) > Current &&
                IsSigned(Trim(version), lines[1], lines[2]))
                return new UpdateInfo(Trim(version), lines[1], lines[2]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        ClearStaged();
        return null;
    }

    private static void ClearStaged()
    {
        TryDelete(StagedInfo);
        TryDelete(StagedExe);
    }

    /// <summary>"Restart and update": installs the staged version now. True when the caller should quit.</summary>
    public bool InstallNow(string[] args)
    {
        if (Ready is null || IsBusy) return false;
        return Apply(Ready, args);
    }

    /// <summary>
    /// On start: if a checked update is waiting, puts it in place of this exe
    /// and starts it with the same arguments. True when the caller should
    /// exit and let the new version run.
    /// </summary>
    public static bool ApplyStaged(string[] args)
    {
        if (!Supported) return false;
        var staged = ReadStaged();
        return staged is not null && Apply(staged, args);
    }

    private static bool Apply(UpdateInfo update, string[] args)
    {
        var target = Environment.ProcessPath!;
        var old = target + ".old";
        try
        {
            // Checked again: the file waited on disk since it was downloaded.
            using (var stream = File.OpenRead(StagedExe))
            {
                if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != update.Sha256)
                    throw new InvalidDataException("The downloaded update changed on disk since it was checked; it was discarded.");
            }

            // A running exe cannot be overwritten, but it can be renamed out of the way.
            TryDelete(old);
            File.Move(target, old);
            try
            {
                File.Move(StagedExe, target);
            }
            catch
            {
                File.Move(old, target);
                throw;
            }
            TryDelete(StagedInfo);

            Log.Info($"Updated from {Current} to {update.Version}; starting the new version");
            var start = new ProcessStartInfo(target) { UseShellExecute = false };
            foreach (var arg in WithoutWaitFor(args)) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(WaitForArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not install the update", ex);
            ClearStaged();
            return false;
        }
    }

    private static IEnumerable<string> WithoutWaitFor(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == WaitForArgument)
            {
                i++;
                continue;
            }
            yield return args[i];
        }
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
        if (Environment.ProcessPath is { } exe) TryDelete(exe + ".old");
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
