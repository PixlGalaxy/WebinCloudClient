using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using CloudClient.Auth;
using CloudClient.Core;

namespace CloudClient.Api;

/// <summary>
/// Talks to one server as one device. Adds the bearer token to every request,
/// renews it shortly before it expires or when the server says 401, and saves
/// each new pair at once: the server invalidates the previous refresh token as
/// soon as it issues a new one.
/// </summary>
public sealed class ApiClient : IDisposable
{
    /// <summary>Renew this long before the access token expires, so requests never race its expiry.</summary>
    private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    /// <summary>For long-lived streams, which must not hit the normal request timeout.</summary>
    private readonly HttpClient _streamHttp;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TokenSet _tokens;

    public string ServerUrl { get; }

    /// <summary>Raised once the server refuses this device's refresh token.</summary>
    public event Action? SignedOut;

    public ApiClient(string serverUrl, TokenSet tokens)
    {
        ServerUrl = serverUrl;
        _tokens = tokens;
        _http = CreateHttp();
        _streamHttp = CreateHttp();
        _streamHttp.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Redirects are off unless asked for: a request carrying the token must
    /// never be passed on to whatever address a response names.
    /// </summary>
    public static HttpClient CreateHttp(bool followRedirects = false)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = followRedirects };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Brand.Id, version));
        return http;
    }

    public Task<MeResponse> GetMeAsync(CancellationToken cancel = default) => GetJsonAsync<MeResponse>("auth/me", cancel);

    public async Task<T> GetJsonAsync<T>(string path, CancellationToken cancel = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ServerAddress.Api(ServerUrl, path)), cancel);
        return (await response.Content.ReadFromJsonAsync<T>(cancel))!;
    }

    /// <summary>Tells the server to forget this device. Best effort: the local tokens go either way.</summary>
    public async Task SignOutAsync()
    {
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ServerAddress.Api(ServerUrl, "devices/current")), default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Server sign-out failed, removing local tokens anyway: {ex.Message}");
        }
        TokenStore.Delete(ServerUrl);
    }

    /// <summary>
    /// Sends with the current token; on 401 renews once and retries. `build`
    /// makes a fresh request each time because a request can only be sent once.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancel, bool stream = false)
    {
        var http = stream ? _streamHttp : _http;
        if (_tokens.AccessExpiresAt - DateTimeOffset.UtcNow < RenewMargin) await RenewAsync(_tokens.AccessToken, cancel);

        var response = await SendOnceAsync(http, build, cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var used = _tokens.AccessToken;
            response.Dispose();
            await RenewAsync(used, cancel);
            response = await SendOnceAsync(http, build, cancel);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancel);
            response.Dispose();
            if (error.Status == 401) throw Fail();
            throw error;
        }
        return response;
    }

    private Task<HttpResponseMessage> SendOnceAsync(HttpClient http, Func<HttpRequestMessage> build, CancellationToken cancel)
    {
        var request = build();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
    }

    /// <summary>One renewal at a time; a caller whose token was already replaced just uses the new one.</summary>
    private async Task RenewAsync(string staleAccessToken, CancellationToken cancel)
    {
        await _refreshGate.WaitAsync(cancel);
        try
        {
            if (_tokens.AccessToken != staleAccessToken) return;

            using var response = await _http.PostAsJsonAsync(
                ServerAddress.Api(ServerUrl, "devices/token"),
                new { grantType = "refresh_token", refreshToken = _tokens.RefreshToken },
                cancel);

            if (response.StatusCode == HttpStatusCode.Unauthorized) throw Fail();
            if (!response.IsSuccessStatusCode) throw await ReadErrorAsync(response, cancel);

            var issued = (await response.Content.ReadFromJsonAsync<TokenResponse>(cancel))!;
            _tokens = ToTokenSet(issued);
            TokenStore.Save(ServerUrl, _tokens);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>The server said this device was disconnected: drop the tokens and tell the app.</summary>
    public void ReportSignedOut() => Fail();

    private SignedOutException Fail()
    {
        Log.Warn("The server refused this device's tokens; signing out");
        TokenStore.Delete(ServerUrl);
        SignedOut?.Invoke();
        return new SignedOutException();
    }

    public static TokenSet ToTokenSet(TokenResponse issued) =>
        new(issued.AccessToken, issued.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(issued.ExpiresIn));

    public static async Task<ApiException> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        string? message = null;
        try
        {
            message = (await response.Content.ReadFromJsonAsync<ErrorBody>(cancel))?.Error;
        }
        catch
        {
            // Not JSON (a proxy error page, say): fall back to the status.
        }
        return new ApiException((int)response.StatusCode, message ?? $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public void Dispose()
    {
        _http.Dispose();
        _streamHttp.Dispose();
        _refreshGate.Dispose();
    }
}
