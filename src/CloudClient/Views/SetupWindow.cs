using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudClient.Api;
using CloudClient.Auth;
using CloudClient.Core;
using CloudClient.UI;

namespace CloudClient.Views;

/// <summary>
/// First run, or after signing out: the server's address, then signing in
/// (the browser by default, a password as a fallback).
/// </summary>
public sealed class SetupWindow : AppWindow
{
    private readonly AppSettings _settings;
    private readonly Action<SignInResult> _onSignedIn;
    private readonly ContentControl _step = new();
    private CancellationTokenSource? _busy;
    private string _serverUrl = "";
    private string _serverName = Brand.Name;

    public SetupWindow(AppSettings settings, Action<SignInResult> onSignedIn)
    {
        _settings = settings;
        _onSignedIn = onSignedIn;
        Title = Brand.Name;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var logo = Ui.BrandImage("logo.png", 54);
        logo.Margin = new Thickness(0, 0, 0, 28);
        Content = new Border { Padding = new Thickness(40, 34, 40, 36), Child = Ui.Stack(logo, _step) };

        if (settings.ServerUrl is not null)
        {
            _serverUrl = settings.ServerUrl;
            ShowSignIn();
        }
        else
        {
            ShowServer();
        }
        Closed += (_, _) => _busy?.Cancel();
    }

    // --- step 1: server --------------------------------------------------------

    private void ShowServer(string? error = null)
    {
        var address = new TextBox { Text = _serverUrl };
        var continueButton = Ui.Button("Continue", "arrow-right", "PrimaryButton");
        var status = Ui.Sentence(error ?? "", "Danger");
        status.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;

        async void Submit()
        {
            var normalized = ServerAddress.Normalize(address.Text);
            if (normalized is null)
            {
                Show(status, "Enter the address you open in your browser, like cloud.example.com.", "Danger");
                return;
            }
            await RunAsync(new Control[] { address, continueButton }, status, "Looking for the server…", async cancel =>
            {
                (_serverUrl, _serverName) = await SignIn.CheckServerAsync(normalized, cancel);
                ShowSignIn();
            });
        }

        continueButton.Click += (_, _) => Submit();
        continueButton.IsDefault = true;
        continueButton.HorizontalAlignment = HorizontalAlignment.Left;

        _step.Content = Ui.Stack(
            Ui.Text("Connect to your server", "PageTitle"),
            Gap(Ui.Text($"Enter the address you use to open {Brand.Name} in your browser.", "PageSubtitle"), 6),
            Gap(Ui.Field("Server address", address), 26),
            Gap(status, 12),
            Gap(continueButton, 22));
        address.Focus();
        address.SelectAll();
    }

    // --- step 2: sign in ---------------------------------------------------------

    private void ShowSignIn(string? error = null)
    {
        var deviceName = new TextBox { Text = _settings.DeviceName, MaxLength = 64 };
        var browserButton = Ui.Button("Sign in with browser", "globe", "PrimaryButton");
        browserButton.IsDefault = true;
        browserButton.HorizontalAlignment = HorizontalAlignment.Left;
        var status = Ui.Sentence(error ?? "", "Danger");
        status.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;

        browserButton.Click += async (_, _) =>
        {
            var name = DeviceName(deviceName.Text);
            await RunAsync(new Control[] { deviceName, browserButton }, status,
                "Finish signing in in your browser, then come back here.", async cancel =>
                {
                    var result = await SignIn.WithBrowserAsync(_serverUrl, name, cancel);
                    Activate();
                    Finish(result, name);
                }, cancellable: true);
        };

        var server = Ui.Text(_serverUrl, "Mono");
        _step.Content = Ui.Stack(
            Ui.Text("Sign in", "PageTitle"),
            Gap(Ui.Text($"to {_serverName}", "PageSubtitle"), 6),
            Gap(server, 2),
            Gap(Ui.Field("This computer's name", deviceName), 26),
            Gap(Ui.Text("Shown in Settings → Devices on the website, where you can disconnect it.", "CardHint"), 6),
            Gap(status, 12),
            Gap(browserButton, 22),
            Gap(Ui.Row(
                Ui.Link("Use a password instead", (_, _) => ShowPassword(deviceName.Text)),
                Dot(),
                Ui.Link("Change server", (_, _) => ShowServer())), 18));
    }

