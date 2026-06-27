#pragma once
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>
#include <cstdint>
#include <cstring>
#include <memory>

// ── Enumerations (match C# AlertElementType / AlertEasing) ───────────────────
enum class ElemType        : uint8_t { Rect=0, Text=1, Image=2, Gif=3, Audio=4, GoalBar=5, Video=6 };
enum class EasingKind      : uint8_t { Linear=0, EaseIn=1, EaseOut=2, EaseInOut=3, Bounce=4 };
enum class TextTransition  : uint8_t { Cut=0, TypeOn=1, SlideLeft=2, SlideRight=3, Fade=4, Morph=5 };
// Numeric values are part of the ALT3 wire format; keep existing values stable across C# and C++.
enum class VideoEndBehavior: uint8_t { Loop=0, Hold=1, EndHide=2, EndFade=3, HoldFirst=4 };

// Streaming video decoder (defined in layout_renderer.cpp). Forward-declared so LayoutElement can
// own one without pulling Media Foundation headers into every translation unit. LayoutElement is
// only ever moved (never copied) and is only destroyed inside layout_renderer.cpp, where the type
// is complete — so the unique_ptr below compiles cleanly.
class VideoDecoder;

// ── Image frame (used for both static images and GIF frames) ─────────────────
struct ImageFrame {
    std::vector<uint8_t> bgra;  // premultiplied BGRA pixels
    int   width  = 0;
    int   height = 0;
    float delay  = 0.1f;    // seconds (GIFs only)
};

// ── Rich text span (ALT3 format) ──────────────────────────────────────────────
struct TextSpan {
    std::wstring text;
    std::wstring fontFamily = L"Segoe UI";
    int          fontSize   = 24;
    bool         bold       = false;
    bool         italic     = false;
    uint32_t     color      = 0xFFFFFFFFu;  // ARGB
};

// ── Keyframe ─────────────────────────────────────────────────────────────────
// Wire: time(f32) mask(u8) easing(u8) extMask(u8) [fields per mask] [extMask fields]
//   extMask bit 0: fillColor (u32 ARGB)
//   extMask bit 1: kfSpans (u16 count + span records)
//   extMask bit 2: kfShadow (u8 on, u32 ARGB color)
//   extMask bit 3: kfOutline (u8 on, u32 ARGB color, u8 width)
//   extMask bit 4: cornerRadius (u8)
//   extMask bit 5: shadowGeom (i16 shadowX, i16 shadowY, u8 blur)
//   extMask bit 6: textAlign (u8: bits0-1=hAlign 0-2, bits2-3=vAlign 0-2)
//   extMask bit 7: spanTransition (u8 TextTransition enum, only written when != Cut)
struct Keyframe {
    float      time     = 0.f;
    EasingKind easing   = EasingKind::Linear;
    uint8_t    mask     = 0;     // bits: 0=x 1=y 2=w 3=h 4=opacity 5=sx 6=sy 7=rotation
    uint8_t    extMask  = 0;
    float      x=0,y=0,w=0,h=0,opacity=1.f,sx=1.f,sy=1.f,rotation=0.f;
    uint32_t   kfFillColor = 0u; // ARGB, valid if extMask & 0x01
    std::vector<TextSpan> kfSpans;  // valid if extMask & 0x02 (empty = no span override)
    bool       kfShadowHas = false; // extMask & 0x04
    bool       kfShadowOn  = false;
    uint32_t   kfShadowColor = 0u;  // ARGB
    bool       kfOutlineHas  = false; // extMask & 0x08
    bool       kfOutlineOn   = false;
    uint32_t   kfOutlineColor = 0u; // ARGB
    uint8_t    kfOutlineWidth = 1;
    // New animated properties (extMask bits 4-6)
    bool       kfCornerHas    = false; // extMask & 0x10
    int        kfCornerRadius = 0;
    bool       kfShadowGeomHas = false; // extMask & 0x20
    int        kfShadowX = 0;
    int        kfShadowY = 0;
    int        kfShadowBlur = 0;
    bool       kfAlignHas  = false; // extMask & 0x40
    int        kfTextAlign = 1;
    int        kfVertAlign = 1;
    // extMask bit 7: span transition (stored on the NEXT span-KF, describes how to animate from prev → this)
    TextTransition kfSpanTransition = TextTransition::Cut; // default = Cut (step change)
};

