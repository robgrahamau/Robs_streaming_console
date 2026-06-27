using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Steaming.Application.Services;
using Steaming.Core;
using Steaming.Core.Auth;
using Steaming.Core.Ipc;
using Steaming.Core.Models;
using Steaming.Core.Platforms;
using Steaming.Core.Services;
using Steaming.Data;

namespace Steaming.Application.ViewModels;

public class MainViewModel : ViewModelBase
{
    public sealed record SavedAuthState(
        string TwitchChannel,
        bool HasTwitchToken,
        string TwitchUsername,
        bool HasKickToken,
        string KickUsername,
        bool HasYouTubeToken,
        string YouTubeChannelTitle);

    public sealed record TwitchLoginResult(string Username, string Channel);

    /// <summary>Raised when, on stream start, the live channel title/category does not match what
    /// the user last set. The UI prompts the user and may call <see cref="ReapplyStreamMetadataAsync"/>.</summary>
    public sealed record StreamMetadataMismatch(
        string Detail,
        bool TitleMismatch,
        bool GameMismatch);

    public sealed record KickLoginResult(
        bool Success,
        string Username,
        int ChatroomId,
        string Diagnostics,
        string? ErrorMessage);

    public sealed record YouTubeLoginResult(
        bool Success,
        string ChannelId,
        string ChannelTitle,
        string? ErrorMessage);

    public sealed record AuthReconnectPrompt(
        string Platform,
        string Title,
        string Message);

    // ── Services (injected, no WPF) ──────────────────────────────────────────
    public readonly TwitchAdapter           Twitch;
    public readonly KickAdapter             Kick;
    public readonly YouTubeLiveChatService  YouTube;
    public readonly IKickBridgeClient       KickBridge;
    public readonly EventBus                Bus;
    public readonly PluginPipeServer        Pipe;
    public readonly TokenStore              Tokens;
    public readonly AppSettings             Settings;
    public readonly ObsWebSocketService     ObsWs;
    public readonly ModerationService       Mod;
    public readonly StreamManagementService Stream;
    public readonly ViewerListService       Viewers;
    public readonly ChatbotService          Chatbot;
    public readonly ActivityRepository      Activity;
    public readonly OverlayDispatcher       OverlayDispatcher;
    public readonly IntegrationConfigService IntegrationConfig;
    public readonly PlatformCredentialService Credentials;

    private readonly IDispatcherService _dispatcher;

    // ── Version ──────────────────────────────────────────────────────────────
    public string AppVersion => VersionInfo.DisplayVersion;

    // ── Navigation ───────────────────────────────────────────────────────────
    private string _activePanel = "Chat";
    public string ActivePanel
    {
        get => _activePanel;
        set => Set(ref _activePanel, value);
    }

    // ── Connection state ─────────────────────────────────────────────────────
    private bool _twitchConnected;
    public bool TwitchConnected { get => _twitchConnected; set => Set(ref _twitchConnected, value); }

    private string _twitchStatus = "Not connected";
    public string TwitchStatus { get => _twitchStatus; set => Set(ref _twitchStatus, value); }

    private string _twitchUsername = "";
    public string TwitchUsername { get => _twitchUsername; set => Set(ref _twitchUsername, value); }

    private bool _isTwitchLoggedIn;
    public bool IsTwitchLoggedIn { get => _isTwitchLoggedIn; set => Set(ref _isTwitchLoggedIn, value); }

    private string _twitchBadgeText = "";
    public string TwitchBadgeText { get => _twitchBadgeText; set => Set(ref _twitchBadgeText, value); }

    private bool _kickConnected;
    public bool KickConnected { get => _kickConnected; set => Set(ref _kickConnected, value); }

    private string _kickStatus = "Not connected";
    public string KickStatus { get => _kickStatus; set => Set(ref _kickStatus, value); }

    private string _kickUsername = "";
    public string KickUsername { get => _kickUsername; set => Set(ref _kickUsername, value); }

    private bool _isKickLoggedIn;
    public bool IsKickLoggedIn { get => _isKickLoggedIn; set => Set(ref _isKickLoggedIn, value); }

    private string _kickBadgeText = "";
    public string KickBadgeText { get => _kickBadgeText; set => Set(ref _kickBadgeText, value); }

    private string _kickBridgeSummary = "Not configured";
    public string KickBridgeSummary { get => _kickBridgeSummary; set => Set(ref _kickBridgeSummary, value); }

    private string _kickBridgeDetails = "";
    public string KickBridgeDetails { get => _kickBridgeDetails; set => Set(ref _kickBridgeDetails, value); }

    private bool _youtubeConnected;
    public bool YouTubeConnected { get => _youtubeConnected; set => Set(ref _youtubeConnected, value); }

    private string _youtubeStatus = "Not connected";
    public string YouTubeStatus { get => _youtubeStatus; set => Set(ref _youtubeStatus, value); }

    private string _youtubeChannelTitle = "";
    public string YouTubeChannelTitle { get => _youtubeChannelTitle; set => Set(ref _youtubeChannelTitle, value); }

    private bool _isYouTubeLoggedIn;
    public bool IsYouTubeLoggedIn { get => _isYouTubeLoggedIn; set => Set(ref _isYouTubeLoggedIn, value); }

    private string _youtubeBadgeText = "";
    public string YouTubeBadgeText { get => _youtubeBadgeText; set => Set(ref _youtubeBadgeText, value); }

    private bool _isTwitchBotConnected;
    public bool IsTwitchBotConnected { get => _isTwitchBotConnected; set => Set(ref _isTwitchBotConnected, value); }

    private string _twitchBotUsername = "";
    public string TwitchBotUsername { get => _twitchBotUsername; set => Set(ref _twitchBotUsername, value); }

    private bool _isKickBotConnected;
    public bool IsKickBotConnected { get => _isKickBotConnected; set => Set(ref _isKickBotConnected, value); }

    private string _kickBotUsername = "";
    public string KickBotUsername { get => _kickBotUsername; set => Set(ref _kickBotUsername, value); }

    public void SetTwitchLoggedIn(string username)
    {
        TwitchUsername   = username;
        IsTwitchLoggedIn = true;
        TwitchBadgeText  = $"Connected as {username}";
        TwitchStatus     = $"Twitch: #{Twitch.Channel}";
        TwitchConnected  = true;
    }

    public void SetTwitchLoggedOut()
    {
        TwitchUsername   = "";
        IsTwitchLoggedIn = false;
        TwitchBadgeText  = "";
        TwitchStatus     = "Not connected";
        TwitchConnected  = false;
    }

    public void SetKickLoggedIn(string username)
    {
        KickUsername   = username;
        IsKickLoggedIn = true;
        KickBadgeText  = $"Connected as {username}";
        KickStatus     = "Kick: Connected";
        KickConnected  = true;
    }

    public void SetKickLoggedOut()
    {
        KickUsername   = "";
        IsKickLoggedIn = false;
        KickBadgeText  = "";
        KickStatus     = "Kick: Not connected";
        KickConnected  = false;
    }

    public void SetYouTubeLoggedIn(string channelTitle)
    {
        YouTubeChannelTitle = channelTitle;
        IsYouTubeLoggedIn   = true;
        YouTubeBadgeText    = $"Authorized as {channelTitle}";
        YouTubeStatus       = "YouTube: Authorized";
        YouTubeConnected    = false;
    }

    public void SetYouTubeLoggedOut()
    {
        YouTubeChannelTitle = "";
        IsYouTubeLoggedIn   = false;
        YouTubeBadgeText    = "";
        YouTubeStatus       = "YouTube: Not connected";
        YouTubeConnected    = false;
    }

    private bool _obsConnected;
    public bool ObsConnected { get => _obsConnected; set => Set(ref _obsConnected, value); }

    private string _obsStatus = "Not connected";
    public string ObsStatus { get => _obsStatus; set => Set(ref _obsStatus, value); }

    private bool _obsStreaming;
    public bool ObsStreaming { get => _obsStreaming; set => Set(ref _obsStreaming, value); }

    private bool _obsAutoReconnect;
    public bool ObsAutoReconnect { get => _obsAutoReconnect; set => Set(ref _obsAutoReconnect, value); }

    // ── Stream health (per-destination) ───────────────────────────────────────
    private bool _warnOnUnhealthyTwitch;
    public bool WarnOnUnhealthyTwitch { get => _warnOnUnhealthyTwitch; set => Set(ref _warnOnUnhealthyTwitch, value); }

    private bool _warnOnUnhealthyKick;
    public bool WarnOnUnhealthyKick { get => _warnOnUnhealthyKick; set => Set(ref _warnOnUnhealthyKick, value); }

    private bool _streamHealthy = true;
    public bool StreamHealthy { get => _streamHealthy; set => Set(ref _streamHealthy, value); }

    private string _streamHealthText = "OBS: Offline · Twitch: Offline · Kick: Offline";
    public string StreamHealthText { get => _streamHealthText; set => Set(ref _streamHealthText, value); }

    private string _healthWarning = "";
    public string HealthWarning { get => _healthWarning; set => Set(ref _healthWarning, value); }

    public void SetWarnOnUnhealthyTwitch(bool enabled)
    {
        IntegrationConfig.SaveWarnOnUnhealthyTwitch(enabled);
        WarnOnUnhealthyTwitch = enabled;
        EvaluateStreamHealth();
    }

    public void SetWarnOnUnhealthyKick(bool enabled)
    {
        IntegrationConfig.SaveWarnOnUnhealthyKick(enabled);
        WarnOnUnhealthyKick = enabled;
        EvaluateStreamHealth();
    }

    // Edge-detection state — tracks ACTUAL health independent of the warn toggles, so enabling
    // a toggle later does not retro-fire a warning for a long-standing condition.
    private bool _prevTwitchHealthy = true;
    private bool _prevKickHealthy   = true;

    public void EvaluateStreamHealth()
    {
        bool twitchHooksOk = !ServiceStatuses.Any(s => s.Key.StartsWith("twitch-", StringComparison.Ordinal) && s.State == "Error");
        bool kickHooksOk   = !ServiceStatuses.Any(s => s.Key == "kick-bridge" && s.State == "Error");

        // A liveness mismatch is only meaningful while OBS is actually streaming.
        bool twitchLiveOk = !ObsStreaming || TwitchIsLive;
        bool kickLiveOk   = !ObsStreaming || KickIsLive;

        bool twitchHealthy = twitchHooksOk && twitchLiveOk;
        bool kickHealthy   = kickHooksOk && kickLiveOk;

        StreamHealthText =
            $"OBS: {(ObsStreaming ? "Live" : "Offline")} · " +
            $"Twitch: {(TwitchIsLive ? "Live" : "Offline")} · " +
            $"Kick: {(KickIsLive ? "Live" : "Offline")}";

        bool twitchProblem = WarnOnUnhealthyTwitch && !twitchHealthy;
        bool kickProblem   = WarnOnUnhealthyKick   && !kickHealthy;
        StreamHealthy = !twitchProblem && !kickProblem;

        // Warnings are edge-triggered (only on transition into unhealthy) and gated per destination.
        if (WarnOnUnhealthyTwitch && _prevTwitchHealthy && !twitchHealthy)
            RaiseHealthWarning("Twitch", twitchHooksOk, twitchLiveOk);
        if (WarnOnUnhealthyKick && _prevKickHealthy && !kickHealthy)
            RaiseHealthWarning("Kick", kickHooksOk, kickLiveOk);

        if (WarnOnUnhealthyTwitch && !_prevTwitchHealthy && twitchHealthy)
            NoteHealthRecovered("Twitch");
        if (WarnOnUnhealthyKick && !_prevKickHealthy && kickHealthy)
            NoteHealthRecovered("Kick");

        _prevTwitchHealthy = twitchHealthy;
        _prevKickHealthy   = kickHealthy;

        if (StreamHealthy) HealthWarning = "";
    }

    private void RaiseHealthWarning(string platform, bool hooksOk, bool liveOk)
    {
        string reason = !hooksOk
            ? $"{platform} event hooks/auth failed — a token may need a full re-login"
            : $"OBS is streaming but {platform} is not showing live — check ingest / stream key";
        HealthWarning = "⚠ " + reason;
        _dispatcher.Invoke(() => AddActivityLine($"{DateTime.Now:HH:mm:ss}  ⚠ {reason}"));
    }

    private void NoteHealthRecovered(string platform)
        => _dispatcher.Invoke(() => AddActivityLine($"{DateTime.Now:HH:mm:ss}  ✔ {platform} stream health recovered"));

    private void RequestAuthReconnectPrompt(string platform, string title, string message)
        => _dispatcher.Invoke(() =>
        {
            if (!_authReconnectPromptActive.Add(platform)) return;
            var prompt = new AuthReconnectPrompt(platform, title, message);
            _pendingAuthReconnectPrompts[platform] = prompt;
            AuthReconnectRequired?.Invoke(prompt);
        });

    public IReadOnlyList<AuthReconnectPrompt> GetPendingAuthReconnectPrompts()
        => _pendingAuthReconnectPrompts.Values.ToList();

    public void MarkAuthReconnectPromptShown(string platform)
        => _dispatcher.Invoke(() => _pendingAuthReconnectPrompts.Remove(platform));

    public void ClearAuthReconnectPrompt(string platform)
        => _dispatcher.Invoke(() =>
        {
            _pendingAuthReconnectPrompts.Remove(platform);
            _authReconnectPromptActive.Remove(platform);
        });

    public void HandleTwitchAuthFailure(string summary, string details)
    {
        UpdateServiceStatus("twitch-poll", "Error", summary, details);
        TwitchStatus = "Twitch: Auth failed";
        RequestAuthReconnectPrompt(
            "Twitch",
            "Reconnect Twitch",
            $"Unable to auth with Twitch. {details}\n\nPlease reconnect Twitch from the Connections page.");
    }

