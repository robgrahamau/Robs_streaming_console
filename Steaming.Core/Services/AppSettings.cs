using System.Text.Json;
using Steaming.Core.Models;

namespace Steaming.Core.Services;

public class EventConfig
{
    public bool    Enabled   { get; set; } = true;
    public string  Text      { get; set; } = "";
    public int     Duration  { get; set; } = 5;
    public string? SoundFile { get; set; }
    public string? ImageFile { get; set; }
    public float   Volume    { get; set; } = 1.0f;
    public string? LayoutJson { get; set; }  // serialised AlertLayout; null = use default
}

public class ChatOverlayConfig
{
    public string SourceName { get; set; } = "Steaming Chat";
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 600;
    public int Margin { get; set; } = 8;
    public string BackgroundColor { get; set; } = "#101010";
    public int BackgroundOpacity { get; set; } = 180;
    public string TextColor { get; set; } = "#E1E1E1";
    public string BitsColor { get; set; } = "#FFD700";
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontSize { get; set; } = 20;
    public int FontWeight { get; set; } = 700;
    public string TextAlign { get; set; } = "Left";
    public int TextShadow { get; set; } = 0;
    public int OutlineSize { get; set; } = 0;
    public int LineSpacing { get; set; } = 6;
    public int MessagePadding { get; set; } = 8;
    public int MaxLinesShown { get; set; } = 20;
    public bool ShowChatMessages { get; set; } = true;
    public bool ShowFollowEvents { get; set; } = true;
    public bool ShowSubscriptionEvents { get; set; } = true;
    public bool ShowDonationEvents { get; set; } = true;
    public bool ShowRaidEvents { get; set; } = true;
    public bool ShowChannelPointRedemptions { get; set; } = true;
    public string MessageStyle { get; set; } = "BottomUp";
    public bool ShowPlatformIcon { get; set; } = true;
    public string BadgePlacement { get; set; } = "BeforeUsername";
    public string DisplayNameColorMode { get; set; } = "UserColor";
    public bool DisappearMessages { get; set; } = false;
    public int DisappearAfterSeconds { get; set; } = 360;
    public bool FadeMessages { get; set; } = false;
    public int FadeSeconds { get; set; } = 30;
    public string PlatformFilter { get; set; } = "All";
    public bool ShowTimestamps { get; set; } = false;

    public ChatOverlayConfig Clone() => new()
    {
        SourceName = SourceName,
        Width = Width,
        Height = Height,
        Margin = Margin,
        BackgroundColor = BackgroundColor,
        BackgroundOpacity = BackgroundOpacity,
        TextColor = TextColor,
        BitsColor = BitsColor,
        FontFamily = FontFamily,
        FontSize = FontSize,
        FontWeight = FontWeight,
        TextAlign = TextAlign,
        TextShadow = TextShadow,
        OutlineSize = OutlineSize,
        LineSpacing = LineSpacing,
        MessagePadding = MessagePadding,
        MaxLinesShown = MaxLinesShown,
        ShowChatMessages = ShowChatMessages,
        ShowFollowEvents = ShowFollowEvents,
        ShowSubscriptionEvents = ShowSubscriptionEvents,
        ShowDonationEvents = ShowDonationEvents,
        ShowRaidEvents = ShowRaidEvents,
        ShowChannelPointRedemptions = ShowChannelPointRedemptions,
        MessageStyle = MessageStyle,
        ShowPlatformIcon = ShowPlatformIcon,
        BadgePlacement = BadgePlacement,
        DisplayNameColorMode = DisplayNameColorMode,
        DisappearMessages = DisappearMessages,
        DisappearAfterSeconds = DisappearAfterSeconds,
        FadeMessages = FadeMessages,
        FadeSeconds = FadeSeconds,
        PlatformFilter = PlatformFilter,
        ShowTimestamps = ShowTimestamps,
    };
}

// ── Label configuration (one per label type) ─────────────────────────────────
public class LabelConfig
{
    public string? LayoutJson { get; set; }  // serialised AlertLayout; null = blank / no source yet
}

// ── Goal configuration (one per goal type) ───────────────────────────────────
public class GoalConfig
{
    public bool    Enabled      { get; set; } = false;
    public string  Title        { get; set; } = "Goal";
    public int     Target       { get; set; } = 100;
    public int     Current      { get; set; } = 0;
    public string  LinkType     { get; set; } = "Manual";
    public string? LayoutJson   { get; set; }  // serialised AlertLayout
}