// ── Audio volume envelope keyframe ────────────────────────────────────────────
struct AudioVolumeKf {
    float time   = 0.f;
    float volume = 1.f;
};

// ── Layout element ────────────────────────────────────────────────────────────
struct LayoutElement {
    ElemType type     = ElemType::Rect;
    float    x=0,y=0,w=100,h=100;
    float    rotation = 0.f; // degrees
    int      zOrder   = 0;
    std::vector<Keyframe> keyframes;

    // Rect
    uint32_t fillColor    = 0xCC000000u;  // ARGB
    int      cornerRadius = 0;

    // Text (ALT3: span-based)
    std::vector<TextSpan> spans;
    int      textAlign    = 1;           // 0=left 1=center 2=right
    int      vertAlign    = 1;           // 0=top 1=middle 2=bottom (packed in textFlags bits 2-3)
    bool     shadow       = false;
    uint32_t shadowColor  = 0xAA000000u;
    int      shadowOffX   = 2;
    int      shadowOffY   = 2;
    int      shadowBlur   = 0;   // px; 0=sharp; software box blur applied to shadow layer
    bool     outline      = false;
    uint32_t outlineColor = 0xFF000000u;
    int      outlineWidth = 1;

    // Image / Gif
    std::wstring filePath;
    std::vector<ImageFrame> imageFrames;  // loaded at parse time
    float gifTotalDuration  = 0.f;
    float gifAnimStartTime  = 0.f;  // derived from first keyframe; GIF animates from frame 0 at this time

    // Video (ElemType::Video) — frames streamed on a worker thread, NOT pre-decoded.
    VideoEndBehavior videoEnd   = VideoEndBehavior::Loop;
    bool             videoMuted = false;
    float            videoVolume = 1.f;       // 0–2, applied to the decoded audio track
    float            videoStartTime = 0.f;    // derived from first keyframe (like gifAnimStartTime)
    std::unique_ptr<VideoDecoder> videoDecoder;  // created at parse, released when elements clear

    // Audio clip (ElemType::Audio) — PCM decoded once at parse time
    // Binary layout: filePath(str) startTime(f32) volL(f32) volR(f32) fadeIn(f32) fadeOut(f32)
    //                envCount(u16) [time(f32) vol(f32)]...
    float audioStartTime = 0.f;
    float audioVolumeL   = 1.f;  // left channel scale 0–2
    float audioVolumeR   = 1.f;  // right channel scale 0–2
    float audioFadeIn    = 0.f;  // seconds
    float audioFadeOut   = 0.f;  // seconds
    float pcmDurationSec = 0.f;  // decoded audio duration
    std::vector<AudioVolumeKf> audioEnvelope;
    std::vector<float>         pcmSamples;   // float32 interleaved stereo 44100Hz, loaded at parse time
};

// ── Resolved per-element animated state ──────────────────────────────────────
struct ElemState {
    float    x, y, w, h;
    float    opacity;
    float    scaleX, scaleY;
    float    rotation;     // degrees
    uint32_t fillColor;    // ARGB; 0 = use element's static fillColor
    int      cornerRadius; // -1 = use element's static cornerRadius
    int      shadowOffX;   // INT_MIN = use element's static shadow
    int      shadowOffY;
    int      shadowBlur;
    int      textAlign;    // -1 = use element's static textAlign
    int      vertAlign;    // -1 = use element's static vertAlign
};

// ── Full parsed layout ────────────────────────────────────────────────────────
struct AlertLayoutData {
    bool  valid      = false;
    int   canvasW    = 800;
    int   canvasH    = 200;
    float duration   = 5.f;

    std::vector<LayoutElement> elements;

    // Substituted variable values
    std::wstring username;
    std::wstring message;
    std::wstring platform;
    int          amount = 0;

    // Audio
    std::wstring soundFile; // absolute path, empty = no sound
    float        volume    = 1.f;
    std::vector<AudioVolumeKf> volumeEnvelope; // sorted by time; empty = flat volume
};