    public void HandleTwitchAuthRestored(string summary, string details)
    {
        UpdateServiceStatus("twitch-poll", "Healthy", summary, details);
        ClearAuthReconnectPrompt("Twitch");
    }

    public void HandleKickApiAuthFailure(string summary, string details)
    {
        UpdateServiceStatus("kick-api", "Error", summary, details);
        KickStatus = "Kick: Auth failed";
        RequestAuthReconnectPrompt(
            "Kick",
            "Reconnect Kick",
            $"Unable to auth with Kick. {details}\n\nPlease reconnect Kick from the Connections page.");
    }

    public void HandleKickApiAuthRestored(string summary, string details)
    {
        UpdateServiceStatus("kick-api", "Healthy", summary, details);
        ClearAuthReconnectPrompt("Kick");
    }

    // ── Stream-start metadata verification ────────────────────────────────────
    /// <summary>Raised on the UI thread when a stream-start title/category mismatch is detected.</summary>
    public event Action<StreamMetadataMismatch>? StreamMetadataMismatchDetected;
    public event Action<AuthReconnectPrompt>? AuthReconnectRequired;

    private int _verifyingMetadata;                       // single-flight guard
    private DateTimeOffset _lastMetadataVerify = DateTimeOffset.MinValue;
    private readonly Dictionary<string, AuthReconnectPrompt> _pendingAuthReconnectPrompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _authReconnectPromptActive = new(StringComparer.OrdinalIgnoreCase);

