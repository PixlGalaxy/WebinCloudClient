namespace CloudClient.Updates;

/// <summary>
/// Where updates come from. {brand} becomes WebinCloud or RyzhellCloud.
/// A relative path is on the signed-in server's API (/backend/api/...) and is
/// requested with this device's sign-in; a full https:// URL is used as is,
/// without it, so the files can live on any other host. Whatever the host,
/// the publisher's signature is checked before anything is installed.
/// </summary>
public static class UpdateSource
{
    /// <summary>Plain text: the latest version, e.g. 1.0.0.</summary>
    public const string VersionUrl = "client/{brand}/version";

    /// <summary>Plain text: the SHA-256 of the latest exe.</summary>
    public const string Sha256Url = "client/{brand}/sha256";

    /// <summary>Plain text: the publisher's signature over brand, version and SHA-256.</summary>
    public const string SignatureUrl = "client/{brand}/signature";

    /// <summary>The latest exe.</summary>
    public const string ExeUrl = "client/{brand}/download";

    public static string For(string template) => template.Replace("{brand}", Brand.Id);

    public static bool IsExternal(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}