// ── Volume envelope interpolation ────────────────────────────────────────────
// Returns interpolated volume at time t. Falls back to baseVolume if envelope is empty.
inline float EvalVolumeEnvelope(const std::vector<AudioVolumeKf>& env, float baseVolume, float t)
{
    if (env.empty()) return baseVolume;
    if (t <= env.front().time) return env.front().volume;
    if (t >= env.back().time)  return env.back().volume;
    for (size_t i = 1; i < env.size(); i++) {
        if (t <= env[i].time) {
            float span = env[i].time - env[i-1].time;
            float frac = span > 0.f ? (t - env[i-1].time) / span : 0.f;
            return env[i-1].volume + frac * (env[i].volume - env[i-1].volume);
        }
    }
    return baseVolume;
}

// ── Easing function ───────────────────────────────────────────────────────────
inline float ApplyEasing(EasingKind kind, float t)
{
    t = t < 0.f ? 0.f : (t > 1.f ? 1.f : t);
    switch (kind) {
        case EasingKind::EaseIn:    return t * t;
        case EasingKind::EaseOut:   return 1.f - (1.f - t) * (1.f - t);
        case EasingKind::EaseInOut: return t < .5f ? 2.f*t*t : 1.f - 2.f*(1.f-t)*(1.f-t);
        case EasingKind::Bounce: {
            float u = 1.f - t;
            if (u < 1.f/2.75f)      return 1.f - 7.5625f*u*u;
            else if (u < 2.f/2.75f) { u -= 1.5f/2.75f;   return 1.f - (7.5625f*u*u + .75f);  }
            else if (u < 2.5f/2.75f){ u -= 2.25f/2.75f;  return 1.f - (7.5625f*u*u + .9375f);}
            else                    { u -= 2.625f/2.75f;  return 1.f - (7.5625f*u*u + .984375f);}
        }
        default: return t;
    }
}

