#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <obs-module.h>
#include "label_source.h"
#include "layout_renderer.h"
#include <mutex>
#include <vector>
#include <string>
#include <algorithm>

// ── Global label layout storage ──────────────────────────────────────────────
// One payload per label type, set by the C# app.  All instances of a given
// type share the same payload and re-parse it when it changes.
static constexpr int LABEL_TYPE_COUNT = 10;
static std::vector<uint8_t> s_payloads[LABEL_TYPE_COUNT];
static std::mutex            s_payloadMutex;

// ── Per-instance data ─────────────────────────────────────────────────────────
struct LabelData {
    obs_source_t*   source    = nullptr;
    gs_texture_t*   texture   = nullptr;
    LayoutRenderer* renderer  = nullptr;
    LabelType       labelType = LabelType::RecentFollower;
    std::mutex      mutex;
    std::vector<uint8_t> pendingLayout;
    bool            layoutDirty = false;

    float  elapsed    = 0.f;
    bool   hasLayout  = false;
    int    outW       = 400;
    int    outH       = 100;
};

static std::vector<LabelData*> s_instances;
static std::mutex               s_instancesMutex;

// ── Helpers ───────────────────────────────────────────────────────────────────

static void rebuild_texture(LabelData* d)
{
    if (!d->renderer || !d->hasLayout) return;

    obs_enter_graphics();
    if (d->texture) {
        gs_texture_destroy(d->texture);
        d->texture = nullptr;
    }

    std::vector<uint8_t> pixels;
    float dur = d->renderer->Duration();
    float t   = (dur > 0.f) ? std::min(d->elapsed, dur) : 0.f;
    d->renderer->RenderFrame(t, pixels);

    int w = d->renderer->Width();
    int h = d->renderer->Height();
    if (w > 0 && h > 0 && (int)pixels.size() == w * h * 4) {
        const uint8_t* rows[1] = { pixels.data() };
        d->texture = gs_texture_create(w, h, GS_BGRA, 1,
                                        (const uint8_t**)rows, 0);
        d->outW = w;
        d->outH = h;
    }
    obs_leave_graphics();
}

static void apply_payload(LabelData* d, const std::vector<uint8_t>& payload)
{
    if (!d->renderer) d->renderer = new LayoutRenderer();
    d->elapsed = 0.f;
    d->hasLayout = d->renderer->Parse(payload);
    if (d->hasLayout && d->outW > 0 && d->outH > 0)
        d->renderer->ScaleToFit(d->outW, d->outH);
    rebuild_texture(d);
}

static void queue_payload(LabelData* d, const std::vector<uint8_t>& payload)
{
    std::lock_guard<std::mutex> lock(d->mutex);
    d->pendingLayout = payload;
    d->layoutDirty = true;
}

static void apply_pending_layout(LabelData* d)
{
    std::vector<uint8_t> payload;
    {
        std::lock_guard<std::mutex> lock(d->mutex);
        if (!d->layoutDirty)
            return;
        payload = d->pendingLayout;
        d->layoutDirty = false;
    }

    if (!payload.empty())
        apply_payload(d, payload);
}

// ── OBS source callbacks ──────────────────────────────────────────────────────

static const char* label_get_name(void*) { return "Streaming Label"; }

static obs_properties_t* label_get_properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_property_t* p = obs_properties_add_list(props, "label_type", "Label Type",
                                                  OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_INT);
    obs_property_list_add_int(p, "Recent Follower",   0);
    obs_property_list_add_int(p, "Recent Subscriber", 1);
    obs_property_list_add_int(p, "Subscriber Count",  2);
    obs_property_list_add_int(p, "Viewer Count",      3);
    obs_property_list_add_int(p, "Follower Count",    4);
    obs_property_list_add_int(p, "Stream Uptime",     5);
    obs_property_list_add_int(p, "Recent Donation",   6);
    obs_property_list_add_int(p, "Top Donation",      7);
    obs_property_list_add_int(p, "Donation Total",    8);
    obs_property_list_add_int(p, "Recent Gift Sub",   9);
    obs_properties_add_int(props, "width",  "Width (px)",  50, 3840, 10);
    obs_properties_add_int(props, "height", "Height (px)", 20, 2160, 10);
    obs_properties_add_text(props, "_note",
        "Sets the label source output size. Label layouts are scaled to fit.", OBS_TEXT_INFO);
    return props;
}

static void label_get_defaults(obs_data_t* settings)
{
    obs_data_set_default_int(settings, "label_type", 0);
    obs_data_set_default_int(settings, "width", 400);
    obs_data_set_default_int(settings, "height", 60);
}