    private void ShowPassword(string deviceNameText)
    {
        var user = new TextBox();
        var password = new PasswordBox();
        var signInButton = Ui.Button("Sign in", "log-in", "PrimaryButton");
        signInButton.IsDefault = true;
        signInButton.HorizontalAlignment = HorizontalAlignment.Left;
        var status = Ui.Sentence("", "Danger");
        status.Visibility = Visibility.Collapsed;

        signInButton.Click += async (_, _) =>
        {
            if (user.Text.Trim().Length == 0 || password.Password.Length == 0)
            {
                Show(status, "Enter your username and password.", "Danger");
                return;
            }
            var name = DeviceName(deviceNameText);
            await RunAsync(new Control[] { user, password, signInButton }, status, "Signing in…", async cancel =>
            {
                var result = await SignIn.WithPasswordAsync(_serverUrl, user.Text.Trim(), password.Password, name, cancel);
                Finish(result, name);
            });
        };

        _step.Content = Ui.Stack(
            Ui.Text("Sign in with a password", "PageTitle"),
            Gap(Ui.Text($"to {_serverName}", "PageSubtitle"), 6),
            Gap(Ui.Field("Username or email", user), 26),
            Gap(Ui.Field("Password", password), 16),
            Gap(status, 12),
            Gap(signInButton, 22),
            Gap(Ui.Link("Sign in with the browser instead", (_, _) => ShowSignIn()), 18));
        user.Focus();
    }

    private void Finish(SignInResult result, string deviceName)
    {
        _settings.ServerUrl = result.ServerUrl;
        _settings.Username = result.User.Username;
        _settings.DeviceId = result.Device.Id;
        _settings.DeviceName = deviceName;
        _settings.Save();
        _onSignedIn(result);
        Close();
    }

    // --- helpers -------------------------------------------------------------------

    /// <summary>
    /// Runs one step with its inputs disabled and a progress sentence; errors
    /// end up in the sentence in words, not exception text.
    /// </summary>
    private async Task RunAsync(Control[] inputs, TextBlock status, string progress, Func<CancellationToken, Task> work, bool cancellable = false)
    {
        _busy?.Cancel();
        var busy = _busy = new CancellationTokenSource(cancellable ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(60));
        foreach (var input in inputs) input.IsEnabled = false;
        Show(status, progress, "TextMuted");

        Button? cancelButton = null;
        if (cancellable && status.Parent is Panel panel)
        {
            cancelButton = Ui.Button("Cancel", "x", "SecondaryButton", (_, _) => busy.Cancel());
            cancelButton.HorizontalAlignment = HorizontalAlignment.Left;
            cancelButton.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Insert(panel.Children.IndexOf(status) + 1, cancelButton);
        }

        try
        {
            await work(busy.Token);
        }
        catch (OperationCanceledException) when (busy.IsCancellationRequested && !IsLoaded)
        {
            // The window closed.
        }
        catch (Exception ex)
        {
            Log.Error("Setup step failed", ex);
            Show(status, Explain(ex, busy.IsCancellationRequested), "Danger");
        }
        finally
        {
            foreach (var input in inputs) input.IsEnabled = true;
            if (cancelButton?.Parent is Panel owner) owner.Children.Remove(cancelButton);
        }
    }

    private static string Explain(Exception ex, bool cancelled) => ex switch
    {
        ApiException api => api.Message,
        OperationCanceledException when cancelled => "Cancelled.",
        OperationCanceledException => ex.Message.Length > 0 && !ex.Message.StartsWith("The operation", StringComparison.Ordinal)
            ? ex.Message
            : "The server took too long to answer. Try again.",
        HttpRequestException => "Could not reach the server. Check the address and your connection.",
        _ => "Something went wrong. Try again.",
    };

    private static void Show(TextBlock status, string text, string brushKey)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string DeviceName(string text) => text.Trim().Length > 0 ? text.Trim() : Environment.MachineName;

    private static FrameworkElement Gap(FrameworkElement element, double top)
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }

    private static TextBlock Dot()
    {
        var dot = Ui.Text("·", "Body");
        dot.Margin = new Thickness(10, 0, 10, 0);
        dot.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return dot;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _busy?.Cancel();
        base.OnKeyDown(e);
    }
}
