using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.Services;
using Steaming.Application.ViewModels;
using Steaming.Core.Configuration;

namespace Steaming.WinUI.Pages;

public sealed partial class ConnectionsPage : Page
{
    private MainViewModel? _vm;
    private PlatformSessionFlowService? _flows;
    private bool _suppressRaidToggle;
    private bool _suppressActiveToggle;

    private static readonly SolidColorBrush GreenBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush GrayBrush  = new(Colors.Gray);

    public ConnectionsPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm    = vm;
        _flows = App.Services?.GetService<PlatformSessionFlowService>();
        _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshAll();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(RefreshAll);

    private bool _bridgeSettingsLoaded;

    private void RefreshAll()
    {
        if (_vm == null) return;
        var savedAuth = _vm.GetSavedAuthState();

        TwitchDot.Fill         = _vm.TwitchConnected ? GreenBrush : GrayBrush;
        TwitchStatusLabel.Text = _vm.TwitchConnected ? $"Connected as {_vm.TwitchUsername}" : "Not connected";
        TwitchLoginBtn.Content = _vm.IsTwitchLoggedIn ? "Disconnect Twitch" : "Connect Twitch";

        var kickLoggedIn = _vm.IsKickLoggedIn || savedAuth.HasKickToken;
        var kickUsername = !string.IsNullOrWhiteSpace(_vm.KickUsername) ? _vm.KickUsername : savedAuth.KickUsername;
        KickDot.Fill         = (_vm.KickConnected || kickLoggedIn) ? GreenBrush : GrayBrush;
        KickStatusLabel.Text = kickLoggedIn ? $"Connected as {kickUsername}" : "Not connected";
        KickLoginBtn.Content = kickLoggedIn ? "Disconnect Kick" : "Connect Kick";

        var youTubeLoggedIn = _vm.IsYouTubeLoggedIn || savedAuth.HasYouTubeToken;
        var youTubeTitle = !string.IsNullOrWhiteSpace(_vm.YouTubeChannelTitle) ? _vm.YouTubeChannelTitle : savedAuth.YouTubeChannelTitle;
        if (string.IsNullOrWhiteSpace(youTubeTitle)) youTubeTitle = "YouTube";
        YouTubeDot.Fill         = _vm.YouTubeConnected ? GreenBrush : GrayBrush;
        YouTubeStatusLabel.Text = _vm.YouTubeConnected
            ? $"Live chat connected as {youTubeTitle}"
            : (youTubeLoggedIn ? $"Authorized as {youTubeTitle} (waiting for active live chat)" : "Not connected");
        YouTubeLoginBtn.Content = youTubeLoggedIn ? "Disconnect YouTube" : "Connect YouTube";

        _suppressActiveToggle = true;
        TwitchActiveToggle.IsOn  = _vm.TwitchActive;
        KickActiveToggle.IsOn    = _vm.KickActive;
        YouTubeActiveToggle.IsOn = _vm.YouTubeActive;
        _suppressActiveToggle = false;

        _suppressRaidToggle = true;
        KickRaidToggle.IsOn = _vm.KickRaidAlertsEnabled;
        _suppressRaidToggle = false;
        KickRaidStatusLabel.Text = _vm.KickRaidAlertsEnabled ? _vm.KickRaidStatus : "Off — uses Kick's unsupported browser/socket path for raids and follower totals.";

        BridgeStatusLabel.Text = _vm.KickBridgeSummary;

        if (!_bridgeSettingsLoaded) LoadBridgeSettings();

        ObsDot.Fill         = _vm.ObsConnected ? GreenBrush : GrayBrush;
        ObsStatusLabel.Text = _vm.ObsStatus;
        if (string.IsNullOrEmpty(ObsAddressBox.Text))
            ObsAddressBox.Text = _vm.IntegrationConfig.ObsAddress;
        if (ObsPasswordBox.Password.Length == 0)
            ObsPasswordBox.Password = _vm.IntegrationConfig.ObsPassword;
    }

    private void LoadBridgeSettings()
    {
        if (_vm == null) return;
        var cfg = _vm.GetKickBridgeConfig();
        BridgeEnabled.IsOn       = cfg.Enabled;
        BridgeHost.Text          = cfg.Host;
        BridgePort.Text          = cfg.Port.ToString();
        BridgeUseTls.IsOn        = cfg.UseTls;
        BridgePath.Text          = cfg.WebSocketPath;
        BridgeToken.Password     = cfg.ClientToken;
        BridgeAllowOutbound.IsOn = cfg.AllowOutboundChat;
        _bridgeSettingsLoaded    = true;
    }