// ── Emoji Rain configuration ──────────────────────────────────────────────────
public class EmojiRainConfig
{
    public bool   TriggerOnFollow    { get; set; } = true;
    public bool   TriggerOnSubscribe { get; set; } = true;
    public bool   TriggerOnBits      { get; set; } = false;
    public bool   TriggerOnRaid      { get; set; } = true;
    public string FollowEmojis       { get; set; } = "❤️";
    public string SubscribeEmojis    { get; set; } = "⭐";
    public string BitsEmojis         { get; set; } = "💎";
    public string RaidEmojis         { get; set; } = "⚡";
    // Per-trigger color (ARGB, 0xFFRRGGBB). White = default (no tint on GDI text).
    public uint   FollowColor        { get; set; } = 0xFFFFFFFF;
    public uint   SubscribeColor     { get; set; } = 0xFFFFD700;
    public uint   BitsColor          { get; set; } = 0xFF00BFFF;
    public uint   RaidColor          { get; set; } = 0xFFFF6600;
    // Per-trigger GIF path (null = use emoji text instead)
    public string? FollowGif         { get; set; } = null;
    public string? SubscribeGif      { get; set; } = null;
    public string? BitsGif           { get; set; } = null;
    public string? RaidGif           { get; set; } = null;
    public int    CountPerTrigger    { get; set; } = 20;
    // Particle appearance sent to C++ via EmojiRainSettings pipe message
    public int    EmojiSize          { get; set; } = 48;
    public int    FallSpeed          { get; set; } = 400;
    public int    ParticleLifeSec    { get; set; } = 4;
    public int    MaxParticles       { get; set; } = 100;
    public int    Spread             { get; set; } = 30;  // 0–100
    public bool   FadeOut            { get; set; } = true;
    public bool   Spin               { get; set; } = false;
}

// ── Music player configuration ────────────────────────────────────────────────
// A user-curated, named playlist. Tracks are stored as absolute file paths and resolved
// against the scanned library (or loaded directly) when shown.
public class MusicPlaylist
{
    public string       Name       { get; set; } = "";
    public List<string> TrackPaths { get; set; } = new();
}

public class MusicConfig
{
    public string  LibraryRoot     { get; set; } = "";   // root folder scanned recursively
    public string  OutputDeviceId  { get; set; } = "";   // MMDevice ID; "" = default device
    public bool    Shuffle         { get; set; } = false;
    public float   Volume          { get; set; } = 0.8f;  // 0..1

    public List<MusicPlaylist> Playlists { get; set; } = new();
    public Dictionary<string, string> TitleOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Now-Playing overlay style (sent to C++ steaming_music source)
    public string  NpFontFamily    { get; set; } = "Segoe UI";
    public int     NpTitleSize     { get; set; } = 32;
    public int     NpArtistSize    { get; set; } = 22;
    public uint    NpTextColor     { get; set; } = 0xFFFFFFFF; // ARGB
    public bool    NpShowArt       { get; set; } = true;

    // Lyrics overlay style (sent to C++ steaming_lyrics source)
    public string  LyFontFamily    { get; set; } = "Segoe UI";
    public int     LyFontSize      { get; set; } = 30;
    public uint    LyTextColor     { get; set; } = 0xFFB0B0B0; // inactive lines (grey)
    public uint    LyActiveColor   { get; set; } = 0xFFFFFFFF; // current line (white, enlarged)
    public uint    LyBackgroundColor { get; set; } = 0x00000000; // ARGB; alpha 0 = transparent
    public int     LyLineCount     { get; set; } = 5;          // total lines shown (odd = centred)
    public bool    LyHorizontal    { get; set; } = false;      // false = vertical stack; true = single horizontal line
    public int     LyMinLineMs     { get; set; } = 400;        // skip lines that would show < this long (0 = off); guards bad .lrc files
}

public class KickBridgeConfig
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7449;
    public bool UseTls { get; set; } = true;
    public string WebSocketPath { get; set; } = "/ws/kick-bridge";
    public string ClientToken { get; set; } = "";
    public bool AllowOutboundChat { get; set; } = true;
}

public class DebugLogConfig
{
    public string FilePath { get; set; } = DebugLogFile.DefaultPath;
}

public class AppSettings
{
    // null = ask on next launch, "Classic" = WPF, "WinUI3" = WinUI 3
    public string? UiPreference { get; set; } = null;

