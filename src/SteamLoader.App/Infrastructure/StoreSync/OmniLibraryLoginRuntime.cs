using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfApplication = System.Windows.Application;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Shared isolated WebView sign-in host for OmniLibrary stores. Store-specific
/// endpoints and token handlers live in a small profile; window lifecycle,
/// redirect validation, controller escape behavior and browser isolation are
/// identical for every managed web sign-in.
/// </summary>
internal static class OmniLibraryLoginRuntime
{
    internal const string LoginArgumentPrefix = "--omnilibrary-store-login=";

    public static bool TryParseArguments(IReadOnlyList<string> arguments, out string storeId)
    {
        var argument = arguments.FirstOrDefault(value =>
            value.StartsWith(LoginArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        storeId = argument is null
            ? string.Empty
            : argument[LoginArgumentPrefix.Length..].Trim().ToLowerInvariant();
        return TryGetProfile(storeId, out _);
    }

    public static int Run(string storeId)
    {
        var profile = GetRequiredProfile(storeId);
        var application = new WpfApplication
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };
        var window = new StoreLoginWindow(profile);
        application.Run(window);
        return window.ResultCode;
    }

    public static Process StartProcess(string storeId)
    {
        var profile = GetRequiredProfile(storeId);
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                $"Tools for Steam could not open the {profile.Title} sign-in window.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        startInfo.ArgumentList.Add($"{LoginArgumentPrefix}{profile.StoreId}");
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException(
                   $"Tools for Steam could not start the {profile.Title} sign-in window.");
    }

    public static void ClearUserData(string storeId)
    {
        var profile = GetRequiredProfile(storeId);
        if (!Directory.Exists(profile.UserDataFolder))
        {
            return;
        }

        try
        {
            Directory.Delete(profile.UserDataFolder, recursive: true);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{profile.Title} browser data is still in use. Close the sign-in window and try again.",
                exception);
        }
    }

    private static StoreLoginProfile GetRequiredProfile(string storeId) =>
        TryGetProfile(storeId, out var profile)
            ? profile
            : throw new InvalidOperationException("This OmniLibrary store does not support managed web sign-in.");