// ── Evaluate all animated properties for an element at time t ─────────────────
inline ElemState EvalElemState(const LayoutElement& el, float t)
{
    ElemState s;
    s.x            = el.x;
    s.y            = el.y;
    s.w            = el.w;
    s.h            = el.h;
    s.opacity      = 1.f;
    s.scaleX       = 1.f;
    s.scaleY       = 1.f;
    s.rotation     = el.rotation;
    s.fillColor    = 0u;    // 0 = use element's static fillColor
    s.cornerRadius = -1;    // -1 = use element's static cornerRadius
    s.shadowOffX   = INT_MIN; // INT_MIN = use element's static shadow geometry
    s.shadowOffY   = INT_MIN;
    s.shadowBlur   = INT_MIN;
    s.textAlign    = -1;    // -1 = use element's static alignment
    s.vertAlign    = -1;

    const auto& kfs = el.keyframes;
    if (kfs.empty()) return s;

    for (int bit = 0; bit < 8; bit++) {
        uint8_t bmask = (uint8_t)(1 << bit);

        int prev = -1, next = -1;
        for (int i = 0; i < (int)kfs.size(); i++) {
            if (!(kfs[i].mask & bmask)) continue;
            if (kfs[i].time <= t) prev = i;
            else if (next == -1)  next = i;
        }

        if (prev == -1 && next == -1) continue;

        auto kfVal = [&](const Keyframe& k) -> float {
            switch (bit) {
                case 0: return k.x;
                case 1: return k.y;
                case 2: return k.w;
                case 3: return k.h;
                case 4: return k.opacity;
                case 5: return k.sx;
                case 6: return k.sy;
                case 7: return k.rotation;
                default: return 0.f;
            }
        };

        float val;
        if (prev == -1) {
            val = kfVal(kfs[next]);
        } else if (next == -1) {
            val = kfVal(kfs[prev]);
        } else {
            float pval = kfVal(kfs[prev]);
            float nval = kfVal(kfs[next]);
            float span = kfs[next].time - kfs[prev].time;
            float raw  = (span > 0.f) ? (t - kfs[prev].time) / span : 1.f;
            float et   = ApplyEasing(kfs[next].easing, raw);
            val = pval + et * (nval - pval);
        }

        switch (bit) {
            case 0: s.x        = val; break;
            case 1: s.y        = val; break;
            case 2: s.w        = val; break;
            case 3: s.h        = val; break;
            case 4: s.opacity  = val; break;
            case 5: s.scaleX   = val; break;
            case 6: s.scaleY   = val; break;
            case 7: s.rotation = val; break;
        }
    }

    // Interpolate fill colour (extMask bit 0) — ARGB components separately
    {
        int prevC = -1, nextC = -1;
        for (int i = 0; i < (int)kfs.size(); i++) {
            if (!(kfs[i].extMask & 0x01)) continue;
            if (kfs[i].time <= t) prevC = i;
            else if (nextC == -1) nextC = i;
        }
        if (prevC != -1 || nextC != -1) {
            uint32_t col;
            if (prevC == -1) {
                col = kfs[nextC].kfFillColor;
            } else if (nextC == -1) {
                col = kfs[prevC].kfFillColor;
            } else {
                float span = kfs[nextC].time - kfs[prevC].time;
                float raw  = (span > 0.f) ? (t - kfs[prevC].time) / span : 1.f;
                float et   = ApplyEasing(kfs[nextC].easing, raw);
                uint32_t a0 = (kfs[prevC].kfFillColor >> 24) & 0xFF;
                uint32_t r0 = (kfs[prevC].kfFillColor >> 16) & 0xFF;
                uint32_t g0 = (kfs[prevC].kfFillColor >>  8) & 0xFF;
                uint32_t b0 =  kfs[prevC].kfFillColor        & 0xFF;
                uint32_t a1 = (kfs[nextC].kfFillColor >> 24) & 0xFF;
                uint32_t r1 = (kfs[nextC].kfFillColor >> 16) & 0xFF;
                uint32_t g1 = (kfs[nextC].kfFillColor >>  8) & 0xFF;
                uint32_t b1 =  kfs[nextC].kfFillColor        & 0xFF;
                auto lerp = [&](uint32_t c0, uint32_t c1) -> uint32_t {
                    return (uint32_t)(c0 + et * (float)((int)c1 - (int)c0) + 0.5f);
                };
                col = (lerp(a0,a1)<<24)|(lerp(r0,r1)<<16)|(lerp(g0,g1)<<8)|lerp(b0,b1);
            }
            s.fillColor = col;
        }
    }

    // ── Corner radius (extMask bit 4) ─────────────────────────────────────────
    {
        int prevC = -1, nextC = -1;
        for (int i = 0; i < (int)kfs.size(); i++) {
            if (!(kfs[i].kfCornerHas)) continue;
            if (kfs[i].time <= t) prevC = i;
            else if (nextC == -1) nextC = i;
        }
        if (prevC != -1 || nextC != -1) {
            if (prevC == -1) {
                s.cornerRadius = kfs[nextC].kfCornerRadius;
            } else if (nextC == -1) {
                s.cornerRadius = kfs[prevC].kfCornerRadius;
            } else {
                float span = kfs[nextC].time - kfs[prevC].time;
                float raw  = (span > 0.f) ? (t - kfs[prevC].time) / span : 1.f;
                float et   = ApplyEasing(kfs[nextC].easing, raw);
                s.cornerRadius = (int)(kfs[prevC].kfCornerRadius + et * (kfs[nextC].kfCornerRadius - kfs[prevC].kfCornerRadius) + 0.5f);
            }
        }
    }

    // ── Shadow geometry (extMask bit 5) ──────────────────────────────────────
    {
        int prevC = -1, nextC = -1;
        for (int i = 0; i < (int)kfs.size(); i++) {
            if (!kfs[i].kfShadowGeomHas) continue;
            if (kfs[i].time <= t) prevC = i;
            else if (nextC == -1) nextC = i;
        }
        if (prevC != -1 || nextC != -1) {
            auto lerpInt = [](int a, int b, float f) { return (int)(a + f * (b - a) + 0.5f); };
            if (prevC == -1) {
                s.shadowOffX = kfs[nextC].kfShadowX; s.shadowOffY = kfs[nextC].kfShadowY; s.shadowBlur = kfs[nextC].kfShadowBlur;
            } else if (nextC == -1) {
                s.shadowOffX = kfs[prevC].kfShadowX; s.shadowOffY = kfs[prevC].kfShadowY; s.shadowBlur = kfs[prevC].kfShadowBlur;
            } else {
                float span = kfs[nextC].time - kfs[prevC].time;
                float raw  = (span > 0.f) ? (t - kfs[prevC].time) / span : 1.f;
                float et   = ApplyEasing(kfs[nextC].easing, raw);
                s.shadowOffX = lerpInt(kfs[prevC].kfShadowX, kfs[nextC].kfShadowX, et);
                s.shadowOffY = lerpInt(kfs[prevC].kfShadowY, kfs[nextC].kfShadowY, et);
                s.shadowBlur = lerpInt(kfs[prevC].kfShadowBlur, kfs[nextC].kfShadowBlur, et);
            }
        }
    }

    // ── Text alignment (extMask bit 6) ────────────────────────────────────────
    // Step-change: use value of the last KF at or before t
    {
        int prevC = -1, nextC = -1;
        for (int i = 0; i < (int)kfs.size(); i++) {
            if (!kfs[i].kfAlignHas) continue;
            if (kfs[i].time <= t) prevC = i;
            else if (nextC == -1) nextC = i;
        }
        if (prevC != -1) {
            s.textAlign = kfs[prevC].kfTextAlign;
            s.vertAlign = kfs[prevC].kfVertAlign;
        } else if (nextC != -1) {
            s.textAlign = kfs[nextC].kfTextAlign;
            s.vertAlign = kfs[nextC].kfVertAlign;
        }
    }

    return s;
}

