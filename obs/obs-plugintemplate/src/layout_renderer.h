#pragma once
#include "layout_types.h"
#include "renderer.h"
#include <vector>
#include <cstdint>

class LayoutRenderer {
public:
    LayoutRenderer();
    ~LayoutRenderer();

    // Parse the RenderAlertV2 binary payload.
    // Returns false if magic or format is wrong.
    bool Parse(const std::vector<uint8_t>& payload);

    int          Width()    const { return m_data.canvasW; }
    int          Height()   const { return m_data.canvasH; }
    float        Duration() const { return m_data.duration; }
    const std::wstring& SoundFile()     const { return m_data.soundFile; }
    float               Volume()        const { return m_data.volume; }
    const std::vector<AudioVolumeKf>& VolumeEnvelope() const { return m_data.volumeEnvelope; }
    const std::vector<LayoutElement>& Elements()       const { return m_data.elements; }

    // Render the layout at elapsed time t (seconds since alert start).
    // Writes premultiplied BGRA into `out` (size = Width*Height*4).
    void RenderFrame(float elapsed, std::vector<uint8_t>& out);

    // Scale all element positions, sizes, font sizes, and keyframe values to
    // fit targetW x targetH.  Call after Parse() when a size override is set.
    void ScaleToFit(int targetW, int targetH);

private:
    AlertLayoutData m_data;
    RenderBitmap*   m_bitmap = nullptr;

    // Load image / GIF frames from disk into el.imageFrames
    static void LoadMedia(LayoutElement& el);
    static bool LoadStaticImage(const std::wstring& path, ImageFrame& frame);
    static bool LoadGifFrames(const std::wstring& path, LayoutElement& el);
    // Decode audio file into el.pcmSamples (float32 interleaved stereo 44100Hz)
    static void LoadAudioPCM(LayoutElement& el);

    void RenderElement(const LayoutElement& el, const ElemState& state, float elapsed);
    void RenderGifElement(const LayoutElement& el, const ElemState& state, float elapsed);
    void RenderVideoElement(const LayoutElement& el, const ElemState& state, float elapsed);
    void BlitFrame(const ImageFrame& frame, const ElemState& state);
    // Blit a raw premultiplied-BGRA buffer (used by video frames — avoids an ImageFrame copy).
    void BlitFrameRaw(const uint8_t* bgra, int srcW, int srcH, const ElemState& state);

    // Render a single text pass with explicit spans (used for transition dual-pass).
    // elapsed used to evaluate KF-based shadow/outline overrides.
    void RenderTextWithSpans(const LayoutElement& el, const ElemState& state,
                             const std::vector<TextSpan>& spans,
                             int ix, int iy, int iw, int ih, uint8_t alpha, float elapsed);

    // Dual-pass rendering for Fade, Slide*, and Morph transitions.
    void RenderTextTransition(const LayoutElement& el, const ElemState& state,
                              const TextTransitionInfo& trans,
                              int ix, int iy, int iw, int ih, uint8_t alpha, float elapsed);
};