    private static bool MetaEquals(string? actual, string expected)
        => string.Equals((actual ?? "").Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On stream start, re-fetch the live Twitch/Kick channel info and compare it to the title and
    /// category the user last pushed (per the platforms they targeted). On any mismatch, raise
    /// <see cref="StreamMetadataMismatchDetected"/> so the UI can offer to re-apply.
    /// </summary>
    public async Task VerifyStreamMetadataAsync()
    {
        var expectedTitle = Settings.LastAppliedStreamTitle;
        var expectedGame  = Settings.LastAppliedGameName;
        bool wantTitle = !string.IsNullOrWhiteSpace(expectedTitle) &&
                         (Settings.LastAppliedTitleTwitch || Settings.LastAppliedTitleKick);
        bool wantGame  = !string.IsNullOrWhiteSpace(expectedGame) &&
                         (Settings.LastAppliedGameTwitch || Settings.LastAppliedGameKick);
        if (!wantTitle && !wantGame) return;

        // Debounce repeated triggers (e.g. an OBS reconnect mid-stream re-emits the "live" state).
        if (DateTimeOffset.UtcNow - _lastMetadataVerify < TimeSpan.FromSeconds(60)) return;
        if (System.Threading.Interlocked.Exchange(ref _verifyingMetadata, 1) == 1) return;
        try
        {
            _lastMetadataVerify = DateTimeOffset.UtcNow;

            var twitch = await Stream.GetTwitchChannelInfoAsync();
            var kick   = await Stream.GetKickChannelInfoAsync();

            var mismatches = new List<string>();
            bool titleMismatch = false, gameMismatch = false;

            if (wantTitle && Settings.LastAppliedTitleTwitch && twitch != null && !MetaEquals(twitch.Title, expectedTitle))
            { mismatches.Add($"Twitch title is \"{twitch.Title}\""); titleMismatch = true; }
            if (wantTitle && Settings.LastAppliedTitleKick && kick != null && !MetaEquals(kick.Title, expectedTitle))
            { mismatches.Add($"Kick title is \"{kick.Title}\""); titleMismatch = true; }

            if (wantGame && Settings.LastAppliedGameTwitch && twitch != null && !MetaEquals(twitch.GameName, expectedGame))
            { mismatches.Add($"Twitch category is \"{twitch.GameName}\""); gameMismatch = true; }
            if (wantGame && Settings.LastAppliedGameKick && kick != null && !MetaEquals(kick.GameName, expectedGame))
            { mismatches.Add($"Kick category is \"{kick.GameName}\""); gameMismatch = true; }

            if (mismatches.Count == 0) return;

            var intended = wantTitle ? $"title \"{expectedTitle}\"" : "";
            if (wantGame) intended += (intended.Length > 0 ? " and " : "") + $"category \"{expectedGame}\"";
            var detail = $"You set the {intended}, but {string.Join("; ", mismatches)}.";

            _dispatcher.Invoke(() =>
            {
                AddActivityLine($"{DateTime.Now:HH:mm:ss}  ⚠ Stream info mismatch on go-live — {detail}");
                StreamMetadataMismatchDetected?.Invoke(new StreamMetadataMismatch(detail, titleMismatch, gameMismatch));
            });
        }
        catch { /* verification is best-effort; never disrupt the stream */ }
        finally { System.Threading.Interlocked.Exchange(ref _verifyingMetadata, 0); }
    }

    /// <summary>Re-push the last-set title/category to the platforms the user originally targeted.</summary>
    public async Task<PlatformUpdateResult?> ReapplyStreamMetadataAsync(bool reapplyTitle, bool reapplyGame)
    {
        PlatformUpdateResult? last = null;

        if (reapplyTitle && !string.IsNullOrWhiteSpace(Settings.LastAppliedStreamTitle))
            last = await Stream.UpdateTitleAsync(
                Settings.LastAppliedStreamTitle, Settings.LastAppliedTitleTwitch, Settings.LastAppliedTitleKick);

        if (reapplyGame && !string.IsNullOrWhiteSpace(Settings.LastAppliedGameName))
        {
            var matches = await Stream.SearchGamesAsync(
                Settings.LastAppliedGameName, Settings.LastAppliedGameTwitch, Settings.LastAppliedGameKick);
            var best = matches.FirstOrDefault(g =>
                           string.Equals(g.Name, Settings.LastAppliedGameName, StringComparison.OrdinalIgnoreCase))
                       ?? matches.FirstOrDefault();
            if (best != null)
                last = await Stream.UpdateGameAsync(best, Settings.LastAppliedGameTwitch, Settings.LastAppliedGameKick);
        }

        _dispatcher.Invoke(() => AddActivityLine($"{DateTime.Now:HH:mm:ss}  ↻ Re-applied stream info to the platforms you set."));
        return last;
    }

    private bool _pipeConnected;
    public bool PipeConnected { get => _pipeConnected; set => Set(ref _pipeConnected, value); }

    private string _pipeStatus = "OBS Plugin: Not connected";
    public string PipeStatus { get => _pipeStatus; set => Set(ref _pipeStatus, value); }

    // ── Collections ──────────────────────────────────────────────────────────
    public ObservableCollection<ChatMessageItem>  ChatMessages  { get; } = [];
    public ObservableCollection<string>           ActivityItems { get; } = [];
    public ObservableCollection<ViewerInfo>       ViewerItems   { get; } = [];
    public ObservableCollection<GameSearchResult> GameResults   { get; } = [];
    public ObservableCollection<ServiceStatusItem> ServiceStatuses { get; } = [];

    private readonly Dictionary<string, ServiceStatusItem> _serviceStatusIndex = [];

    private int _messageCount;
    public int MessageCount { get => _messageCount; set => Set(ref _messageCount, value); }

    private int _viewerCount;
    public int ViewerCount { get => _viewerCount; set => Set(ref _viewerCount, value); }

    private int _twitchViewerCount;
    public int TwitchViewerCount { get => _twitchViewerCount; set => Set(ref _twitchViewerCount, value); }

    private int _kickViewerCount;
    public int KickViewerCount { get => _kickViewerCount; set => Set(ref _kickViewerCount, value); }

    private int _youtubeViewerCount;
    public int YouTubeViewerCount { get => _youtubeViewerCount; set => Set(ref _youtubeViewerCount, value); }

    private int _followerCount;
    public int FollowerCount { get => _followerCount; set => Set(ref _followerCount, value); }

    private int _twitchFollowerCount;
    public int TwitchFollowerCount { get => _twitchFollowerCount; set => Set(ref _twitchFollowerCount, value); }

    private int _kickFollowerCount;
    public int KickFollowerCount { get => _kickFollowerCount; set => Set(ref _kickFollowerCount, value); }

    private bool _kickFollowerCountKnown;
    public bool KickFollowerCountKnown { get => _kickFollowerCountKnown; set => Set(ref _kickFollowerCountKnown, value); }

    private int _subscriberCount;
    public int SubscriberCount { get => _subscriberCount; set => Set(ref _subscriberCount, value); }

    private int _twitchSubscriberCount;
    public int TwitchSubscriberCount { get => _twitchSubscriberCount; set => Set(ref _twitchSubscriberCount, value); }

    private int _kickSubscriberCount;
    public int KickSubscriberCount { get => _kickSubscriberCount; set => Set(ref _kickSubscriberCount, value); }

    private DateTimeOffset? _streamStartedAt;
    public DateTimeOffset? StreamStartedAt { get => _streamStartedAt; set => Set(ref _streamStartedAt, value); }

    private bool _showChatTimestampsInApp;
    public bool ShowChatTimestampsInApp
    {
        get => _showChatTimestampsInApp;
        private set => Set(ref _showChatTimestampsInApp, value);
    }

    private bool _chatTtsEnabled;
    public bool ChatTtsEnabled
    {
        get => _chatTtsEnabled;
        private set => Set(ref _chatTtsEnabled, value);
    }

    private bool _alertTtsEnabled;
    public bool AlertTtsEnabled
    {
        get => _alertTtsEnabled;
        private set => Set(ref _alertTtsEnabled, value);
    }

    // ── Kick raid alerts (opt-in, raid-only Pusher listener) ──────────────────────
    private KickRaidListener? _kickRaidListener;
    private bool _kickRaidAlertsEnabled;
    public bool KickRaidAlertsEnabled { get => _kickRaidAlertsEnabled; private set => Set(ref _kickRaidAlertsEnabled, value); }
    public string KickRaidStatus => _kickRaidListener?.Status ?? "Disabled";

    public void AttachKickRaidListener(KickRaidListener listener)
    {
        _kickRaidListener = listener;
        listener.StatusChanged += () => _dispatcher.Invoke(() => Notify(nameof(KickRaidStatus)));
        Notify(nameof(KickRaidStatus));
    }

    public void SetKickRaidAlertsEnabled(bool enabled)
    {
        Settings.KickRaidAlertsEnabled = enabled;
        Settings.Save();
        KickRaidAlertsEnabled = enabled;
        if (enabled) _kickRaidListener?.Start(Tokens.Credentials.KickUsername ?? "");
        else _ = _kickRaidListener?.StopAsync();
        Notify(nameof(KickRaidStatus));
    }

    // ── TTS engine (WinRT default; Kokoro ONNX optional, fully in-process) ──────
    private string _ttsEngine = "WinRt";
    public string TtsEngine { get => _ttsEngine; private set => Set(ref _ttsEngine, value); }

    private string _kokoroVoiceName = "af_heart";
    public string KokoroVoiceName { get => _kokoroVoiceName; private set => Set(ref _kokoroVoiceName, value); }

    // ── Labels / Goals ────────────────────────────────────────────────────────
    public ObservableCollection<LabelRow> Labels { get; } = [];
    public ObservableCollection<GoalRow>  Goals  { get; } = [];

    public static readonly (string Name, string Desc)[] LabelMeta =
    {
        ("Recent Follower",   "Last user who followed"),
        ("Recent Subscriber", "Last user who subscribed"),
        ("Subscriber Count",  "Total subscriber count"),
        ("Viewer Count",      "Current viewer count"),
        ("Follower Count",    "Total follower count"),
        ("Stream Uptime",     "Time since stream started"),
        ("Recent Donation",   "Last Bits donation"),
        ("Top Donation",      "Highest Bits donation"),
        ("Donation Total",    "Total Bits donated"),
        ("Recent Gift Sub",   "Last gift subscription sender"),
    };

    public void RefreshLabels()
    {
        Labels.Clear();
        for (int i = 0; i < LabelMeta.Length; i++)
        {
            AlertLayout layout;
            bool hasLayout = Settings.Labels.TryGetValue(i.ToString(), out var cfg)
                             && !string.IsNullOrEmpty(cfg?.LayoutJson);
            layout = hasLayout && cfg?.LayoutJson != null
                ? AlertLayout.FromJson(cfg.LayoutJson) ?? AlertLayout.CreateDefaultLabel()
                : AlertLayout.CreateDefaultLabel();
            Labels.Add(new LabelRow
            {
                Index       = i,
                Name        = LabelMeta[i].Name,
                Description = LabelMeta[i].Desc,
                Status      = hasLayout ? "Custom layout" : "Default layout",
                Size        = $"{layout.Width} × {layout.Height}",
                PreviewText = GetLabelPreviewValue(i),
            });
        }
    }

    private static string GetLabelPreviewValue(int idx) => idx switch
    {
        0 => "RecentFollower",
        1 => "RecentSubscriber",
        2 => "1234",
        3 => "567",
        4 => "8901",
        5 => "2h 34m",
        6 => "Rob: 500 bits",
        7 => "Rob: 2500 bits",
        8 => "12000",
        9 => "GiftMaster",
        _ => "Preview",
    };

    public void RefreshGoals()
    {
        Goals.Clear();
        for (int i = 0; ; i++)
        {
            if (!Settings.Goals.TryGetValue(i.ToString(), out var cfg)) break;
            Goals.Add(new GoalRow
            {
                Index      = i,
                IndexLabel = $"#{i}",
                Name       = cfg.Title,
                Progress   = $"{cfg.Current} / {cfg.Target}",
                Enabled    = cfg.Enabled,
                Title      = cfg.Title,
                Target     = cfg.Target.ToString(),
                CurrentStr = cfg.Current.ToString(),
                LinkType   = string.IsNullOrWhiteSpace(cfg.LinkType) ? "Manual" : cfg.LinkType,
            });
        }
    }

    // ── Chat source names from plugin ─────────────────────────────────────────
    public ObservableCollection<string> PluginChatSources { get; } = [];

    // ── Live / stream status ─────────────────────────────────────────────────
    private bool _twitchIsLive;
    public bool TwitchIsLive { get => _twitchIsLive; set => Set(ref _twitchIsLive, value); }

    private bool _kickIsLive;
    public bool KickIsLive { get => _kickIsLive; set => Set(ref _kickIsLive, value); }

    private bool _youtubeIsLive;
    public bool YouTubeIsLive { get => _youtubeIsLive; set => Set(ref _youtubeIsLive, value); }

    private bool _isLive;
    public bool IsLive { get => _isLive; set => Set(ref _isLive, value); }

    private string _liveStatusText = "Offline";
    public string LiveStatusText { get => _liveStatusText; set => Set(ref _liveStatusText, value); }

    private void UpdateLiveStatus(bool twitchLive, bool kickLive, bool youtubeLive)
    {
        TwitchIsLive  = twitchLive;
        KickIsLive    = kickLive;
        YouTubeIsLive = youtubeLive;
        IsLive        = twitchLive || kickLive || youtubeLive;
        var parts = new List<string>(3);
        if (twitchLive)  parts.Add("Twitch");
        if (kickLive)    parts.Add("Kick");
        if (youtubeLive) parts.Add("YouTube");
        LiveStatusText = parts.Count > 0 ? "LIVE · " + string.Join(" + ", parts) : "Offline";
        EvaluateStreamHealth();
    }

    // ── Per-platform "active / streaming to this" master switches ─────────────
    // Independent of login. When a platform is inactive the app treats it as if you are not streaming
    // there: it is not polled, its inbound chat/alerts are dropped at the EventBus, and it never
    // receives a send. Initialised from AppSettings in the constructor; toggled at runtime (Dashboard /
    // Connections page) via SetPlatformActive — no logout required.
    private bool _twitchActive = true;
    public bool TwitchActive { get => _twitchActive; private set => Set(ref _twitchActive, value); }

    private bool _kickActive = true;
    public bool KickActive { get => _kickActive; private set => Set(ref _kickActive, value); }

    private bool _youtubeActive = true;
    public bool YouTubeActive { get => _youtubeActive; private set => Set(ref _youtubeActive, value); }

    // The single source of truth for whether a platform is on. Steam / any other platform is never gated.
    public bool IsPlatformActive(Platform platform) => platform switch
    {
        Platform.Twitch  => Settings.TwitchActive,
        Platform.Kick    => Settings.KickActive,
        Platform.YouTube => Settings.YouTubeActive,
        _                => true,
    };

    private static bool IsLocalTestEvent(StreamEvent evt)
        => evt.Data.TryGetValue("isLocalTest", out var v) && v is true;

    private Platform GetPreferredLocalTestPlatform()
    {
        if (Settings.KickActive) return Platform.Kick;
        if (Settings.TwitchActive) return Platform.Twitch;
        if (Settings.YouTubeActive) return Platform.YouTube;
        // No platform is active: still allow local overlay tests to exercise the alert pipeline.
        return Platform.Kick;
    }

    // EventBus gate: drop an inactive platform's inbound events at one chokepoint. Internal/aggregate
    // messages use Platform.System, and local synthetic tests mark themselves with isLocalTest so core
    // overlay verification is never blocked by platform login/active state.
    public bool ShouldDispatchEvent(StreamEvent evt)
        => IsLocalTestEvent(evt) || IsPlatformActive(evt.Platform);

    // Flip a platform on/off WITHOUT logging out. The gates (EventBus filter, poll loop, send paths)
    // all read these flags live, so the platform goes dark — or comes back — immediately. We deliberately
    // do NOT tear down the Twitch/Kick chat sockets here: that would re-touch the sensitive
    // connect/disconnect lifecycle, and the gates already silence everything the user can see.
    public void SetPlatformActive(Platform platform, bool active)
    {
        switch (platform)
        {
            case Platform.Twitch:
                if (Settings.TwitchActive == active) return;
                Settings.TwitchActive = active;
                TwitchActive = active;
                if (!active) { TwitchViewerCount = 0; TwitchIsLive = false; }
                break;
            case Platform.Kick:
                if (Settings.KickActive == active) return;
                Settings.KickActive = active;
                KickActive = active;
                if (!active) { KickViewerCount = 0; KickIsLive = false; }
                break;
            case Platform.YouTube:
                if (Settings.YouTubeActive == active) return;
                Settings.YouTubeActive = active;
                YouTubeActive = active;
                if (!active) { YouTubeViewerCount = 0; YouTubeIsLive = false; }
                _ = SyncYouTubeChatMonitoringAsync();
                break;
            default:
                return;
        }
        Settings.Save();
        // Recompute the combined live banner from the per-platform flags we may have just cleared.
        UpdateLiveStatus(TwitchIsLive, KickIsLive, YouTubeIsLive);
    }

    // ── Status bar ────────────────────────────────────────────────────────────
    private string _statusLeft = "";
    public string StatusLeft { get => _statusLeft; set => Set(ref _statusLeft, value); }

    private void InitializeServiceStatuses()
    {
        AddServiceStatus("twitch-chat",    "Twitch Chat",       "Pending",  "Waiting for Twitch login",   "Connects to Twitch IRC chat.");
        AddServiceStatus("twitch-eventsub","Twitch EventSub",   "Pending",  "Waiting for Twitch login",   "Receives follows, raids, subs, bits, and redemptions.");
        AddServiceStatus("twitch-badges",  "Twitch Badges",     "Pending",  "Waiting for Twitch login",   "Downloads native Twitch badge images for overlays.");
        AddServiceStatus("twitch-emotes",  "Third-Party Emotes","Pending",  "Waiting for Twitch login",   "Optional BTTV / FFZ / 7TV provider lookups.");
        AddServiceStatus("kick-bridge",    "Kick Integration",  KickBridge.IsConfigured ? "Pending" : "Disabled", KickBridge.StatusSummary, KickBridge.StatusDetails);
        AddServiceStatus("kick-api",       "Kick API",          "Pending",  "Waiting for Kick login",     "Kick viewer/sub count polling uses the stored Kick OAuth session.");
        AddServiceStatus("youtube-chat",   "YouTube Chat",      "Pending",  "Waiting for YouTube login",  "Connects to YouTube live chat and maps Super Chats and memberships into alerts.");
        AddServiceStatus("obs-plugin",     "OBS Plugin Pipe",   "Pending",  "Waiting for OBS plugin",     "Named pipe link to the OBS-side plugin.");
        AddServiceStatus("obs-websocket",  "OBS WebSocket",     "Pending",  "Waiting for connection",     "Remote OBS control and scene management.");
    }

    private void AddServiceStatus(string key, string name, string state, string summary, string details)
    {
        var item = new ServiceStatusItem { Key = key, Name = name };
        ServiceStatuses.Add(item);
        _serviceStatusIndex[key] = item;
        ApplyServiceStatus(item, state, summary, details);
    }

    private static void ApplyServiceStatus(ServiceStatusItem item, string state, string summary, string details)
    {
        item.State   = state;
        item.Summary = summary;
        item.Details = details;
        item.Accent  = state switch
        {
            "Healthy"  => "#FF38B26D",
            "Warning"  => "#FFE0A227",
            "Error"    => "#FFD9534F",
            "Disabled" => "#FF8A8A8A",
            _          => "#FF5AA9E6",
        };
    }

    public void UpdateServiceStatus(string key, string state, string summary, string details)
        => _dispatcher.Invoke(() =>
        {
            if (_serviceStatusIndex.TryGetValue(key, out var item))
                ApplyServiceStatus(item, state, summary, details);
            EvaluateStreamHealth();
        });

    private static string GetYouTubeStatusState(bool connected, string summary)
    {
        if (connected) return "Healthy";
        return summary switch
        {
            "Waiting for YouTube login" => "Pending",
            "YouTube authorized" => "Pending",
            "YouTube offline" => "Error",
            "YouTube live chat unavailable" => "Error",
            "YouTube live chat disabled" => "Error",
            "YouTube live chat ended" => "Error",
            "YouTube broadcast lookup failed" => "Error",
            "YouTube chat forbidden" => "Error",
            "YouTube chat error" => "Error",
            "YouTube login expired" => "Error",
            "YouTube login missing" => "Error",
            "YouTube send failed" => "Error",
            "YouTube quota exceeded" => "Error",
            _ => "Pending",
        };
    }

    public void RefreshStatusBar()
    {
        TwitchConnected = Twitch.IsConnected;
        TwitchStatus    = Twitch.IsConnected ? $"Twitch: #{Twitch.Channel}" : "Twitch: Not connected";
        UpdateServiceStatus(
            "twitch-chat",
            Twitch.IsConnected ? "Healthy" : "Pending",
            Twitch.IsConnected ? $"Connected to #{Twitch.Channel}" : "Waiting for Twitch login",
            Twitch.IsConnected ? "Twitch chat is live and messages will flow into the app." : "Log in to Twitch to enable chat and related services.");

        KickConnected = KickBridge.IsConnected || Kick.IsConnected;
        KickStatus    = KickBridge.IsConnected
            ? "Kick: Bridge connected"
            : (Kick.IsConnected ? "Kick: Connected" : "Kick: Not connected");

        if (KickBridge.IsConnected)
            UpdateServiceStatus("kick-bridge", "Healthy", "Kick bridge connected", KickBridge.StatusDetails);
        else if (Kick.IsConnected)
            UpdateServiceStatus("kick-bridge", "Healthy", "Kick connected", "Kick chat is currently connected.");

        YouTubeConnected = YouTube.IsConnected;
        YouTubeStatus    = YouTube.IsConnected
            ? "YouTube: Connected"
            : (IsYouTubeLoggedIn ? "YouTube: Authorized / offline" : "YouTube: Not connected");
        UpdateServiceStatus(
            "youtube-chat",
            GetYouTubeStatusState(YouTube.IsConnected, IsYouTubeLoggedIn ? "YouTube authorized" : "Waiting for YouTube login"),
            IsYouTubeLoggedIn ? "YouTube authorized" : "Waiting for YouTube login",
            IsYouTubeLoggedIn ? "YouTube live chat will connect only while an active live broadcast with chat is available." : "Log in to YouTube to enable live chat and Super Chat alerts.");

        PipeConnected = Pipe.IsConnected;
        var pluginStatus = Pipe.GetPluginStatus();
        PipeStatus = pluginStatus.State switch
        {
            "Healthy" => $"OBS Plugin: Connected (v{Pipe.PluginVersion})",
            "Error"   => "OBS Plugin: Version mismatch",
            "Warning" => "OBS Plugin: Handshake pending",
            _         => "OBS Plugin: Waiting",
        };
        UpdateServiceStatus("obs-plugin", pluginStatus.State, pluginStatus.Summary, pluginStatus.Details);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public RelayCommand NavigateCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainViewModel(
        TwitchAdapter twitch, KickAdapter kick, YouTubeLiveChatService youTube, IKickBridgeClient kickBridge, EventBus bus,
        PluginPipeServer pipe, TokenStore tokens, AppSettings settings,
        ObsWebSocketService obsWs, ModerationService mod,
        StreamManagementService stream, ViewerListService viewers,
        ChatbotService chatbot, ActivityRepository activity,
        OverlayDispatcher overlayDispatcher,
        IntegrationConfigService integrationConfig,
        PlatformCredentialService credentials,
        IDispatcherService dispatcher)
    {
        Twitch            = twitch;   Kick     = kick;    YouTube = youTube; KickBridge = kickBridge; Bus = bus;
        Pipe              = pipe;     Tokens   = tokens;  Settings = settings;
        ObsWs             = obsWs;    Mod      = mod;     Stream   = stream;
        Viewers           = viewers;  Chatbot  = chatbot; Activity = activity;
        OverlayDispatcher = overlayDispatcher;
        IntegrationConfig = integrationConfig;
        Credentials       = credentials;
        _dispatcher       = dispatcher;

        NavigateCommand = new RelayCommand(p => { if (p is string panel) ActivePanel = panel; });

        KickBridgeSummary = kickBridge.StatusSummary;
        KickBridgeDetails = kickBridge.StatusDetails;
        ShowChatTimestampsInApp = settings.ShowChatTimestampsInApp;
        ChatTtsEnabled = settings.EnableChatTts;
        AlertTtsEnabled = settings.EnableAlertTts;
        KickRaidAlertsEnabled = settings.KickRaidAlertsEnabled;
        TwitchActive  = settings.TwitchActive;
        KickActive    = settings.KickActive;
        YouTubeActive = settings.YouTubeActive;
        TtsEngine = string.IsNullOrWhiteSpace(settings.TtsEngine) ? "WinRt" : settings.TtsEngine;
        KokoroVoiceName = string.IsNullOrWhiteSpace(settings.KokoroVoiceName) ? "af_heart" : settings.KokoroVoiceName;
        InitializeServiceStatuses();
        SubscribeToServices();
    }

    // ── Service subscriptions ─────────────────────────────────────────────────
    private void SubscribeToServices()
    {
        Viewers.ViewersUpdated += list => _dispatcher.Invoke(() =>
        {
            ViewerItems.Clear();
            foreach (var v in list) ViewerItems.Add(v);
            // Do NOT write ViewerCount here — list.Count is "names in Twitch chat", not stream
            // viewership. Writing it made the dashboard stat flip between the combined
            // Twitch+Kick viewer count (StreamDataUpdated) and the chatter count every poll.
        });

        // Apply persisted OBS / health preferences to the service + mirrors.
        ObsWs.AutoReconnect   = IntegrationConfig.ObsAutoReconnect;
        ObsAutoReconnect      = IntegrationConfig.ObsAutoReconnect;
        WarnOnUnhealthyTwitch = IntegrationConfig.WarnOnUnhealthyTwitch;
        WarnOnUnhealthyKick   = IntegrationConfig.WarnOnUnhealthyKick;

        ObsWs.ConnectionChanged += connected => _dispatcher.Invoke(() =>
        {
            ObsConnected = connected;
            ObsStatus    = connected ? "OBS WS: Connected" : "OBS WS: Not connected";
            if (!connected) ObsStreaming = false;
            UpdateServiceStatus(
                "obs-websocket",
                connected ? "Healthy" : "Pending",
                connected ? "OBS WebSocket connected" : "Waiting for OBS WebSocket connection",
                connected ? "Scene switching and OBS state sync are available." : "Connect OBS WebSocket from the OBS page to enable remote control.");
            EvaluateStreamHealth();
            _ = SyncYouTubeChatMonitoringAsync();
        });

        ObsWs.StreamStateChanged += active => _dispatcher.Invoke(() =>
        {
            bool wasStreaming = ObsStreaming;
            ObsStreaming = active;
            EvaluateStreamHealth();
            _ = SyncYouTubeChatMonitoringAsync();
            // On the transition into streaming, verify the live title/category matches what was set.
            if (active && !wasStreaming)
                _ = VerifyStreamMetadataAsync();
        });

        ObsWs.ReconnectStatusChanged += status => _dispatcher.Invoke(() => ObsStatus = status);

        Twitch.ThirdPartyEmoteStatusChanged += summary => _dispatcher.Invoke(() =>
        {
            UpdateServiceStatus(
                "twitch-emotes",
                summary.HasWarnings ? "Warning" : "Healthy",
                summary.Summary,
                summary.Details);
        });

        KickBridge.StatusChanged += (connected, summary, details) => _dispatcher.Invoke(() =>
        {
            KickBridgeSummary = summary;
            KickBridgeDetails = details;
            KickConnected     = connected || Kick.IsConnected;
            KickStatus        = connected ? "Kick: Bridge connected" : (Kick.IsConnected ? "Kick: Connected" : "Kick: Not connected");
            UpdateServiceStatus(
                "kick-bridge",
                connected ? "Healthy" : (KickBridge.IsConfigured ? "Pending" : "Disabled"),
                summary,
                details);
        });

        KickBridge.Reconnected += () => _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await BootstrapKickBridgeFromStoredLoginAsync();
        });

        KickBridge.AuthRejected += details =>
        {
            _dispatcher.Invoke(() =>
            {
                UpdateServiceStatus("kick-bridge", "Error", "Kick rejected the bridge session (401)", details);
                KickStatus = "Kick: Auth failed";
                AddActivityLine($"{DateTime.Now:HH:mm:ss}  ⚠ Kick rejected the bridge session (401) — refreshing token and re-sending login to bridge");
            });
            _ = TryRecoverKickBridgeAuthAsync();
        };

        YouTube.StatusChanged += (connected, summary, details) => _dispatcher.Invoke(() =>
        {
            YouTubeConnected = connected;
            if (connected && !string.IsNullOrWhiteSpace(Tokens.Credentials.YouTubeChannelTitle))
                YouTubeChannelTitle = Tokens.Credentials.YouTubeChannelTitle!;
            YouTubeStatus = connected
                ? "YouTube: Connected"
                : (IsYouTubeLoggedIn ? "YouTube: Authorized / offline" : "YouTube: Not connected");
            UpdateServiceStatus(
                "youtube-chat",
                GetYouTubeStatusState(connected, summary),
                summary,
                details);
        });
    }