// ── Evaluate effective text spans at time t ───────────────────────────────────
// Returns a pointer to the element's span list when no interpolation is needed,
// or fills `out` with interpolated spans and returns a pointer to `out`.
// Text/font/bold/italic/size: taken from FROM spans.
// Colors: RGBA-interpolated via character-position matching (handles count mismatches).
// el.spans acts as an implicit T=0 anchor so a single span KF still animates smoothly.

// Find the color of the span that covers character position charPos.
static inline uint32_t ColorAtCharCpp(const std::vector<TextSpan>& spans, int charPos)
{
    int pos = 0;
    for (const auto& s : spans) {
        int len = (int)s.text.size();
        if (charPos < pos + len) return s.color;
        pos += len;
    }
    return spans.empty() ? 0xFFFFFFFFu : spans.back().color;
}

static inline void InterpolateSpanColorsCpp(
    const std::vector<TextSpan>& from, const std::vector<TextSpan>& to,
    float frac, std::vector<TextSpan>& out)
{
    out.resize(from.size());
    int charOffset = 0;
    for (size_t i = 0; i < from.size(); i++) {
        out[i]       = from[i];
        uint32_t ca  = from[i].color;
        uint32_t cb  = ColorAtCharCpp(to, charOffset);
        auto lerpCh  = [&](int shift) -> uint32_t {
            int a = (int)((ca >> shift) & 0xFF);
            int b = (int)((cb >> shift) & 0xFF);
            return (uint32_t)(a + frac * (float)(b - a) + 0.5f);
        };
        out[i].color = (lerpCh(24)<<24)|(lerpCh(16)<<16)|(lerpCh(8)<<8)|lerpCh(0);
        charOffset  += (int)from[i].text.size();
    }
}

