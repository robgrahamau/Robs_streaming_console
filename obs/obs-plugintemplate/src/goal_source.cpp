#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <obs-module.h>
#include "goal_source.h"
#include "layout_renderer.h"
#include "renderer.h"
#include <mutex>
#include <vector>
#include <string>
#include <unordered_map>
#include <algorithm>

// ── Global goal layout storage (index → ALT3 payload) ────────────────────────
static std::unordered_map<int, std::vector<uint8_t>> s_goal_payloads;
static std::mutex                                      s_goal_payloadMutex;

// ── Global goal names (populated by SetGoalNames pipe message) ────────────────
static std::vector<std::string> s_goal_names;   // UTF-8 strings
static std::mutex                s_goal_namesMutex;

// ── Per-instance data ─────────────────────────────────────────────────────────
struct GoalData {
    obs_source_t*   source   = nullptr;
    gs_texture_t*   texture  = nullptr;
    LayoutRenderer* renderer = nullptr;
    int             goalIdx  = 0;
    std::mutex      mutex;
    std::vector<uint8_t> pendingLayout;
    bool            layoutDirty = false;

    float elapsed   = 0.f;
    bool  hasLayout = false;
    int   outW      = 600;
    int   outH      = 120;
};

static std::vector<GoalData*> s_goal_instances;
static std::mutex              s_goal_instancesMutex;

// ── Helpers ────────────────────────────────────────────────────────────────────

static void goal_rebuild_texture(GoalData* d)
{
    if (!d->renderer || !d->hasLayout) return;
    obs_enter_graphics();
    if (d->texture) { gs_texture_destroy(d->texture); d->texture = nullptr; }
    std::vector<uint8_t> pixels;
    float dur = d->renderer->Duration();
    float t   = (dur > 0.f) ? std::min(d->elapsed, dur) : 0.f;
    d->renderer->RenderFrame(t, pixels);
    int w = d->renderer->Width();
    int h = d->renderer->Height();
    if (w > 0 && h > 0 && (int)pixels.size() == w * h * 4) {
        const uint8_t* rows[1] = { pixels.data() };
        d->texture = gs_texture_create(w, h, GS_BGRA, 1, (const uint8_t**)rows, 0);
        d->outW = w; d->outH = h;
    }
    obs_leave_graphics();
}

static void goal_apply_payload(GoalData* d, const std::vector<uint8_t>& payload)
{
    if (!d->renderer) d->renderer = new LayoutRenderer();
    d->elapsed = 0.f;
    d->hasLayout = d->renderer->Parse(payload);
    goal_rebuild_texture(d);
}

static void goal_queue_payload(GoalData* d, const std::vector<uint8_t>& payload)
{
    std::lock_guard<std::mutex> lock(d->mutex);
    d->pendingLayout = payload;
    d->layoutDirty = true;
}

static void goal_apply_pending_layout(GoalData* d)
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
        goal_apply_payload(d, payload);
}

// ── OBS source callbacks ──────────────────────────────────────────────────────

static const char* goal_get_name(void*) { return "Streaming Goal"; }

static void* goal_create(obs_data_t* settings, obs_source_t* source)
{
    auto* d = new GoalData();
    d->source  = source;
    d->goalIdx = (int)obs_data_get_int(settings, "goal_type");
    std::vector<uint8_t> initialPayload;
    {
        std::lock_guard<std::mutex> lp(s_goal_payloadMutex);
        auto it = s_goal_payloads.find(d->goalIdx);
        if (it != s_goal_payloads.end() && !it->second.empty())
            initialPayload = it->second;
    }
    if (!initialPayload.empty())
        goal_queue_payload(d, initialPayload);
    std::lock_guard<std::mutex> li(s_goal_instancesMutex);
    s_goal_instances.push_back(d);
    return d;
}

static void goal_destroy(void* data)
{
    auto* d = static_cast<GoalData*>(data);
    { std::lock_guard<std::mutex> lock(s_goal_instancesMutex);
      s_goal_instances.erase(std::remove(s_goal_instances.begin(), s_goal_instances.end(), d),
                              s_goal_instances.end()); }
    obs_enter_graphics();
    if (d->texture) gs_texture_destroy(d->texture);
    obs_leave_graphics();
    delete d->renderer;
    delete d;
}

