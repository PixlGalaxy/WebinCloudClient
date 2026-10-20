namespace CloudClient.Updates;

/// <summary>
/// Where every copy of the app looks for updates: public links on the
/// publisher's own server, the same whatever server the user signs in to.
/// Only the publisher puts files there, and the app still checks the
/// signature before installing anything. The files are the ones
/// scripts/build.ps1 writes, uploaded as they are.
/// </summary>
public static class UpdateSource
{
#if BRAND_RYZHELLCLOUD
    private const string Folder = "https://webincloud.itzgalaxy.com/backend/api/public/durazznoapp/DurazznoApp/Apps/Dist/RyzhellCloudClient/";
#else
    private const string Folder = "https://webincloud.itzgalaxy.com/backend/api/public/durazznoapp/DurazznoApp/Apps/Dist/WebinCloudClient/";
#endif

    /// <summary>Plain text: the latest version, e.g. 1.0.0.</summary>
    public const string VersionUrl = Folder + Brand.Id + "Version.txt/download?inline=1&path=";

    /// <summary>Plain text: the SHA-256 of the latest exe.</summary>
    public const string Sha256Url = Folder + Brand.Id + "Sha.txt/download?inline=1&path=";

    /// <summary>Plain text: the publisher's signature over brand, version and SHA-256.</summary>
    public const string SignatureUrl = Folder + Brand.Id + "Signature.txt/download?inline=1&path=";

    /// <summary>The latest exe.</summary>
    public const string ExeUrl = Folder + Brand.Id + ".exe/download?inline=1&path=";
}