inline const std::vector<TextSpan>* EvalSpansAt(
    const LayoutElement& el, float t, std::vector<TextSpan>& out)
{
    // Collect span KFs sorted by time (keyframes are already sorted by Parse).
    int first = -1, prev = -1, next = -1;
    for (int i = 0; i < (int)el.keyframes.size(); i++) {
        if (el.keyframes[i].kfSpans.empty()) continue;
        if (first == -1) first = i;
        if (el.keyframes[i].time <= t) prev = i;
        else if (next == -1) { next = i; break; }
    }

    if (first == -1) return &el.spans; // no span KFs — use element default

    if (prev == -1) {
        // Before the first span KF — treat el.spans as an implicit T=0 anchor.
        // Interpolate from el.spans toward the first span KF.
        float firstTime = el.keyframes[first].time;
        if (firstTime <= 0.f) return &el.keyframes[first].kfSpans;
        float frac = t / firstTime;
        if (frac < 0.f) frac = 0.f; if (frac > 1.f) frac = 1.f;
        InterpolateSpanColorsCpp(el.spans, el.keyframes[first].kfSpans, frac, out);
        return &out;
    }

    if (next == -1) return &el.keyframes[prev].kfSpans; // after last span KF: hold

    float span = el.keyframes[next].time - el.keyframes[prev].time;
    float frac = (span > 0.f) ? (t - el.keyframes[prev].time) / span : 1.f;
    if (frac < 0.f) frac = 0.f; if (frac > 1.f) frac = 1.f;

    // TypeOn: reveal next-KF text character by character
    if (el.keyframes[next].kfSpanTransition == TextTransition::TypeOn) {
        const auto& toSpans = el.keyframes[next].kfSpans;
        int total = 0;
        for (const auto& sp : toSpans) total += (int)sp.text.size();
        int visible = (int)(total * frac);
        if (visible <= 0) { out.clear(); return &out; }
        out.clear();
        int rem = visible;
        for (const auto& sp : toSpans) {
            if (rem <= 0) break;
            if (rem >= (int)sp.text.size()) {
                out.push_back(sp);
                rem -= (int)sp.text.size();
            } else {
                TextSpan partial = sp;
                partial.text = sp.text.substr(0, rem);
                out.push_back(partial);
                break;
            }
        }
        return &out;
    }

    InterpolateSpanColorsCpp(el.keyframes[prev].kfSpans, el.keyframes[next].kfSpans, frac, out);
    return &out;
}

// ── Evaluate text transition state at time t (for dual-pass Fade/Slide/Morph) ─
// Returns inTransition=false for Cut, TypeOn (handled by EvalSpansAt), or outside a gap.
struct TextTransitionInfo {
    bool inTransition = false;
    TextTransition type = TextTransition::Cut;
    float frac = 0.f;
    const std::vector<TextSpan>* fromSpans = nullptr;
    const std::vector<TextSpan>* toSpans   = nullptr;
};

inline TextTransitionInfo EvalTextTransitionAt(const LayoutElement& el, float t)
{
    int prev = -1, next = -1;
    for (int i = 0; i < (int)el.keyframes.size(); i++) {
        if (el.keyframes[i].kfSpans.empty()) continue;
        if (el.keyframes[i].time <= t) prev = i;
        else if (next == -1) { next = i; break; }
    }
    if (prev == -1 || next == -1) return {};

    TextTransition type = el.keyframes[next].kfSpanTransition;
    if (type == TextTransition::Cut || type == TextTransition::TypeOn) return {};

    float span = el.keyframes[next].time - el.keyframes[prev].time;
    float frac = (span > 0.f) ? (t - el.keyframes[prev].time) / span : 1.f;
    if (frac <= 0.f || frac >= 1.f) return {};

    return { true, type, frac,
             &el.keyframes[prev].kfSpans,
             &el.keyframes[next].kfSpans };
}

// ── Evaluate effective shadow/outline state at time t ────────────────────────
struct KfShadowState { bool has; bool on; uint32_t color; };
struct KfOutlineState { bool has; bool on; uint32_t color; uint8_t width; };

inline KfShadowState EvalShadowKfAt(const LayoutElement& el, float t)
{
    for (int i = (int)el.keyframes.size() - 1; i >= 0; i--) {
        if (el.keyframes[i].kfShadowHas && el.keyframes[i].time <= t)
            return { true, el.keyframes[i].kfShadowOn, el.keyframes[i].kfShadowColor };
    }
    return { false, false, 0u };
}

inline KfOutlineState EvalOutlineKfAt(const LayoutElement& el, float t)
{
    for (int i = (int)el.keyframes.size() - 1; i >= 0; i--) {
        if (el.keyframes[i].kfOutlineHas && el.keyframes[i].time <= t)
            return { true, el.keyframes[i].kfOutlineOn, el.keyframes[i].kfOutlineColor, el.keyframes[i].kfOutlineWidth };
    }
    return { false, false, 0u, 1u };
}