    public bool ShowChatTimestampsInApp { get; set; } = false;
    public bool EnableChatTts { get; set; } = false;
    public bool EnableAlertTts { get; set; } = false;   // speak alert events (follows, subs, bits, raids…) aloud
    public string TtsVoiceName { get; set; } = "";         // WPF SAPI voice name
    public string TtsVoiceNameWinUI { get; set; } = "";    // WinRT voice display name
    public string TtsAudioDeviceId { get; set; } = "";     // empty = default audio device
    public double TtsSpeed { get; set; } = 1.0;            // WinRT speaking rate, 1.0 = default
    public string TtsIgnoredUsers { get; set; } = "";      // comma-separated usernames never read aloud

    // ── Kokoro (ONNX) TTS — OPTIONAL alternative engine. WinRT remains the default and is never
    //    removed. Runs fully in-process; model/voices/espeak-ng are auto-downloaded into app data
    //    by KokoroAssetService on first enable (no manual setup). Inert unless TtsEngine=="Kokoro".
    public string TtsEngine { get; set; } = "WinRt";           // "WinRt" (default) | "Kokoro"
    public string KokoroVoiceName { get; set; } = "af_heart";  // selected voice (see KokoroAssetService.Voices)
    public string KokoroModelVariant { get; set; } = "model.onnx"; // file under onnx/ on HF (fp32 default)
    public string SoundAudioDeviceId { get; set; } = "";   // MMDevice ID for app-played sounds (event/command); empty = default
    public int KickFollowerCountEstimate { get; set; } = 0;
    public string LastFollowerName { get; set; } = "";     // most recent follower across both platforms
    public DateTimeOffset? LastFollowerAt { get; set; }    // when that follow happened

    public Dictionary<string, EventConfig> Events { get; set; } = DefaultEvents();

    // User-defined "Unique" alerts (display name → config). Created on the Alerts page and
    // fired by bot commands that opt in via a dropdown. Same shape as event alerts.
    public Dictionary<string, EventConfig> CustomAlerts { get; set; } = new();

    // Channel-point rewards (Twitch + Kick), each optionally assigned to a custom alert. Populated
    // manually or by a non-destructive auto-fetch from the platform APIs (see MergeRewards). A
    // redeemed reward fires its AssignedAlert; with none assigned it falls back to the generic
    // RewardRedemption alert.
    public List<ChannelReward> Rewards { get; set; } = new();

