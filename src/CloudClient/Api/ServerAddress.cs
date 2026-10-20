using System.Net;
using System.Net.Sockets;

namespace CloudClient.Api;

/// <summary>
/// Turns whatever the user typed ("cloud.example.com", "https://cloud.example.com/files",
/// ".../backend/api") into the server root, and from it the API base.
/// </summary>
public static class ServerAddress
{
    public static string? Normalize(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        // Keep any sub-path the server is published under, minus app routes and the API itself.
        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/backend/api", "/backend", "/files", "/login", "/settings", "/share", "/connect" })
        {
            var index = path.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                path = path[..index];
                break;
            }
        }
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    /// <summary>
    /// Plain http sends the password and the tokens unencrypted, so it is only
    /// accepted on the local network (loopback, private and link-local
    /// addresses, single-label or .local/.lan/.home.arpa names). Anything
    /// reachable from the internet must use https.
    /// </summary>
    public static bool IsAllowed(string serverUrl)
    {
        var uri = new Uri(serverUrl);
        if (uri.Scheme == Uri.UriSchemeHttps) return true;

        var host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        if (host == "localhost" || !host.Contains('.') && !IPAddress.TryParse(host, out _)) return true;
        if (host.EndsWith(".local") || host.EndsWith(".lan") || host.EndsWith(".home.arpa") || host.EndsWith(".localhost")) return true;
        return IPAddress.TryParse(host, out var ip) && IsLocal(ip);
    }

    private static bool IsLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;

        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    public static Uri Api(string serverUrl, string relative) => new($"{serverUrl}/backend/api/{relative.TrimStart('/')}");
}
