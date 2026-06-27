using Microsoft.Extensions.Logging;
using Steaming.Core.Ipc;
using Steaming.Core.Models;
using EmoteSegment = Steaming.Core.Ipc.EmoteSegment;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Steaming.Core.Services;

// Label type indices — must match label_source.h LabelType enum
internal static class LabelTypeIndex
{
    public const int RecentFollower   = 0;
    public const int RecentSubscriber = 1;
    public const int SubscriberCount  = 2;
    public const int ViewerCount      = 3;
    public const int FollowerCount    = 4;
    public const int StreamUptime     = 5;
    public const int RecentDonation   = 6;
    public const int TopDonation      = 7;
    public const int DonationTotal    = 8;
    public const int RecentGiftSub    = 9;
}

// Goal type indices — dynamic (user-defined goals). These constants are kept for
// the auto-updating labels (viewer/follower/sub counts from StreamDataService).
// User-defined goals beyond index 5 are also supported.
internal static class GoalTypeIndex
{
    public const int Followers        = 0;
    public const int Subscribers      = 1;
    public const int DailyFollowers   = 2;
    public const int DailySubscribers = 3;
    public const int Custom           = 4;
    public const int Donation         = 5;
}

// Subscribes to EventBus and forwards events to the OBS plugin via named pipe.
// Uses AppSettings text templates and durations configured by the user.
[SupportedOSPlatform("windows")]
public class OverlayDispatcher
{
    private sealed class PendingOutgoingOverlayEcho
    {
        public required string Message { get; init; }
        public required HashSet<string> SenderNames { get; init; }
        public required HashSet<string> SenderIds { get; init; }
        public required HashSet<Platform> RemainingPlatforms { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private readonly EventBus _bus;
    private readonly PluginPipeServer _pipe;
    private readonly AppSettings _settings;
    private readonly ILogger<OverlayDispatcher> _logger;
    private readonly object _pendingOutgoingChatLock = new();
    private readonly List<PendingOutgoingOverlayEcho> _pendingOutgoingChat = [];
    // StreamDataService reference — set by MainWindow after starting it
    public StreamDataService? StreamData { get; set; }

    public OverlayDispatcher(EventBus bus, PluginPipeServer pipe,
                             AppSettings settings, ILogger<OverlayDispatcher> logger)
    {
        _bus      = bus;
        _pipe     = pipe;
        _settings = settings;
        _logger   = logger;
    }

    private bool _started;
    public void Start()
    {
        if (_started) return;
        _started = true;
        _bus.Subscribe(OnEvent);
        EmoteCache.Instance.OnDownloadComplete += OnEmoteDownloadComplete;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _bus.Unsubscribe(OnEvent);
        EmoteCache.Instance.OnDownloadComplete -= OnEmoteDownloadComplete;
    }

    private void OnEmoteDownloadComplete()
        => _ = _pipe.SendAsync(PipeMessageType.RefreshChat, Array.Empty<byte>());

    private async Task OnEvent(StreamEvent evt)
    {
        try
        {
            if (evt.Type != EventType.Chat)
                LogDebug($"OnEvent received. Type={evt.Type} Platform={evt.Platform} User={evt.User.DisplayName}");

            switch (evt.Type)
            {
                case EventType.Chat:
                    await SendChat(evt);
                    break;

                case EventType.Follow:
                    await SendConfiguredAlert(evt, AlertType.Follow, "Follow",
                        "{user} just followed!", 0);
                    if (_settings.ChatOverlay.ShowFollowEvents)
                        await SendOverlayEventChat(evt, "just followed!");
                    // Update Recent Follower label
                    if (StreamData != null) await StreamData.NoteFollowAsync(evt.Platform, evt.User.DisplayName);
                    await SendLabelUpdate(LabelTypeIndex.RecentFollower, evt.User.DisplayName, evt.Platform.ToString());
                    // Emoji rain
                    if (_settings.EmojiRain.TriggerOnFollow)
                        await TriggerEmojiRain(_settings.EmojiRain.FollowEmojis, _settings.EmojiRain.FollowGif,
                            _settings.EmojiRain.FollowColor, _settings.EmojiRain.CountPerTrigger);
                    break;

                case EventType.Subscribe:
                {
                    var months = GetInt(evt, "months");
                    await SendConfiguredAlert(evt, AlertType.Subscribe, "Subscribe",
                        "{user} subscribed! ({months} months)", months);
                    if (_settings.ChatOverlay.ShowSubscriptionEvents)
                        await SendOverlayEventChat(evt, $"subscribed! ({months} months)");
                    if (StreamData != null) StreamData.RecentSubscriber = evt.User.DisplayName;
                    if (StreamData != null) await StreamData.NoteSubscribeAsync(evt.Platform);
                    await SendLabelUpdate(LabelTypeIndex.RecentSubscriber, evt.User.DisplayName, evt.Platform.ToString());
                    if (_settings.EmojiRain.TriggerOnSubscribe)
                        await TriggerEmojiRain(_settings.EmojiRain.SubscribeEmojis, _settings.EmojiRain.SubscribeGif,
                            _settings.EmojiRain.SubscribeColor, _settings.EmojiRain.CountPerTrigger);
                    break;
                }

                case EventType.GiftSubscribe:
                {
                    var recipient = GetStr(evt, "recipient");
                    await SendConfiguredAlert(evt, AlertType.GiftSub, "GiftSubscribe",
                        $"{{user}} gifted a sub to {recipient}!", 0);
                    if (_settings.ChatOverlay.ShowSubscriptionEvents)
                        await SendOverlayEventChat(evt, $"gifted a sub to {recipient}!");
                    if (StreamData != null) StreamData.RecentGiftSub = evt.User.DisplayName;
                    if (StreamData != null) await StreamData.NoteSubscribeAsync(evt.Platform, Math.Max(1, GetInt(evt, "count")));
                    await SendLabelUpdate(LabelTypeIndex.RecentGiftSub, evt.User.DisplayName, evt.Platform.ToString());
                    break;
                }

                case EventType.Bits:
                {
                    var bits = GetInt(evt, "bits");
                    var amountDisplay = GetStr(evt, "amountDisplay");
                    await SendConfiguredAlert(evt, AlertType.Bits, "Bits",
                        evt.Platform == Platform.YouTube ? "{user} sent {amountDisplay}!" : "{user} cheered {amount} bits!", bits);
                    if (_settings.ChatOverlay.ShowDonationEvents)
                        await SendOverlayEventChat(evt, evt.Platform == Platform.YouTube ? $"sent {amountDisplay}" : "cheered!", bitsAmount: bits);
                    // Update donation labels
                    string donationStr = evt.Platform == Platform.YouTube && !string.IsNullOrWhiteSpace(amountDisplay)
                        ? $"{evt.User.DisplayName}: {amountDisplay}"
                        : $"{evt.User.DisplayName}: {bits} bits";
                    if (StreamData != null)
                    {
                        StreamData.RecentDonation = donationStr;
                        StreamData.DonationTotal += bits;
                    }
                    await SendLabelUpdate(LabelTypeIndex.RecentDonation, donationStr, evt.Platform.ToString());
                    if (_settings.EmojiRain.TriggerOnBits)
                        await TriggerEmojiRain(_settings.EmojiRain.BitsEmojis, _settings.EmojiRain.BitsGif,
                            _settings.EmojiRain.BitsColor, _settings.EmojiRain.CountPerTrigger);
                    await UpdateLinkedGoalsAsync();
                    break;
                }

                case EventType.Raid:
                {
                    var viewers = GetInt(evt, "viewers");
                    await SendConfiguredAlert(evt, AlertType.Raid, "Raid",
                        "{user} raided with {amount} viewers!", viewers);
                    if (_settings.ChatOverlay.ShowRaidEvents)
                        await SendOverlayEventChat(evt, $"raided with {viewers} viewers!");
                    if (_settings.EmojiRain.TriggerOnRaid)
                        await TriggerEmojiRain(_settings.EmojiRain.RaidEmojis, _settings.EmojiRain.RaidGif,
                            _settings.EmojiRain.RaidColor, _settings.EmojiRain.CountPerTrigger);
                    break;
                }

                case EventType.ChannelPointRedemption:
                {
                    var rewardCost = GetInt(evt, "rewardCost");
                    if (TryGetMatchingCustomRewardAlert(evt, out _, out var rewardAlert))
                    {
                        await SendCustomAlertAsync(
                            rewardAlert,
                            evt.User.DisplayName,
                            GetStr(evt, "rewardTitle"),
                            rewardCost,
                            reward: GetStr(evt, "rewardTitle"),
                            input: GetStr(evt, "userInput"),
                            platform: evt.Platform.ToString());
                    }
                    else
                    {
                        await SendConfiguredAlert(evt, AlertType.RewardRedemption, "RewardRedemption",
                            "{user} redeemed {reward} for {amount}: {input}", rewardCost);
                    }

                    if (_settings.ChatOverlay.ShowChannelPointRedemptions)
                        await SendOverlayEventChat(evt, GetStr(evt, "rewardTitle", "redeemed channel points"));
                    break;
                }

                case EventType.KicksGifted:
                {
                    // Kick's monetary gifting is the equivalent of Twitch bits/cheers, so it reuses the
                    // configured "Bits" alert config (layout/sound/duration the user already set up and
                    // tested) rather than an unconfigured "KicksGifted" key that would fire a blank
                    // default. AlertType is vestigial in the V2 path (not serialized), so this needs no
                    // C++/wire change.
                    var kicks = GetInt(evt, "amount");
                    await SendConfiguredAlert(evt, AlertType.Bits, "Bits",
                        "{user} gifted {amount} Kicks!", kicks);
                    if (_settings.ChatOverlay.ShowDonationEvents)
                        await SendOverlayEventChat(evt, $"gifted {kicks} Kicks!");
                    string kicksStr = $"{evt.User.DisplayName}: {kicks} Kicks";
                    if (StreamData != null)
                    {
                        StreamData.RecentDonation = kicksStr;
                        StreamData.DonationTotal += kicks;
                    }
                    await SendLabelUpdate(LabelTypeIndex.RecentDonation, kicksStr, evt.Platform.ToString());
                    if (_settings.EmojiRain.TriggerOnBits)
                        await TriggerEmojiRain(_settings.EmojiRain.BitsEmojis, _settings.EmojiRain.BitsGif,
                            _settings.EmojiRain.BitsColor, _settings.EmojiRain.CountPerTrigger);
                    await UpdateLinkedGoalsAsync();
                    break;
                }

                case EventType.Achievement:
                {
                    // StreamDataUpdated pseudo-event from StreamDataService
                    if (GetStr(evt, "type") == "StreamDataUpdated")
                    {
                        int vc  = GetInt(evt, "viewerCount");
                        int fc  = GetInt(evt, "followerCount");
                        int sc  = GetInt(evt, "subscriberCount");
                        string up = StreamData?.GetUptime() ?? "—";
                        await SendLabelUpdate(LabelTypeIndex.ViewerCount,     vc.ToString("N0"));
                        await SendLabelUpdate(LabelTypeIndex.FollowerCount,   fc.ToString("N0"));
                        await SendLabelUpdate(LabelTypeIndex.SubscriberCount, sc.ToString("N0"));
                        await SendLabelUpdate(LabelTypeIndex.StreamUptime,    up);
                        await UpdateLinkedGoalsAsync();
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Dispatcher] Failed to forward event: {Message}", ex.Message);
        }
    }

    // ── Label / Goal / Emoji Rain helpers ────────────────────────────────────────

    // Sends a label update (design + live value) to OBS for the given label type.
    // If no custom layout is configured, sends a minimal default text-only ALT3.
    public async Task SendLabelUpdate(int labelTypeIdx, string value, string platform = "")
    {
        string key = labelTypeIdx.ToString();
        AlertLayout? layout = null;
        if (_settings.Labels.TryGetValue(key, out var cfg) && !string.IsNullOrEmpty(cfg.LayoutJson))
            layout = AlertLayout.FromJson(cfg.LayoutJson);
        layout ??= AlertLayout.CreateDefaultLabel();

        // value → both username and message so {user} and {value}/{current} all resolve in label layouts
        var payload = layout.Serialize(value, value, 0, 0f, platform);
        var msg = new byte[1 + payload.Length];
        msg[0] = (byte)labelTypeIdx;
        payload.CopyTo(msg, 1);
        await _pipe.SendAsync(PipeMessageType.SetLabelLayout, msg);
    }

    // Sends a goal layout + current progress to OBS for the given goal type.
    public async Task SendGoalProgress(int goalTypeIdx, int current)
    {
        string key = goalTypeIdx.ToString();
        if (!_settings.Goals.TryGetValue(key, out var cfg)) return;
        if (!cfg.Enabled) return;

        AlertLayout? layout = null;
        if (!string.IsNullOrEmpty(cfg.LayoutJson))
            layout = AlertLayout.FromJson(cfg.LayoutJson);
        layout ??= AlertLayout.CreateDefaultGoal(cfg.Title, cfg.Target);

        // current → message field (string), target → amount field
        // C++ GoalBar element reads these for width scaling
        var payload = layout.Serialize(cfg.Title, current.ToString(), cfg.Target, 0f);
        var msg = new byte[1 + payload.Length];
        msg[0] = (byte)goalTypeIdx;
        payload.CopyTo(msg, 1);
        await _pipe.SendAsync(PipeMessageType.SetGoalLayout, msg);
    }

    // Sends the full current label layout + value for every configured label type.
    // Called on pipe connect so OBS sources immediately show content.
    public async Task SendAllLabelsAsync()
    {
        string[] names = { "Recent Follower","Recent Subscriber","Subscribers","Viewers",
                            "Followers","Uptime","Recent Donation","Top Donation","Total Donated","Recent Gift Sub" };
        string[] values = {
            StreamData?.RecentFollower   ?? "",
            StreamData?.RecentSubscriber ?? "",
            StreamData?.SubscriberCount.ToString("N0") ?? "",
            StreamData?.ViewerCount.ToString("N0")     ?? "",
            StreamData?.FollowerCount.ToString("N0")   ?? "",
            StreamData?.GetUptime()      ?? "",
            StreamData?.RecentDonation   ?? "",
            StreamData?.TopDonation      ?? "",
            StreamData?.DonationTotal.ToString("N0") ?? "",
            StreamData?.RecentGiftSub    ?? "",
        };
        for (int i = 0; i < values.Length; i++)
            await SendLabelUpdate(i, values[i]);
    }

    // Sends all goal layouts to OBS. Called on pipe connect.
    public async Task SendAllGoalsAsync()
    {
        await UpdateLinkedGoalsAsync();

        for (int i = 0; i < 6; i++)
        {
            string key = i.ToString();
            if (_settings.Goals.TryGetValue(key, out var cfg) && cfg.Enabled && !IsLinkedGoal(cfg))
                await SendGoalProgress(i, cfg.Current);
        }
    }

    public Task RefreshLinkedGoalsAsync() => UpdateLinkedGoalsAsync();

    private async Task UpdateLinkedGoalsAsync()
    {
        if (StreamData == null) return;

        foreach (var kv in _settings.Goals)
        {
            if (!int.TryParse(kv.Key, out var idx))
                continue;

            var cfg = kv.Value;
            var linkType = string.IsNullOrWhiteSpace(cfg.LinkType) ? "Manual" : cfg.LinkType;
            int? current = linkType switch
            {
                "Followers" => StreamData.FollowerCount,
                "Subscribers" => StreamData.SubscriberCount,
                "Viewers" => StreamData.ViewerCount,
                "DonationTotalBits" => (int)Math.Round(StreamData.DonationTotal),
                _ => null
            };

            if (!current.HasValue)
                continue;

            cfg.Current = current.Value;
            if (cfg.Enabled)
                await SendGoalProgress(idx, cfg.Current);
        }
    }

    private static bool IsLinkedGoal(GoalConfig cfg)
    {
        var linkType = string.IsNullOrWhiteSpace(cfg.LinkType) ? "Manual" : cfg.LinkType;
        return linkType != "Manual";
    }

    // Sends all saved chat overlay profiles to OBS. Called on pipe connect so
    // OBS sources immediately use the settings from the C# app rather than
    // whatever was last saved in the OBS properties file.
    public async Task SendAllChatSettingsAsync()
    {
        foreach (var config in _settings.ChatOverlayProfiles.Values)
        {
            var payload = ChatOverlaySettingsPayload.FromConfig(config).Serialize();
            await _pipe.SendAsync(PipeMessageType.UpdateChatSettings, payload);
        }
    }

    // Sends EmojiRainSettings to OBS whenever config changes.
    public async Task SendEmojiRainSettingsAsync()
    {
        var cfg = _settings.EmojiRain;
        var payload = new byte[8];
        payload[0] = (byte)Math.Clamp(cfg.EmojiSize, 8, 200);
        var spd = (ushort)Math.Clamp(cfg.FallSpeed, 50, 3000);
        payload[1] = (byte)(spd & 0xFF);
        payload[2] = (byte)(spd >> 8);
        payload[3] = (byte)Math.Clamp(cfg.ParticleLifeSec, 1, 30);
        payload[4] = (byte)Math.Clamp(cfg.MaxParticles, 1, 200);
        payload[5] = (byte)Math.Clamp(cfg.Spread, 0, 100);
        payload[6] = cfg.FadeOut ? (byte)1 : (byte)0;
        payload[7] = cfg.Spin    ? (byte)1 : (byte)0;
        await _pipe.SendAsync(PipeMessageType.EmojiRainSettings, payload);
    }

    // Sends TriggerEmojiRain to OBS.
    // Wire: [1]isGif [4]color_argb [2+N]content_utf8 [1]count
    private async Task TriggerEmojiRain(string emojis, string? gifPath, uint colorArgb, int count)
    {
        bool isGif = !string.IsNullOrEmpty(gifPath);
        string content = isGif ? gifPath! : emojis;
        if (string.IsNullOrEmpty(content)) return;
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var payload = new byte[1 + 4 + 2 + contentBytes.Length + 1];
        int off = 0;
        payload[off++] = isGif ? (byte)1 : (byte)0;
        payload[off++] = (byte)((colorArgb >> 24) & 0xFF); // A
        payload[off++] = (byte)((colorArgb >> 16) & 0xFF); // R
        payload[off++] = (byte)((colorArgb >>  8) & 0xFF); // G
        payload[off++] = (byte)((colorArgb      ) & 0xFF); // B
        payload[off++] = (byte)(contentBytes.Length & 0xFF);
        payload[off++] = (byte)(contentBytes.Length >> 8);
        contentBytes.CopyTo(payload, off);
        off += contentBytes.Length;
        payload[off] = (byte)Math.Clamp(count, 1, 255);
        await _pipe.SendAsync(PipeMessageType.TriggerEmojiRain, payload);
    }

    // Sends goal names to C++ so the OBS goal_type dropdown is populated.
    // Wire: [2]count [2+N]name0_utf8 [2+N]name1_utf8 ...
    public async Task SendGoalNamesAsync()
    {
        var goals = _settings.Goals;
        var names = new List<byte[]>();
        for (int i = 0; ; i++)
        {
            if (!goals.TryGetValue(i.ToString(), out var cfg)) break;
            names.Add(Encoding.UTF8.GetBytes(cfg.Title ?? $"Goal {i}"));
        }

        int total = 2; // count u16
        foreach (var n in names) total += 2 + n.Length;
        var payload = new byte[total];
        int off = 0;
        payload[off++] = (byte)(names.Count & 0xFF);
        payload[off++] = (byte)(names.Count >> 8);
        foreach (var n in names) {
            payload[off++] = (byte)(n.Length & 0xFF);
            payload[off++] = (byte)(n.Length >> 8);
            n.CopyTo(payload, off);
            off += n.Length;
        }
        await _pipe.SendAsync(PipeMessageType.SetGoalNames, payload);
    }

    // Fires a user-defined "Unique" alert (created on the Alerts page) on demand — e.g. from a
    // bot command. Same wire path as event alerts: the full layout is serialized into RenderAlertV2,
    // so the C++ alert source needs no knowledge of custom alert types.
    public async Task SendCustomAlertAsync(
        EventConfig cfg,
        string user,
        string message,
        int amount = 0,
        string? reward = null,
        string? input = null,
        string? platform = null)
    {
        AlertLayout? layout = !string.IsNullOrEmpty(cfg.LayoutJson) ? AlertLayout.FromJson(cfg.LayoutJson) : null;
        layout ??= AlertLayout.CreateDefault();

        var duration = cfg.Duration > 0 ? cfg.Duration : 5.0f;
        var text = string.IsNullOrEmpty(cfg.Text) ? message : cfg.Text;
        var resolvedMessage = text
            .Replace("{user}",   user)
            .Replace("{amount}", amount.ToString())
            .Replace("{arg}",    message)
            .Replace("{input}",  input ?? message)
            .Replace("{reward}", reward ?? message)
            .Replace("{platform}", platform ?? "");

        var payload = layout.Serialize(user, resolvedMessage, amount, duration);
        LogDebug($"OverlayDispatcher sending custom alert. User={user} Duration={duration}");
        await _pipe.SendAsync(PipeMessageType.RenderAlertV2, payload);
    }

    internal bool TryGetMatchingCustomRewardAlert(StreamEvent evt, out string matchedName, out EventConfig matchedConfig)
        => CustomAlertMatcher.TryResolveRewardAlert(
            _settings,
            GetStr(evt, "rewardId"),
            GetStr(evt, "rewardTitle"),
            evt.Platform.ToString(),
            out matchedName,
            out matchedConfig);

    private async Task SendConfiguredAlert(StreamEvent evt, AlertType alertType,
        string settingsKey, string fallbackTemplate, int amount)
    {
        var text     = fallbackTemplate;
        var duration = 5.0f;
        AlertLayout? layout = null;

        if (_settings.Events.TryGetValue(settingsKey, out var cfg))
        {
            if (!cfg.Enabled) { LogDebug($"SendConfiguredAlert skipped — {settingsKey} is disabled."); return; }
            duration = cfg.Duration;
            text     = cfg.Text;
            if (evt.Platform == Platform.YouTube &&
                settingsKey == "Bits" &&
                string.Equals(text, "{user} cheered {amount} bits!", StringComparison.Ordinal))
            {
                text = "{user} sent {amountDisplay}!";
            }
            if (!string.IsNullOrEmpty(cfg.LayoutJson))
                layout = AlertLayout.FromJson(cfg.LayoutJson);
        }
        else { LogDebug($"SendConfiguredAlert — no config entry for key '{settingsKey}', using defaults."); }

        layout ??= AlertLayout.CreateDefault();

        var resolvedMessage = text
            .Replace("{user}",     evt.User.DisplayName)
            .Replace("{platform}", evt.Platform.ToString())
            .Replace("{amount}",   amount.ToString())
            .Replace("{amountDisplay}", GetStr(evt, "amountDisplay"))
            .Replace("{months}",   amount.ToString())
            .Replace("{target}",   GetStr(evt, "recipient"))
            .Replace("{reward}",   GetStr(evt, "rewardTitle"))
            .Replace("{input}",    GetStr(evt, "userInput"));

        var payload = layout.Serialize(evt.User.DisplayName, resolvedMessage, amount, duration);
        LogDebug($"OverlayDispatcher sending V2 alert. Key={settingsKey} User={evt.User.DisplayName} Duration={duration}");
        await _pipe.SendAsync(PipeMessageType.RenderAlertV2, payload);
    }

    private async Task SendChat(StreamEvent evt)
    {
        var message = GetStr(evt, "message");
        var color   = GetStr(evt, "color", "#FFFFFF");
        if (TryConsumePendingOutgoingChatEcho(evt.Platform, evt.User, message))
            return;

        var emotes = evt.Data.TryGetValue("emotes", out var em)
            ? em as List<EmoteSegment> ?? new()
            : new List<EmoteSegment>();

        var badgePaths = evt.Data.TryGetValue("badgePaths", out var bp)
            ? bp as List<string> ?? new()
            : new List<string>();

        var payload = new ChatPayload(
            Platform:      evt.Platform.ToString(),
            PlatformIcons: GetStr(evt, "platformIcons", evt.Platform.ToString()),
            Username:      evt.User.DisplayName,
            Message:       message,
            Color:         color,
            TimestampText: evt.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
            IsBroadcaster: GetBool(evt, "isBroadcaster"),
            IsModerator:   GetBool(evt, "isModerator"),
            IsSubscriber:  GetBool(evt, "isSubscriber"),
            IsVip:         GetBool(evt, "isVip"),
            IsHighlighted: GetBool(evt, "isHighlighted"),
            BitsAmount:    GetInt(evt, "bits"),
            SubMonths:     GetInt(evt, "subMonths"),
            BadgePaths:    badgePaths,
            Emotes:        emotes);

        LogDebug($"OverlayDispatcher sending chat. Platform={evt.Platform} User={evt.User.DisplayName} Badges=mod:{GetBool(evt,"isModerator")} sub:{GetBool(evt,"isSubscriber")} Emotes={emotes.Count}");
        await _pipe.SendAsync(PipeMessageType.RenderChat, payload.Serialize());
    }

    // Sends a fully-specified chat line straight to the OBS overlay over the pipe.
    // No platform send and no EventBus publish — used by the "Send test messages"
    // overlay-verification feature so nothing reaches Twitch/Kick/YouTube, analytics,
    // TTS or the chatbot.
    public async Task SendChatPayloadAsync(ChatPayload payload)
    {
        if (!_pipe.IsConnected) return;
        await _pipe.SendAsync(PipeMessageType.RenderChat, payload.Serialize());
    }

    private async Task SendOverlayEventChat(StreamEvent evt, string message, int bitsAmount = 0)
    {
        var payload = new ChatPayload(
            Platform:      evt.Platform.ToString(),
            PlatformIcons: evt.Platform.ToString(),
            Username:      evt.User.DisplayName,
            Message:       message,
            Color:         "#FFFFFF",
            TimestampText: evt.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
            BitsAmount:    bitsAmount,
            BadgePaths: new List<string>(),
            Emotes: new List<EmoteSegment>());

        await _pipe.SendAsync(PipeMessageType.RenderChat, payload.Serialize());
    }

    public async Task SendLocalOutgoingChatEchoAsync(
        string message,
        string displayName,
        IReadOnlyCollection<string> senderNames,
        IReadOnlyCollection<string> senderIds,
        IReadOnlyCollection<Platform> platforms,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(message) || platforms.Count == 0)
            return;

        lock (_pendingOutgoingChatLock)
        {
            CleanupExpiredPendingOutgoingChat(DateTimeOffset.UtcNow);
            _pendingOutgoingChat.Add(new PendingOutgoingOverlayEcho
            {
                Message = message,
                SenderNames = new HashSet<string>(senderNames, StringComparer.OrdinalIgnoreCase),
                SenderIds = new HashSet<string>(senderIds, StringComparer.OrdinalIgnoreCase),
                RemainingPlatforms = [.. platforms],
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var primaryPlatform = platforms.First();
        var payload = new ChatPayload(
            Platform:      primaryPlatform.ToString(),
            PlatformIcons: BuildPlatformIconsText(platforms),
            Username:      displayName,
            Message:       message,
            Color:         "#FFFFFF",
            TimestampText: timestamp.ToLocalTime().ToString("HH:mm:ss"),
            BadgePaths:    new List<string>(),
            Emotes:        new List<EmoteSegment>());

        LogDebug($"OverlayDispatcher sending local outgoing chat. Platforms={payload.PlatformIcons} User={displayName}");
        await _pipe.SendAsync(PipeMessageType.RenderChat, payload.Serialize());
    }

    private static bool GetBool(StreamEvent e, string key)
        => e.Data.TryGetValue(key, out var v) && v is bool b && b;

    private static string GetStr(StreamEvent e, string key, string def = "")
        => e.Data.TryGetValue(key, out var v) ? v?.ToString() ?? def : def;

    private static int GetInt(StreamEvent e, string key)
        => e.Data.TryGetValue(key, out var v) ? Convert.ToInt32(v) : 0;

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

    private static string BuildPlatformIconsText(IReadOnlyCollection<Platform> platforms)
    {
        var parts = new List<string>(3);
        if (platforms.Contains(Platform.Twitch)) parts.Add(Platform.Twitch.ToString());
        if (platforms.Contains(Platform.Kick)) parts.Add(Platform.Kick.ToString());
        if (platforms.Contains(Platform.YouTube)) parts.Add(Platform.YouTube.ToString());
        return parts.Count > 0 ? string.Join('|', parts) : Platform.Twitch.ToString();
    }

    private void LogDebug(string message)
    {
        try { DebugLogFile.Append(message); } catch { }
    }
}
