#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <obs-module.h>
#include "lyrics_source.h"
#include "music_render.h"
#include <mutex>
#include <atomic>
#include <vector>
#include <string>
#include <algorithm>

using namespace music_render;

// ── Wire helpers ────────────────────────────────────────────────────────────────
namespace {
struct Reader {
    const uint8_t* p; size_t n; size_t off = 0;
    explicit Reader(const std::vector<uint8_t>& v) : p(v.data()), n(v.size()) {}
    uint8_t  u8()  { return off < n ? p[off++] : 0; }
    uint16_t u16() { uint16_t a = u8(); uint16_t b = u8(); return (uint16_t)(a | (b << 8)); }
    uint32_t u32() { uint32_t a = u16(); uint32_t b = u16(); return a | (b << 16); }
    std::string str() {
        uint16_t len = u16();
        std::string s;
        for (uint16_t i = 0; i < len && off < n; ++i) s += (char)p[off++];
        return s;
    }
};

std::wstring Utf8To16(const std::string& s)
{
    if (s.empty()) return L"";
    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), len);
    return w;
}

struct LyricLineW { uint32_t timeMs; std::wstring text; };
} // namespace

// ── Shared lyrics state ──────────────────────────────────────────────────────────
namespace {
struct LyricsState {
    std::mutex              mutex;
    std::vector<LyricLineW> lines;     // sorted by time
    bool                    isPlaying = false;
    // style
    uint32_t textColor   = 0xFFB0B0B0;
    uint32_t activeColor = 0xFFFFFFFF;
    uint32_t bgColor     = 0x00000000;  // ARGB; alpha 0 = transparent
    int      fontSize    = 30;
    int      lineCount   = 5;
    bool     horizontal  = false;        // true = single active line; false = vertical stack
    int      minLineMs   = 400;          // skip lines shown < this long (0 = off) — guards bad .lrc
    std::wstring font    = L"Segoe UI";

