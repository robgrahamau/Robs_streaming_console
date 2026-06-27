#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincodec.h>
#include <obs-module.h>
#include "emoji_rain_source.h"
#include "renderer.h"
#include <mutex>
#include <vector>
#include <string>
#include <unordered_map>
#include <algorithm>
#include <random>
#include <cmath>
#include <cstring>

// ── GIF frame cache ────────────────────────────────────────────────────────────
struct GifFrameData {
    std::vector<std::vector<uint8_t>> frames; // BGRA pixels per frame
    std::vector<float>                delays; // seconds per frame
    int    width  = 0;
    int    height = 0;
    float  total  = 0.f;
    uint64_t lastUsed = 0;   // LRU clock value
    size_t   byteSize = 0;   // total decoded bytes
};

static std::unordered_map<std::wstring, GifFrameData> s_gifCache;
static std::mutex                                       s_gifCacheMutex;
static uint64_t s_gifClock = 0;
static size_t   s_gifCacheBytes = 0;
// Cap the GIF cache so it can't grow without bound over a long session (was keep-forever).
// Called at the top of a render rebuild, before any GifFrameData* is taken for the frame.
static const size_t kGifCacheBudgetBytes = 64ull * 1024 * 1024;

static void PruneGifCache()
{
    std::lock_guard<std::mutex> lock(s_gifCacheMutex);
    while (s_gifCacheBytes > kGifCacheBudgetBytes && s_gifCache.size() > 1) {
        auto oldest = s_gifCache.end();
        for (auto it = s_gifCache.begin(); it != s_gifCache.end(); ++it)
            if (oldest == s_gifCache.end() || it->second.lastUsed < oldest->second.lastUsed)
                oldest = it;
        if (oldest == s_gifCache.end())
            break;
        s_gifCacheBytes -= oldest->second.byteSize;
        s_gifCache.erase(oldest);
    }
}

// Load a GIF file into GifFrameData via WIC. Returns false if load fails.
static bool LoadGifFrames(const std::wstring& path, GifFrameData& out)
{
    IWICImagingFactory* pFac = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&pFac))))
        return false;

    IWICBitmapDecoder* pDec = nullptr;
    if (FAILED(pFac->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                               WICDecodeMetadataCacheOnLoad, &pDec))) {
        pFac->Release(); return false;
    }

    UINT frameCount = 0;
    pDec->GetFrameCount(&frameCount);
    if (frameCount == 0) { pDec->Release(); pFac->Release(); return false; }

    for (UINT fi = 0; fi < frameCount; fi++) {
        IWICBitmapFrameDecode* pFrame = nullptr;
        if (FAILED(pDec->GetFrame(fi, &pFrame))) continue;

        UINT w = 0, h = 0;
        pFrame->GetSize(&w, &h);
        if (out.width == 0) { out.width = (int)w; out.height = (int)h; }

        IWICFormatConverter* pConv = nullptr;
        if (SUCCEEDED(pFac->CreateFormatConverter(&pConv))) {
            pConv->Initialize(pFrame, GUID_WICPixelFormat32bppBGRA,
                              WICBitmapDitherTypeNone, nullptr, 0.0,
                              WICBitmapPaletteTypeCustom);
            std::vector<uint8_t> pixels(w * h * 4);
            pConv->CopyPixels(nullptr, w * 4, (UINT)pixels.size(), pixels.data());
            out.frames.push_back(std::move(pixels));
            pConv->Release();
        }

        // Read frame delay from GIF metadata (units: centiseconds)
        float delay = 0.1f;
        IWICMetadataQueryReader* pMeta = nullptr;
        if (SUCCEEDED(pFrame->GetMetadataQueryReader(&pMeta))) {
            PROPVARIANT pv; PropVariantInit(&pv);
            if (SUCCEEDED(pMeta->GetMetadataByName(L"/grctlext/Delay", &pv)) &&
                pv.vt == VT_UI2 && pv.uiVal > 0)
                delay = pv.uiVal / 100.f;
            PropVariantClear(&pv);
            pMeta->Release();
        }
        out.delays.push_back(delay);
        out.total += delay;
        pFrame->Release();
    }

    pDec->Release();
    pFac->Release();
    return !out.frames.empty();
}