    // ── Kick bridge 401 recovery ──────────────────────────────────────────────
    private int _kickBridgeAuthRecoveryActive;
    private DateTimeOffset _lastKickBridgeAuthRecovery = DateTimeOffset.MinValue;

    private async Task TryRecoverKickBridgeAuthAsync()
    {
        // Single-flight with a cooldown: the bridge can report several 401s in a
        // burst (one per failed send) — one recovery attempt per 30s is enough and
        // avoids hammering Kick's token endpoint with rotating refresh tokens.
        if (System.Threading.Interlocked.Exchange(ref _kickBridgeAuthRecoveryActive, 1) == 1) return;
        try
        {
            if (DateTimeOffset.UtcNow - _lastKickBridgeAuthRecovery < TimeSpan.FromSeconds(30)) return;
            _lastKickBridgeAuthRecovery = DateTimeOffset.UtcNow;
            var ok = await BootstrapKickBridgeFromStoredLoginAsync();
            _dispatcher.Invoke(() =>
            {
                AddActivityLine(ok
                    ? $"{DateTime.Now:HH:mm:ss}  ✔ Kick bridge session restored with a fresh token"
                    : $"{DateTime.Now:HH:mm:ss}  ⚠ Kick bridge session restore FAILED — re-login to Kick from the Connections page");
                if (ok)
                {
                    ClearAuthReconnectPrompt("Kick");
                }
                else
                {
                    RequestAuthReconnectPrompt(
                        "Kick",
                        "Reconnect Kick",
                        "Unable to auth with Kick. The bridge could not restore the desktop Kick session with a fresh token.\n\nPlease reconnect Kick from the Connections page.");
                }
            });
        }
        finally { System.Threading.Interlocked.Exchange(ref _kickBridgeAuthRecoveryActive, 0); }
    }

    // ── Pipe source list update (called from platform window code-behind) ─────
    public void UpdatePluginChatSources(IEnumerable<string> names)
    {
        PluginChatSources.Clear();
        foreach (var n in names) PluginChatSources.Add(n);
        PipeConnected = true;
        var pluginStatus = Pipe.GetPluginStatus();
        PipeStatus = pluginStatus.State switch
        {
            "Healthy" => $"OBS Plugin: Connected (v{Pipe.PluginVersion})",
            "Error"   => "OBS Plugin: Version mismatch",
            "Warning" => "OBS Plugin: Handshake pending",
            _         => "OBS Plugin: Connected",
        };
        UpdateServiceStatus("obs-plugin", pluginStatus.State, pluginStatus.Summary, pluginStatus.Details);
    }

    private int _activityCount;
    public int ActivityCount { get => _activityCount; set => Set(ref _activityCount, value); }

    private sealed class PendingOutgoingChatEcho
    {
        public required string Message { get; init; }
        public required HashSet<string> SenderNames { get; init; }
        public required HashSet<string> SenderIds { get; init; }
        public required HashSet<Platform> RemainingPlatforms { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private readonly object _pendingOutgoingChatLock = new();
    private readonly List<PendingOutgoingChatEcho> _pendingOutgoingChat = [];

    // ── EventBus handler (no WPF) ─────────────────────────────────────────────
    public System.Threading.Tasks.Task OnEvent(StreamEvent evt)
    {
        _dispatcher.Invoke(() =>
        {
            if (evt.Type == EventType.Achievement &&
                evt.Data.TryGetValue("type", out var evtType) &&
                evtType?.ToString() == "StreamDataUpdated")
            {
                bool tw = evt.Data.TryGetValue("twitchIsLive", out var tl) && tl is bool tb && tb;
                bool kk = evt.Data.TryGetValue("kickIsLive",   out var kl) && kl is bool kb && kb;
                bool yt = evt.Data.TryGetValue("youtubeIsLive", out var yl) && yl is bool yb && yb;
                UpdateLiveStatus(tw, kk, yt);

                if (evt.Data.TryGetValue("viewerCount",    out var vc) && vc is int vi)  ViewerCount    = vi;
                if (evt.Data.TryGetValue("followerCount",  out var fc) && fc is int fi)  FollowerCount  = fi;
                if (evt.Data.TryGetValue("subscriberCount",out var sc) && sc is int si)  SubscriberCount = si;
                if (evt.Data.TryGetValue("twitchViewerCount", out var tvc) && tvc is int tvi) TwitchViewerCount = tvi;
                if (evt.Data.TryGetValue("kickViewerCount",   out var kvc) && kvc is int kvi) KickViewerCount = kvi;
                if (evt.Data.TryGetValue("youtubeViewerCount", out var yvc) && yvc is int yvi) YouTubeViewerCount = yvi;
                if (evt.Data.TryGetValue("twitchFollowerCount", out var tfc) && tfc is int tfi) TwitchFollowerCount = tfi;
                if (evt.Data.TryGetValue("kickFollowerCount",   out var kfc) && kfc is int kfi) KickFollowerCount = kfi;
                if (evt.Data.TryGetValue("kickFollowerCountKnown", out var kfck) && kfck is bool kfkb) KickFollowerCountKnown = kfkb;
                if (evt.Data.TryGetValue("twitchSubscriberCount", out var tsc) && tsc is int tsi) TwitchSubscriberCount = tsi;
                if (evt.Data.TryGetValue("kickSubscriberCount",   out var ksc) && ksc is int ksi) KickSubscriberCount = ksi;
                if (evt.Data.TryGetValue("streamStartedAt",out var sa) && sa is DateTimeOffset dto)
                    StreamStartedAt = dto;
                else if (evt.Data.TryGetValue("streamStartedAt", out var sa2) && sa2 is DBNull)
                    StreamStartedAt = null;
                return;
            }

            if (evt.Type == EventType.Chat)
            {
                var msg   = evt.Data.TryGetValue("message",   out var m)  ? m?.ToString()  ?? "" : "";
                var color = evt.Data.TryGetValue("color",     out var c)  ? c?.ToString()  ?? "#FFFFFF" : "#FFFFFF";
                var msgId = evt.Data.TryGetValue("messageId", out var id) ? id?.ToString() ?? "" : "";

                if (TryConsumePendingOutgoingChatEcho(evt.Platform, evt.User, msg))
                    return;

                var item = new ChatMessageItem(evt.User.Id, evt.User.Username,
                    evt.User.DisplayName, msgId, msg, evt.Platform, color, evt.Timestamp, ShowChatTimestampsInApp);
                ChatMessages.Add(item);
                if (ChatMessages.Count > 500) ChatMessages.RemoveAt(0);
                MessageCount++;
            }
            else
            {
                var line = evt.Type switch
                {
                    EventType.Follow                 => $"❤  {evt.User.DisplayName} followed! [{evt.Platform}]",
                    EventType.Subscribe              => $"★  {evt.User.DisplayName} subscribed! [{evt.Platform}]",
                    EventType.GiftSubscribe          => $"🎁  {evt.User.DisplayName} gifted a sub! [{evt.Platform}]",
                    EventType.Raid                   => $"⚡  {evt.User.DisplayName} raided! [{evt.Platform}]",
                    EventType.Bits                   => evt.Platform == Platform.YouTube
                        ? $"💎  {evt.User.DisplayName} sent {GetEvtStr(evt, "amountDisplay", "a Super Chat")}! [{evt.Platform}]"
                        : $"💎  {evt.User.DisplayName} cheered {GetEvtInt(evt, "bits")} bits! [{evt.Platform}]",
                    EventType.ChannelPointRedemption => $"🎯  {evt.User.DisplayName} redeemed {GetEvtStr(evt, "rewardTitle", "a reward")}! [{evt.Platform}]",
                    EventType.KicksGifted            => $"💚  {evt.User.DisplayName} gifted {GetEvtInt(evt, "amount")} Kicks! [{evt.Platform}]",
                    _                                => null
                };
                if (line != null) AddActivityLine($"{DateTime.Now:HH:mm:ss}  {line}");
            }
        });
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static int GetEvtInt(StreamEvent evt, string key)
        => evt.Data.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var i) ? i : 0;

    private static string GetEvtStr(StreamEvent evt, string key, string fallback = "")
        => evt.Data.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v?.ToString()) ? v!.ToString()! : fallback;

    // ── Activity helpers ──────────────────────────────────────────────────────
    public void AddActivityLine(string text)
    {
        ActivityItems.Insert(0, text);
        if (ActivityItems.Count > 200) ActivityItems.RemoveAt(ActivityItems.Count - 1);
        ActivityCount = ActivityItems.Count;
    }

    // Set once the DB history preload has run (or the user cleared the feed) so
    // navigating back to the activity view doesn't re-import days-old entries.
    public bool ActivityHistoryLoaded { get; set; }

    // Clears the in-app activity feed only — the activity DB keeps full history.
    public void ClearActivity()
    {
        ActivityItems.Clear();
        ActivityCount = 0;
        ActivityHistoryLoaded = true;
    }

    public void AddChatMessage(ChatMessageItem msg) => _dispatcher.Invoke(() =>
    {
        msg.ShowTimestamp = ShowChatTimestampsInApp;
        ChatMessages.Add(msg);
        MessageCount++;
        if (ChatMessages.Count > 500) ChatMessages.RemoveAt(0);
    });

    public void SetShowChatTimestampsInApp(bool enabled)
    {
        Settings.ShowChatTimestampsInApp = enabled;
        Settings.Save();
        ShowChatTimestampsInApp = enabled;
        foreach (var item in ChatMessages)
            item.ShowTimestamp = enabled;
    }

    public void SetChatTtsEnabled(bool enabled)
    {
        Settings.EnableChatTts = enabled;
        Settings.Save();
        ChatTtsEnabled = enabled;
    }

    public void SetAlertTtsEnabled(bool enabled)
    {
        Settings.EnableAlertTts = enabled;
        Settings.Save();
        AlertTtsEnabled = enabled;
    }

    public void SetTtsVoice(string voiceName)
    {
        Settings.TtsVoiceName = voiceName;
        Settings.Save();
    }

    public void SetTtsVoiceWinUI(string voiceName)
    {
        Settings.TtsVoiceNameWinUI = voiceName;
        Settings.Save();
    }

    public void SetTtsAudioDevice(string deviceId)
    {
        Settings.TtsAudioDeviceId = deviceId;
        Settings.Save();
    }

    public void SetTtsSpeed(double speed)
    {
        Settings.TtsSpeed = Math.Clamp(speed, 0.5, 6.0);
        Settings.Save();
    }

    public void SetTtsIgnoredUsers(string users)
    {
        Settings.TtsIgnoredUsers = users?.Trim() ?? "";
        Settings.Save();
    }