    // Throttle state for the minimum-line-time safeguard (guarded by mutex).
    int      committedActive = -2;
    uint64_t lastCommitTick  = 0;
    uint32_t lastPosMs       = 0xFFFFFFFFu;  // for seek detection (sentinel = none yet)
};
LyricsState g_ly;
std::atomic<uint64_t> g_lyGen{ 1 };   // bumped on lyrics/style change
std::atomic<uint32_t> g_lyPosMs{ 0 };

// Active line index for a position (-1 = before first line). lines assumed time-sorted.
int active_index_locked(uint32_t posMs)
{
    const auto& L = g_ly.lines;
    if (L.empty()) return -1;
    int lo = 0, hi = (int)L.size() - 1, res = -1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2;
        if (L[mid].timeMs <= posMs) { res = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    return res;
}

// Applies the minimum-line-time safeguard: while lines change faster than minLineMs, hold the
// committed line, then jump straight to the current one (skipping the unreadable intermediates).
// Always returns a line that is <= the current position, so it never lags. Call under g_ly.mutex.
int throttle_active_locked(int trueActive)
{
    if (g_ly.minLineMs <= 0) return trueActive;   // safeguard off
    if (trueActive == g_ly.committedActive) return g_ly.committedActive;
    uint64_t now = GetTickCount64();
    if (g_ly.committedActive < -1 || (now - g_ly.lastCommitTick) >= (uint64_t)g_ly.minLineMs) {
        g_ly.committedActive = trueActive;
        g_ly.lastCommitTick = now;
    }
    return g_ly.committedActive;
}

struct LyricsData {
    obs_source_t* source  = nullptr;
    gs_texture_t* texture = nullptr;
    int           outW = 700;
    int           outH = 240;
    uint64_t      seenGen = 0;
    int           lastActive = -2;
};
std::vector<LyricsData*> s_instances;
std::mutex               s_instancesMutex;
} // namespace

// ── Rendering ────────────────────────────────────────────────────────────────────
static void rebuild_texture(LyricsData* d, int activeIdx)
{
    int w = d->outW, h = d->outH;
    if (w <= 0 || h <= 0) return;
    std::vector<uint8_t> px((size_t)w * h * 4, 0);

    {
        std::lock_guard<std::mutex> lock(g_ly.mutex);
        int total = (int)g_ly.lines.size();
        bool showLyrics = g_ly.isPlaying && total > 0;

        // Match the now-playing source: when inactive, the source stays fully transparent.
        // Only draw the configured lyrics background while playback is active and lyrics exist.
        uint8_t ba = (uint8_t)((g_ly.bgColor >> 24) & 0xFF);
        if (showLyrics && ba > 0) {
            uint8_t br = (uint8_t)((g_ly.bgColor >> 16) & 0xFF);
            uint8_t bg = (uint8_t)((g_ly.bgColor >> 8) & 0xFF);
            uint8_t bb = (uint8_t)(g_ly.bgColor & 0xFF);
            uint8_t pb = (uint8_t)(bb * ba / 255), pg = (uint8_t)(bg * ba / 255), pr = (uint8_t)(br * ba / 255);
            for (size_t i = 0; i + 3 < px.size(); i += 4) {
                px[i] = pb; px[i + 1] = pg; px[i + 2] = pr; px[i + 3] = ba;
            }
        }

        if (showLyrics && g_ly.horizontal) {
            // Single current line, centred both axes.
            int sz = (int)(g_ly.fontSize * 1.18f);
            int lh = LineHeight(sz);
            int y = std::max(0, (h - lh) / 2);
            int idx = std::clamp(activeIdx < 0 ? 0 : activeIdx, 0, total - 1);
            bool isActive = activeIdx >= 0;
            uint32_t col = isActive ? g_ly.activeColor : g_ly.textColor;
            TextStyle ts{ g_ly.font, sz, isActive ? FW_BOLD : FW_NORMAL, col };
            DrawTextLine(px, w, h, 0, y, w, g_ly.lines[idx].text, ts, Align::Center);
        }
        else if (showLyrics) {
            int lineCount = std::clamp(g_ly.lineCount, 1, 15);
            int activeSize = (int)(g_ly.fontSize * 1.18f);
            int normalH = LineHeight(g_ly.fontSize);
            int activeH = LineHeight(activeSize);

            int center = std::clamp(activeIdx < 0 ? 0 : activeIdx, 0, total - 1);
            int half = lineCount / 2;
            int first = center - half;
            int last  = center + (lineCount - half - 1);

            // Total height of the visible window (active row is taller).
            int blockH = 0;
            for (int i = first; i <= last; ++i)
                blockH += (i == activeIdx) ? activeH : normalH;
            int y = std::max(0, (h - blockH) / 2);

            for (int i = first; i <= last; ++i) {
                if (i < 0 || i >= total) {
                    y += normalH;
                    continue;
                }
                bool isActive = (i == activeIdx);
                int sz = isActive ? activeSize : g_ly.fontSize;
                uint32_t col = isActive ? g_ly.activeColor : g_ly.textColor;
                TextStyle ts{ g_ly.font, sz, isActive ? FW_BOLD : FW_NORMAL, col };
                DrawTextLine(px, w, h, 0, y, w, g_ly.lines[i].text, ts, Align::Center);
                y += isActive ? activeH : normalH;
            }
        }
    }

    obs_enter_graphics();
    if (d->texture) { gs_texture_destroy(d->texture); d->texture = nullptr; }
    const uint8_t* rows[1] = { px.data() };
    d->texture = gs_texture_create(w, h, GS_BGRA, 1, rows, 0);
    obs_leave_graphics();
}

// ── OBS callbacks ────────────────────────────────────────────────────────────────
static const char* lyrics_get_name(void*) { return "Streaming Lyrics"; }

static obs_properties_t* lyrics_get_properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_int(props, "width",  "Width (px)",  100, 3840, 10);
    obs_properties_add_int(props, "height", "Height (px)", 60,  2160, 10);
    obs_properties_add_text(props, "_note",
        "Time-synced lyrics. Style + number of lines are set in the app's Music page.", OBS_TEXT_INFO);
    return props;
}

static void lyrics_get_defaults(obs_data_t* s)
{
    obs_data_set_default_int(s, "width", 700);
    obs_data_set_default_int(s, "height", 240);
}

static void* lyrics_create(obs_data_t* settings, obs_source_t* source)
{
    auto* d = new LyricsData();
    d->source = source;
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    d->outW = (w >= 100) ? w : 700;
    d->outH = (h >= 60) ? h : 240;
    d->seenGen = 0;
    d->lastActive = -2;
    std::lock_guard<std::mutex> lock(s_instancesMutex);
    s_instances.push_back(d);
    return d;
}

static void lyrics_destroy(void* data)
{
    auto* d = static_cast<LyricsData*>(data);
    {
        std::lock_guard<std::mutex> lock(s_instancesMutex);
        s_instances.erase(std::remove(s_instances.begin(), s_instances.end(), d), s_instances.end());
    }
    obs_enter_graphics();
    if (d->texture) gs_texture_destroy(d->texture);
    obs_leave_graphics();
    delete d;
}

static void lyrics_update(void* data, obs_data_t* settings)
{
    auto* d = static_cast<LyricsData*>(data);
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    int nw = (w >= 100) ? w : 700;
    int nh = (h >= 60) ? h : 240;
    if (nw != d->outW || nh != d->outH) {
        d->outW = nw; d->outH = nh;
        d->seenGen = 0;
        d->lastActive = -2;
    }
}