    private static bool TryGetProfile(string? storeId, out StoreLoginProfile profile)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedStoreId == "xbox-game-pass")
        {
            var authorization = XboxLiveAuthService.CreateAuthorizationRequest();
            profile = new StoreLoginProfile(
                "xbox-game-pass",
                "Xbox",
                authorization.Url,
                "login.microsoftonline.com",
                "/common/oauth2/nativeclient",
                Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "xbox-webview2"),
                code =>
                {
                    using var client = new HttpClient
                    {
                        Timeout = TimeSpan.FromSeconds(60),
                    };
                    var session = XboxLiveAuthService.CompleteLoginAsync(
                            code,
                            authorization.CodeVerifier,
                            client,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    var settings = new StoreSyncSettingsStore(
                        Path.Combine(AppContext.BaseDirectory, "data", "store-sync.json"));
                    XboxLiveAuthService.PersistSession(settings, session);
                    return session.Gamertag;
                },
                authorization.State,
                XboxLiveAuthService.IsTrustedLoginUri);
            return true;
        }
        profile = normalizedStoreId switch
        {
            "epic-games" => new StoreLoginProfile(
                "epic-games",
                "Epic Games",
                UnifySteamService.BuildEpicLoginUrl(),
                "www.epicgames.com",
                "/id/api/redirect",
                Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "epic-webview2"),
                ManagedLegendaryHelper.Authenticate),
            "gog-galaxy" => new StoreLoginProfile(
                "gog-galaxy",
                "GOG",
                UnifySteamService.BuildGogLoginUrl(),
                "embed.gog.com",
                "/on_login_success",
                Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary", "gog-webview2"),
                ManagedGogDlHelper.Authenticate),
            _ => null!,
        };
        return profile is not null;
    }

    // Covers a first-time helper download (bounded separately) plus the
    // helper's own two-minute sign-in limit.
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromMinutes(6);

    private const int ShowWindowRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    /// <summary>
    /// Brings an already open sign-in window back in front. A second Sign In
    /// press used to do nothing when the window had fallen behind Big Picture.
    /// </summary>
    public static void TryActivate(Process process)
    {
        try
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(handle, ShowWindowRestore);
            SetForegroundWindow(handle);
        }
        catch
        {
            // Activation is a convenience; the window remains usable.
        }
    }

    private sealed class StoreLoginWindow : Window
    {
        private static readonly SolidColorBrush WindowBackground = new(WpfColor.FromRgb(12, 16, 22));
        private static readonly SolidColorBrush HeaderBackground = new(WpfColor.FromRgb(20, 25, 33));
        private static readonly SolidColorBrush MutedForeground = new(WpfColor.FromRgb(163, 174, 190));
        private static readonly SolidColorBrush AccentBackground = new(WpfColor.FromRgb(48, 111, 219));

        private readonly StoreLoginProfile _profile;
        private readonly WebView2 _webView = new();
        private readonly TextBlock _statusText = new();
        private readonly System.Windows.Controls.Button _closeButton = new();
        private bool _completing;
        private bool _successful;

        public StoreLoginWindow(StoreLoginProfile profile)
        {
            _profile = profile;
            Title = $"OmniLibrary - {profile.Title} Sign-In";
            Background = WindowBackground;
            Foreground = WpfBrushes.White;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = true;
            Content = BuildContent();
            KeyDown += OnKeyDown;
            Loaded += OnLoaded;
            Closed += (_, _) => _webView.Dispose();
        }

        public int ResultCode => _successful ? 0 : 1;

        private UIElement BuildContent()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid
            {
                Background = HeaderBackground,
                Height = 86,
                Margin = new Thickness(0),
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel
            {
                Margin = new Thickness(28, 14, 16, 12),
                VerticalAlignment = VerticalAlignment.Center,
            };
            textStack.Children.Add(new TextBlock
            {
                Text = $"{_profile.Title} Sign-In",
                FontSize = 25,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfBrushes.White,
            });
            _statusText.Text =
                $"Sign in on {_profile.Title}'s secure page. OmniLibrary never reads or stores your password.";
            _statusText.FontSize = 14;
            _statusText.Foreground = MutedForeground;
            _statusText.Margin = new Thickness(0, 5, 0, 0);
            textStack.Children.Add(_statusText);
            header.Children.Add(textStack);

            _closeButton.Content = "Cancel";
            _closeButton.FontSize = 16;
            _closeButton.FontWeight = FontWeights.SemiBold;
            _closeButton.Foreground = WpfBrushes.White;
            _closeButton.Background = AccentBackground;
            _closeButton.BorderThickness = new Thickness(0);
            _closeButton.Padding = new Thickness(26, 11, 26, 11);
            _closeButton.Margin = new Thickness(12, 18, 24, 18);
            _closeButton.MinWidth = 118;
            _closeButton.Click += (_, _) => Close();
            Grid.SetColumn(_closeButton, 1);
            header.Children.Add(_closeButton);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 12, 16, 22);
            Grid.SetRow(_webView, 1);
            root.Children.Add(_webView);
            return root;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_profile.UserDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _profile.UserDataFolder);
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.AreDevToolsEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;

                _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _webView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
                _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                _webView.CoreWebView2.Navigate(_profile.LoginUrl);
            }
            catch (Exception exception)
            {
                ShowFailure(
                    $"The secure {_profile.Title} sign-in window could not be loaded. " +
                    $"Make sure Microsoft Edge WebView2 Runtime is installed. {exception.Message}");
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                return;
            }

            if (IsRedirect(uri))
            {
                if (!string.IsNullOrWhiteSpace(_profile.ExpectedState) &&
                    !XboxLiveAuthService.ReadQueryValue(uri, "state").Equals(
                        _profile.ExpectedState,
                        StringComparison.Ordinal))
                {
                    e.Cancel = true;
                    ShowFailure("Xbox sign-in returned an invalid security state.");
                    return;
                }
                var providerError = XboxLiveAuthService.ReadQueryValue(
                    uri,
                    "error_description");
                if (!string.IsNullOrWhiteSpace(providerError))
                {
                    e.Cancel = true;
                    ShowFailure(providerError);
                    return;
                }
                var code = ExtractAuthorizationCode(uri.Query);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    e.Cancel = true;
                    _ = CompleteSignInAsync(code);
                }
                return;
            }

            if (_profile.IsTrustedNavigation is not null &&
                !_profile.IsTrustedNavigation(uri))
            {
                e.Cancel = true;
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_completing || !e.IsSuccess || _webView.Source is not { } source || !IsRedirect(source))
            {
                return;
            }

            try
            {
                var serializedText = await _webView.CoreWebView2.ExecuteScriptAsync(
                    "document.body ? document.body.innerText : ''");
                var text = JsonSerializer.Deserialize<string>(serializedText) ?? string.Empty;
                var code = ExtractAuthorizationCode(text);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    await CompleteSignInAsync(code);
                }
            }
            catch
            {
                // The response hook below handles redirects that do not expose DOM text.
            }
        }

        private async void OnWebResourceResponseReceived(
            object? sender,
            CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (_completing ||
                !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
                !IsRedirect(uri))
            {
                return;
            }

            try
            {
                using var content = await e.Response.GetContentAsync();
                using var reader = new StreamReader(content);
                var code = ExtractAuthorizationCode(await reader.ReadToEndAsync());
                if (!string.IsNullOrWhiteSpace(code))
                {
                    await Dispatcher.InvokeAsync(() => _ = CompleteSignInAsync(code));
                }
            }
            catch
            {
                // NavigationCompleted remains as the second extraction path.
            }
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                (_profile.IsTrustedNavigation is null ||
                 _profile.IsTrustedNavigation(uri)))
            {
                _webView.CoreWebView2.Navigate(uri.AbsoluteUri);
            }
        }

        private async Task CompleteSignInAsync(string authorizationCode)
        {
            if (_completing)
            {
                return;
            }

            _completing = true;
            _closeButton.IsEnabled = false;
            _statusText.Text = $"Sign-in confirmed. Connecting {_profile.Title} to OmniLibrary…";
            try
            {
                // Close and Escape are disabled while the account is being
                // connected. Bound that phase so a stalled helper download or
                // network call can never leave this full-screen, top-most
                // window stuck over Big Picture without a way out.
                await Task.Run(() => _profile.Authenticate(authorizationCode))
                    .WaitAsync(AuthenticationTimeout);
                if (!string.IsNullOrWhiteSpace(_profile.ExpectedState))
                {
                    _webView.CoreWebView2.CookieManager.DeleteAllCookies();
                }
                _successful = true;
                _statusText.Text =
                    $"{_profile.Title} is connected. Your library will now sync automatically.";
                await Task.Delay(650);
                Close();
            }
            catch (TimeoutException)
            {
                _completing = false;
                _closeButton.IsEnabled = true;
                ShowFailure(
                    $"Connecting {_profile.Title} took too long. Check the internet connection, close this window and sign in again.");
            }
            catch (Exception exception)
            {
                _completing = false;
                _closeButton.IsEnabled = true;
                ShowFailure(exception.Message);
            }
        }

        private void ShowFailure(string message)
        {
            _statusText.Text = $"Sign-in failed: {message}";
            _statusText.Foreground = WpfBrushes.OrangeRed;
            _closeButton.Content = "Close";
        }

        private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !_completing)
            {
                Close();
            }
        }

        private bool IsRedirect(Uri uri) =>
            uri.Host.Equals(_profile.RedirectHost, StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Equals(_profile.RedirectPath, StringComparison.OrdinalIgnoreCase);

        private static string ExtractAuthorizationCode(string value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            if (trimmed.StartsWith('?'))
            {
                foreach (var segment in trimmed[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = segment.Split('=', 2);
                    if (parts.Length == 2 &&
                        (parts[0].Equals("authorizationCode", StringComparison.OrdinalIgnoreCase) ||
                         parts[0].Equals("code", StringComparison.OrdinalIgnoreCase)))
                    {
                        return Uri.UnescapeDataString(parts[1]);
                    }
                }
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var propertyName in new[] { "authorizationCode", "code" })
                    {
                        if (document.RootElement.TryGetProperty(propertyName, out var node) &&
                            node.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(node.GetString()))
                        {
                            return node.GetString()!.Trim();
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"(?:authorizationCode|code)\s*[=:]\s*[""']?(?<code>[A-Za-z0-9._-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["code"].Value : string.Empty;
        }
    }

    private sealed record StoreLoginProfile(
        string StoreId,
        string Title,
        string LoginUrl,
        string RedirectHost,
        string RedirectPath,
        string UserDataFolder,
        Func<string, string> Authenticate,
        string ExpectedState = "",
        Func<Uri, bool>? IsTrustedNavigation = null);
}