    // Merge freshly-fetched rewards for ONE platform into the saved list WITHOUT destroying it:
    //  - existing entries keep their AssignedAlert; their Title/Cost/Enabled refresh from the fetch
    //  - rewards not seen before are appended
    //  - saved entries absent from the fetch are LEFT INTACT (never auto-deleted)
    // Matches by platform reward id first, then by title for older id-less manual entries.
    public void MergeRewards(string platform, IEnumerable<ChannelReward> fetched)
    {
        Rewards ??= new List<ChannelReward>();
        foreach (var f in fetched)
        {
            if (f == null) continue;
            f.Platform = platform;

            var existing = !string.IsNullOrEmpty(f.Id)
                ? Rewards.FirstOrDefault(r =>
                    string.Equals(r.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Id, f.Id, StringComparison.OrdinalIgnoreCase))
                : null;
            existing ??= Rewards.FirstOrDefault(r =>
                string.Equals(r.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(r.Id) &&
                string.Equals(r.Title, f.Title, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (!string.IsNullOrEmpty(f.Id)) existing.Id = f.Id;
                existing.Title = f.Title;
                existing.Cost = f.Cost;
                existing.Enabled = f.Enabled;
                // existing.AssignedAlert intentionally preserved.
            }
            else
            {
                Rewards.Add(f);
            }
        }
    }

    private static Dictionary<string, EventConfig> DefaultEvents() => new()
    {
        ["Follow"]           = new() { Text = "{user} just followed!",                          Duration = 4 },
        ["Subscribe"]        = new() { Text = "{user} just subscribed!",                        Duration = 6 },
        ["GiftSubscribe"]    = new() { Text = "{user} gifted a sub to {target}!",              Duration = 6 },
        ["Bits"]             = new() { Text = "{user} cheered {amount} bits!",                  Duration = 5 },
        ["Raid"]             = new() { Text = "{user} is raiding with {amount}!",               Duration = 8 },
        ["RewardRedemption"] = new() { Text = "{user} redeemed {reward} for {amount}: {input}", Duration = 6 },
    };

    // Settings files written before a new alert event type existed don't contain its key —
    // deserialization REPLACES the dictionary, silently dropping the default entry (this is why
    // RewardRedemption could never be enabled on configs saved before v0.6.9). Re-add missing keys.
    public void EnsureDefaultEvents()
    {
        foreach (var (key, cfg) in DefaultEvents())
            if (!Events.ContainsKey(key))
                Events[key] = cfg;
    }

    // Label layouts keyed by LabelType enum value ("0".."9")
    public Dictionary<string, LabelConfig> Labels { get; set; } = new();
    // Goal configs keyed by GoalType enum value ("0".."5")
    public Dictionary<string, GoalConfig>  Goals  { get; set; } = new();
    public EmojiRainConfig EmojiRain { get; set; } = new();
    public MusicConfig Music { get; set; } = new();

    public ChatOverlayConfig ChatOverlay { get; set; } = new();
    public Dictionary<string, ChatOverlayConfig> ChatOverlayProfiles { get; set; } = new();
    public KickBridgeConfig KickBridge { get; set; } = new();
    public DebugLogConfig DebugLog { get; set; } = new();

    public string ObsWebSocketAddress  { get; set; } = "ws://localhost:4455";
    public string ObsWebSocketPassword { get; set; } = "";
    public bool   ObsWebSocketAutoReconnect { get; set; } = false;

    // Last title/category the user pushed via the Stream/Dashboard "Update" controls, plus which
    // platforms it was sent to. On stream start we re-fetch the live channel info and compare
    // against these so we can warn (and offer to re-apply) if a platform silently didn't take it.
    public string LastAppliedStreamTitle { get; set; } = "";
    public bool   LastAppliedTitleTwitch { get; set; } = false;
    public bool   LastAppliedTitleKick   { get; set; } = false;
    public string LastAppliedGameName    { get; set; } = "";
    public bool   LastAppliedGameTwitch  { get; set; } = false;
    public bool   LastAppliedGameKick    { get; set; } = false;

    // Per-destination stream-health warnings: warn when OBS is streaming but this platform
    // is not live, or when this platform's event hooks/auth fail. Per-destination because the
    // user may stream to only one platform.
    public bool   WarnOnUnhealthyTwitch { get; set; } = false;
    public bool   WarnOnUnhealthyKick   { get; set; } = false;

    // OPT-IN Kick unsupported extras. Uses Kick's unofficial browser/Pusher path for things the
    // official integration does not provide here (currently raids + follower totals). Off by default.
    public bool   KickRaidAlertsEnabled { get; set; } = false;

    // Per-platform "active / streaming to this" master switches, INDEPENDENT of being logged in.
    // Default true so existing setups are unchanged. When a platform is inactive the app treats it as
    // if you are not streaming there at all: it is not polled, its inbound chat/alerts are dropped at
    // the EventBus, it never receives sends, and its producers are not started at launch. Flip these
    // (Dashboard or Connections page) to choose what you're streaming to today without logging out.
    public bool   TwitchActive  { get; set; } = true;
    public bool   KickActive    { get; set; } = true;
    public bool   YouTubeActive { get; set; } = true;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Steaming", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                loaded.NormalizeChatOverlayProfiles();
                loaded.EnsureDefaultEvents();
                return loaded;
            }
        }
        catch { }
        var settings = new AppSettings();
        settings.NormalizeChatOverlayProfiles();
        return settings;
    }

    public void Save()
    {
        NormalizeChatOverlayProfiles();
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void NormalizeChatOverlayProfiles()
    {
        ChatOverlay ??= new ChatOverlayConfig();
        ChatOverlayProfiles ??= new Dictionary<string, ChatOverlayConfig>();
        CustomAlerts ??= new Dictionary<string, EventConfig>();
        Rewards ??= new List<ChannelReward>();
        Music ??= new MusicConfig();
        Music.Playlists ??= new List<MusicPlaylist>();
        Music.TitleOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        KickBridge ??= new KickBridgeConfig();
        DebugLog ??= new DebugLogConfig();
        if (string.IsNullOrWhiteSpace(DebugLog.FilePath))
            DebugLog.FilePath = DebugLogFile.DefaultPath;

        if (ChatOverlayProfiles.Count == 0)
        {
            var defaultName = string.IsNullOrWhiteSpace(ChatOverlay.SourceName) ? "Steaming Chat" : ChatOverlay.SourceName.Trim();
            ChatOverlay.SourceName = defaultName;
            ChatOverlayProfiles[defaultName] = ChatOverlay.Clone();
        }

        var currentName = string.IsNullOrWhiteSpace(ChatOverlay.SourceName) ? "Steaming Chat" : ChatOverlay.SourceName.Trim();
        if (!ChatOverlayProfiles.TryGetValue(currentName, out var existing))
        {
            ChatOverlayProfiles[currentName] = ChatOverlay.Clone();
            existing = ChatOverlayProfiles[currentName];
        }

        ChatOverlay = existing.Clone();
    }
}
