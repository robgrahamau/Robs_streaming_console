#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <obs-module.h>
#include <util/platform.h>
#include "alert_source.h"
#include "layout_renderer.h"
#include <queue>
#include <mutex>
#include <vector>
#include <string>
#include <algorithm>
#include <cmath>

#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfuuid.lib")

static const int AUDIO_SAMPLE_RATE  = 44100;
static const int AUDIO_CHANNELS     = 2;

// Load an audio file via Media Foundation, decode to interleaved float32 stereo 44100Hz.
// Returns true on success; out is filled with (channels * frames) float samples.
static bool LoadAudioMF(const std::wstring& path, std::vector<float>& out)
{
    out.clear();
    if (path.empty()) return false;

    IMFSourceReader* reader = nullptr;
    HRESULT hr = MFCreateSourceReaderFromURL(path.c_str(), nullptr, &reader);
    if (FAILED(hr)) return false;

    // Configure output: float32, stereo, 44100 Hz
    IMFMediaType* audioType = nullptr;
    MFCreateMediaType(&audioType);
    audioType->SetGUID(MF_MT_MAJOR_TYPE,              MFMediaType_Audio);
    audioType->SetGUID(MF_MT_SUBTYPE,                 MFAudioFormat_Float);
    audioType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
    audioType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS,    AUDIO_CHANNELS);
    audioType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, AUDIO_SAMPLE_RATE);
    HRESULT hrFmt = reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, nullptr, audioType);
    audioType->Release();
    if (FAILED(hrFmt)) {
        reader->Release();
        return false;
    }

    bool ok = false;
    while (true) {
        DWORD flags = 0;
        IMFSample* sample = nullptr;
        hr = reader->ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                                nullptr, &flags, nullptr, &sample);
        if (FAILED(hr) || (flags & MF_SOURCE_READERF_ENDOFSTREAM)) {
            if (sample) sample->Release(); // MF can return a sample alongside ENDOFSTREAM
            break;
        }
        if (!sample) continue;

        IMFMediaBuffer* buf = nullptr;
        if (SUCCEEDED(sample->ConvertToContiguousBuffer(&buf))) {
            BYTE* data = nullptr;
            DWORD len  = 0;
            if (SUCCEEDED(buf->Lock(&data, nullptr, &len))) {
                size_t n = len / sizeof(float);
                float* f = reinterpret_cast<float*>(data);
                out.insert(out.end(), f, f + n);
                buf->Unlock();
                ok = true;
            }
            buf->Release();
        }
        sample->Release();
    }
    reader->Release();
    return ok && !out.empty();
}

// ── Per-alert item (already parsed by LayoutRenderer) ────────────────────────
struct AlertItem {
    std::vector<uint8_t> payload;  // raw V2 binary — parsed lazily per instance
};

// ── Per-source instance data ──────────────────────────────────────────────────
struct AlertData {
    obs_source_t*   source   = nullptr;
    gs_texture_t*   texture  = nullptr;
    LayoutRenderer* renderer = nullptr;

    bool  active   = false;
    float elapsed  = 0.0f;
    float duration = 5.0f;

    // Output size set via OBS Properties
    int outW = 800;
    int outH = 200;

    // Legacy single-clip audio (from AlertLayoutData.soundFile)
    std::vector<float> audioSamples;   // decoded float32 interleaved stereo
    size_t             audioPos  = 0;
    float              audioVolume = 1.0f;
    std::vector<AudioVolumeKf> audioEnvelope;

    // Pre-allocated mix buffer — reused every tick, never re-allocated after first use
    std::vector<float> mixBuffer;

    // Cached texture dimensions — texture is updated in-place when size unchanged
    int texW = 0;
    int texH = 0;

    std::queue<AlertItem> queue;
    std::mutex            queueMutex;
};

// ── Registry: all live instances, so enqueue broadcasts to all ────────────────
static std::vector<AlertData*> s_instances;
static std::mutex              s_instancesMutex;

// ── OBS source callbacks ──────────────────────────────────────────────────────
static const char* alert_get_name(void*) { return "Streaming Alert"; }

static obs_properties_t* alert_get_properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_int(props, "width",  "Width (px)",  100, 3840, 10);
    obs_properties_add_int(props, "height", "Height (px)", 100, 2160, 10);
    obs_properties_add_text(props, "_note",
        "Sets the source output size. Alerts are scaled to fit.", OBS_TEXT_INFO);
    return props;
}

static void alert_get_defaults(obs_data_t* settings)
{
    obs_data_set_default_int(settings, "width",  800);
    obs_data_set_default_int(settings, "height", 200);
}

static void alert_update(void* data, obs_data_t* settings)
{
    auto* d = static_cast<AlertData*>(data);
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    d->outW = (w >= 100) ? w : 800;
    d->outH = (h >= 100) ? h : 200;
}