    private async void BridgeSave_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (!int.TryParse(BridgePort.Text, out var port) || port < 1 || port > 65535)
        {
            var dlg = new ContentDialog
            {
                Title = "Kick Bridge", Content = "Port must be between 1 and 65535.",
                CloseButtonText = "OK", XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
            return;
        }
        _vm.SaveKickBridgeConfig(new Steaming.Core.Services.KickBridgeConfig
        {
            Enabled          = BridgeEnabled.IsOn,
            Host             = BridgeHost.Text.Trim(),
            Port             = port,
            UseTls           = BridgeUseTls.IsOn,
            WebSocketPath    = BridgePath.Text.Trim(),
            ClientToken      = BridgeToken.Password,
            AllowOutboundChat = BridgeAllowOutbound.IsOn,
        });
    }

    private async void TwitchLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (_vm.IsTwitchLoggedIn)
        {
            await _vm.LogoutTwitchAsync();
            var es = App.Services?.GetService<Steaming.Core.Platforms.TwitchEventSubClient>();
            if (es != null) await es.DisconnectAsync();
            // Wipe the WebView2 session cookies too, or the next "Connect Twitch"
            // silently re-authenticates the same account without a login prompt.
            await LoginWindow.ClearPlatformCookiesAsync(App.WebViewHost, "Twitch");
            RefreshAll();
            return;
        }

        if (_flows == null) return;
        var request  = _flows.CreateTwitchLoginRequest();
        var loginWin = new LoginWindow("Login with Twitch", request.AuthUrl, request.RedirectUri, isFragment: true, profileName: "Twitch");
        loginWin.Activate();
        var result = await loginWin.WaitForResultAsync();

