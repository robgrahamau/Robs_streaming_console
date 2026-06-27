#pragma once
#define NOMINMAX
#include <windows.h>
#include <string>
#include <functional>
#include <thread>
#include <atomic>
#include <mutex>
#include <vector>

// Message type IDs (must match PipeMessageType enum in C#)
// Wire frame: [1]type [4]payloadLen LE [N]payload
enum class PipeMessageType : uint8_t {
    Ping               = 0x01,
    Pong               = 0x02,
    Hello              = 0x03,
    RenderAlert        = 0x10,
    RenderChat         = 0x11,
    UpdateGoal         = 0x12,
    Clear              = 0x13,
    RenderAlertV2      = 0x14,
    RefreshChat        = 0x15,
    UpdateChatSettings = 0x16,
    ChatSourceList     = 0x17,
    // Persistent overlay sources (labels + goals): design + live value sent together
    // SetLabelLayout:  [1]labelType [N]ALT3_bytes  (labelType = LabelType enum)
    SetLabelLayout     = 0x18,
    // SetGoalLayout:   [1]goalType  [N]ALT3_bytes  (goalType  = GoalType enum)
    SetGoalLayout      = 0x19,
    // TriggerEmojiRain:[1]isGif [4]color_argb [2+N]content_utf8 [1]count
    TriggerEmojiRain   = 0x1A,
    // EmojiRainSettings:[N]settings — sent on config change (font size, speed, etc.)
    EmojiRainSettings  = 0x1B,
    // SetGoalNames:[2]count [2+N]name0 [2+N]name1 ... — populates goal_type dropdown
    SetGoalNames       = 0x1C,
    // ── Music player overlays (Now Playing + Lyrics) ──────────────────────────
    // MusicNowPlaying:[2+N]title [2+N]artist [2+N]artPath [4]durationMs_le (empty title = clear)
    MusicNowPlaying          = 0x1D,
    // MusicPosition:[4]positionMs_le [1]isPlaying — sent ~5/sec; drives lyric sync
    MusicPosition            = 0x1E,
    // MusicLyrics:[2]count_le then per line [4]timeMs_le [2+N]text_utf8
    MusicLyrics              = 0x1F,
    // MusicNowPlayingSettings:[N] style payload (font/size/colour/show-art)
    MusicNowPlayingSettings  = 0x20,
    // MusicLyricsSettings:[N] style payload (font/size/colour/highlight/line-count)
    MusicLyricsSettings      = 0x21,
};

struct PipeMessage {
    PipeMessageType       type;
    std::vector<uint8_t>  payload;
};

class PipeClient {
public:
    explicit PipeClient(const std::string& pipeName);
    ~PipeClient();

    // Starts the background thread that connects and auto-reconnects forever.
    void start();
    void stop();

    bool isConnected() const { return m_connected.load(); }

    void setMessageCallback(std::function<void(const PipeMessage&)> cb) { m_callback = cb; }
    void setConnectedCallback(std::function<void()> cb) { m_connectedCallback = cb; }
    bool sendMessage(PipeMessageType type, const std::vector<uint8_t>& payload = {});

private:
    void threadProc();
    bool tryConnect();
    bool readExact(void* buf, DWORD len);
    bool sendBytes(const void* buf, DWORD len);

    std::string                              m_pipeName;
    HANDLE                                   m_pipe = INVALID_HANDLE_VALUE;
    std::atomic<bool>                        m_connected{ false };
    std::atomic<bool>                        m_running{ false };
    std::thread                              m_thread;
    std::mutex                               m_sendMutex;  // serialises concurrent sendMessage calls
    std::function<void(const PipeMessage&)>  m_callback;
    std::function<void()>                    m_connectedCallback;
};
