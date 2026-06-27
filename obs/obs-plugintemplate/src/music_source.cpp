#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <obs-module.h>
#include "music_source.h"
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
} // namespace

// ── Shared now-playing state ────────────────────────────────────────────────────
namespace {
struct NowPlaying {
    std::mutex            mutex;
    std::wstring          title, artist;
    std::wstring          artPath;
    std::vector<uint8_t>  artBgra; int artW = 0, artH = 0;
    // style
    uint32_t font_argb = 0xFFFFFFFF;
    int      titleSize = 32;
    int      artistSize = 22;
    bool     showArt = true;
    std::wstring font = L"Segoe UI";
};
NowPlaying g_np;
std::atomic<uint64_t> g_npGen{ 1 };

struct MusicData {
    obs_source_t* source  = nullptr;
    gs_texture_t* texture = nullptr;
    int           outW = 600;
    int           outH = 140;
    uint64_t      seenGen = 0;
};
std::vector<MusicData*> s_instances;
std::mutex              s_instancesMutex;
} // namespace

// ── Rendering ────────────────────────────────────────────────────────────────────
static void rebuild_texture(MusicData* d)
{
    int w = d->outW, h = d->outH;
    if (w <= 0 || h <= 0) return;
    std::vector<uint8_t> px((size_t)w * h * 4, 0);

    {
        std::lock_guard<std::mutex> lock(g_np.mutex);
        if (!g_np.title.empty()) {
            int margin = std::max(6, h / 12);
            int artSize = (g_np.showArt && g_np.artW > 0 && g_np.artH > 0) ? (h - 2 * margin) : 0;
            int textX = margin + (artSize > 0 ? artSize + margin : 0);
            int textW = w - textX - margin;

            if (artSize > 0 && textW > 10) {
                std::vector<uint8_t> scaled;
                ScaleBGRA(g_np.artBgra, g_np.artW, g_np.artH, scaled, artSize, artSize);
                BlitOver(px, w, h, scaled, artSize, artSize, margin, margin);
            }

            if (textW > 10) {
                int titleH = LineHeight(g_np.titleSize);
                bool hasArtist = !g_np.artist.empty();
                int artistH = hasArtist ? LineHeight(g_np.artistSize) : 0;
                int blockH = titleH + artistH;
                int ty = std::max(0, (h - blockH) / 2);

                TextStyle ts{ g_np.font, g_np.titleSize, FW_BOLD, g_np.font_argb };
                DrawTextLine(px, w, h, textX, ty, textW, g_np.title, ts, Align::Left);

                if (hasArtist) {
                    // Artist slightly dimmer than the title.
                    uint32_t a = (g_np.font_argb >> 24) & 0xFF;
                    a = (a * 75) / 100;
                    uint32_t dim = (g_np.font_argb & 0x00FFFFFF) | (a << 24);
                    TextStyle as{ g_np.font, g_np.artistSize, FW_NORMAL, dim };
                    DrawTextLine(px, w, h, textX, ty + titleH, textW, g_np.artist, as, Align::Left);
                }
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
static const char* music_get_name(void*) { return "Streaming Now Playing"; }

static obs_properties_t* music_get_properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_int(props, "width",  "Width (px)",  100, 3840, 10);
    obs_properties_add_int(props, "height", "Height (px)", 40,  2160, 10);
    obs_properties_add_text(props, "_note",
        "Album art + current track. Style is set in the app's Music page.", OBS_TEXT_INFO);
    return props;
}

static void music_get_defaults(obs_data_t* s)
{
    obs_data_set_default_int(s, "width", 600);
    obs_data_set_default_int(s, "height", 140);
}

static void* music_create(obs_data_t* settings, obs_source_t* source)
{
    auto* d = new MusicData();
    d->source = source;
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    d->outW = (w >= 100) ? w : 600;
    d->outH = (h >= 40) ? h : 140;
    d->seenGen = 0; // force first build
    std::lock_guard<std::mutex> lock(s_instancesMutex);
    s_instances.push_back(d);
    return d;
}

static void music_destroy(void* data)
{
    auto* d = static_cast<MusicData*>(data);
    {
        std::lock_guard<std::mutex> lock(s_instancesMutex);
        s_instances.erase(std::remove(s_instances.begin(), s_instances.end(), d), s_instances.end());
    }
    obs_enter_graphics();
    if (d->texture) gs_texture_destroy(d->texture);
    obs_leave_graphics();
    delete d;
}

static void music_update(void* data, obs_data_t* settings)
{
    auto* d = static_cast<MusicData*>(data);
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    int nw = (w >= 100) ? w : 600;
    int nh = (h >= 40) ? h : 140;
    if (nw != d->outW || nh != d->outH) {
        d->outW = nw; d->outH = nh;
        d->seenGen = 0; // force rebuild at new size
    }
}

static void music_tick(void* data, float)
{
    auto* d = static_cast<MusicData*>(data);
    uint64_t gen = g_npGen.load();
    if (d->seenGen != gen || !d->texture) {
        d->seenGen = gen;
        rebuild_texture(d);
    }
}

static void music_render_frame(void* data, gs_effect_t* effect)
{
    auto* d = static_cast<MusicData*>(data);
    if (!d->texture) return;
    gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), d->texture);
    gs_draw_sprite(d->texture, 0, (uint32_t)d->outW, (uint32_t)d->outH);
}

static uint32_t music_width(void* data)  { return (uint32_t)static_cast<MusicData*>(data)->outW; }
static uint32_t music_height(void* data) { return (uint32_t)static_cast<MusicData*>(data)->outH; }

void music_source_register()
{
    struct obs_source_info info = {};
    info.id             = "steaming_music";
    info.type           = OBS_SOURCE_TYPE_INPUT;
    info.output_flags   = OBS_SOURCE_VIDEO;
    info.get_name       = music_get_name;
    info.create         = music_create;
    info.destroy        = music_destroy;
    info.update         = music_update;
    info.video_tick     = music_tick;
    info.video_render   = music_render_frame;
    info.get_width      = music_width;
    info.get_height     = music_height;
    info.get_properties = music_get_properties;
    info.get_defaults   = music_get_defaults;
    obs_register_source(&info);
}

// ── Pipe dispatch ────────────────────────────────────────────────────────────────
void music_source_set_now_playing(const std::vector<uint8_t>& payload)
{
    Reader r(payload);
    std::wstring title  = Utf8To16(r.str());
    std::wstring artist = Utf8To16(r.str());
    std::wstring artPath = Utf8To16(r.str());
    (void)r.u32(); // durationMs — not used by the now-playing visual yet

    // Decode art outside the lock when the path changed.
    std::wstring prevPath;
    { std::lock_guard<std::mutex> lock(g_np.mutex); prevPath = g_np.artPath; }

    std::vector<uint8_t> newArt; int aw = 0, ah = 0;
    bool reloadArt = (artPath != prevPath);
    if (reloadArt && !artPath.empty())
        LoadImageBGRA(artPath, newArt, aw, ah);

    {
        std::lock_guard<std::mutex> lock(g_np.mutex);
        g_np.title = title;
        g_np.artist = artist;
        if (reloadArt) {
            g_np.artPath = artPath;
            g_np.artBgra = std::move(newArt);
            g_np.artW = aw; g_np.artH = ah;
        }
    }
    g_npGen.fetch_add(1);
}

void music_source_apply_settings(const std::vector<uint8_t>& payload)
{
    Reader r(payload);
    uint32_t color = r.u32();
    int titleSize  = (int)r.u16();
    int artistSize = (int)r.u16();
    bool showArt   = r.u8() != 0;
    std::wstring font = Utf8To16(r.str());

    {
        std::lock_guard<std::mutex> lock(g_np.mutex);
        g_np.font_argb = color;
        g_np.titleSize = std::clamp(titleSize, 6, 200);
        g_np.artistSize = std::clamp(artistSize, 6, 200);
        g_np.showArt = showArt;
        g_np.font = font.empty() ? L"Segoe UI" : font;
    }
    g_npGen.fetch_add(1);
}