static void* alert_create(obs_data_t* settings, obs_source_t* source)
{
    auto* d = new AlertData();
    d->source   = source;
    d->renderer = new LayoutRenderer();
    int w = (int)obs_data_get_int(settings, "width");
    int h = (int)obs_data_get_int(settings, "height");
    d->outW = (w >= 100) ? w : 800;
    d->outH = (h >= 100) ? h : 200;
    {
        std::lock_guard<std::mutex> lk(s_instancesMutex);
        s_instances.push_back(d);
    }
    blog(LOG_INFO, "[Steaming] Alert instance created (%zu total)", s_instances.size());
    return d;
}

static void alert_destroy(void* data)
{
    auto* d = static_cast<AlertData*>(data);
    {
        std::lock_guard<std::mutex> lk(s_instancesMutex);
        s_instances.erase(std::remove(s_instances.begin(), s_instances.end(), d), s_instances.end());
    }
    obs_enter_graphics();
    if (d->texture) gs_texture_destroy(d->texture);
    obs_leave_graphics();
    delete d->renderer;
    delete d;
}

static uint32_t alert_width(void* data)  { return (uint32_t)static_cast<AlertData*>(data)->outW; }
static uint32_t alert_height(void* data) { return (uint32_t)static_cast<AlertData*>(data)->outH; }

static void alert_tick(void* data, float seconds)
{
    auto* d = static_cast<AlertData*>(data);

    if (d->active) {
        // ── Audio mixing: legacy SoundFile clip + per-element Audio clips ────────
        uint32_t framesToOutput = std::max(1u, (uint32_t)(seconds * AUDIO_SAMPLE_RATE));
        size_t   totalSamples   = (size_t)framesToOutput * AUDIO_CHANNELS;

        // Grow mix buffer only — never shrink (avoids allocation in steady state)
        if (d->mixBuffer.size() < totalSamples)
            d->mixBuffer.resize(totalSamples);
        std::fill(d->mixBuffer.begin(), d->mixBuffer.begin() + totalSamples, 0.f);

        bool anyAudio = false;

        // Legacy single-clip (from layout-level SoundFile field)
        if (!d->audioSamples.empty() && d->audioPos < d->audioSamples.size()) {
            size_t remain = d->audioSamples.size() - d->audioPos;
            size_t n      = std::min(remain, totalSamples);
            uint32_t frames = (uint32_t)(n / AUDIO_CHANNELS);
            for (uint32_t fi = 0; fi < frames; fi++) {
                float t   = d->elapsed + (float)fi / AUDIO_SAMPLE_RATE;
                float vol = EvalVolumeEnvelope(d->audioEnvelope, d->audioVolume, t);
                d->mixBuffer[fi * AUDIO_CHANNELS + 0] += d->audioSamples[d->audioPos + fi*AUDIO_CHANNELS + 0] * vol;
                d->mixBuffer[fi * AUDIO_CHANNELS + 1] += d->audioSamples[d->audioPos + fi*AUDIO_CHANNELS + 1] * vol;
            }
            d->audioPos += n;
            anyAudio = true;
        }

        // Per-element Audio clips — positions computed from elapsed time (no state needed)
        for (const auto& el : d->renderer->Elements()) {
            // Audio clips AND video elements both feed their decoded PCM through this mixer.
            if ((el.type != ElemType::Audio && el.type != ElemType::Video) || el.pcmSamples.empty()) continue;
            float clipElapsed = d->elapsed - el.audioStartTime;
            if (clipElapsed < 0.f) continue; // not started yet
            size_t startPos = (size_t)(clipElapsed * AUDIO_SAMPLE_RATE) * AUDIO_CHANNELS;
            if (startPos >= el.pcmSamples.size()) continue; // finished

            size_t remain = el.pcmSamples.size() - startPos;
            size_t n      = std::min(remain, totalSamples);
            uint32_t frames = (uint32_t)(n / AUDIO_CHANNELS);

            for (uint32_t fi = 0; fi < frames; fi++) {
                float t = clipElapsed + (float)fi / AUDIO_SAMPLE_RATE;

                // Fade in/out
                float fade = 1.f;
                if (el.audioFadeIn  > 0.f && t < el.audioFadeIn)
                    fade *= t / el.audioFadeIn;
                if (el.audioFadeOut > 0.f && el.pcmDurationSec > 0.f) {
                    float remaining2 = el.pcmDurationSec - t;
                    if (remaining2 < el.audioFadeOut)
                        fade *= std::max(0.f, remaining2 / el.audioFadeOut);
                }

                float env = EvalVolumeEnvelope(el.audioEnvelope, 1.f, t);
                float sL  = el.pcmSamples[startPos + fi*AUDIO_CHANNELS + 0];
                float sR  = el.pcmSamples[startPos + fi*AUDIO_CHANNELS + 1];
                d->mixBuffer[fi*AUDIO_CHANNELS + 0] += sL * el.audioVolumeL * fade * env;
                d->mixBuffer[fi*AUDIO_CHANNELS + 1] += sR * el.audioVolumeR * fade * env;
            }
            anyAudio = true;
        }

        if (anyAudio) {
            obs_source_audio oa = {};
            oa.data[0]          = reinterpret_cast<const uint8_t*>(d->mixBuffer.data());
            oa.frames           = framesToOutput;
            oa.format           = AUDIO_FORMAT_FLOAT;
            oa.samples_per_sec  = (uint32_t)AUDIO_SAMPLE_RATE;
            oa.speakers         = SPEAKERS_STEREO;
            oa.timestamp        = os_gettime_ns();
            obs_source_output_audio(d->source, &oa);
        }

        d->elapsed += seconds;
        if (d->elapsed >= d->duration) {
            d->active  = false;
            d->elapsed = 0.0f;
            d->audioPos = 0;
            blog(LOG_INFO, "[Steaming] Alert completed.");
        }
    }

    if (!d->active) {
        std::lock_guard<std::mutex> lk(d->queueMutex);
        if (!d->queue.empty()) {
            AlertItem item = std::move(d->queue.front());
            d->queue.pop();
            if (d->renderer->Parse(item.payload)) {
                // Use canvas dimensions directly — no squish scaling
                d->outW         = d->renderer->Width();
                d->outH         = d->renderer->Height();
                d->duration     = d->renderer->Duration();
                d->elapsed      = 0.0f;
                d->active       = true;
                d->audioPos     = 0;
                d->audioVolume  = d->renderer->Volume();
                d->audioEnvelope = d->renderer->VolumeEnvelope();
                d->audioSamples.clear();

                const std::wstring& sf = d->renderer->SoundFile();
                if (!sf.empty()) {
                    if (!LoadAudioMF(sf, d->audioSamples))
                        blog(LOG_WARNING, "[Steaming] Failed to load sound: could not decode file");
                    else
                        blog(LOG_INFO, "[Steaming] Loaded audio: %zu frames", d->audioSamples.size() / AUDIO_CHANNELS);
                }

                blog(LOG_INFO, "[Steaming] Alert started. dur=%.1fs size=%dx%d sound=%s",
                     d->duration, d->renderer->Width(), d->renderer->Height(),
                     sf.empty() ? "none" : "yes");
            }
        }
    }
}