    public void SetTtsEngine(string engine)
    {
        var e = string.Equals(engine, "Kokoro", StringComparison.OrdinalIgnoreCase) ? "Kokoro" : "WinRt";
        Settings.TtsEngine = e;
        Settings.Save();
        TtsEngine = e;
    }

    public void SetKokoroVoiceName(string name)
    {
        Settings.KokoroVoiceName = string.IsNullOrWhiteSpace(name) ? "af_heart" : name.Trim();
        Settings.Save();
        KokoroVoiceName = Settings.KokoroVoiceName;
    }

    public void SetSoundAudioDevice(string deviceId)
    {
        Settings.SoundAudioDeviceId = deviceId ?? "";
        Settings.Save();
    }

    // ── Selected chat item (for moderation commands) ──────────────────────────
    private ChatMessageItem? _selectedChatItem;
    public ChatMessageItem? SelectedChatItem
    {
        get => _selectedChatItem;
        set => Set(ref _selectedChatItem, value);
    }

    // ── Chat send ─────────────────────────────────────────────────────────────
    public async Task<bool> SendChatAsync(string text, BotReplyTarget target)
    {
        var sentAny = false;
        var sentPlatforms = new List<Platform>(3);
        var senderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var senderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // "All" (BotReplyTarget.Both) only ever targets platforms that are LIVE right now. Explicit
        // single-platform targets always send because the user chose that platform deliberately.
        bool all = target == BotReplyTarget.Both;
        // An inactive platform never receives a send, even an explicit single-target one — the user has
        // declared they are not streaming there. ("All" already skips non-live, and an inactive platform
        // is forced offline by the poll gate, so it drops out of "All" automatically.)
        if ((target == BotReplyTarget.Twitch || (all && TwitchIsLive)) && Settings.TwitchActive && Twitch.IsConnected)
        {
            await Twitch.SendMessageAsync(text);
            sentAny = true;
            sentPlatforms.Add(Platform.Twitch);
            AddOutgoingSenderIdentity(
                senderNames,
                senderIds,
                Twitch.IsBotConnected ? Twitch.BotUsername : TwitchUsername,
                Twitch.IsBotConnected ? null : (Twitch.UserId ?? Tokens.Credentials.TwitchUserId));
        }
        if ((target == BotReplyTarget.Kick || (all && KickIsLive)) && Settings.KickActive)
        {
            if (KickBridge.IsConnected)
            {
                var sent = await KickBridge.SendMessageAsync(text);
                // If the bridge is connected but the send failed, the bridge session likely dropped —
                // re-bootstrap from the stored Kick login once and retry before giving up.
                if (!sent && KickBridge.IsConnected && await BootstrapKickBridgeFromStoredLoginAsync())
                    sent = await KickBridge.SendMessageAsync(text);
                sentAny = sent || sentAny;
                if (sent)
                {
                    sentPlatforms.Add(Platform.Kick);
                    AddOutgoingSenderIdentity(
                        senderNames,
                        senderIds,
                        KickUsername,
                        Tokens.Credentials.KickChatroomId > 0 ? Tokens.Credentials.KickChatroomId.ToString() : null);
                }
            }
            else if (Kick.IsConnected)
            {
                await Kick.SendMessageAsync(text);
                sentAny = true;
                sentPlatforms.Add(Platform.Kick);
                AddOutgoingSenderIdentity(
                    senderNames,
                    senderIds,
                    IsKickBotConnected && !string.IsNullOrWhiteSpace(KickBotUsername) ? KickBotUsername : KickUsername,
                    IsKickBotConnected ? null : (Tokens.Credentials.KickChatroomId > 0 ? Tokens.Credentials.KickChatroomId.ToString() : null));
            }
        }
        if ((target == BotReplyTarget.YouTube || (all && YouTube.IsConnected)) &&
            Settings.YouTubeActive &&
            !string.IsNullOrWhiteSpace(Tokens.Credentials.YouTubeAccessToken))
        {
            var sent = await YouTube.SendMessageAsync(text);
            sentAny = sent || sentAny;
            if (sent)
            {
                sentPlatforms.Add(Platform.YouTube);
                var usingYouTubeBot = !string.IsNullOrWhiteSpace(Tokens.Credentials.BotYouTubeAccessToken);
                AddOutgoingSenderIdentity(
                    senderNames,
                    senderIds,
                    usingYouTubeBot
                        ? Tokens.Credentials.BotYouTubeUsername
                        : YouTubeChannelTitle,
                    usingYouTubeBot
                        ? Tokens.Credentials.BotYouTubeChannelId
                        : Tokens.Credentials.YouTubeChannelId);
            }
        }
        if (sentPlatforms.Count > 0)
        {
            var displayName = AddLocalOutgoingChatEcho(text, sentPlatforms, senderNames, senderIds);
            await OverlayDispatcher.SendLocalOutgoingChatEchoAsync(
                text,
                displayName,
                senderNames,
                senderIds,
                sentPlatforms,
                DateTimeOffset.UtcNow);
        }
        return sentAny;
    }

    private static void AddOutgoingSenderIdentity(
        HashSet<string> senderNames,
        HashSet<string> senderIds,
        string? senderName,
        string? senderId)
    {
        if (!string.IsNullOrWhiteSpace(senderName))
            senderNames.Add(senderName);
        if (!string.IsNullOrWhiteSpace(senderId))
            senderIds.Add(senderId);
    }