static const GifFrameData* GetGifCached(const std::wstring& path)
{
    std::lock_guard<std::mutex> lock(s_gifCacheMutex);
    auto it = s_gifCache.find(path);
    if (it != s_gifCache.end()) {
        it->second.lastUsed = ++s_gifClock;
        return &it->second;
    }

    GifFrameData data;
    if (!LoadGifFrames(path, data)) return nullptr;
    size_t bytes = 0;
    for (const auto& f : data.frames)
        bytes += f.size();
    data.byteSize = bytes;
    data.lastUsed = ++s_gifClock;
    s_gifCacheBytes += bytes;
    s_gifCache[path] = std::move(data);
    return &s_gifCache[path];
}

static int PickGifFrame(const GifFrameData& gif, float animTime)
{
    float t = fmodf(animTime, gif.total);
    float acc = 0.f;
    for (int i = 0; i < (int)gif.delays.size(); i++) {
        acc += gif.delays[i];
        if (t < acc) return i;
    }
    return (int)gif.frames.size() - 1;
}

// ── Particle ──────────────────────────────────────────────────────────────────
struct EmojiParticle {
    std::wstring emoji;    // text emoji (empty if GIF)
    std::wstring gifPath;  // non-empty = GIF particle
    float     animTime;    // GIF animation clock
    COLORREF  color;       // text particle colour (ABGR in GDI)
    float x, y;
    float vx, vy;
    float life, maxLife;
    float rotation, spinSpeed;
};

// ── Global settings ────────────────────────────────────────────────────────────
struct EmojiRainSettings {
    int   emojiSize    = 48;
    float fallSpeed    = 400.f;
    float particleLife = 4.f;
    int   maxParticles = 100;
    float spread       = 0.3f;
    bool  fadeOut      = true;
    bool  spin         = false;
};
static EmojiRainSettings s_settings;
static std::mutex         s_settingsMutex;

static std::vector<EmojiParticle> s_particles;
static std::mutex                  s_particlesMutex;
static std::mt19937                s_rng{ std::random_device{}() };

// ── Per-instance data ─────────────────────────────────────────────────────────
struct EmojiRainData {
    obs_source_t* source = nullptr;
    gs_texture_t* texture = nullptr;
    int outW = 1920, outH = 1080;
    bool dirty = true;
};

static std::vector<EmojiRainData*> s_instances;
static std::mutex                   s_instancesMutex;

// ── Helpers ────────────────────────────────────────────────────────────────────

static uint16_t ReadU16LE_er(const uint8_t* p) {
    uint16_t v; std::memcpy(&v, p, 2); return v;
}

static void rebuild_rain_texture(EmojiRainData* d)
{
    // Evict stale GIFs before any GifFrameData* is taken for this rebuild (see PruneGifCache).
    PruneGifCache();
    int w = d->outW, h = d->outH;
    RenderBitmap bmp(w, h);

    EmojiRainSettings cfg;
    { std::lock_guard<std::mutex> lock(s_settingsMutex); cfg = s_settings; }

    std::lock_guard<std::mutex> lock(s_particlesMutex);
    for (const auto& p : s_particles) {
        float alpha = 1.f;
        if (cfg.fadeOut) {
            float ratio = p.life / p.maxLife;
            alpha = ratio < 0.3f ? ratio / 0.3f : 1.f;
        }
        uint8_t a = (uint8_t)(alpha * 255.f);

        if (!p.gifPath.empty()) {
            const GifFrameData* gif = GetGifCached(p.gifPath);
            if (gif && !gif->frames.empty()) {
                int fi = PickGifFrame(*gif, p.animTime);
                bmp.BlitImagePublic(gif->frames[fi].data(), gif->width, gif->height,
                                    (int)p.x, (int)p.y, cfg.emojiSize, cfg.emojiSize, a);
            }
        } else {
            bmp.DrawTextGDIPublic(
                p.emoji,
                (int)p.x, (int)p.y, cfg.emojiSize * 2, cfg.emojiSize * 2,
                p.color, L"Segoe UI Emoji",
                cfg.emojiSize, false, false, a,
                DT_LEFT | DT_TOP | DT_SINGLELINE | DT_NOCLIP);
        }
    }

    obs_enter_graphics();
    if (d->texture) { gs_texture_destroy(d->texture); d->texture = nullptr; }
    std::vector<uint8_t> pixels;
    bmp.GetPixels(1.0f, pixels);
    if ((int)pixels.size() == w * h * 4) {
        const uint8_t* rows[1] = { pixels.data() };
        d->texture = gs_texture_create(w, h, GS_BGRA, 1, (const uint8_t**)rows, 0);
    }
    obs_leave_graphics();
    d->dirty = false;
}

// ── OBS callbacks ─────────────────────────────────────────────────────────────