        if (result.Token != null)
        {
            var channelText = "";
            var loginResult = await _vm.CompleteTwitchLoginAsync(result.Token, request.ClientId, channelText);
            if (!string.IsNullOrEmpty(loginResult.Username))
            {
                // Kick off post-connect startup services inline
                var svc = App.Services;
                if (svc != null)
                    _ = Task.Run(async () => await PostTwitchConnectAsync(svc, loginResult.Username));
            }
        }
    }

    private async Task PostTwitchConnectAsync(IServiceProvider svc, string username)
    {
        // Full post-connect init matching App.xaml.cs startup flow
        var twitch   = svc.GetRequiredService<Steaming.Core.Platforms.TwitchAdapter>();
        var tokens   = svc.GetRequiredService<Steaming.Core.Auth.TokenStore>();
        var auth     = svc.GetRequiredService<PlatformAuthConfig>();
        var mod      = svc.GetRequiredService<Steaming.Core.Services.ModerationService>();
        var overlay  = svc.GetRequiredService<Steaming.Core.Services.OverlayDispatcher>();
        var vm       = svc.GetRequiredService<MainViewModel>();

        var token    = tokens.Credentials.TwitchAccessToken ?? "";
        var clientId = auth.TwitchClientId;
        var userId   = await twitch.FetchUserIdAsync(token, clientId, username) ?? tokens.Credentials.TwitchUserId;
        if (string.IsNullOrEmpty(userId)) return;

        tokens.Credentials.TwitchUserId = userId; tokens.Save();
        mod.Configure(token, clientId, userId);
        svc.GetRequiredService<Steaming.Core.Services.StreamManagementService>().Configure(token, clientId, userId);

        var viewers = svc.GetRequiredService<Steaming.Core.Services.ViewerListService>();
        viewers.Configure(token, clientId, userId); viewers.Start();

        try { await twitch.InitializeServicesAsync(clientId, userId); }
        catch { }

        try { await svc.GetRequiredService<Steaming.Core.Platforms.TwitchEventSubClient>().ConnectAsync(token, clientId, userId); }
        catch { }

        var streamData = svc.GetRequiredService<Steaming.Core.Services.StreamDataService>();
        streamData.Start(token, clientId, userId, username);
        overlay.StreamData = streamData;
    }

    private async void KickLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var savedAuth = _vm.GetSavedAuthState();
        if (_vm.IsKickLoggedIn || savedAuth.HasKickToken)
        {
            await _vm.LogoutKickAsync();
            await LoginWindow.ClearPlatformCookiesAsync(App.WebViewHost, "Kick");
            RefreshAll();
            return;
        }

        if (_flows == null) return;
        var request = _flows.CreateKickLoginRequest();
        if (!request.IsEnabled)
        {
            var dlg = new ContentDialog
            {
                Title = "Kick Login",
                Content = request.DisabledMessage ?? "Kick login is currently disabled.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
            return;
        }

        var auth     = App.Services?.GetRequiredService<PlatformAuthConfig>();
        var loginWin = new LoginWindow("Login with Kick", request.AuthUrl!, request.RedirectUri!, isFragment: false, profileName: "Kick");
        loginWin.Activate();
        var result = await loginWin.WaitForResultAsync();

        if (result.Code != null && auth != null)
        {
            var loginResult = await _vm.CompleteKickLoginAsync(
                result.Code, request.CodeVerifier!, auth.KickClientId, auth.KickClientSecret, auth.RedirectUri);

            RefreshAll();
            if (loginResult.Success)
            {
                savedAuth = _vm.GetSavedAuthState();
                var username = string.IsNullOrWhiteSpace(savedAuth.KickUsername)
                    ? (string.IsNullOrWhiteSpace(loginResult.Username) ? "Kick" : loginResult.Username)
                    : savedAuth.KickUsername;

                _vm.SetKickLoggedIn(username);
                _vm.UpdateServiceStatus(
                    "kick-api",
                    "Healthy",
                    "Kick API session available",
                    "Kick viewer/sub count polling can use the stored OAuth session.");

                KickStatusLabel.Text = $"Connected as {username}";
                KickLoginBtn.Content = "Disconnect Kick";
                KickDot.Fill = GreenBrush;
                RefreshAll();
            }
            else if (!string.IsNullOrWhiteSpace(loginResult.ErrorMessage))
            {
                var dlg = new ContentDialog
                {
                    Title = "Kick Login Failed", Content = loginResult.ErrorMessage,
                    CloseButtonText = "OK", XamlRoot = XamlRoot,
                };
                await dlg.ShowAsync();
            }
        }
    }

    private void KickRaidToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressRaidToggle || _vm == null) return;
        _vm.SetKickRaidAlertsEnabled(KickRaidToggle.IsOn);
        KickRaidStatusLabel.Text = KickRaidToggle.IsOn ? _vm.KickRaidStatus : "Off — uses Kick's unsupported browser/socket path for raids and follower totals.";
    }

    private void TwitchActive_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressActiveToggle || _vm == null) return;
        _vm.SetPlatformActive(Steaming.Core.Models.Platform.Twitch, TwitchActiveToggle.IsOn);
    }

    private void KickActive_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressActiveToggle || _vm == null) return;
        _vm.SetPlatformActive(Steaming.Core.Models.Platform.Kick, KickActiveToggle.IsOn);
    }

    private void YouTubeActive_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressActiveToggle || _vm == null) return;
        _vm.SetPlatformActive(Steaming.Core.Models.Platform.YouTube, YouTubeActiveToggle.IsOn);
    }

    private async void YouTubeLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _flows == null) return;

        var savedAuth = _vm.GetSavedAuthState();
        if (_vm.IsYouTubeLoggedIn || savedAuth.HasYouTubeToken)
        {
            await _vm.LogoutYouTubeAsync();
            await LoginWindow.ClearPlatformCookiesAsync(App.WebViewHost, "YouTube");
            RefreshAll();
            return;
        }

        var auth = App.Services?.GetRequiredService<PlatformAuthConfig>();
        if (auth == null || string.IsNullOrWhiteSpace(auth.YouTubeClientId))
        {
            var dlg = new ContentDialog
            {
                Title = "YouTube Login",
                Content = "YouTube client ID is not configured in PlatformAuthConfig.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
            return;
        }

        var request = _flows.CreateYouTubeLoginRequest();
        var loginWin = new LoginWindow("Login with YouTube", request.AuthUrl, request.RedirectUri, isFragment: false, profileName: "YouTube");
        loginWin.Activate();
        var result = await loginWin.WaitForResultAsync();
        if (string.IsNullOrWhiteSpace(result.Code)) return;

        var loginResult = await _vm.CompleteYouTubeLoginAsync(
            result.Code,
            request.CodeVerifier,
            auth.YouTubeClientId,
            auth.YouTubeClientSecret,
            auth.RedirectUri);

        RefreshAll();
        if (!loginResult.Success && !string.IsNullOrWhiteSpace(loginResult.ErrorMessage))
        {
            var dlg = new ContentDialog
            {
                Title = "YouTube Login Failed",
                Content = loginResult.ErrorMessage,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }

    private async void BridgeConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) await _vm.ConnectKickBridgeAsync();
    }

    private async void BridgeDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) await _vm.DisconnectKickBridgeAsync();
    }

    private async void ObsConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var address  = ObsAddressBox.Text.Trim();
        var password = ObsPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            await _vm.ConnectObsAsync(address, password);
        }
        catch (Exception ex)
        {
            ObsStatusLabel.Text = "Connection failed";
            var dlg = new ContentDialog
            {
                Title = "OBS WebSocket",
                Content = $"Connection failed: {ex.Message}\n\nIs OBS running with the WebSocket server enabled (Tools → WebSocket Server Settings)?",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }
}