    private string AddLocalOutgoingChatEcho(string text, IReadOnlyCollection<Platform> platforms, HashSet<string> senderNames, HashSet<string> senderIds)
    {
        if (string.IsNullOrWhiteSpace(text) || platforms.Count == 0) return "You";

        var displayName = GetOutgoingDisplayName(senderNames, platforms);
        var prefix = BuildOutgoingChatPrefix(platforms);

        AddChatMessage(new ChatMessageItem(
            userId: "",
            username: displayName,
            displayName: displayName,
            messageId: $"local-outgoing-{Guid.NewGuid():N}",
            message: text,
            platform: platforms.First(),
            color: "#FFFFFF",
            timestamp: DateTimeOffset.UtcNow,
            showTimestamp: ShowChatTimestampsInApp,
            prefixOverride: prefix));

        lock (_pendingOutgoingChatLock)
        {
            CleanupExpiredPendingOutgoingChat(DateTimeOffset.UtcNow);
            _pendingOutgoingChat.Add(new PendingOutgoingChatEcho
            {
                Message = text,
                SenderNames = senderNames,
                SenderIds = senderIds,
                RemainingPlatforms = [.. platforms],
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        return displayName;
    }

    private bool TryConsumePendingOutgoingChatEcho(Platform platform, StreamUser user, string message)
    {
        lock (_pendingOutgoingChatLock)
        {
            var now = DateTimeOffset.UtcNow;
            CleanupExpiredPendingOutgoingChat(now);

            for (int i = 0; i < _pendingOutgoingChat.Count; i++)
            {
                var pending = _pendingOutgoingChat[i];
                if (!pending.RemainingPlatforms.Contains(platform)) continue;
                if (!string.Equals(pending.Message, message, StringComparison.Ordinal)) continue;

                var senderIdMatched =
                    !string.IsNullOrWhiteSpace(user.Id) &&
                    pending.SenderIds.Contains(user.Id);
                var senderNameMatched =
                    pending.SenderNames.Contains(user.DisplayName) ||
                    pending.SenderNames.Contains(user.Username);

                if (pending.SenderIds.Count > 0)
                {
                    if (!senderIdMatched && (pending.SenderNames.Count == 0 || !senderNameMatched))
                        continue;
                }
                else if (pending.SenderNames.Count > 0 && !senderNameMatched)
                    continue;

                pending.RemainingPlatforms.Remove(platform);
                if (pending.RemainingPlatforms.Count == 0)
                    _pendingOutgoingChat.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    private void CleanupExpiredPendingOutgoingChat(DateTimeOffset now)
        => _pendingOutgoingChat.RemoveAll(p => now - p.CreatedAt > TimeSpan.FromSeconds(8));

    private static string GetOutgoingDisplayName(HashSet<string> senderNames, IReadOnlyCollection<Platform> platforms)
    {
        if (senderNames.Count == 1) return senderNames.First();
        if (senderNames.Count > 1) return "You";
        return platforms.FirstOrDefault() switch
        {
            Platform.Twitch => "Twitch",
            Platform.Kick => "Kick",
            Platform.YouTube => "YouTube",
            _ => "You",
        };
    }

    private static string BuildOutgoingChatPrefix(IReadOnlyCollection<Platform> platforms)
    {
        var parts = new List<string>(3);
        if (platforms.Contains(Platform.Twitch)) parts.Add("[T]");
        if (platforms.Contains(Platform.Kick)) parts.Add("[K]");
        if (platforms.Contains(Platform.YouTube)) parts.Add("[Y]");
        return parts.Count > 0 ? string.Concat(parts) : "[?]";
    }

    public async Task SyncYouTubeChatMonitoringAsync()
    {
        if (!Settings.YouTubeActive)
        {
            await YouTube.StopAsync();
            UpdateServiceStatus("youtube-chat", "Pending", "YouTube turned off", "YouTube is set inactive — live chat discovery is paused. Turn YouTube on to resume.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Tokens.Credentials.YouTubeAccessToken))
        {
            await YouTube.StopAsync();
            UpdateServiceStatus("youtube-chat", "Pending", "Waiting for YouTube login", "YouTube chat will connect after YouTube login is completed.");
            return;
        }

        if (ObsConnected)
        {
            if (ObsStreaming)
            {
                YouTube.Start();
                UpdateServiceStatus("youtube-chat", "Pending", "Connecting YouTube chat", "OBS is live, so YouTube live chat discovery is active.");
            }
            else
            {
                await YouTube.StopAsync();
                UpdateServiceStatus("youtube-chat", "Pending", "YouTube idle until OBS goes live", "OBS is connected and not streaming, so YouTube live chat discovery is paused.");
            }

            return;
        }

        YouTube.Start();
        UpdateServiceStatus("youtube-chat", "Pending", "Watching for YouTube live chat", "OBS state is unavailable, so YouTube live chat discovery is running in fallback mode.");
    }

    // ── Moderation ────────────────────────────────────────────────────────────
    public Task TimeoutUserAsync(Platform platform, string userId)
        => Mod.CanModerate(platform)
            ? Mod.TimeoutAsync(platform, userId, 600, "Timed out via Streaming")
            : Task.CompletedTask;

    public Task BanUserAsync(Platform platform, string userId)
        => Mod.CanModerate(platform)
            ? Mod.BanAsync(platform, userId, "Banned via Streaming")
            : Task.CompletedTask;

    public Task DeleteChatMessageAsync(Platform platform, string messageId)
        => Mod.CanModerate(platform)
            ? Mod.DeleteMessageAsync(platform, messageId)
            : Task.CompletedTask;

    public Task ClearChatAsync()
        => Mod.IsConfigured
            ? Mod.ClearChatAsync()
            : Task.CompletedTask;

    // ── Stream management ─────────────────────────────────────────────────────
    private string _streamTitle = "";
    public string StreamTitle { get => _streamTitle; set => Set(ref _streamTitle, value); }

    private string _streamCurrentGame = "";
    public string StreamCurrentGame { get => _streamCurrentGame; set => Set(ref _streamCurrentGame, value); }

    private string _twitchStreamTitle = "—";
    public string TwitchStreamTitle { get => _twitchStreamTitle; set => Set(ref _twitchStreamTitle, value); }

    private string _twitchStreamCurrentGame = "—";
    public string TwitchStreamCurrentGame { get => _twitchStreamCurrentGame; set => Set(ref _twitchStreamCurrentGame, value); }

    private string _kickStreamTitle = "—";
    public string KickStreamTitle { get => _kickStreamTitle; set => Set(ref _kickStreamTitle, value); }

    private string _kickStreamCurrentGame = "—";
    public string KickStreamCurrentGame { get => _kickStreamCurrentGame; set => Set(ref _kickStreamCurrentGame, value); }

    public async Task<PlatformUpdateResult> UpdateTitleAsync(string title, bool updateTwitch, bool updateKick)
    {
        var result = await Stream.UpdateTitleAsync(title, updateTwitch, updateKick);
        if (result.AnyRequested && !string.IsNullOrWhiteSpace(title))
        {
            Settings.LastAppliedStreamTitle = title;
            Settings.LastAppliedTitleTwitch = updateTwitch;
            Settings.LastAppliedTitleKick   = updateKick;
            Settings.Save();
        }
        return result;
    }

    public async Task SearchGamesAsync(string query, bool searchTwitch, bool searchKick)
    {
        var results = await Stream.SearchGamesAsync(query, searchTwitch, searchKick);
        _dispatcher.Invoke(() =>
        {
            GameResults.Clear();
            foreach (var r in results) GameResults.Add(r);
        });
    }

    public async Task<PlatformUpdateResult> UpdateGameAsync(GameSearchResult game, bool updateTwitch, bool updateKick)
    {
        var result = await Stream.UpdateGameAsync(game, updateTwitch, updateKick);
        if (result.AnyRequested && !string.IsNullOrWhiteSpace(game.Name))
        {
            Settings.LastAppliedGameName   = game.Name;
            Settings.LastAppliedGameTwitch = updateTwitch;
            Settings.LastAppliedGameKick   = updateKick;
            Settings.Save();
        }
        return result;
    }

    public async Task RefreshChannelInfoAsync()
    {
        var twitchTask = Stream.GetTwitchChannelInfoAsync();
        var kickTask   = Stream.GetKickChannelInfoAsync();
        await Task.WhenAll(twitchTask, kickTask);
        _dispatcher.Invoke(() =>
        {
            var twitch = twitchTask.Result;
            var kick   = kickTask.Result;

            TwitchStreamTitle       = string.IsNullOrWhiteSpace(twitch?.Title)    ? "—" : twitch!.Title;
            TwitchStreamCurrentGame = string.IsNullOrWhiteSpace(twitch?.GameName) ? "—" : twitch!.GameName;
            KickStreamTitle         = string.IsNullOrWhiteSpace(kick?.Title)      ? "—" : kick!.Title;
            KickStreamCurrentGame   = string.IsNullOrWhiteSpace(kick?.GameName)   ? "—" : kick!.GameName;

            StreamTitle       = twitch?.Title    ?? kick?.Title    ?? "";
            StreamCurrentGame = twitch?.GameName ?? kick?.GameName ?? "";
        });
    }

    // ── OBS ───────────────────────────────────────────────────────────────────
    public async Task ConnectObsAsync(string address, string password)
    {
        IntegrationConfig.SaveObsConnection(address, password);
        await ObsWs.ConnectAsync(IntegrationConfig.ObsAddress, password);
    }

    public void DisconnectObs() => ObsWs.Disconnect();

    /// <summary>
    /// Called once at startup. When the OBS auto-connect/reconnect toggle is on and an address is
    /// saved, connects to OBS on launch (and keeps retrying in the background if OBS is not up yet).
    /// </summary>
    public async Task TryAutoConnectObsAsync()
    {
        if (!IntegrationConfig.ObsAutoReconnect) return;
        var address = IntegrationConfig.ObsAddress;
        if (string.IsNullOrWhiteSpace(address)) return;
        ObsWs.AutoReconnect = true;
        await ObsWs.TryConnectWithReconnectAsync(address, IntegrationConfig.ObsPassword);
    }

    public void SetObsAutoReconnect(bool enabled)
    {
        IntegrationConfig.SaveObsAutoReconnect(enabled);
        ObsWs.AutoReconnect = enabled;
        ObsAutoReconnect    = enabled;
    }

    public async Task SwitchObsSceneAsync(string scene)
        => await ObsWs.SetCurrentSceneAsync(scene);

    public async Task<(IReadOnlyList<string> Scenes, string Current)> GetObsScenesAsync()
    {
        var scenes  = await ObsWs.GetScenesAsync();
        var current = await ObsWs.GetCurrentSceneAsync();
        return (scenes, current);
    }

    // ── Auth helpers ──────────────────────────────────────────────────────────
    public SavedAuthState GetSavedAuthState()
    {
        var saved = Credentials.GetSavedAuthState();
        return new SavedAuthState(
            saved.TwitchChannel,
            saved.HasTwitchToken,
            saved.TwitchUsername,
            saved.HasKickToken,
            saved.KickUsername,
            saved.HasYouTubeToken,
            saved.YouTubeChannelTitle);
    }

    public KickBridgeConfig GetKickBridgeConfig() => IntegrationConfig.GetKickBridgeConfig();

    public void SaveKickBridgeConfig(KickBridgeConfig config)
    {
        var details = IntegrationConfig.SaveKickBridgeConfig(config);
        UpdateServiceStatus(
            "kick-bridge",
            config.Enabled && !string.IsNullOrWhiteSpace(config.Host) ? "Pending" : "Disabled",
            config.Enabled && !string.IsNullOrWhiteSpace(config.Host) ? "Bridge configured" : "Bridge disabled",
            details);
    }

    public Task ConnectKickBridgeAsync()    => ConnectAndBootstrapKickBridgeAsync();
    public Task DisconnectKickBridgeAsync() => KickBridge.DisconnectAsync();

    public async Task ConnectAndBootstrapKickBridgeAsync()
    {
        await KickBridge.ConnectAsync();
        await BootstrapKickBridgeFromStoredLoginAsync();
    }

    // refreshToken=false skips the OAuth refresh when the caller has JUST stored a
    // fresh token (e.g. StreamDataService poll recovery) — Kick refresh tokens are
    // single-use, so a redundant rotation here is a wasted round-trip and a race window.
    public async Task<bool> BootstrapKickBridgeFromStoredLoginAsync(bool refreshToken = true)
    {
        var token              = Tokens.Credentials.KickAccessToken;
        var username           = Tokens.Credentials.KickUsername;
        var broadcasterUserId  = Tokens.Credentials.KickChatroomId;

        if (!KickBridge.IsConnected)
        {
            UpdateServiceStatus("kick-bridge", "Pending", "Bridge connected but not bootstrapped", "Connect the remote bridge first, then send the desktop Kick login context.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            var (resolvedId, resolvedUsername, _) = await Credentials.FetchKickUserInfoAsync(token);
            if (resolvedId > 0 && !string.IsNullOrWhiteSpace(resolvedUsername))
            {
                broadcasterUserId = resolvedId;
                username          = resolvedUsername;
                Credentials.SaveKickIdentity(resolvedId, resolvedUsername);
            }
        }

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(username) || broadcasterUserId <= 0)
        {
            UpdateServiceStatus("kick-bridge", "Pending", "Bridge awaiting desktop Kick login", "Log into Kick on the desktop side so the bridge can receive broadcaster identity and token context.");
            return false;
        }

        if (refreshToken)
        {
            var refreshed = await Credentials.RefreshKickTokenAsync();
            if (refreshed != null)
            {
                Credentials.SaveKickLogin(refreshed.AccessToken, refreshed.RefreshToken,
                    Tokens.Credentials.KickClientId     ?? "",
                    Tokens.Credentials.KickClientSecret ?? "",
                    broadcasterUserId, username);
                token = refreshed.AccessToken;
            }
        }

        Mod.ConfigureKick(token, broadcasterUserId);

        var sent = await KickBridge.SendBootstrapAsync(new KickBridgeSessionBootstrap(
            token, username, broadcasterUserId, broadcasterUserId,
            IntegrationConfig.KickBridgeAllowsOutboundChat));

        UpdateServiceStatus("kick-bridge",
            sent ? "Healthy" : "Error",
            sent ? "Desktop Kick session sent to bridge" : "Failed to send desktop Kick session",
            sent ? $"Bridge bound for {username} ({broadcasterUserId})."
                 : "The bridge connection is up but the bootstrap packet was not accepted.");

        return sent;
    }

    public async Task<TwitchLoginResult> CompleteTwitchLoginAsync(string token, string clientId, string channelInput)
    {
        var username = await Credentials.FetchTwitchUsernameAsync(token, clientId);
        var channel  = string.IsNullOrWhiteSpace(channelInput) ? username : channelInput.Trim();
        Credentials.SaveTwitchLogin(token, clientId, username, channel);
        ClearAuthReconnectPrompt("Twitch");
        SetTwitchLoggedIn(username);
        await Twitch.ConnectAsync(username, $"oauth:{token}", channel);
        return new TwitchLoginResult(username, channel);
    }

    public async Task LogoutTwitchAsync()
    {
        await Twitch.DisposeAsync();
        Credentials.ClearTwitchLogin();
        ClearAuthReconnectPrompt("Twitch");
        SetTwitchLoggedOut();
        UpdateServiceStatus("twitch-eventsub", "Pending", "Waiting for Twitch login", "Receives follows, raids, subs, bits, and redemptions.");
        UpdateServiceStatus("twitch-badges",   "Pending", "Waiting for Twitch login", "Downloads native Twitch badge images for overlays.");
        UpdateServiceStatus("twitch-emotes",   "Pending", "Waiting for Twitch login", "Optional BTTV / FFZ / 7TV provider lookups.");
    }

    public async Task<KickLoginResult> CompleteKickLoginAsync(
        string code, string codeVerifier, string clientId, string clientSecret, string redirectUri)
    {
        var token = await Credentials.ExchangeKickCodeAsync(code, codeVerifier, clientId, clientSecret, redirectUri);
        if (token == null)
            return new KickLoginResult(false, "", 0, "", "Kick token exchange failed.");

        var (chatroomId, username, diagnostics) = await Credentials.FetchKickUserInfoAsync(token.AccessToken);

        Credentials.SaveKickLogin(token.AccessToken, token.RefreshToken, clientId, clientSecret, chatroomId, username);
        if (chatroomId > 0)
            Mod.ConfigureKick(token.AccessToken, chatroomId);

        ClearAuthReconnectPrompt("Kick");
        SetKickLoggedIn(string.IsNullOrWhiteSpace(username) ? "Kick" : username);

        if (chatroomId <= 0)
            return new KickLoginResult(false, username, chatroomId, diagnostics, "Could not determine Kick user ID");

        await BootstrapKickBridgeFromStoredLoginAsync();
        return new KickLoginResult(true, username, chatroomId, diagnostics, null);
    }

    public async Task LogoutKickAsync()
    {
        await Kick.DisposeAsync();
        await KickBridge.DisconnectAsync();
        try { await (_kickRaidListener?.StopAsync() ?? Task.CompletedTask); } catch { }
        Credentials.ClearKickLogin();
        ClearAuthReconnectPrompt("Kick");
        Mod.ClearKick();
        SetKickLoggedOut();
        UpdateServiceStatus("kick-bridge", KickBridge.IsConfigured ? "Pending" : "Disabled", KickBridge.StatusSummary, KickBridge.StatusDetails);
    }

    public async Task<YouTubeLoginResult> CompleteYouTubeLoginAsync(
        string code, string codeVerifier, string clientId, string clientSecret, string redirectUri)
    {
        var token = await Credentials.ExchangeYouTubeCodeAsync(code, codeVerifier, clientId, clientSecret, redirectUri);
        if (token == null)
            return new YouTubeLoginResult(false, "", "", "YouTube token exchange failed.");

        var (channelId, title) = await Credentials.FetchYouTubeChannelInfoAsync(token.AccessToken);
        if (string.IsNullOrWhiteSpace(channelId))
            return new YouTubeLoginResult(false, "", "", "Could not determine the YouTube channel for this login.");

        Credentials.SaveYouTubeLogin(
            token.AccessToken,
            token.RefreshToken,
            token.ExpiryUtc,
            clientId,
            clientSecret,
            channelId,
            title);

        SetYouTubeLoggedIn(string.IsNullOrWhiteSpace(title) ? "YouTube" : title);
        UpdateServiceStatus("youtube-chat", "Pending", "YouTube authorized", "YouTube live chat will connect only while an active live broadcast with chat is available.");
        await SyncYouTubeChatMonitoringAsync();
        return new YouTubeLoginResult(true, channelId, title, null);
    }

    public async Task LogoutYouTubeAsync()
    {
        await YouTube.StopAsync();
        Credentials.ClearYouTubeLogin();
        SetYouTubeLoggedOut();
        UpdateServiceStatus("youtube-chat", "Pending", "Waiting for YouTube login", "Connects to YouTube live chat and maps Super Chats and memberships into alerts.");
    }

    // ── EmojiRain settings ────────────────────────────────────────────────────
    private static EmojiRainConfig CloneEmojiRainConfig(EmojiRainConfig cfg) => new()
    {
        TriggerOnFollow    = cfg.TriggerOnFollow,
        TriggerOnSubscribe = cfg.TriggerOnSubscribe,
        TriggerOnBits      = cfg.TriggerOnBits,
        TriggerOnRaid      = cfg.TriggerOnRaid,
        FollowEmojis       = cfg.FollowEmojis,
        SubscribeEmojis    = cfg.SubscribeEmojis,
        BitsEmojis         = cfg.BitsEmojis,
        RaidEmojis         = cfg.RaidEmojis,
        FollowColor        = cfg.FollowColor,
        SubscribeColor     = cfg.SubscribeColor,
        BitsColor          = cfg.BitsColor,
        RaidColor          = cfg.RaidColor,
        FollowGif          = cfg.FollowGif,
        SubscribeGif       = cfg.SubscribeGif,
        BitsGif            = cfg.BitsGif,
        RaidGif            = cfg.RaidGif,
        CountPerTrigger    = cfg.CountPerTrigger,
        EmojiSize          = cfg.EmojiSize,
        FallSpeed          = cfg.FallSpeed,
        ParticleLifeSec    = cfg.ParticleLifeSec,
        MaxParticles       = cfg.MaxParticles,
        Spread             = cfg.Spread,
        FadeOut            = cfg.FadeOut,
        Spin               = cfg.Spin,
    };

    public EmojiRainConfig GetEmojiRainConfig() => CloneEmojiRainConfig(Settings.EmojiRain);

    public void SaveEmojiRainSettings(EmojiRainConfig cfg)
    {
        Settings.EmojiRain = cfg;
        Settings.Save();
        _ = OverlayDispatcher.SendEmojiRainSettingsAsync();
    }

    public EmojiRainConfig AppendEmojiRainEmoji(string trigger, string emoji)
    {
        var cfg = CloneEmojiRainConfig(Settings.EmojiRain);
        switch (trigger)
        {
            case "Follow":    cfg.FollowEmojis    += emoji; break;
            case "Subscribe": cfg.SubscribeEmojis += emoji; break;
            case "Bits":      cfg.BitsEmojis      += emoji; break;
            case "Raid":      cfg.RaidEmojis      += emoji; break;
        }
        SaveEmojiRainSettings(cfg);
        return cfg;
    }

    public EmojiRainConfig SetEmojiRainColor(string trigger, uint argb)
    {
        var cfg = CloneEmojiRainConfig(Settings.EmojiRain);
        switch (trigger)
        {
            case "Follow":    cfg.FollowColor    = argb; break;
            case "Subscribe": cfg.SubscribeColor = argb; break;
            case "Bits":      cfg.BitsColor      = argb; break;
            case "Raid":      cfg.RaidColor      = argb; break;
        }
        SaveEmojiRainSettings(cfg);
        return cfg;
    }

    public EmojiRainConfig SetEmojiRainGif(string trigger, string path)
    {
        var cfg = CloneEmojiRainConfig(Settings.EmojiRain);
        switch (trigger)
        {
            case "Follow":    cfg.FollowGif    = path; break;
            case "Subscribe": cfg.SubscribeGif = path; break;
            case "Bits":      cfg.BitsGif      = path; break;
            case "Raid":      cfg.RaidGif      = path; break;
        }
        SaveEmojiRainSettings(cfg);
        return cfg;
    }

    public async Task TriggerEmojiRainForAsync(string trigger)
    {
        var cfg = Settings.EmojiRain;
        var (emojis, gif, color) = trigger switch
        {
            "Subscribe" => (cfg.SubscribeEmojis, cfg.SubscribeGif, cfg.SubscribeColor),
            "Bits"      => (cfg.BitsEmojis,      cfg.BitsGif,      cfg.BitsColor),
            "Raid"      => (cfg.RaidEmojis,       cfg.RaidGif,      cfg.RaidColor),
            _           => (cfg.FollowEmojis,     cfg.FollowGif,    cfg.FollowColor),
        };
        string? content = !string.IsNullOrEmpty(gif) ? gif : emojis;
        if (string.IsNullOrEmpty(content)) content = "❤️⭐🎉";
        bool isGif = !string.IsNullOrEmpty(gif);

        await OverlayDispatcher.SendEmojiRainSettingsAsync();
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
        var payload = new byte[1 + 4 + 2 + contentBytes.Length + 1];
        int off = 0;
        payload[off++] = isGif ? (byte)1 : (byte)0;
        payload[off++] = (byte)(color >> 24);
        payload[off++] = (byte)(color >> 16);
        payload[off++] = (byte)(color >> 8);
        payload[off++] = (byte)color;
        payload[off++] = (byte)(contentBytes.Length & 0xFF);
        payload[off++] = (byte)(contentBytes.Length >> 8);
        contentBytes.CopyTo(payload, off); off += contentBytes.Length;
        payload[off] = (byte)Math.Clamp(cfg.CountPerTrigger, 1, 255);
        await Pipe.SendAsync(PipeMessageType.TriggerEmojiRain, payload);
    }

    public async Task TriggerTestEmojiRainAsync()
    {
        var cfg    = Settings.EmojiRain;
        string emojis = string.IsNullOrWhiteSpace(cfg.FollowEmojis) ? "❤️⭐🎉" : cfg.FollowEmojis;
        string? gif   = cfg.FollowGif;
        uint color    = cfg.FollowColor;
        bool isGif    = !string.IsNullOrEmpty(gif);
        string content = isGif ? gif! : emojis;

        await OverlayDispatcher.SendEmojiRainSettingsAsync();
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
        var payload = new byte[1 + 4 + 2 + contentBytes.Length + 1];
        int off = 0;
        payload[off++] = isGif ? (byte)1 : (byte)0;
        payload[off++] = (byte)(color >> 24);
        payload[off++] = (byte)(color >> 16);
        payload[off++] = (byte)(color >> 8);
        payload[off++] = (byte)color;
        payload[off++] = (byte)(contentBytes.Length & 0xFF);
        payload[off++] = (byte)(contentBytes.Length >> 8);
        contentBytes.CopyTo(payload, off); off += contentBytes.Length;
        payload[off] = (byte)Math.Clamp(cfg.CountPerTrigger, 1, 255);
        await Pipe.SendAsync(PipeMessageType.TriggerEmojiRain, payload);
    }

    public EventConfig? GetEventConfig(string key)
        => Settings.Events.TryGetValue(key, out var cfg) ? cfg : null;

    public bool HasCustomEventLayout(string key)
        => Settings.Events.TryGetValue(key, out var cfg) && !string.IsNullOrWhiteSpace(cfg.LayoutJson);

    public void SaveEventEditorResult(string key, string layoutJson, string? soundFile, float volume, float resultDuration)
    {
        // Create the config when missing — silently returning here threw away editor work
        // for event keys absent from an older settings file.
        if (!Settings.Events.TryGetValue(key, out var cfg))
        {
            cfg = new EventConfig();
            Settings.Events[key] = cfg;
        }
        cfg.LayoutJson = layoutJson;
        cfg.SoundFile  = soundFile;
        cfg.Volume     = volume;
        if (resultDuration > 0)
            cfg.Duration = (int)Math.Round(resultDuration);
        Settings.Save();
    }

    public void SaveAllEventSettings(Dictionary<string, EventConfig> events)
    {
        foreach (var (key, cfg) in events)
        {
            // Preserve layout and image set via the layout editor — EventsPage never edits those fields
            if (Settings.Events.TryGetValue(key, out var existing))
            {
                cfg.LayoutJson = existing.LayoutJson;
                cfg.ImageFile  = existing.ImageFile;
            }
            Settings.Events[key] = cfg;
        }
        Settings.Save();
    }

    // ── Unique (custom) alerts ────────────────────────────────────────────────
    public ObservableCollection<CustomAlertItem> CustomAlerts { get; } = [];
    // Names only — bound by the chatbot command dropdown.
    public ObservableCollection<string> CustomAlertNames { get; } = [];

    public void LoadCustomAlerts()
    {
        CustomAlerts.Clear();
        foreach (var (name, cfg) in Settings.CustomAlerts)
            CustomAlerts.Add(new CustomAlertItem
            {
                Name         = name,
                Enabled      = cfg.Enabled,
                Text         = cfg.Text,
                DurationText = cfg.Duration.ToString(),
                VolumeText   = cfg.Volume.ToString("F1"),
                SoundFile    = cfg.SoundFile ?? "",
                LayoutJson   = cfg.LayoutJson,
                ImageFile    = cfg.ImageFile,
            });
        RefreshCustomAlertNames();
    }

    private void RefreshCustomAlertNames()
    {
        CustomAlertNames.Clear();
        foreach (var a in CustomAlerts) CustomAlertNames.Add(a.Name);
        RefreshRewardAlertOptions();
    }

    // ── Channel-point rewards (assign a custom alert to a reward) ───────────────
    public const string NoneAlert = "(none)";
    public ObservableCollection<RewardItem> Rewards { get; } = [];
    // "(none)" + every custom alert name — bound by the per-reward assignment dropdown.
    public ObservableCollection<string> RewardAlertOptions { get; } = [];

    private void RefreshRewardAlertOptions()
    {
        RewardAlertOptions.Clear();
        RewardAlertOptions.Add(NoneAlert);
        foreach (var a in CustomAlerts) RewardAlertOptions.Add(a.Name);
    }

    public void LoadRewards()
    {
        Rewards.Clear();
        foreach (var r in Settings.Rewards)
            Rewards.Add(new RewardItem
            {
                Id            = r.Id,
                Platform      = r.Platform,
                Title         = r.Title,
                Cost          = r.Cost,
                Enabled       = r.Enabled,
                AssignedAlert = string.IsNullOrWhiteSpace(r.AssignedAlert) ? NoneAlert : r.AssignedAlert!,
            });
        RefreshRewardAlertOptions();
    }

    // Writes the in-memory rows back to settings (mapping "(none)" → no assignment) and saves.
    public void SaveRewards()
    {
        Settings.Rewards.Clear();
        foreach (var item in Rewards)
            Settings.Rewards.Add(new ChannelReward
            {
                Id            = item.Id,
                Platform      = item.Platform,
                Title         = item.Title,
                Cost          = item.Cost,
                Enabled       = item.Enabled,
                AssignedAlert = item.AssignedAlert == NoneAlert ? null : item.AssignedAlert,
            });
        Settings.Save();
    }

    // Pulls rewards from Twitch/Kick and merges them non-destructively, then reloads the rows.
    // Returns a short status string for the UI.
    public async Task<string> RefreshRewardsFromPlatformsAsync()
    {
        // Persist current assignments first so a concurrent fetch/merge can't lose unsaved edits.
        SaveRewards();
        var status = await new RewardService(Tokens, Settings).RefreshAsync();
        LoadRewards();
        return status;
    }

    // Returns the new item, or null if the name is blank or already taken.
    public CustomAlertItem? AddCustomAlert(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (CustomAlerts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;

        var item = new CustomAlertItem { Name = name };
        CustomAlerts.Add(item);
        Settings.CustomAlerts[name] = ToEventConfig(item);
        Settings.Save();
        RefreshCustomAlertNames();
        return item;
    }

    public void RemoveCustomAlert(CustomAlertItem item)
    {
        CustomAlerts.Remove(item);
        Settings.CustomAlerts.Remove(item.Name);
        Settings.Save();
        RefreshCustomAlertNames();
    }

    // Persists the inline-edited fields of every unique alert (called by the Alerts page "Save All").
    public void SaveCustomAlerts()
    {
        Settings.CustomAlerts.Clear();
        foreach (var item in CustomAlerts)
            Settings.CustomAlerts[item.Name] = ToEventConfig(item);
        Settings.Save();
        RefreshCustomAlertNames();
    }

    public void SaveCustomAlertEditorResult(CustomAlertItem item, string layoutJson, string? soundFile, float volume, float resultDuration)
    {
        item.LayoutJson = layoutJson;
        item.SoundFile  = soundFile ?? "";
        item.VolumeText = volume.ToString("F1");
        if (resultDuration > 0) item.DurationText = ((int)Math.Round(resultDuration)).ToString();
        Settings.CustomAlerts[item.Name] = ToEventConfig(item);
        Settings.Save();
    }

    public Task TriggerCustomAlertTestAsync(CustomAlertItem item)
        => OverlayDispatcher.SendCustomAlertAsync(ToEventConfig(item), "TestUser", "");

    private static EventConfig ToEventConfig(CustomAlertItem item) => new()
    {
        Enabled    = item.Enabled,
        Text       = item.Text,
        Duration   = int.TryParse(item.DurationText, out var d) ? Math.Clamp(d, 1, 60) : 5,
        Volume     = float.TryParse(item.VolumeText, out var v) ? Math.Clamp(v, 0f, 1f) : 1f,
        SoundFile  = string.IsNullOrWhiteSpace(item.SoundFile) ? null : item.SoundFile,
        LayoutJson = item.LayoutJson,
        ImageFile  = item.ImageFile,
    };

    // ── Test alert ────────────────────────────────────────────────────────────
    public Task TriggerTestAlertAsync(string tag = "Follow")
    {
        var (evtType, data) = tag switch
        {
            "Subscribe"        => (EventType.Subscribe,              (IReadOnlyDictionary<string,object>)new Dictionary<string,object> { ["months"] = 6, ["plan"] = "1000", ["message"] = "" }),
            "GiftSubscribe"    => (EventType.GiftSubscribe,          (IReadOnlyDictionary<string,object>)new Dictionary<string,object> { ["recipient"] = "SomeViewer" }),
            "Bits"             => (EventType.Bits,                   (IReadOnlyDictionary<string,object>)new Dictionary<string,object> { ["bits"] = 100 }),
            "Raid"             => (EventType.Raid,                   (IReadOnlyDictionary<string,object>)new Dictionary<string,object> { ["viewers"] = 42 }),
            "RewardRedemption" => (EventType.ChannelPointRedemption, (IReadOnlyDictionary<string,object>)new Dictionary<string,object>
            {
                ["rewardTitle"] = "Hydrate",
                ["rewardCost"] = 1000,
                ["userInput"] = "Big sip",
            }),
            _                  => (EventType.Follow,                 (IReadOnlyDictionary<string,object>)new Dictionary<string,object>()),
        };
        var testData = new Dictionary<string, object>(data.Count + 1);
        foreach (var (key, value) in data)
            testData[key] = value;
        testData["isLocalTest"] = true;

        var fakeUser = new StreamUser("0", "testuser", "TestUser");
        var fakeEvt  = new StreamEvent(GetPreferredLocalTestPlatform(), evtType, fakeUser, testData, DateTimeOffset.UtcNow);
        return Bus.PublishAsync(fakeEvt);
    }

    // ── Chatbot ───────────────────────────────────────────────────────────────

    // Observable mirrors of the chatbot service lists — kept in sync so WinUI/WPF can bind to them.
    public ObservableCollection<BotCommand> ObservableCommands { get; } = [];
    public ObservableCollection<BotTimer>   ObservableTimers   { get; } = [];
    public ObservableCollection<BotShout>   ObservableShouts   { get; } = [];

    // Call after Chatbot.Load() to populate the observable mirrors from the loaded data.
    public void SyncChatbotCollections()
    {
        ObservableCommands.Clear();
        foreach (var c in Chatbot.Commands) ObservableCommands.Add(c);
        ObservableTimers.Clear();
        foreach (var t in Chatbot.Timers)   ObservableTimers.Add(t);
        ObservableShouts.Clear();
        foreach (var s in Chatbot.Shouts)   ObservableShouts.Add(s);
    }

    public void AddBotCommand(BotCommand cmd)
    {
        ObservableCommands.Remove(ObservableCommands.FirstOrDefault(c =>
            c.Name.Equals(cmd.Name, StringComparison.OrdinalIgnoreCase))!);
        Chatbot.AddCommand(cmd);
        ObservableCommands.Add(cmd);
        Chatbot.Save();
    }

    public void RemoveBotCommand(BotCommand cmd)
    {
        Chatbot.RemoveCommand(cmd.Name);
        ObservableCommands.Remove(cmd);
        Chatbot.Save();
    }

    public void AddBotTimer(BotTimer timer)
    {
        ObservableTimers.Remove(ObservableTimers.FirstOrDefault(t =>
            t.Name.Equals(timer.Name, StringComparison.OrdinalIgnoreCase))!);
        Chatbot.AddTimer(timer);
        ObservableTimers.Add(timer);
        Chatbot.Save();
    }

    public void RemoveBotTimer(BotTimer timer)
    {
        Chatbot.RemoveTimer(timer.Name);
        ObservableTimers.Remove(timer);
        Chatbot.Save();
    }

    public void AddBotShout(BotShout shout)
    {
        Chatbot.AddShout(shout);
        ObservableShouts.Add(shout);
        Chatbot.Save();
    }

    public void RemoveBotShout(BotShout shout)
    {
        Chatbot.RemoveShout(shout);
        ObservableShouts.Remove(shout);
        Chatbot.Save();
    }

    public void SetAnnounceLive(bool enabled, string message)
    {
        Chatbot.AnnounceLiveEnabled = enabled;
        if (!string.IsNullOrWhiteSpace(message))
            Chatbot.AnnounceLiveMessage = message.Trim();
        Chatbot.Save();
    }

    public void SaveChatbotSettings(bool filterLinks, bool filterCaps, bool timeoutOnViolation,
        int timeoutSeconds, IEnumerable<string> blockedWords)
    {
        Chatbot.AutoMod.FilterLinks        = filterLinks;
        Chatbot.AutoMod.FilterAllCaps      = filterCaps;
        Chatbot.AutoMod.TimeoutOnViolation = timeoutOnViolation;
        Chatbot.AutoMod.TimeoutSeconds     = timeoutSeconds;
        Chatbot.AutoMod.BlockedWords.Clear();
        foreach (var w in blockedWords) Chatbot.AutoMod.BlockedWords.Add(w);
        Chatbot.Save();
    }

    // ── Bot account ───────────────────────────────────────────────────────────

    public async Task ConnectTwitchBotAsync(string token, string clientId)
    {
        var username = await Credentials.FetchTwitchUsernameAsync(token, clientId);
        Credentials.SaveTwitchBotLogin(token, username);
        await Twitch.ConnectBotAsync(username, $"oauth:{token}");
        IsTwitchBotConnected = true;
        TwitchBotUsername    = username;
    }

    public async Task DisconnectTwitchBotAsync()
    {
        await Twitch.DisconnectBotAsync();
        Credentials.ClearTwitchBotLogin();
        IsTwitchBotConnected = false;
        TwitchBotUsername    = "";
    }

    public void ConnectKickBot(string accessToken, string? refreshToken, string username)
    {
        Credentials.SaveKickBotLogin(accessToken, refreshToken, username);
        Kick.SetBotToken(accessToken, username);
        IsKickBotConnected = true;
        KickBotUsername    = username;
    }

    public void DisconnectKickBot()
    {
        Kick.ClearBotToken();
        Credentials.ClearKickBotLogin();
        IsKickBotConnected = false;
        KickBotUsername    = "";
    }

    public BotAuthState GetBotAuthState() => Credentials.GetBotAuthState();

    // ── Labels ────────────────────────────────────────────────────────────────
    public Task TestLabelAsync(int idx)
    {
        var testValues = new[]
        {
            "TestFollower", "TestSubscriber", "1234", "567", "8901", "2h 34m",
            "$12.50", "$99.99", "$250.00", "TestGifter",
        };
        string val = idx < testValues.Length ? testValues[idx] : "Test Value";
        return OverlayDispatcher.SendLabelUpdate(idx, val, "Twitch");
    }

    public Task ClearLabelAsync(int idx)   => OverlayDispatcher.SendLabelUpdate(idx, " ");

    public LabelConfig GetLabelConfig(int idx)
    {
        string key = idx.ToString();
        return Settings.Labels.TryGetValue(key, out var cfg) ? cfg : new LabelConfig();
    }

    public void SaveLabelLayout(int idx, string layoutJson)
    {
        string key = idx.ToString();
        if (!Settings.Labels.TryGetValue(key, out var cfg))
            cfg = new LabelConfig();
        cfg.LayoutJson = layoutJson;
        Settings.Labels[key] = cfg;
        Settings.Save();
        RefreshLabels();
    }

    public Task PushAllLabelsAsync()               => OverlayDispatcher.SendAllLabelsAsync();
    public Task PushGoalNamesAsync()               => OverlayDispatcher.SendGoalNamesAsync();
    public Task PushGoalProgressAsync(int idx, int current) => OverlayDispatcher.SendGoalProgress(idx, current);
    public Task RefreshLinkedGoalsAsync()          => OverlayDispatcher.RefreshLinkedGoalsAsync();
    public Task PushAllGoalsAsync()                => OverlayDispatcher.SendAllGoalsAsync();

    public Task PushLabelAsync(int idx)
    {
        var sd = OverlayDispatcher.StreamData;
        string value = idx switch
        {
            0 => sd?.RecentFollower   ?? "",
            1 => sd?.RecentSubscriber ?? "",
            2 => sd?.SubscriberCount.ToString("N0") ?? "",
            3 => sd?.ViewerCount.ToString("N0")     ?? "",
            4 => sd?.FollowerCount.ToString("N0")   ?? "",
            5 => sd?.GetUptime()      ?? "",
            6 => sd?.RecentDonation   ?? "",
            7 => sd?.TopDonation      ?? "",
            8 => sd?.DonationTotal.ToString("N0") ?? "",
            9 => sd?.RecentGiftSub    ?? "",
            _ => "",
        };
        return OverlayDispatcher.SendLabelUpdate(idx, value);
    }

    // ── Goals ─────────────────────────────────────────────────────────────────
    public void AddGoal()
    {
        int next = 0;
        while (Settings.Goals.ContainsKey(next.ToString())) next++;
        Settings.Goals[next.ToString()] = new GoalConfig { Title = $"Goal {next}", Target = 100, LinkType = "Manual" };
        Settings.Save();
        RefreshGoals();
    }

    public void DeleteGoal(int idx)
    {
        var ordered = Settings.Goals
            .Where(kv => int.TryParse(kv.Key, out _))
            .OrderBy(kv => int.Parse(kv.Key))
            .Where(kv => int.Parse(kv.Key) != idx)
            .Select(kv => kv.Value)
            .ToList();
        Settings.Goals.Clear();
        for (int i = 0; i < ordered.Count; i++)
            Settings.Goals[i.ToString()] = ordered[i];
        Settings.Save();
        RefreshGoals();
    }

    public bool TrySetGoalEnabled(int idx, bool enabled, out int current)
    {
        current = 0;
        if (!Settings.Goals.TryGetValue(idx.ToString(), out var cfg)) return false;
        cfg.Enabled = enabled; current = cfg.Current;
        Settings.Save(); RefreshGoals();
        return true;
    }

    public bool TrySetGoalTitle(int idx, string title)
    {
        if (!Settings.Goals.TryGetValue(idx.ToString(), out var cfg)) return false;
        cfg.Title = title; Settings.Save(); RefreshGoals(); return true;
    }

    public bool TrySetGoalTarget(int idx, int target)
    {
        if (target < 1) return false;
        if (!Settings.Goals.TryGetValue(idx.ToString(), out var cfg)) return false;
        cfg.Target = target; Settings.Save(); RefreshGoals(); return true;
    }

    public bool TrySetGoalCurrent(int idx, int current, out bool enabled)
    {
        enabled = false;
        if (current < 0) return false;
        if (!Settings.Goals.TryGetValue(idx.ToString(), out var cfg)) return false;
        cfg.Current = current; enabled = cfg.Enabled;
        Settings.Save(); RefreshGoals(); return true;
    }

    public bool TrySetGoalLinkType(int idx, string linkType)
    {
        if (!Settings.Goals.TryGetValue(idx.ToString(), out var cfg)) return false;
        cfg.LinkType = string.IsNullOrWhiteSpace(linkType) ? "Manual" : linkType;
        Settings.Save(); return true;
    }

    public GoalConfig? GetGoalConfig(int idx)
        => Settings.Goals.TryGetValue(idx.ToString(), out var cfg) ? cfg : null;

    public int GetGoalTestValue(int idx)
    {
        if (!Settings.Goals.TryGetValue(idx.ToString(), out var cfg)) return 0;
        return cfg.Current > 0 ? cfg.Current : cfg.Target / 2;
    }

    public void SaveGoalLayout(int idx, string layoutJson)
    {
        if (!Settings.Goals.TryGetValue(idx.ToString(), out var cfg)) return;
        cfg.LayoutJson = layoutJson; Settings.Save(); RefreshGoals();
    }

    // ── Chat overlay ──────────────────────────────────────────────────────────
    public ChatOverlayConfig GetChatOverlayConfig()
    {
        Settings.NormalizeChatOverlayProfiles();
        return Settings.ChatOverlay.Clone();
    }

    public IReadOnlyList<string> GetChatOverlayProfileNames(IEnumerable<string>? pluginSourceNames = null)
    {
        Settings.NormalizeChatOverlayProfiles();
        var names = pluginSourceNames?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (names != null && names.Count > 0)
            return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return Settings.ChatOverlayProfiles.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ChatOverlayConfig ApplyChatOverlaySourceSizes(IReadOnlyDictionary<string, (int W, int H)> sizes)
    {
        Settings.NormalizeChatOverlayProfiles();
        foreach (var (name, size) in sizes)
        {
            if (Settings.ChatOverlayProfiles.TryGetValue(name, out var profile))
            {
                profile.Width  = size.W;
                profile.Height = size.H;
            }
        }
        if (sizes.TryGetValue(Settings.ChatOverlay.SourceName, out var currentSize))
        {
            Settings.ChatOverlay.Width  = currentSize.W;
            Settings.ChatOverlay.Height = currentSize.H;
        }
        Settings.Save();
        return Settings.ChatOverlay.Clone();
    }

    public ChatOverlayConfig SelectChatOverlayProfile(string sourceName)
    {
        Settings.NormalizeChatOverlayProfiles();
        if (Settings.ChatOverlayProfiles.TryGetValue(sourceName, out var config))
            Settings.ChatOverlay = config.Clone();
        else
        {
            Settings.ChatOverlay = Settings.ChatOverlay.Clone();
            Settings.ChatOverlay.SourceName = sourceName;
        }
        Settings.Save();
        return Settings.ChatOverlay.Clone();
    }

    public void DeleteChatOverlayProfile(string sourceName)
    {
        Settings.NormalizeChatOverlayProfiles();
        Settings.ChatOverlayProfiles.Remove(sourceName);
        if (Settings.ChatOverlayProfiles.TryGetValue(Settings.ChatOverlay.SourceName, out var current))
            Settings.ChatOverlay = current.Clone();
        else
            Settings.ChatOverlay.SourceName = sourceName;
        Settings.Save();
    }

    public async Task<bool> SaveAndSendChatOverlayAsync(ChatOverlayConfig config)
    {
        Settings.ChatOverlay = config;
        Settings.ChatOverlayProfiles[config.SourceName] = config.Clone();
        Settings.Save();
        if (!Pipe.IsConnected) return false;
        var payload = ChatOverlaySettingsPayload.FromConfig(config).Serialize();
        await Pipe.SendAsync(PipeMessageType.UpdateChatSettings, payload);
        return true;
    }

    public async Task<bool> ClearObsChatAsync()
    {
        if (!Pipe.IsConnected) return false;
        await Pipe.SendAsync(PipeMessageType.Clear, Array.Empty<byte>());
        return true;
    }

    // Pushes a mixed set of pseudo chat messages straight to the OBS overlay AND the
    // in-app chat list so the overlay rendering can be compared against the app without
    // sending anything to Twitch/Kick/YouTube (or touching analytics/TTS/the chatbot).
    // Covers: single-platform, multi-platform "both"/"all", badge, and event lines.
    public async Task<bool> SendTestChatMessagesAsync()
    {
        if (!Pipe.IsConnected) return false;
        var ts = DateTimeOffset.Now;

        async Task Send(Platform platform, string icons, string user, string text, string color,
                        bool broadcaster = false, bool mod = false, bool sub = false, bool vip = false,
                        bool highlighted = false, int bits = 0, int subMonths = 0, string? appPrefix = null)
        {
            var payload = new ChatPayload(
                Platform:      platform.ToString(),
                PlatformIcons: icons,
                Username:      user,
                Message:       text,
                Color:         color,
                TimestampText: ts.ToLocalTime().ToString("HH:mm:ss"),
                IsBroadcaster: broadcaster,
                IsModerator:   mod,
                IsSubscriber:  sub,
                IsVip:         vip,
                IsHighlighted: highlighted,
                BitsAmount:    bits,
                SubMonths:     subMonths,
                BadgePaths:    new List<string>(),
                Emotes:        new List<EmoteSegment>());
            await OverlayDispatcher.SendChatPayloadAsync(payload);

            AddChatMessage(new ChatMessageItem(
                userId: "test", username: user, displayName: user, messageId: "",
                message: text, platform: platform, color: color, timestamp: ts,
                showTimestamp: ShowChatTimestampsInApp, prefixOverride: appPrefix));
        }

        // Single-platform lines (user colours per platform)
        await Send(Platform.Twitch,  "Twitch",  "TwitchViewer", "Single-platform Twitch test 👋", "#9146FF");
        await Send(Platform.Kick,    "Kick",    "KickFan",      "Single-platform Kick line here",  "#53FC18");
        await Send(Platform.YouTube, "YouTube", "YT_Watcher",   "Single-platform YouTube message", "#FF4E45");
        // Multi-platform sent lines ("both" / "all")
        await Send(Platform.Twitch,  "Twitch|Kick",         "You", "Sent to BOTH Twitch + Kick",  "#FFFFFF", appPrefix: "[T][K]");
        await Send(Platform.Twitch,  "Twitch|Kick|YouTube", "You", "Sent to ALL live platforms",  "#FFFFFF", appPrefix: "[T][K][Y]");
        // Badge lines
        await Send(Platform.Twitch,  "Twitch",  "ModSquad",     "Moderator + VIP badge test", "#1E90FF", mod: true, vip: true);
        await Send(Platform.Kick,    "Kick",    "StreamerRob",  "Broadcaster badge test",     "#FF0000", broadcaster: true);
        // Event-style lines (bits / super chat / highlighted resub)
        await Send(Platform.Twitch,  "Twitch",  "CheerLeader",  "Cheer500 amazing stream!!",  "#9146FF", sub: true, bits: 500, subMonths: 6);
        await Send(Platform.YouTube, "YouTube", "BigFan",       "Thanks for the content! (Super Chat)", "#FF4E45", highlighted: true, bits: 1000);
        await Send(Platform.Twitch,  "Twitch",  "LoyalSub",     "12-month resub, highlighted line", "#9146FF", sub: true, highlighted: true, subMonths: 12);

        return true;
    }
}