static void* label_create(obs_data_t* settings, obs_source_t* source)
{
    auto* d = new LabelData();
    d->source    = source;
    d->labelType = (LabelType)obs_data_get_int(settings, "label_type");
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    d->outW = (w >= 50) ? w : 400;
    d->outH = (h >= 20) ? h : 60;

    {
        std::vector<uint8_t> initialPayload;
        std::lock_guard<std::mutex> lock(s_payloadMutex);
        int idx = (int)d->labelType;
        if (idx >= 0 && idx < LABEL_TYPE_COUNT && !s_payloads[idx].empty())
            initialPayload = s_payloads[idx];
        if (!initialPayload.empty())
            queue_payload(d, initialPayload);
    }

    std::lock_guard<std::mutex> lock(s_instancesMutex);
    s_instances.push_back(d);
    return d;
}

static void label_destroy(void* data)
{
    auto* d = static_cast<LabelData*>(data);
    {
        std::lock_guard<std::mutex> lock(s_instancesMutex);
        s_instances.erase(std::remove(s_instances.begin(), s_instances.end(), d),
                          s_instances.end());
    }
    obs_enter_graphics();
    if (d->texture) gs_texture_destroy(d->texture);
    obs_leave_graphics();
    delete d->renderer;
    delete d;
}

static void label_update(void* data, obs_data_t* settings)
{
    auto* d = static_cast<LabelData*>(data);
    LabelType newType = (LabelType)obs_data_get_int(settings, "label_type");
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    int newOutW = (w >= 50) ? w : 400;
    int newOutH = (h >= 20) ? h : 60;
    bool sizeChanged = newOutW != d->outW || newOutH != d->outH;
    d->outW = newOutW;
    d->outH = newOutH;

    if (newType != d->labelType || sizeChanged) {
        d->labelType = newType;
        std::vector<uint8_t> payload;
        {
            std::lock_guard<std::mutex> lock(s_payloadMutex);
            int idx = (int)newType;
            if (idx >= 0 && idx < LABEL_TYPE_COUNT && !s_payloads[idx].empty())
                payload = s_payloads[idx];
        }
        if (!payload.empty())
            queue_payload(d, payload);
    }
}

static void label_tick(void* data, float seconds)
{
    auto* d = static_cast<LabelData*>(data);
    apply_pending_layout(d);
    if (!d->hasLayout || !d->renderer) return;
    float dur = d->renderer->Duration();
    if (d->elapsed >= dur && dur > 0.f) return; // already at final frame
    d->elapsed += seconds;
    if (dur > 0.f && d->elapsed > dur) d->elapsed = dur;
    rebuild_texture(d);
}

static void label_render(void* data, gs_effect_t* effect)
{
    auto* d = static_cast<LabelData*>(data);
    if (!d->texture) return;
    gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), d->texture);
    gs_draw_sprite(d->texture, 0, (uint32_t)d->outW, (uint32_t)d->outH);
}

static uint32_t label_width(void* data)  { return (uint32_t)static_cast<LabelData*>(data)->outW; }
static uint32_t label_height(void* data) { return (uint32_t)static_cast<LabelData*>(data)->outH; }

// ── Registration ──────────────────────────────────────────────────────────────

void label_source_register()
{
    struct obs_source_info info = {};
    info.id             = "steaming_label";
    info.type           = OBS_SOURCE_TYPE_INPUT;
    info.output_flags   = OBS_SOURCE_VIDEO;
    info.get_name       = label_get_name;
    info.create         = label_create;
    info.destroy        = label_destroy;
    info.update         = label_update;
    info.video_tick     = label_tick;
    info.video_render   = label_render;
    info.get_width      = label_width;
    info.get_height     = label_height;
    info.get_properties = label_get_properties;
    info.get_defaults   = label_get_defaults;
    obs_register_source(&info);
}

// ── Global dispatch from pipe thread ─────────────────────────────────────────

void label_source_set_layout(const std::vector<uint8_t>& payload)
{
    // payload: [1]labelType [N]ALT3_bytes
    if (payload.size() < 2) return;
    int idx = (int)payload[0];
    if (idx < 0 || idx >= LABEL_TYPE_COUNT) return;

    std::vector<uint8_t> alt3(payload.begin() + 1, payload.end());

    {
        std::lock_guard<std::mutex> lock(s_payloadMutex);
        s_payloads[idx] = alt3;
    }

    // Update all instances of this label type
    std::lock_guard<std::mutex> lock(s_instancesMutex);
    for (auto* d : s_instances) {
        if ((int)d->labelType == idx)
            queue_payload(d, alt3);
    }
}