static const char* rain_get_name(void*) { return "Streaming Emoji Rain"; }

static void* rain_create(obs_data_t* settings, obs_source_t* source)
{
    auto* d = new EmojiRainData();
    d->source = source;
    d->outW = (int)obs_data_get_int(settings, "width");
    d->outH = (int)obs_data_get_int(settings, "height");
    if (d->outW < 1) d->outW = 1920;
    if (d->outH < 1) d->outH = 1080;
    std::lock_guard<std::mutex> lock(s_instancesMutex);
    s_instances.push_back(d);
    return d;
}

static void rain_destroy(void* data)
{
    auto* d = static_cast<EmojiRainData*>(data);
    { std::lock_guard<std::mutex> lock(s_instancesMutex);
      s_instances.erase(std::remove(s_instances.begin(), s_instances.end(), d),
                        s_instances.end()); }
    obs_enter_graphics();
    if (d->texture) gs_texture_destroy(d->texture);
    obs_leave_graphics();
    delete d;
}

static void rain_update(void* data, obs_data_t* settings)
{
    auto* d = static_cast<EmojiRainData*>(data);
    d->outW = (int)obs_data_get_int(settings, "width");
    d->outH = (int)obs_data_get_int(settings, "height");
    if (d->outW < 1) d->outW = 1920;
    if (d->outH < 1) d->outH = 1080;
}

static void rain_tick(void* data, float seconds)
{
    auto* d = static_cast<EmojiRainData*>(data);

    EmojiRainSettings cfg;
    { std::lock_guard<std::mutex> lock(s_settingsMutex); cfg = s_settings; }

    bool changed = false;
    {
        std::lock_guard<std::mutex> lock(s_particlesMutex);
        for (auto& p : s_particles) {
            p.x    += p.vx * seconds;
            p.y    += p.vy * seconds;
            p.life -= seconds;
            if (!p.gifPath.empty()) p.animTime += seconds;
            if (cfg.spin) p.rotation += p.spinSpeed * seconds;
            changed = true;
        }
        auto it = std::remove_if(s_particles.begin(), s_particles.end(),
                                  [](const EmojiParticle& p){ return p.life <= 0.f; });
        if (it != s_particles.end()) { s_particles.erase(it, s_particles.end()); changed = true; }
    }

    if (changed) {
        d->dirty = true;
        rebuild_rain_texture(d);
    }
}

static void rain_render(void* data, gs_effect_t* effect)
{
    auto* d = static_cast<EmojiRainData*>(data);
    if (!d->texture) return;
    gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), d->texture);
    gs_draw_sprite(d->texture, 0, (uint32_t)d->outW, (uint32_t)d->outH);
}

static uint32_t rain_width(void* data)  { return (uint32_t)static_cast<EmojiRainData*>(data)->outW; }
static uint32_t rain_height(void* data) { return (uint32_t)static_cast<EmojiRainData*>(data)->outH; }

static obs_properties_t* rain_get_properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_int(props, "width",  "Canvas Width",  100, 7680, 10);
    obs_properties_add_int(props, "height", "Canvas Height", 100, 4320, 10);
    return props;
}

static void rain_get_defaults(obs_data_t* settings)
{
    obs_data_set_default_int(settings, "width",  1920);
    obs_data_set_default_int(settings, "height", 1080);
}

// ── Registration ──────────────────────────────────────────────────────────────

void emoji_rain_source_register()
{
    struct obs_source_info info = {};
    info.id             = "steaming_emoji_rain";
    info.type           = OBS_SOURCE_TYPE_INPUT;
    info.output_flags   = OBS_SOURCE_VIDEO;
    info.get_name       = rain_get_name;
    info.create         = rain_create;
    info.destroy        = rain_destroy;
    info.update         = rain_update;
    info.video_tick     = rain_tick;
    info.video_render   = rain_render;
    info.get_width      = rain_width;
    info.get_height     = rain_height;
    info.get_properties = rain_get_properties;
    info.get_defaults   = rain_get_defaults;
    obs_register_source(&info);
}

// ── Global dispatch ────────────────────────────────────────────────────────────

