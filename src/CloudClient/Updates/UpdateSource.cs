namespace CloudClient.Updates;

/// <summary>
/// Where every copy of the app looks for updates: the publisher's own
/// server, whatever server the user is signed in to. Only the publisher
/// puts files there, and the app still checks the signature before
/// installing anything. The files are the ones scripts/build.ps1 writes,
/// uploaded as they are.
/// </summary>
public static class UpdateSource
{
    public const string BaseUrl = "https://webincloud.itsgalaxy.com/updates/";

    /// <summary>Plain text: the latest version, e.g. 1.0.0.</summary>
    public static string VersionUrl => $"{BaseUrl}{Brand.Id}Version.txt";

    /// <summary>Plain text: the SHA-256 of the latest exe.</summary>
    public static string Sha256Url => $"{BaseUrl}{Brand.Id}Sha.txt";

    /// <summary>Plain text: the publisher's signature over brand, version and SHA-256.</summary>
    public static string SignatureUrl => $"{BaseUrl}{Brand.Id}Signature.txt";

    /// <summary>The latest exe.</summary>
    public static string ExeUrl => $"{BaseUrl}{Brand.Id}.exe";
}
