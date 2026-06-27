using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NAudio.Wave;
using Steaming.Application.Services;
using Steaming.Application.Services.Tts;
using Steaming.Application.ViewModels;
using Steaming.Core;
using Steaming.Core.Auth;
using Steaming.Core.Configuration;
using Steaming.Core.Ipc;
using Steaming.Core.Models;
using Steaming.Core.Platforms;
using Steaming.Core.Services;
using Steaming.Data;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Steaming.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private IHost? _host;
    private MainWindow? _window;
    private SplashWindow? _splash;
    private DispatcherQueue? _uiQueue;

    private static string GetYouTubeStatusState(bool connected, string summary)
        => connected ? "Healthy" : summary switch
        {
            "Waiting for YouTube login" => "Pending",
            "YouTube authorized" => "Pending",
            _ => "Error",
        };

    public static nint MainWindowHandle { get; private set; }
    public static IServiceProvider? Services { get; private set; }

    // The hidden 0-size host panel used for off-screen WebView2 work (Kick chatroom-id resolve and
    // logout cookie clearing). Set once the main window exists.
    public static Microsoft.UI.Xaml.Controls.Panel? WebViewHost { get; private set; }

    public App()
    {
        RedirectWebView2UserData();
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
    }

    // WebView2's default user-data folder is "<exe>.WebView2\" NEXT TO THE EXE. When the app is
    // installed under C:\Program Files a standard user cannot write there, so CoreWebView2 fails to
    // start with "We couldn't create the data directory". Redirect the whole process's WebView2 store
    // to a writable per-user location BEFORE any WebView2 is created (login windows + Kick resolver all
    // honour this single env var, so they keep sharing one cookie store as before).
    private static void RedirectWebView2UserData()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Steaming", "WebView2");
            Directory.CreateDirectory(folder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", folder);
        }
        catch { /* fall back to default folder; nothing else we can safely do this early */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        var dispatcherService = new WinUIDispatcherService(_uiQueue);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));

                services.AddSingleton<IDispatcherService>(dispatcherService);

                services.AddSingleton<EventBus>();
                services.AddSingleton<PluginPipeServer>();
                services.AddSingleton<TwitchAdapter>();
                services.AddSingleton<KickAdapter>();
                services.AddSingleton<YouTubeLiveChatService>();
                services.AddSingleton<KickRaidListener>();
                services.AddSingleton<IKickBridgeClient, RemoteKickBridgeClient>();
                services.AddSingleton<TwitchEventSubClient>();

                services.AddSingleton(_ => AppSettings.Load());
                services.AddSingleton<PlatformAuthConfig>();
                services.AddSingleton<TokenStore>();

                services.AddSingleton<OverlayDispatcher>();
                services.AddSingleton<SoundDispatcher>();

                services.AddSingleton<StreamDataService>();
                services.AddSingleton<ObsWebSocketService>();
                services.AddSingleton<ModerationService>();
                services.AddSingleton<StreamManagementService>();
                services.AddSingleton<ViewerListService>();
                services.AddSingleton<ChatbotService>();
                services.AddSingleton<ActivityRepository>();
                services.AddSingleton<Steaming.Data.AnalyticsRepository>();
                services.AddSingleton<AnalyticsCollectorService>();
                services.AddSingleton<IntegrationConfigService>();
                services.AddSingleton<PlatformCredentialService>();
                services.AddSingleton<PlatformSessionFlowService>();

                // TTS backends — WinRT (default) + optional Kokoro ONNX. Kokoro runs fully in-process;
                // its assets are auto-downloaded by KokoroAssetService into app data on first enable.
                services.AddSingleton(sp =>
                {
                    var s = sp.GetRequiredService<AppSettings>();
                    return new KokoroAssetService(() => s.KokoroModelVariant);
                });
                services.AddSingleton<IPhonemizer>(sp =>
                    new EspeakNgPhonemizer(sp.GetRequiredService<KokoroAssetService>()));
                services.AddSingleton<WinRtTtsBackend>();
                services.AddSingleton(sp =>
                    new KokoroTtsBackend(sp.GetRequiredService<KokoroAssetService>(),
                                         sp.GetRequiredService<IPhonemizer>()));
                services.AddSingleton<ChatTtsService>();

                // ── Music player ──────────────────────────────────────────────
                services.AddSingleton<MusicLibraryService>();
                services.AddSingleton<MusicPlayerService>();
                services.AddSingleton<MusicOverlayDispatcher>();
                services.AddSingleton<Steaming.WinUI.Services.MediaTransportControlsService>();
                services.AddSingleton<MusicViewModel>();

                services.AddSingleton<MainViewModel>();

                // Avatar / VTuber services
                services.AddSingleton<MicCaptureService>();
                services.AddSingleton<NdiSendService>();
                services.AddSingleton<CameraCaptureService>();
                services.AddSingleton<FaceTrackingDiagnosticsService>();
                services.AddSingleton<ReplayFrameService>();
                services.AddSingleton<FaceTrackingPersistenceService>();
                services.AddSingleton<FaceRetargetService>();
                services.AddSingleton<FaceTrackingService>();
                services.AddSingleton<AvatarRenderService>();
                services.AddSingleton<AvatarViewModel>();
            })
            .Build();

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            DebugLogFile.Append($"[WinUIApp] Unobserved task exception: {e.Exception}");
            ShowFatalError(e.Exception);
        };

        // Show splash before any async work — replaces the old in-window overlay
        _splash = new SplashWindow();
        _splash.Activate();

        _ = StartCoreServicesAsync();
    }

    private void ShowFatalError(Exception ex)
    {
        DebugLogFile.Append($"[WinUIApp] Fatal exception: {ex}");
        if (Debugger.IsAttached)
        {
            Debugger.Break();
            return;
        }

        var msg = ex.InnerException?.Message ?? ex.Message;
        _uiQueue?.TryEnqueue(async () =>
        {
            var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title           = "Streaming — startup error",
                Content         = msg,
                CloseButtonText = "Close"
            };
            if (_window != null)
                dlg.XamlRoot = _window.Content.XamlRoot;
            await dlg.ShowAsync();
        });
        System.Diagnostics.Debug.WriteLine($"[FATAL] {ex}");
    }

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        DebugLogFile.Append($"[WinUIApp] UI unhandled exception: {e.Exception}");
        if (Debugger.IsAttached)
        {
            Debugger.Break();
            return;
        }

        ShowFatalError(e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            DebugLogFile.Append($"[WinUIApp] AppDomain unhandled exception: {ex}");
            if (Debugger.IsAttached)
                Debugger.Break();
        }
    }

    private async Task StartCoreServicesAsync()
    {
        try
        {
            if (_host == null) return;
            UpdateSplash("Starting services...");
            await _host.StartAsync();

            Services = _host.Services;
            var svc  = _host.Services;
            var tokens   = svc.GetRequiredService<TokenStore>();
            var settings = svc.GetRequiredService<AppSettings>();
            var vm       = svc.GetRequiredService<MainViewModel>();
            var credentials = svc.GetRequiredService<PlatformCredentialService>();

            tokens.Load();

            // ── EventBus subscriptions ────────────────────────────────────────
            var bus = svc.GetRequiredService<EventBus>();
            // Per-platform "active" gate: an inactive platform's inbound chat/alerts/activity are
            // dropped here, the single chokepoint, so toggling a platform off (without logging out)
            // makes it fully dark. Reads the live AppSettings flags via the view model.
            bus.PlatformFilter = vm.ShouldDispatchEvent;
            bus.Subscribe(vm.OnEvent);
            var youtube = svc.GetRequiredService<YouTubeLiveChatService>();

            var activityRepo = svc.GetRequiredService<ActivityRepository>();
            bus.Subscribe(evt =>
            {
                // System events (the StreamDataUpdated aggregate) and chat are not activity-feed items.
                if (evt.Type == EventType.Chat || evt.Platform == Platform.System) return Task.CompletedTask;
                var desc = evt.Type switch
                {
                    EventType.Follow                 => "followed",
                    EventType.Subscribe              => "subscribed",
                    EventType.GiftSubscribe          => "gifted a sub",
                    EventType.Bits                   => evt.Platform == Platform.YouTube
                        ? $"sent {(evt.Data.TryGetValue("amountDisplay", out var ad) ? ad?.ToString() : null) ?? "a Super Chat"}"
                        : "cheered bits",
                    EventType.Raid                   => "raided",
                    EventType.ChannelPointRedemption => $"redeemed {(evt.Data.TryGetValue("rewardTitle", out var rt) ? rt?.ToString() : null) ?? "a reward"}",
                    EventType.KicksGifted            => $"gifted {(evt.Data.TryGetValue("amount", out var ka) ? ka?.ToString() : null) ?? "0"} Kicks",
                    _                                => evt.Type.ToString(),
                };
                try { activityRepo.Insert(evt.Timestamp, evt.Platform.ToString(), evt.Type.ToString(), evt.User.DisplayName, desc); }
                catch { }
                return Task.CompletedTask;
            });

            // ── Kick moderation configuration ─────────────────────────────────
            var moderation = svc.GetRequiredService<ModerationService>();
            if (!string.IsNullOrWhiteSpace(tokens.Credentials.KickAccessToken) &&
                tokens.Credentials.KickChatroomId > 0)
            {
                moderation.ConfigureKick(tokens.Credentials.KickAccessToken, tokens.Credentials.KickChatroomId);
            }

            if (!string.IsNullOrWhiteSpace(tokens.Credentials.KickAccessToken))
            {
                UpdateSplash("Resolving Kick account...");
                var kickUsername = tokens.Credentials.KickUsername;
                if (string.IsNullOrWhiteSpace(kickUsername) || tokens.Credentials.KickChatroomId <= 0)
                {
                    var (resolvedId, resolvedUsername, _) = await credentials.FetchKickUserInfoAsync(tokens.Credentials.KickAccessToken);
                    if (resolvedId > 0 && !string.IsNullOrWhiteSpace(resolvedUsername))
                    {
                        credentials.SaveKickIdentity(resolvedId, resolvedUsername);
                        kickUsername = resolvedUsername;
                    }
                }

                _uiQueue!.TryEnqueue(() =>
                {
                    vm.SetKickLoggedIn(string.IsNullOrWhiteSpace(kickUsername) ? "Kick" : kickUsername);
                    vm.UpdateServiceStatus(
                        "kick-api",
                        "Healthy",
                        "Kick API session available",
                        "Kick viewer/sub count polling can use the stored OAuth session.");
                });
            }

            // ── OBS pipe ─────────────────────────────────────────────────────
            UpdateSplash("Starting OBS connection...");
            var pipe    = svc.GetRequiredService<PluginPipeServer>();
            var overlay = svc.GetRequiredService<OverlayDispatcher>();

            var musicOverlay = svc.GetRequiredService<MusicOverlayDispatcher>();
            musicOverlay.Start();
            // Ensure the MusicViewModel singleton is constructed so it begins driving playback
            // state even before the Music page is first navigated to.
            _ = svc.GetRequiredService<MusicViewModel>();

            pipe.Connected += async () =>
            {
                await overlay.SendGoalNamesAsync();
                await overlay.SendAllLabelsAsync();
                await overlay.SendAllGoalsAsync();
                await overlay.SendEmojiRainSettingsAsync();
                await overlay.SendAllChatSettingsAsync();
                musicOverlay.SendAllAsync();
            };

            pipe.MessageReceived += (type, payload) =>
            {
                if (type != PipeMessageType.ChatSourceList) return;
                var names = ParseChatSourceNames(payload);
                _uiQueue!.TryEnqueue(() => vm.UpdatePluginChatSources(names));
            };

            pipe.Start();
            overlay.Start();

            // ── Sound ─────────────────────────────────────────────────────────
            // Plays on the output device selected in Settings (SoundAudioDeviceId)
            var sound = svc.GetRequiredService<SoundDispatcher>();
            var appSoundPlayer = new AppSoundPlayer(settings);
            sound.PlayFile = appSoundPlayer.Play;
            sound.Start();

            // ── Chat TTS ──────────────────────────────────────────────────────
            var chatTts = svc.GetRequiredService<ChatTtsService>();
            chatTts.VoiceNameProvider = () => settings.TtsVoiceNameWinUI;
            chatTts.Start();

            // ── Analytics ────────────────────────────────────────────────────
            svc.GetRequiredService<AnalyticsCollectorService>().Start();

            // ── Chatbot ───────────────────────────────────────────────────────
            UpdateSplash("Starting chatbot...");
            var twitch    = svc.GetRequiredService<TwitchAdapter>();
            var kick      = svc.GetRequiredService<KickAdapter>();
            var kickBridge = svc.GetRequiredService<IKickBridgeClient>();

            var chatbot = svc.GetRequiredService<ChatbotService>();
            // Route bot output (commands, shouts, timers, live announce) through the SAME path as a manual
            // dashboard send. That path live-gates "All" to currently-live platforms, renders ONE merged
            // chat line (combined platform badges in-app and in OBS), and suppresses the real per-platform
            // bounces so an "All" announcement shows as a single [T][K][Y] line instead of one line per
            // platform. Single-platform targets render a single badge.
            chatbot.SendMessage = async (msg, target) => await vm.SendChatAsync(msg, target);
            chatbot.TimeoutUser    = async (uid, secs) => { if (moderation.IsConfigured) await moderation.TimeoutAsync(uid, secs); };
            chatbot.DeleteMessage  = async msgId       => { if (moderation.IsConfigured) await moderation.DeleteMessageAsync(msgId); };
            chatbot.PlaySound      = (path, vol) => sound.PlayFile?.Invoke(path, vol);
            var overlayDispatcher  = svc.GetRequiredService<OverlayDispatcher>();
            chatbot.TriggerCustomAlert = async (name, user, arg) =>
            {
                if (settings.CustomAlerts.TryGetValue(name, out var cfg))
                    await overlayDispatcher.SendCustomAlertAsync(cfg, user.DisplayName, arg);
            };
            chatbot.Load();
            chatbot.Start();

            var streamData = svc.GetRequiredService<StreamDataService>();
            chatbot.GetCurrentGame   = () => streamData.StreamCategory;
            chatbot.GetCurrentTitle  = () => streamData.StreamTitle;
            chatbot.GetCurrentUptime = () => streamData.GetUptime();
            chatbot.IsLive           = () => streamData.IsLive;
            vm.SyncChatbotCollections();
            vm.LoadCustomAlerts();
            streamData.KickAuthStatusChanged += (ok, summary, details) =>
                _uiQueue!.TryEnqueue(() =>
                {
                    if (ok) vm.HandleKickApiAuthRestored(summary, details);
                    else vm.HandleKickApiAuthFailure(summary, details);
                });
            streamData.TwitchAuthStatusChanged += (ok, summary, details) =>
                _uiQueue!.TryEnqueue(() =>
                {
                    if (ok) vm.HandleTwitchAuthRestored(summary, details);
                    else vm.HandleTwitchAuthFailure(summary, details);
                });
            // The poll refresh stores a NEW token but the bridge still holds the old one —
            // re-send the bootstrap or bridge sends/webhooks start 401ing while UI stays green.
            streamData.KickTokenRefreshed += () => _ = Task.Run(async () =>
            {
                if (kickBridge.IsConnected)
                    await vm.BootstrapKickBridgeFromStoredLoginAsync(refreshToken: false);
            });
            streamData.Start();
            overlay.StreamData = streamData;
            youtube.StatusChanged += (ok, summary, details) =>
                _uiQueue!.TryEnqueue(() =>
                {
                    vm.UpdateServiceStatus("youtube-chat", GetYouTubeStatusState(ok, summary), summary, details);
                    if (ok)
                        vm.SetYouTubeLoggedIn(string.IsNullOrWhiteSpace(youtube.ChannelTitle) ? "YouTube" : youtube.ChannelTitle);
                });
            await vm.SyncYouTubeChatMonitoringAsync();

            // ── Twitch auto-connect ───────────────────────────────────────────
            if (!string.IsNullOrEmpty(tokens.Credentials.TwitchAccessToken) &&
                !string.IsNullOrEmpty(tokens.Credentials.TwitchUsername))
            {
                var username = tokens.Credentials.TwitchUsername;
                var token    = tokens.Credentials.TwitchAccessToken;
                var channel  = tokens.Credentials.TwitchChannel ?? username;
                var clientId = svc.GetRequiredService<PlatformAuthConfig>().TwitchClientId;

                UpdateSplash($"Connecting to Twitch as {username}...");
                await twitch.ConnectAsync(username, $"oauth:{token}", channel);
                _uiQueue!.TryEnqueue(() => vm.SetTwitchLoggedIn(username));

                // Badges, EventSub, stream data polling, moderation, viewers
                var userId = await twitch.FetchUserIdAsync(token, clientId, username)
                             ?? tokens.Credentials.TwitchUserId;
                if (!string.IsNullOrEmpty(userId))
                {
                    tokens.Credentials.TwitchUserId = userId;
                    tokens.Save();

                    moderation.Configure(token, clientId, userId);
                    svc.GetRequiredService<StreamManagementService>().Configure(token, clientId, userId);

                    var viewers = svc.GetRequiredService<ViewerListService>();
                    viewers.Configure(token, clientId, userId);
                    viewers.Start();

                    try
                    {
                        await twitch.InitializeServicesAsync(clientId, userId);
                        _uiQueue!.TryEnqueue(() => vm.UpdateServiceStatus("twitch-badges", "Healthy", "Badge metadata loaded", "Twitch badge images are ready for overlays."));
                    }
                    catch (Exception ex)
                    {
                        _uiQueue!.TryEnqueue(() => vm.UpdateServiceStatus("twitch-badges", "Error", "Badge init failed", ex.Message));
                    }

                    try
                    {
                        await svc.GetRequiredService<TwitchEventSubClient>().ConnectAsync(token, clientId, userId);
                        _uiQueue!.TryEnqueue(() => vm.UpdateServiceStatus("twitch-eventsub", "Healthy", "EventSub connected", "Twitch event notifications are active."));
                    }
                    catch (Exception ex)
                    {
                        _uiQueue!.TryEnqueue(() => vm.UpdateServiceStatus("twitch-eventsub", "Error", "EventSub connection failed", ex.Message));
                    }

                    streamData.Start(token, clientId, userId, username);
                    overlay.StreamData = streamData;
                }
            }

            // ── Twitch bot auto-connect ───────────────────────────────────────
            if (!string.IsNullOrEmpty(tokens.Credentials.BotTwitchAccessToken) &&
                !string.IsNullOrEmpty(tokens.Credentials.BotTwitchUsername))
            {
                try
                {
                    await twitch.ConnectBotAsync(tokens.Credentials.BotTwitchUsername,
                        $"oauth:{tokens.Credentials.BotTwitchAccessToken}");
                    _uiQueue!.TryEnqueue(() =>
                    {
                        vm.IsTwitchBotConnected = true;
                        vm.TwitchBotUsername    = tokens.Credentials.BotTwitchUsername!;
                    });
                }
                catch (Exception ex)
                {
                    _uiQueue!.TryEnqueue(() => vm.UpdateServiceStatus("twitch-bot", "Error", "Bot connect failed", ex.Message));
                }
            }

            // ── Kick bot auto-connect ─────────────────────────────────────────
            if (!string.IsNullOrEmpty(tokens.Credentials.BotKickAccessToken))
            {
                kick.SetBotToken(tokens.Credentials.BotKickAccessToken, tokens.Credentials.BotKickUsername);
                _uiQueue!.TryEnqueue(() =>
                {
                    vm.IsKickBotConnected = true;
                    vm.KickBotUsername    = tokens.Credentials.BotKickUsername ?? "";
                });
            }

            if (!string.IsNullOrWhiteSpace(tokens.Credentials.YouTubeAccessToken))
            {
                var title = string.IsNullOrWhiteSpace(tokens.Credentials.YouTubeChannelTitle)
                    ? "YouTube"
                    : tokens.Credentials.YouTubeChannelTitle;
                _uiQueue!.TryEnqueue(() => vm.SetYouTubeLoggedIn(title));
            }

            // ── Kick bridge ───────────────────────────────────────────────────
            if (settings.KickBridge.Enabled && !string.IsNullOrWhiteSpace(settings.KickBridge.Host))
            {
                await kickBridge.ConnectAsync();
                await vm.BootstrapKickBridgeFromStoredLoginAsync();
            }

            // ── Kick raid alerts (opt-in; raid-only Pusher listener — Kick has no official raid API) ──
            // Started after the main window exists (the chatroom-id lookup needs its hidden WebView2).
            var kickRaidListener = svc.GetRequiredService<KickRaidListener>();
            vm.AttachKickRaidListener(kickRaidListener);
            kickRaidListener.FollowerCountResolved += count => _ = Task.Run(() => streamData.SetKickFollowerCountFromUnsupportedAsync(count));
            kickRaidListener.FollowerObserved += () => _ = Task.Run(() => streamData.IncrementKickFollowerCountFromUnsupportedAsync());

            // ── OBS WebSocket auto-connect (gated by the auto-connect/reconnect toggle) ──
            // Fire-and-forget so a non-running OBS never delays startup; the reconnect loop
            // (already armed by the toggle) keeps trying in the background.
            _ = vm.TryAutoConnectObsAsync();

            // ── Startup complete — close splash, show main window ────────────
            UpdateSplash("Almost ready...");
            _uiQueue!.TryEnqueue(() =>
            {
                // Activate main window BEFORE closing splash — WinUI exits when last window closes.
                _window = new MainWindow(vm);
                WebViewHost = _window.WebViewHost;
                _window.Closed += (_, _) =>
                {
                    AlertEditorWindow.CloseAllOpenEditors();
                    try { _host?.Services.GetRequiredService<AvatarViewModel>().PersistState(); } catch { }
                    try { chatTts.Dispose(); } catch { }
                    _ = ShutdownHostAsync();
                };
                _window.Activate();
                MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                // Hook the keyboard media keys + Windows media flyout to the music player.
                try { _host?.Services.GetRequiredService<Steaming.WinUI.Services.MediaTransportControlsService>().Initialize(MainWindowHandle); } catch { }

                // Kick raid alerts: give the listener a WebView2-based chatroom-id resolver (passes
                // Cloudflare, which 403s HttpClient), then start it if enabled + logged into Kick.
                try
                {
                    kickRaidListener.WebChannelResolver =
                        new Steaming.WinUI.Services.KickWebChannelResolver(_uiQueue!, _window.WebViewHost).ResolveAsync;
                    if (settings.KickRaidAlertsEnabled && !string.IsNullOrWhiteSpace(tokens.Credentials.KickUsername))
                        kickRaidListener.Start(tokens.Credentials.KickUsername!);
                }
                catch (Exception ex) { DebugLogFile.Append($"[KickRaid] startup wire failed: {ex.Message}"); }
                _window.ShowPendingAuthReconnectPrompts();
                _splash?.Close();
                _splash = null;
                vm.RefreshStatusBar();
            });
        }
        catch (Exception ex)
        {
            _uiQueue?.TryEnqueue(() => { _splash?.Close(); _splash = null; });
            ShowFatalError(ex);
        }
    }

    private void UpdateSplash(string text) => _splash?.SetStatus(text);

    private async Task ShutdownHostAsync()
    {
        try { if (_host != null) await _host.StopAsync(TimeSpan.FromSeconds(4)); } catch { }
        Environment.Exit(0);
    }

    private static List<string> ParseChatSourceNames(byte[] payload)
    {
        // Wire format: [2]count, each: [2+N]name [4]width [4]height (int32 LE)
        var names = new List<string>();
        try
        {
            using var ms     = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
            if (ms.Length < 2) return names;
            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++)
            {
                if (ms.Position + 2 > ms.Length) break;
                var len = reader.ReadUInt16();
                if (ms.Position + len > ms.Length) break;
                var name = Encoding.UTF8.GetString(reader.ReadBytes(len));
                if (ms.Position + 8 > ms.Length) break;
                reader.ReadInt32(); // width
                reader.ReadInt32(); // height
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch { }
        return names;
    }

}
