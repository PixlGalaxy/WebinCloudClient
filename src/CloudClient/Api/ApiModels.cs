using System.Text.Json.Serialization;

namespace CloudClient.Api;

/// <summary>Answer of POST /devices/login and /devices/token.</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }
    [JsonPropertyName("device")] public DeviceInfo Device { get; set; } = new();
}

public sealed class DeviceInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
}

public sealed class UserInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "";
}

public sealed class MeResponse
{
    [JsonPropertyName("user")] public UserInfo User { get; set; } = new();
}

/// <summary>GET /config: what the server calls itself.</summary>
public sealed class ServerConfig
{
    [JsonPropertyName("appName")] public string? AppName { get; set; }
    [JsonPropertyName("appTitle")] public string? AppTitle { get; set; }
}

public sealed class ErrorBody
{
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>An error the server explained; Message is its text, already translated.</summary>
public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The device was disconnected (or its tokens are gone): it has to sign in again.</summary>
public sealed class SignedOutException() : Exception("This device was signed out.");