static void goal_update(void* data, obs_data_t* settings)
{
    auto* d = static_cast<GoalData*>(data);
    int newIdx = (int)obs_data_get_int(settings, "goal_type");
    if (newIdx != d->goalIdx) {
        d->goalIdx = newIdx;
        std::vector<uint8_t> payload;
        {
            std::lock_guard<std::mutex> lock(s_goal_payloadMutex);
            auto it = s_goal_payloads.find(newIdx);
            if (it != s_goal_payloads.end() && !it->second.empty())
                payload = it->second;
        }
        if (!payload.empty())
            goal_queue_payload(d, payload);
    }
}

static void goal_tick(void* data, float seconds)
{
    auto* d = static_cast<GoalData*>(data);
    goal_apply_pending_layout(d);
    if (!d->hasLayout || !d->renderer) return;
    float dur = d->renderer->Duration();
    if (d->elapsed >= dur && dur > 0.f) return;
    d->elapsed += seconds;
    if (dur > 0.f && d->elapsed > dur) d->elapsed = dur;
    goal_rebuild_texture(d);
}

static void goal_render(void* data, gs_effect_t* effect)
{
    auto* d = static_cast<GoalData*>(data);
    if (!d->texture) return;
    gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), d->texture);
    gs_draw_sprite(d->texture, 0, (uint32_t)d->outW, (uint32_t)d->outH);
}

static uint32_t goal_width(void* data)  { return (uint32_t)static_cast<GoalData*>(data)->outW; }
static uint32_t goal_height(void* data) { return (uint32_t)static_cast<GoalData*>(data)->outH; }

static obs_properties_t* goal_get_properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_property_t* p = obs_properties_add_list(props, "goal_type", "Goal",
                                                  OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_INT);
    std::lock_guard<std::mutex> lock(s_goal_namesMutex);
    if (s_goal_names.empty()) {
        obs_property_list_add_int(p, "Goal 0", 0);
    } else {
        for (int i = 0; i < (int)s_goal_names.size(); i++)
            obs_property_list_add_int(p, s_goal_names[i].c_str(), i);
    }
    return props;
}

static void goal_get_defaults(obs_data_t* settings)
{
    obs_data_set_default_int(settings, "goal_type", 0);
}

// ── Registration ──────────────────────────────────────────────────────────────

void goal_source_register()
{
    struct obs_source_info info = {};
    info.id             = "steaming_goal";
    info.type           = OBS_SOURCE_TYPE_INPUT;
    info.output_flags   = OBS_SOURCE_VIDEO;
    info.get_name       = goal_get_name;
    info.create         = goal_create;
    info.destroy        = goal_destroy;
    info.update         = goal_update;
    info.video_tick     = goal_tick;
    info.video_render   = goal_render;
    info.get_width      = goal_width;
    info.get_height     = goal_height;
    info.get_properties = goal_get_properties;
    info.get_defaults   = goal_get_defaults;
    obs_register_source(&info);
}

// ── Global dispatch from pipe thread ─────────────────────────────────────────

void goal_source_set_layout(const std::vector<uint8_t>& payload)
{
    // [1]goalIndex [N]ALT3_bytes
    if (payload.size() < 2) return;
    int idx = (int)payload[0];
    std::vector<uint8_t> alt3(payload.begin() + 1, payload.end());
    {
        std::lock_guard<std::mutex> lock(s_goal_payloadMutex);
        s_goal_payloads[idx] = alt3;
    }
    std::lock_guard<std::mutex> lock(s_goal_instancesMutex);
    for (auto* d : s_goal_instances) {
        if (d->goalIdx == idx)
            goal_queue_payload(d, alt3);
    }
}

void goal_source_set_names(const std::vector<uint8_t>& payload)
{
    // [2]count [2+N]name0_utf8 [2+N]name1_utf8 ...
    if (payload.size() < 2) return;
    uint16_t count;
    std::memcpy(&count, payload.data(), 2);
    size_t offset = 2;

    std::vector<std::string> names;
    names.reserve(count);
    for (int i = 0; i < (int)count && offset + 2 <= payload.size(); i++) {
        uint16_t slen;
        std::memcpy(&slen, payload.data() + offset, 2);
        offset += 2;
        if (offset + slen > payload.size()) break;
        names.emplace_back(reinterpret_cast<const char*>(payload.data() + offset), slen);
        offset += slen;
    }

    { std::lock_guard<std::mutex> lock(s_goal_namesMutex);
      s_goal_names = std::move(names); }

    // Do not force property refresh from the pipe thread.
    // OBS will pick up the new names the next time properties are opened.
}
