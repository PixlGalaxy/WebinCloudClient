using System.IO;
using System.Net.Http;
using System.Text.Json;
using CloudClient.Core;

namespace CloudClient.Api;

/// <summary>
/// Keeps GET /devices/events open for as long as the app runs, so the server
/// can tell it at once when a folder changed or when this device was
/// disconnected. While the server is unreachable it tries again every 10
/// seconds.
/// </summary>
public sealed class ServerEvents : IDisposable
{
    /// <summary>The server pings every 30 s; this long without a line means the connection is dead.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private readonly ApiClient _api;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>True while the stream is open, false while reconnecting.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>A folder on the server changed (its path, "" for the root). Raised on a background thread.</summary>
    public event Action<string>? FolderChanged;

    public ServerEvents(ApiClient api)
    {
        _api = api;
    }

    /// <summary>Starts listening; call once every handler is attached.</summary>
    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var response = await _api.SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, ServerAddress.Api(_api.ServerUrl, "devices/events")),
                    _stop.Token,
                    stream: true);
                await using var body = await response.Content.ReadAsStreamAsync(_stop.Token);
                using var reader = new StreamReader(body);
                ConnectionChanged?.Invoke(true);

                string? eventName = null;
                string? data = null;
                while (true)
                {
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    silence.CancelAfter(Silence);
                    var line = await reader.ReadLineAsync(silence.Token);
                    if (line is null) break;
                    if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].Trim();
                    else if (line.Length == 0)
                    {
                        if (eventName == "signed-out")
                        {
                            Log.Info("The server reported this device as disconnected");
                            _api.ReportSignedOut();
                            return;
                        }
                        if (eventName == "changed" && data is not null) RaiseChanged(data);
                        eventName = null;
                        data = null;
                    }
                }
            }
            catch (SignedOutException)
            {
                return;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Server events interrupted: {ex.GetType().Name}: {ex.Message}");
            }

            ConnectionChanged?.Invoke(false);
            try
            {
                await Task.Delay(RetryEvery, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RaiseChanged(string data)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("path", out var path) && path.GetString() is { } folder) FolderChanged?.Invoke(folder);
        }
        catch (JsonException)
        {
            // Not something this version understands; ignore it.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