void emoji_rain_trigger(const std::vector<uint8_t>& payload)
{
    // [1]isGif [4]color_argb [2+N]content_utf8 [1]count
    if (payload.size() < 8) return;
    bool isGif = payload[0] != 0;

    // Bytes from C#: [1]=A [2]=R [3]=G [4]=B — read directly to avoid little-endian memcpy flip
    uint8_t r = payload[2];
    uint8_t g = payload[3];
    uint8_t b = payload[4];
    COLORREF col = RGB(r, g, b);

    uint16_t slen = ReadU16LE_er(payload.data() + 5);
    if ((size_t)(7 + slen) >= payload.size()) return;
    std::string contentUtf8(reinterpret_cast<const char*>(payload.data() + 7), slen);
    uint8_t count = payload[7 + slen];
    if (count == 0) count = 10;

    std::wstring contentW = Utf8ToWide(contentUtf8);
    if (contentW.empty()) return;

    EmojiRainSettings cfg;
    { std::lock_guard<std::mutex> lock(s_settingsMutex); cfg = s_settings; }

    int w = 1920;
    { std::lock_guard<std::mutex> lock(s_instancesMutex);
      if (!s_instances.empty()) w = s_instances[0]->outW; }

    // Split contentW into grapheme clusters (each emoji may be multiple wchar_t values).
    // Strategy: split at surrogate-pair boundaries and variation selectors.
    // Simple: split on non-continuation codepoints (anything that starts a new visible emoji).
    // We walk wchar_t by wchar_t and group surrogates + variation selectors with the preceding char.
    std::vector<std::wstring> clusters;
    if (!isGif && !contentW.empty()) {
        std::wstring cur;
        for (size_t ci = 0; ci < contentW.size(); ci++) {
            wchar_t wc = contentW[ci];
            bool isHighSurrogate  = (wc >= 0xD800 && wc <= 0xDBFF);
            bool isLowSurrogate   = (wc >= 0xDC00 && wc <= 0xDFFF);
            bool isVariationSel   = (wc >= 0xFE00 && wc <= 0xFE0F);
            bool isZWJ            = (wc == 0x200D);
            if (cur.empty() || isLowSurrogate || isVariationSel || isZWJ ||
                (!cur.empty() && (cur.back() >= 0xD800 && cur.back() <= 0xDBFF))) {
                cur += wc; // append to current cluster
            } else {
                if (!cur.empty()) clusters.push_back(cur);
                cur = std::wstring(1, wc);
            }
        }
        if (!cur.empty()) clusters.push_back(cur);
    }

    std::uniform_real_distribution<float> xDist(0.f, (float)w);
    std::uniform_real_distribution<float> vxDist(-cfg.spread * w * 0.5f, cfg.spread * w * 0.5f);
    std::uniform_real_distribution<float> lifeDist(cfg.particleLife * 0.7f, cfg.particleLife * 1.3f);
    std::uniform_real_distribution<float> spinDist(-180.f, 180.f);

    std::lock_guard<std::mutex> lock(s_particlesMutex);
    for (int i = 0; i < (int)count; i++) {
        if ((int)s_particles.size() >= cfg.maxParticles) break;
        EmojiParticle p;
        p.color    = col;
        p.animTime = 0.f;
        if (isGif) {
            p.gifPath = contentW;
        } else {
            if (!clusters.empty()) {
                std::uniform_int_distribution<int> ed(0, (int)clusters.size() - 1);
                p.emoji = clusters[ed(s_rng)];
            }
        }
        p.x        = xDist(s_rng);
        p.y        = -(float)cfg.emojiSize;
        p.vx       = vxDist(s_rng);
        p.vy       = cfg.fallSpeed * (0.8f + 0.4f * (float)s_rng() / (float)s_rng.max());
        float life = lifeDist(s_rng);
        p.life     = life;
        p.maxLife  = life;
        p.rotation  = 0.f;
        p.spinSpeed = cfg.spin ? spinDist(s_rng) : 0.f;
        s_particles.push_back(std::move(p));
    }
}

void emoji_rain_apply_settings(const std::vector<uint8_t>& payload)
{
    // [1]emojiSize [2]fallSpeed [1]particleLifeSec [1]maxParticles [1]spread [1]fadeOut [1]spin
    if (payload.size() < 8) return;
    std::lock_guard<std::mutex> lock(s_settingsMutex);
    s_settings.emojiSize    = (int)payload[0];
    uint16_t spd; std::memcpy(&spd, payload.data() + 1, 2);
    s_settings.fallSpeed    = (float)spd;
    s_settings.particleLife = (float)payload[3];
    s_settings.maxParticles = (int)payload[4];
    s_settings.spread       = (float)payload[5] / 100.f;
    s_settings.fadeOut      = payload[6] != 0;
    s_settings.spin         = payload[7] != 0;
}
