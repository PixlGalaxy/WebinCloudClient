using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CloudClient.Api;
using CloudClient.Core;

namespace CloudClient.Auth;

/// <summary>Who signed in, on which server, as which device.</summary>
public sealed record SignInResult(string ServerUrl, TokenSet Tokens, DeviceInfo Device, UserInfo User);

/// <summary>The two ways a device signs in. See the server's devices.routes.ts.</summary>
public static class SignIn
{
    /// <summary>
    /// Checks the address answers like a Webin Cloud server and returns where
    /// it really is (an http address the server redirects to https becomes the
    /// https one) and its name. Refuses plain http outside the local network.
    /// No credentials are sent here.
    /// </summary>
    public static async Task<(string ServerUrl, string Name)> CheckServerAsync(string serverUrl, CancellationToken cancel)
    {
        using var http = ApiClient.CreateHttp(followRedirects: true);
        http.Timeout = TimeSpan.FromSeconds(15);

        using var health = await http.GetAsync(ServerAddress.Api(serverUrl, "health"), cancel);
        if (!health.IsSuccessStatusCode) throw new ApiException((int)health.StatusCode, "No server was found at this address.");

        var final = health.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path) ?? "";
        const string suffix = "/backend/api/health";
        var resolved = final.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? final[..^suffix.Length] : serverUrl;
        if (!ServerAddress.IsAllowed(resolved))
        {
            throw new ApiException(0,
                "This server isn't using HTTPS, so your password and files would travel unencrypted. Use its https:// address.");
        }

        try
        {
            var config = await http.GetFromJsonAsync<ServerConfig>(ServerAddress.Api(resolved, "config"), cancel);
            return (resolved, config?.AppName ?? Brand.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (resolved, Brand.Name);
        }
    }