static void lyrics_tick(void* data, float)
{
    auto* d = static_cast<LyricsData*>(data);
    uint64_t gen = g_lyGen.load();
    uint32_t pos = g_lyPosMs.load();

    int active;
    {
        std::lock_guard<std::mutex> lock(g_ly.mutex);
        int trueActive = active_index_locked(pos);
        // Seek detection: a position discontinuity (backward, or a big forward jump) is a user
        // seek, not dense playback — bypass the throttle and snap to the correct line immediately.
        bool seeked = g_ly.lastPosMs != 0xFFFFFFFFu &&
                      (pos < g_ly.lastPosMs || pos > g_ly.lastPosMs + 1500);
        g_ly.lastPosMs = pos;
        if (seeked) {
            g_ly.committedActive = trueActive;
            g_ly.lastCommitTick = GetTickCount64();
            active = trueActive;
        } else {
            active = throttle_active_locked(trueActive);
        }
    }

    if (d->seenGen != gen || active != d->lastActive || !d->texture) {
        d->seenGen = gen;
        d->lastActive = active;
        rebuild_texture(d, active);
    }
}

static void lyrics_render(void* data, gs_effect_t* effect)
{
    auto* d = static_cast<LyricsData*>(data);
    if (!d->texture) return;
    gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), d->texture);
    gs_draw_sprite(d->texture, 0, (uint32_t)d->outW, (uint32_t)d->outH);
}

static uint32_t lyrics_width(void* data)  { return (uint32_t)static_cast<LyricsData*>(data)->outW; }
static uint32_t lyrics_height(void* data) { return (uint32_t)static_cast<LyricsData*>(data)->outH; }

void lyrics_source_register()
{
    struct obs_source_info info = {};
    info.id             = "steaming_lyrics";
    info.type           = OBS_SOURCE_TYPE_INPUT;
    info.output_flags   = OBS_SOURCE_VIDEO;
    info.get_name       = lyrics_get_name;
    info.create         = lyrics_create;
    info.destroy        = lyrics_destroy;
    info.update         = lyrics_update;
    info.video_tick     = lyrics_tick;
    info.video_render   = lyrics_render;
    info.get_width      = lyrics_width;
    info.get_height     = lyrics_height;
    info.get_properties = lyrics_get_properties;
    info.get_defaults   = lyrics_get_defaults;
    obs_register_source(&info);
}

// ── Pipe dispatch ────────────────────────────────────────────────────────────────
void lyrics_source_set_lyrics(const std::vector<uint8_t>& payload)
{
    Reader r(payload);
    uint16_t count = r.u16();
    std::vector<LyricLineW> lines;
    lines.reserve(count);
    for (uint16_t i = 0; i < count; ++i) {
        uint32_t t = r.u32();
        std::wstring text = Utf8To16(r.str());
        lines.push_back({ t, std::move(text) });
    }
    {
        std::lock_guard<std::mutex> lock(g_ly.mutex);
        g_ly.lines = std::move(lines);
        g_ly.committedActive = -2;   // reset throttle for the new track
        g_ly.lastCommitTick = 0;
        g_ly.lastPosMs = 0xFFFFFFFFu;
    }
    g_lyGen.fetch_add(1);
}

void lyrics_source_set_position(const std::vector<uint8_t>& payload)
{
    Reader r(payload);
    uint32_t pos = r.u32();
    bool isPlaying = r.u8() != 0;
    bool playingChanged = false;
    {
        std::lock_guard<std::mutex> lock(g_ly.mutex);
        if (g_ly.isPlaying != isPlaying) {
            g_ly.isPlaying = isPlaying;
            playingChanged = true;
        }
    }
    g_lyPosMs.store(pos);
    if (playingChanged)
        g_lyGen.fetch_add(1);
}

void lyrics_source_apply_settings(const std::vector<uint8_t>& payload)
{
    Reader r(payload);
    uint32_t textColor   = r.u32();
    uint32_t activeColor = r.u32();
    uint32_t bgColor     = r.u32();
    int      fontSize    = (int)r.u16();
    int      lineCount   = (int)r.u8();
    bool     horizontal  = r.u8() != 0;
    int      minLineMs   = (int)r.u16();
    std::wstring font    = Utf8To16(r.str());
    {
        std::lock_guard<std::mutex> lock(g_ly.mutex);
        g_ly.textColor   = textColor;
        g_ly.activeColor = activeColor;
        g_ly.bgColor     = bgColor;
        g_ly.fontSize    = std::clamp(fontSize, 6, 200);
        g_ly.lineCount   = std::clamp(lineCount, 1, 15);
        g_ly.horizontal  = horizontal;
        g_ly.minLineMs   = std::clamp(minLineMs, 0, 5000);
        g_ly.font        = font.empty() ? L"Segoe UI" : font;
    }
    g_lyGen.fetch_add(1);
}
