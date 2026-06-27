namespace Steaming.Core.Ipc;

// Wire frame: [1]type [4]payloadLen LE [N]payload
public enum PipeMessageType : byte
{
    Ping               = 0x01,
    Pong               = 0x02,
    Hello              = 0x03,
    RenderAlert        = 0x10,
    RenderChat         = 0x11,
    UpdateGoal         = 0x12,
    Clear              = 0x13,
    RenderAlertV2      = 0x14,
    RefreshChat        = 0x15,  // emote/badge image downloaded — redraw all chat sources
    UpdateChatSettings = 0x16,
    ChatSourceList     = 0x17,
    // Persistent overlay sources (labels + goals): full ALT3 payload with design + live value
    // SetLabelLayout: [1]labelType [N]ALT3_bytes
    SetLabelLayout     = 0x18,
    // SetGoalLayout:  [1]goalType  [N]ALT3_bytes
    SetGoalLayout      = 0x19,
    // TriggerEmojiRain: [1]isGif [4]color_argb [2+N]content_utf8 [1]count
    TriggerEmojiRain   = 0x1A,
    // EmojiRainSettings: [N]settings payload
    EmojiRainSettings  = 0x1B,
    // SetGoalNames: [2]count [2+N]name0 [2+N]name1 ... — populates goal_type dropdown in OBS
    SetGoalNames       = 0x1C,

    // ── Music player overlays (Now Playing + Lyrics) ──────────────────────────
    // MusicNowPlaying: [2+N]title_utf8 [2+N]artist_utf8 [2+N]artPath_utf8 [4]durationMs_le
    //   Empty title = no track / clear the now-playing overlay.
    MusicNowPlaying          = 0x1D,
    // MusicPosition: [4]positionMs_le [1]isPlaying — sent ~5/sec; drives lyric sync.
    MusicPosition            = 0x1E,
    // MusicLyrics: [2]count_le then per line: [4]timeMs_le [2+N]text_utf8 — parsed .lrc, sent per track.
    MusicLyrics              = 0x1F,
    // MusicNowPlayingSettings: [4]textColor_argb [1]fontSize? see OverlayDispatcher for exact layout.
    MusicNowPlayingSettings  = 0x20,
    // MusicLyricsSettings: style for the lyrics overlay — see OverlayDispatcher for exact layout.
    MusicLyricsSettings      = 0x21,
}