static void alert_render(void* data, gs_effect_t* effect)
{
    auto* d = static_cast<AlertData*>(data);
    if (!d->active) return;

    int W = d->renderer->Width();
    int H = d->renderer->Height();

    // Render new frame
    std::vector<uint8_t> pixels;
    d->renderer->RenderFrame(d->elapsed, pixels);
    if (pixels.empty()) return;

    if (d->texture) { gs_texture_destroy(d->texture); d->texture = nullptr; }
    const uint8_t* planes[1] = { pixels.data() };
    d->texture = gs_texture_create((uint32_t)W, (uint32_t)H, GS_BGRA, 1, planes, 0);
    if (!d->texture) return;

    gs_eparam_t* image = gs_effect_get_param_by_name(effect, "image");
    if (image) gs_effect_set_texture(image, d->texture);
    gs_draw_sprite(d->texture, 0, (uint32_t)W, (uint32_t)H);
}

// ── Registration ──────────────────────────────────────────────────────────────
static obs_source_info s_alertInfo = {};

void alert_source_register()
{
    s_alertInfo.id             = "steaming_alert";
    s_alertInfo.type           = OBS_SOURCE_TYPE_INPUT;
    s_alertInfo.output_flags   = OBS_SOURCE_VIDEO | OBS_SOURCE_AUDIO;
    s_alertInfo.get_name       = alert_get_name;
    s_alertInfo.get_properties = alert_get_properties;
    s_alertInfo.get_defaults   = alert_get_defaults;
    s_alertInfo.create         = alert_create;
    s_alertInfo.update         = alert_update;
    s_alertInfo.destroy        = alert_destroy;
    s_alertInfo.get_width      = alert_width;
    s_alertInfo.get_height     = alert_height;
    s_alertInfo.video_render   = alert_render;
    s_alertInfo.video_tick     = alert_tick;
    obs_register_source(&s_alertInfo);
    blog(LOG_INFO, "[Steaming] Registered source: steaming_alert");
}

// Broadcasts to ALL instances so every scene shows the alert simultaneously.
void alert_source_enqueue_v2(const std::vector<uint8_t>& payload)
{
    std::lock_guard<std::mutex> lk(s_instancesMutex);
    for (AlertData* d : s_instances) {
        std::lock_guard<std::mutex> ilk(d->queueMutex);
        d->queue.push({ payload });
    }
    blog(LOG_INFO, "[Steaming] V2 alert broadcast to %zu instance(s)", s_instances.size());
}