    /// <summary>
    /// Browser approval (OAuth 2.0 authorization code with PKCE, RFC 7636 and
    /// RFC 8252): opens the server's /connect page; once the user approves, the
    /// browser hands a one-time code to a listener on 127.0.0.1, which only
    /// this app can redeem because only it knows the verifier.
    /// </summary>
    public static async Task<SignInResult> WithBrowserAsync(string serverUrl, string deviceName, CancellationToken cancel)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        var connectUrl =
            $"{serverUrl}/connect?code_challenge={challenge}&code_challenge_method=S256" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}" +
            $"&device_name={Uri.EscapeDataString(deviceName)}&platform={Brand.Platform}";
        Process.Start(new ProcessStartInfo(connectUrl) { UseShellExecute = true });

        var query = await WaitForCallbackAsync(listener, cancel);
        if (query.GetValueOrDefault("state") != state) throw new ApiException(400, "The sign-in answer did not match this request. Try again.");
        if (query.TryGetValue("error", out _)) throw new OperationCanceledException("Sign-in was cancelled in the browser.");
        if (!query.TryGetValue("code", out var code)) throw new ApiException(400, "The browser did not return a sign-in code. Try again.");

        using var http = ApiClient.CreateHttp();
        using var response = await http.PostAsJsonAsync(
            ServerAddress.Api(serverUrl, "devices/token"),
            new { grantType = "authorization_code", code, codeVerifier = verifier, redirectUri },
            cancel);
        if (!response.IsSuccessStatusCode) throw await ApiClient.ReadErrorAsync(response, cancel);
        var issued = (await response.Content.ReadFromJsonAsync<TokenResponse>(cancel))!;
        return await CompleteAsync(serverUrl, issued, cancel);
    }

    /// <summary>Username and password typed into the app (the fallback; the browser is preferred).</summary>
    public static async Task<SignInResult> WithPasswordAsync(
        string serverUrl, string usernameOrEmail, string password, string deviceName, CancellationToken cancel)
    {
        using var http = ApiClient.CreateHttp();
        using var response = await http.PostAsJsonAsync(
            ServerAddress.Api(serverUrl, "devices/login"),
            new { usernameOrEmail, password, deviceName, platform = Brand.Platform },
            cancel);
        if (!response.IsSuccessStatusCode) throw await ApiClient.ReadErrorAsync(response, cancel);
        var issued = (await response.Content.ReadFromJsonAsync<TokenResponse>(cancel))!;
        return await CompleteAsync(serverUrl, issued, cancel);
    }

    private static async Task<SignInResult> CompleteAsync(string serverUrl, TokenResponse issued, CancellationToken cancel)
    {
        var tokens = ApiClient.ToTokenSet(issued);
        TokenStore.Save(serverUrl, tokens);
        using var api = new ApiClient(serverUrl, tokens);
        var me = await api.GetMeAsync(cancel);
        Log.Info($"Signed in to {serverUrl} as {me.User.Username} (device {issued.Device.Id})");
        return new SignInResult(serverUrl, tokens, issued.Device, me.User);
    }

    /// <summary>
    /// A minimal HTTP server for one request: reads the GET line of the
    /// browser's redirect, answers with a page saying it can be closed, and
    /// returns the query. Anything else that connects gets a 404.
    /// </summary>
    private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken cancel)
    {
        var result = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var registration = stop.Token.Register(() => result.TrySetCanceled(stop.Token));

        // Browsers open spare connections that never send anything, so each one
        // is served on its own, with a deadline, instead of blocking the next.
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch
                {
                    return;
                }
                _ = ServeAsync(client, result, stop.Token);
            }
        });

        try
        {
            return await result.Task;
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static async Task ServeAsync(TcpClient client, TaskCompletionSource<Dictionary<string, string>> result, CancellationToken cancel)
    {
        using (client)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await using var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, deadline.Token);
                var parts = requestLine.Split(' ');
                var target = parts.Length >= 2 ? parts[1] : "";

                if (parts[0] != "GET" || !target.StartsWith("/callback", StringComparison.Ordinal) || result.Task.IsCompleted)
                {
                    await RespondAsync(stream, "404 Not Found", "", deadline.Token);
                    return;
                }

                var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
                await RespondAsync(stream, "200 OK", Page(query.ContainsKey("code")), deadline.Token);
                result.TrySetResult(query);
            }
            catch
            {
                // A connection that sent nothing useful; the real callback may still come.
            }
        }
    }

    /// <summary>The first line of an HTTP request, read up to 8 KB: anything longer is not our callback.</summary>
    private static async Task<string> ReadRequestLineAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancel);
            if (read == 0) break;
            length += read;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
        }
        return "";
    }

    private static async Task RespondAsync(Stream stream, string status, string html, CancellationToken cancel)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancel);
        await stream.WriteAsync(body, cancel);
    }

    private static string Page(bool approved)
    {
        var colors = Brand.Colors;
        var title = approved ? "You're signed in" : "Sign-in cancelled";
        var text = approved ? $"You can close this tab and go back to {Brand.Name}." : $"Go back to {Brand.Name} to try again.";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{Brand.Name}}</title>
            <style>body{margin:0;height:100vh;display:grid;place-items:center;background:{{colors.WindowBg}};color:{{colors.Text}};font-family:Segoe UI,sans-serif}
            .card{background:{{colors.PanelBg}};border:1px solid {{colors.PanelBorder}};padding:32px 40px;max-width:420px;clip-path:polygon(0 0,100% 0,100% calc(100% - 16px),calc(100% - 16px) 100%,0 100%)}
            h1{margin:0 0 8px;font-size:22px;color:{{(approved ? colors.Accent : colors.TextMuted)}}}p{margin:0;color:{{colors.TextMuted}}}</style></head>
            <body><div class="card"><h1>{{title}}</h1><p>{{text}}</p></div></body></html>
            """;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(index < 0 ? pair : pair[..index]);
            var value = index < 0 ? "" : Uri.UnescapeDataString(pair[(index + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
