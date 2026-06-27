#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincodec.h>
#include <propvarutil.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <obs-module.h>
#include "layout_renderer.h"
#include "renderer.h"
#include <algorithm>
#include <cstring>
#include <cmath>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <deque>
#include <memory>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfuuid.lib")

static uint8_t argbA(uint32_t argb) { return (uint8_t)((argb >> 24) & 0xFF); }

// ── Helpers ───────────────────────────────────────────────────────────────────

static uint32_t ReadU32LE(const uint8_t* p) {
    uint32_t v; std::memcpy(&v, p, 4); return v;
}
static uint16_t ReadU16LE(const uint8_t* p) {
    uint16_t v; std::memcpy(&v, p, 2); return v;
}
static float ReadF32LE(const uint8_t* p) {
    float v; std::memcpy(&v, p, 4); return v;
}
static int16_t ReadI16LE(const uint8_t* p) {
    int16_t v; std::memcpy(&v, p, 2); return v;
}

static bool ReadStr(const uint8_t* data, size_t size, size_t& off, std::string& out)
{
    if (off + 2 > size) return false;
    uint16_t len = ReadU16LE(data + off); off += 2;
    if (off + len > size) return false;
    out.assign(reinterpret_cast<const char*>(data + off), len);
    off += len;
    return true;
}

// Separable box blur on premultiplied BGRA data (two-pass, O(w*h*radius))
static void BoxBlur(uint8_t* px, int w, int h, int radius)
{
    if (radius <= 0 || w <= 0 || h <= 0) return;
    std::vector<uint8_t> tmp(w * h * 4);

    // Horizontal pass
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            int b=0, g=0, r=0, a=0, cnt=0;
            for (int dx = -radius; dx <= radius; dx++) {
                int sx = x + dx;
                if (sx < 0 || sx >= w) continue;
                const uint8_t* p = &px[(y*w + sx)*4];
                b += p[0]; g += p[1]; r += p[2]; a += p[3];
                cnt++;
            }
            uint8_t* t = &tmp[(y*w + x)*4];
            t[0]=(uint8_t)(b/cnt); t[1]=(uint8_t)(g/cnt);
            t[2]=(uint8_t)(r/cnt); t[3]=(uint8_t)(a/cnt);
        }
    }
    // Vertical pass
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            int b=0, g=0, r=0, a=0, cnt=0;
            for (int dy = -radius; dy <= radius; dy++) {
                int sy = y + dy;
                if (sy < 0 || sy >= h) continue;
                const uint8_t* p = &tmp[(sy*w + x)*4];
                b += p[0]; g += p[1]; r += p[2]; a += p[3];
                cnt++;
            }
            uint8_t* t = &px[(y*w + x)*4];
            t[0]=(uint8_t)(b/cnt); t[1]=(uint8_t)(g/cnt);
            t[2]=(uint8_t)(r/cnt); t[3]=(uint8_t)(a/cnt);
        }
    }
}

// ARGB uint32 → COLORREF + alpha
static COLORREF ArgbToColorref(uint32_t argb, uint8_t& alpha)
{
    alpha = (uint8_t)((argb >> 24) & 0xFF);
    uint8_t r = (uint8_t)((argb >> 16) & 0xFF);
    uint8_t g = (uint8_t)((argb >>  8) & 0xFF);
    uint8_t b = (uint8_t)( argb        & 0xFF);
    return RGB(r, g, b);
}

// ── Streaming video decoder ─────────────────────────────────────────────────────
// Decodes mp4/mov on a worker thread into a small bounded queue of BGRA frames; the graphics thread
// pulls the current frame by clip-relative time. NO full pre-decode — only a handful of frames are
// resident. Created at parse (load), destroyed when the layout's elements clear (unload).
class VideoDecoder {
public:
    explicit VideoDecoder(const std::wstring& path)
    {
        m_valid = Init(path);
        if (m_valid) m_worker = std::thread(&VideoDecoder::WorkerProc, this);
    }
    ~VideoDecoder()
    {
        { std::lock_guard<std::mutex> lk(m_mtx); m_stop = true; }
        m_cv.notify_all();
        if (m_worker.joinable()) m_worker.join();
        if (m_reader) m_reader->Release();
    }

    bool   Valid()    const { return m_valid; }
    double Duration() const { return m_durationSec; }

    // Returns the current BGRA frame for clip time tSec under endBeh, or nullptr when nothing is
    // available yet or the clip has ended (EndHide). Called only on the graphics thread.
    const uint8_t* GetFrameAt(double tSec, VideoEndBehavior endBeh, int& outW, int& outH)
    {
        double dur = m_durationSec > 0.0 ? m_durationSec : 0.0;
        double pos;
        bool ended = false;
        if (endBeh == VideoEndBehavior::Loop && dur > 0.0) {
            pos = std::fmod(tSec, dur);
            if (pos < 0.0) pos += dur;
        } else if (endBeh == VideoEndBehavior::Hold) {
            pos = (dur > 0.0) ? std::min(tSec, dur) : tSec;
        } else if (endBeh == VideoEndBehavior::HoldFirst) {
            pos = (dur > 0.0 && tSec >= dur) ? 0.0 : tSec;
        } else { // EndHide / EndFade
            if (dur > 0.0 && tSec >= dur) { ended = true; pos = dur; }
            else pos = tSec;
        }

        // Backward jump (loop wrap) → ask the worker to seek back to that position.
        if (pos + 0.05 < m_lastPos) RequestSeek(pos);
        m_lastPos = pos;

        {
            std::unique_lock<std::mutex> lk(m_mtx);
            while (!m_queue.empty() && m_queue.front().t <= pos + 1e-4) {
                m_cur.swap(m_queue.front());
                m_haveCur = true;
                m_queue.pop_front();
            }
            m_cv.notify_all(); // freed queue space
        }

        if (ended && endBeh == VideoEndBehavior::EndHide) return nullptr;
        if (!m_haveCur) return nullptr;
        outW = m_cur.w; outH = m_cur.h;
        return m_cur.bgra.data();
    }

private:
    struct Frame {
        double t = 0.0; int w = 0, h = 0; std::vector<uint8_t> bgra;
        void swap(Frame& o) { std::swap(t,o.t); std::swap(w,o.w); std::swap(h,o.h); bgra.swap(o.bgra); }
    };

    bool Init(const std::wstring& path)
    {
        IMFAttributes* attrs = nullptr;
        MFCreateAttributes(&attrs, 1);
        if (attrs) attrs->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);
        HRESULT hr = MFCreateSourceReaderFromURL(path.c_str(), attrs, &m_reader);
        if (attrs) attrs->Release();
        if (FAILED(hr) || !m_reader) return false;

        m_reader->SetStreamSelection((DWORD)MF_SOURCE_READER_ALL_STREAMS, FALSE);
        m_reader->SetStreamSelection((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE);

        IMFMediaType* outType = nullptr;
        MFCreateMediaType(&outType);
        outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        outType->SetGUID(MF_MT_SUBTYPE,    MFVideoFormat_RGB32);
        hr = m_reader->SetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, outType);
        outType->Release();
        if (FAILED(hr)) return false;

        IMFMediaType* cur = nullptr;
        if (FAILED(m_reader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &cur)) || !cur)
            return false;
        UINT32 w = 0, h = 0;
        MFGetAttributeSize(cur, MF_MT_FRAME_SIZE, &w, &h);
        m_w = (int)w; m_h = (int)h;
        UINT32 strideU = 0;
        if (FAILED(cur->GetUINT32(MF_MT_DEFAULT_STRIDE, &strideU)) || (LONG)strideU == 0) {
            LONG s = 0;
            if (SUCCEEDED(MFGetStrideForBitmapInfoHeader(MFVideoFormat_RGB32.Data1, w, &s))) m_stride = s;
            else m_stride = (LONG)w * 4;
        } else {
            m_stride = (LONG)strideU;
        }
        cur->Release();
        if (m_w <= 0 || m_h <= 0) return false;

        PROPVARIANT pv; PropVariantInit(&pv);
        if (SUCCEEDED(m_reader->GetPresentationAttribute((DWORD)MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION, &pv)))
            m_durationSec = (double)pv.uhVal.QuadPart / 1e7; // 100ns units
        PropVariantClear(&pv);
        return true;
    }

    void RequestSeek(double posSec)
    {
        { std::lock_guard<std::mutex> lk(m_mtx);
          m_seekTo = posSec; m_seekRequested = true; m_queue.clear(); m_generation++; m_eof = false; }
        m_cv.notify_all();
    }

    void WorkerProc()
    {
        const size_t MAXQ = 12;
        while (true) {
            uint64_t gen;
            {
                std::unique_lock<std::mutex> lk(m_mtx);
                if (m_seekRequested) {
                    PROPVARIANT var; PropVariantInit(&var);
                    var.vt = VT_I8; var.hVal.QuadPart = (LONGLONG)(m_seekTo * 1e7);
                    m_reader->SetCurrentPosition(GUID_NULL, var);
                    PropVariantClear(&var);
                    m_seekRequested = false;
                    m_eof = false;
                }
                while (!m_stop && !m_seekRequested && (m_queue.size() >= MAXQ || m_eof))
                    m_cv.wait(lk);
                if (m_stop) break;
                if (m_seekRequested) continue;
                gen = m_generation;
            }

            DWORD flags = 0; LONGLONG ts = 0; IMFSample* sample = nullptr;
            HRESULT hr = m_reader->ReadSample((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                                              nullptr, &flags, &ts, &sample);
            if (FAILED(hr)) { if (sample) sample->Release(); std::lock_guard<std::mutex> lk(m_mtx); m_eof = true; continue; }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
                if (sample) sample->Release();
                std::lock_guard<std::mutex> lk(m_mtx); m_eof = true; m_cv.notify_all(); continue;
            }
            if (!sample) continue;

            Frame f; f.t = (double)ts / 1e7; f.w = m_w; f.h = m_h;
            IMFMediaBuffer* buf = nullptr;
            if (SUCCEEDED(sample->ConvertToContiguousBuffer(&buf))) {
                BYTE* data = nullptr; DWORD len = 0;
                if (SUCCEEDED(buf->Lock(&data, nullptr, &len))) {
                    f.bgra.resize((size_t)m_w * m_h * 4);
                    CopyToBgra(f.bgra.data(), data, len);
                    buf->Unlock();
                    std::lock_guard<std::mutex> lk(m_mtx);
                    if (gen == m_generation) { m_queue.push_back(std::move(f)); m_cv.notify_all(); }
                }
                buf->Release();
            }
            sample->Release();
        }
    }

    // RGB32 (possibly bottom-up via negative stride) → top-down opaque BGRA.
    void CopyToBgra(uint8_t* dst, const uint8_t* src, DWORD srcLen)
    {
        int  absStride = (int)std::abs(m_stride);
        bool bottomUp  = m_stride > 0;
        size_t need = (size_t)absStride * m_h;
        if ((size_t)srcLen < need) return; // malformed buffer — leave frame zeroed
        for (int y = 0; y < m_h; y++) {
            const uint8_t* srow = bottomUp ? (src + (size_t)(m_h - 1 - y) * absStride)
                                           : (src + (size_t)y * absStride);
            uint8_t* drow = dst + (size_t)y * m_w * 4;
            for (int x = 0; x < m_w; x++) {
                drow[x*4+0] = srow[x*4+0];
                drow[x*4+1] = srow[x*4+1];
                drow[x*4+2] = srow[x*4+2];
                drow[x*4+3] = 255;
            }
        }
    }

    IMFSourceReader* m_reader = nullptr;
    bool   m_valid = false;
    int    m_w = 0, m_h = 0;
    LONG   m_stride = 0;
    double m_durationSec = 0.0;

    std::thread             m_worker;
    std::mutex              m_mtx;
    std::condition_variable m_cv;
    std::deque<Frame>       m_queue;
    bool     m_stop = false;
    bool     m_eof  = false;
    bool     m_seekRequested = false;
    double   m_seekTo = 0.0;
    uint64_t m_generation = 0;

    // Held by the graphics thread only.
    Frame  m_cur;
    bool   m_haveCur = false;
    double m_lastPos = -1.0;
};

// ── Constructor / Destructor ──────────────────────────────────────────────────

LayoutRenderer::LayoutRenderer() {}

LayoutRenderer::~LayoutRenderer()
{
    delete m_bitmap;
}

// ── Parse ─────────────────────────────────────────────────────────────────────

bool LayoutRenderer::Parse(const std::vector<uint8_t>& payload)
{
    m_data = AlertLayoutData{};
    delete m_bitmap; m_bitmap = nullptr;

    const uint8_t* d = payload.data();
    size_t sz  = payload.size();
    size_t off = 0;

    if (sz < 14) return false;

    // Magic "ALT2"
    uint32_t magic = ReadU32LE(d + off); off += 4;
    if (magic != 0x33544C41u) {
        blog(LOG_WARNING, "[Steaming] LayoutRenderer: bad magic %08X (expected ALT3)", magic);
        return false;
    }

    m_data.canvasW = (int)ReadU32LE(d + off); off += 4;
    m_data.canvasH = (int)ReadU32LE(d + off); off += 4;

    if (m_data.canvasW < 1 || m_data.canvasW > 4096 ||
        m_data.canvasH < 1 || m_data.canvasH > 4096) return false;

    uint16_t elemCount = ReadU16LE(d + off); off += 2;

    for (int ei = 0; ei < (int)elemCount; ei++) {
        if (off + 26 > sz) return false;
        LayoutElement el{};
        el.type     = (ElemType)d[off++];
        el.x        = ReadF32LE(d + off); off += 4;
        el.y        = ReadF32LE(d + off); off += 4;
        el.w        = ReadF32LE(d + off); off += 4;
        el.h        = ReadF32LE(d + off); off += 4;
        el.rotation = ReadF32LE(d + off); off += 4;
        el.zOrder   = (int)ReadU32LE(d + off); off += 4;

        switch (el.type) {
        case ElemType::Rect:
        case ElemType::GoalBar:  // same wire format as Rect; width is scaled to goal progress at render time
            if (off + 6 > sz) return false;
            el.fillColor    = ReadU32LE(d + off); off += 4;
            el.cornerRadius = (int)ReadU16LE(d + off); off += 2;
            break;

        case ElemType::Text: {
            // ALT3 text: align(1) flags(1) shadowColor(4) shadowOffX(2) shadowOffY(2) shadowBlur(1)
            //            outlineColor(4) outlineWidth(1) spanCount(2) = 18 bytes fixed
            if (off + 18 > sz) return false;
            el.textAlign    = (int)d[off++];
            uint8_t tf      = d[off++];
            el.shadow       = (tf & 0x01) != 0;
            el.outline      = (tf & 0x02) != 0;
            el.vertAlign    = (tf >> 2) & 0x03; // bits 2-3: 0=top 1=middle 2=bottom
            el.shadowColor  = ReadU32LE(d + off); off += 4;
            el.shadowOffX   = (int)ReadI16LE(d + off); off += 2;
            el.shadowOffY   = (int)ReadI16LE(d + off); off += 2;
            el.shadowBlur   = (int)d[off++];
            el.outlineColor = ReadU32LE(d + off); off += 4;
            el.outlineWidth = (int)d[off++];
            uint16_t spanCount = ReadU16LE(d + off); off += 2;
            for (int si = 0; si < (int)spanCount; si++) {
                std::string txt, fam;
                if (!ReadStr(d, sz, off, txt)) return false;
                if (!ReadStr(d, sz, off, fam)) return false;
                if (off + 7 > sz) return false; // 2(size)+1(flags)+4(color)
                TextSpan ts;
                ts.text       = Utf8ToWide(txt);
                ts.fontFamily = Utf8ToWide(fam);
                ts.fontSize   = (int)ReadU16LE(d + off); off += 2;
                uint8_t sf    = d[off++];
                ts.bold       = (sf & 0x01) != 0;
                ts.italic     = (sf & 0x02) != 0;
                ts.color      = ReadU32LE(d + off); off += 4;
                el.spans.push_back(ts);
            }
            break;
        }

        case ElemType::Image:
        case ElemType::Gif: {
            std::string path;
            if (!ReadStr(d, sz, off, path)) return false;
            el.filePath = Utf8ToWide(path);
            LoadMedia(el);
            break;
        }

        case ElemType::Audio: {
            // filePath(str) startTime(f32) volL(f32) volR(f32) fadeIn(f32) fadeOut(f32)
            // envCount(u16) [time(f32) vol(f32)]...
            std::string path;
            if (!ReadStr(d, sz, off, path)) return false;
            if (off + 20 > sz) return false;
            el.filePath       = Utf8ToWide(path);
            el.audioStartTime = ReadF32LE(d + off); off += 4;
            el.audioVolumeL   = ReadF32LE(d + off); off += 4;
            el.audioVolumeR   = ReadF32LE(d + off); off += 4;
            el.audioFadeIn    = ReadF32LE(d + off); off += 4;
            el.audioFadeOut   = ReadF32LE(d + off); off += 4;
            if (off + 2 > sz) return false;
            uint16_t envCount = ReadU16LE(d + off); off += 2;
            for (uint16_t ei2 = 0; ei2 < envCount; ei2++) {
                if (off + 8 > sz) break;
                AudioVolumeKf ekf;
                ekf.time   = ReadF32LE(d + off); off += 4;
                ekf.volume = ReadF32LE(d + off); off += 4;
                el.audioEnvelope.push_back(ekf);
            }
            // Decode PCM at parse time — done once, reused every tick with no re-decode
            LoadAudioPCM(el);
            break;
        }

        case ElemType::Video: {
            // filePath(str) endBehavior(u8) muted(u8) volume(f32)
            std::string path;
            if (!ReadStr(d, sz, off, path)) return false;
            if (off + 6 > sz) return false;
            el.filePath    = Utf8ToWide(path);
            el.videoEnd    = (VideoEndBehavior)d[off++];
            el.videoMuted  = d[off++] != 0;
            el.videoVolume = ReadF32LE(d + off); off += 4;
            // Embedded audio: decode the file's own audio track (reuses the audio-clip PCM path) and
            // play it once through the existing alert mixer. A/V stays in sync on the elapsed clock.
            if (!el.videoMuted) {
                el.audioVolumeL = el.videoVolume;
                el.audioVolumeR = el.videoVolume;
                LoadAudioPCM(el);
            }
            // Streaming video decoder (load). Released when m_data.elements is cleared (unload).
            el.videoDecoder = std::make_unique<VideoDecoder>(el.filePath);
            break;
        }
        }

        // Keyframes
        if (off + 2 > sz) return false;
        uint16_t kfCount = ReadU16LE(d + off); off += 2;
        for (int ki = 0; ki < (int)kfCount; ki++) {
            if (off + 6 > sz) return false;
            Keyframe kf{};
            kf.time    = ReadF32LE(d + off); off += 4;
            kf.mask    = d[off++];
            kf.easing  = (EasingKind)d[off++];
            kf.extMask = d[off++];  // extension byte: bit0=fillColor
            // read values for each set bit in mask (bit order 0-7)
            for (int bit = 0; bit < 8; bit++) {
                if (!(kf.mask & (1 << bit))) continue;
                if (off + 4 > sz) return false;
                float v = ReadF32LE(d + off); off += 4;
                switch (bit) {
                    case 0: kf.x        = v; break;
                    case 1: kf.y        = v; break;
                    case 2: kf.w        = v; break;
                    case 3: kf.h        = v; break;
                    case 4: kf.opacity  = v; break;
                    case 5: kf.sx       = v; break;
                    case 6: kf.sy       = v; break;
                    case 7: kf.rotation = v; break;
                }
            }
            // extended fields
            if (kf.extMask & 0x01) {  // fillColor ARGB u32
                if (off + 4 > sz) return false;
                kf.kfFillColor = ReadU32LE(d + off); off += 4;
            }
            if (kf.extMask & 0x02) {  // kfSpans: u16 count + span records
                if (off + 2 > sz) return false;
                uint16_t sc = ReadU16LE(d + off); off += 2;
                for (int si = 0; si < (int)sc; si++) {
                    std::string txt, fam;
                    if (!ReadStr(d, sz, off, txt)) return false;
                    if (!ReadStr(d, sz, off, fam)) return false;
                    if (off + 7 > sz) return false; // 2(size)+1(flags)+4(color)
                    TextSpan ts;
                    ts.text       = Utf8ToWide(txt);
                    ts.fontFamily = Utf8ToWide(fam);
                    ts.fontSize   = (int)ReadU16LE(d + off); off += 2;
                    uint8_t sf    = d[off++];
                    ts.bold       = (sf & 0x01) != 0;
                    ts.italic     = (sf & 0x02) != 0;
                    ts.color      = ReadU32LE(d + off); off += 4;
                    kf.kfSpans.push_back(ts);
                }
            }
            if (kf.extMask & 0x04) {  // kfShadow: u8 on, u32 ARGB
                if (off + 5 > sz) return false;
                kf.kfShadowHas  = true;
                kf.kfShadowOn   = d[off++] != 0;
                kf.kfShadowColor = ReadU32LE(d + off); off += 4;
            }
            if (kf.extMask & 0x08) {  // kfOutline: u8 on, u32 ARGB, u8 width
                if (off + 6 > sz) return false;
                kf.kfOutlineHas   = true;
                kf.kfOutlineOn    = d[off++] != 0;
                kf.kfOutlineColor = ReadU32LE(d + off); off += 4;
                kf.kfOutlineWidth = d[off++];
            }
            if (kf.extMask & 0x10) {  // kfCornerRadius: u8
                if (off + 1 > sz) return false;
                kf.kfCornerHas    = true;
                kf.kfCornerRadius = (int)d[off++];
            }
            if (kf.extMask & 0x20) {  // kfShadowGeom: i16 shadowX, i16 shadowY, u8 blur
                if (off + 5 > sz) return false;
                kf.kfShadowGeomHas = true;
                kf.kfShadowX  = (int)(int16_t)ReadU16LE(d + off); off += 2;
                kf.kfShadowY  = (int)(int16_t)ReadU16LE(d + off); off += 2;
                kf.kfShadowBlur = (int)d[off++];
            }
            if (kf.extMask & 0x40) {  // kfTextAlign: u8 (bits0-1=hAlign, bits2-3=vAlign)
                if (off + 1 > sz) return false;
                kf.kfAlignHas  = true;
                uint8_t ab     = d[off++];
                kf.kfTextAlign = (int)(ab & 0x03);
                kf.kfVertAlign = (int)((ab >> 2) & 0x03);
            }
            if (kf.extMask & 0x80) {  // spanTransition: u8 TextTransition enum
                if (off + 1 > sz) return false;
                kf.kfSpanTransition = (TextTransition)d[off++];
            }
            el.keyframes.push_back(kf);
        }
        // Ensure keyframes are sorted by time so EvalElemState interpolation is correct
        std::sort(el.keyframes.begin(), el.keyframes.end(),
                  [](const Keyframe& a, const Keyframe& b){ return a.time < b.time; });

        // GIF: start animating from frame 0 when the element first appears (first keyframe time)
        if (el.type == ElemType::Gif && !el.keyframes.empty())
            el.gifAnimStartTime = el.keyframes[0].time;
        if (el.type == ElemType::Video && !el.keyframes.empty())
            el.videoStartTime = el.keyframes[0].time;

        m_data.elements.push_back(std::move(el));
    }

    // Sort by ZOrder
    std::sort(m_data.elements.begin(), m_data.elements.end(),
              [](const LayoutElement& a, const LayoutElement& b){ return a.zOrder < b.zOrder; });

    // Variable values
    std::string un, msg;
    if (!ReadStr(d, sz, off, un)) return false;
    if (!ReadStr(d, sz, off, msg)) return false;
    if (off + 8 > sz) return false;
    m_data.username = Utf8ToWide(un);
    m_data.message  = Utf8ToWide(msg);
    m_data.amount   = (int)ReadU32LE(d + off); off += 4;
    std::string platUtf8;
    if (off < sz && ReadStr(d, sz, off, platUtf8))
        m_data.platform = Utf8ToWide(platUtf8);
    if (off + 4 > sz) return false;
    m_data.duration = ReadF32LE(d + off); off += 4;

    // Sound file path, base volume, and volume envelope (optional — older payloads may omit)
    std::string soundUtf8;
    if (off < sz && ReadStr(d, sz, off, soundUtf8))
        m_data.soundFile = Utf8ToWide(soundUtf8);
    if (off + 4 <= sz) {
        m_data.volume = ReadF32LE(d + off); off += 4;
    }
    if (off + 2 <= sz) {
        uint16_t envCount = ReadU16LE(d + off); off += 2;
        for (uint16_t ei = 0; ei < envCount && off + 8 <= sz; ei++) {
            AudioVolumeKf kf;
            kf.time   = ReadF32LE(d + off); off += 4;
            kf.volume = ReadF32LE(d + off); off += 4;
            m_data.volumeEnvelope.push_back(kf);
        }
    }

    // Apply variable substitutions to each span in text elements.
    // {user}    = username footer field
    // {message} = message footer field (also aliased as {value} and {current})
    // {amount}  = amount footer field  (also aliased as {target})
    // {value}   = alias for {message} — use in label layouts
    // {current} = alias for {message} — use in goal layouts (current progress)
    // {target}  = alias for {amount}  — use in goal layouts (goal target)
    // {percent} = current/target as "XX%" — computed from message (int) / amount
    {
        std::wstring percentStr = L"0%";
        if (m_data.amount > 0) {
            try {
                int cur = std::stoi(m_data.message);
                int pct = (int)((float)cur / (float)m_data.amount * 100.f + 0.5f);
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                percentStr = std::to_wstring(pct) + L"%";
            } catch (...) {}
        }
        auto replaceSpanTokens = [&](std::vector<TextSpan>& spans) {
            for (auto& span : spans) {
                auto& c = span.text;
                auto rep = [&](const std::wstring& from, const std::wstring& to) {
                    size_t pos = 0;
                    while ((pos = c.find(from, pos)) != std::wstring::npos) {
                        c.replace(pos, from.size(), to); pos += to.size();
                    }
                };
                rep(L"{user}",     m_data.username);
                rep(L"{message}",  m_data.message);
                rep(L"{amount}",   std::to_wstring(m_data.amount));
                rep(L"{value}",    m_data.message);
                rep(L"{current}",  m_data.message);
                rep(L"{target}",   std::to_wstring(m_data.amount));
                rep(L"{percent}",  percentStr);
                rep(L"{platform}", m_data.platform);
            }
        };
        for (auto& el : m_data.elements) {
            if (el.type != ElemType::Text) continue;
            replaceSpanTokens(el.spans);
            for (auto& kf : el.keyframes)
                replaceSpanTokens(kf.kfSpans);
        }
    }

    m_bitmap = new RenderBitmap(m_data.canvasW, m_data.canvasH);
    m_data.valid = true;
    blog(LOG_INFO, "[Steaming] LayoutRenderer parsed: %dx%d, %zu elements, dur=%.1fs",
         m_data.canvasW, m_data.canvasH, m_data.elements.size(), m_data.duration);
    return true;
}

// ── Media loading ─────────────────────────────────────────────────────────────

void LayoutRenderer::LoadMedia(LayoutElement& el)
{
    if (el.filePath.empty()) return;
    if (el.type == ElemType::Gif) {
        if (!LoadGifFrames(el.filePath, el))
            LoadStaticImage(el.filePath, el.imageFrames.emplace_back());
    } else {
        LoadStaticImage(el.filePath, el.imageFrames.emplace_back());
    }
}

bool LayoutRenderer::LoadStaticImage(const std::wstring& path, ImageFrame& frame)
{
    IWICImagingFactory* factory = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IWICImagingFactory, (void**)&factory)))
        return false;

    IWICBitmapDecoder* decoder = nullptr;
    bool ok = false;
    if (SUCCEEDED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                                     WICDecodeMetadataCacheOnLoad, &decoder))) {
        IWICBitmapFrameDecode* src = nullptr;
        if (SUCCEEDED(decoder->GetFrame(0, &src))) {
            IWICFormatConverter* conv = nullptr;
            if (SUCCEEDED(factory->CreateFormatConverter(&conv)) &&
                SUCCEEDED(conv->Initialize(src, GUID_WICPixelFormat32bppPBGRA,
                                           WICBitmapDitherTypeNone, nullptr, 0.f,
                                           WICBitmapPaletteTypeCustom))) {
                // PBGRA = premultiplied BGRA — matches what BlitImagePublic expects
                UINT w = 0, h = 0;
                conv->GetSize(&w, &h);
                frame.width  = (int)w;
                frame.height = (int)h;
                frame.bgra.resize(w * h * 4);
                ok = SUCCEEDED(conv->CopyPixels(nullptr, w * 4, (UINT)frame.bgra.size(), frame.bgra.data()));
            }
            if (conv) conv->Release();
            src->Release();
        }
        decoder->Release();
    }
    factory->Release();
    return ok;
}

bool LayoutRenderer::LoadGifFrames(const std::wstring& path, LayoutElement& el)
{
    IWICImagingFactory* factory = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IWICImagingFactory, (void**)&factory)))
        return false;

    IWICBitmapDecoder* decoder = nullptr;
    bool ok = false;

    if (SUCCEEDED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                                     WICDecodeMetadataCacheOnLoad, &decoder))) {
        UINT frameCount = 0;
        decoder->GetFrameCount(&frameCount);

        // Get logical screen size from global metadata
        IWICMetadataQueryReader* gmeta = nullptr;
        UINT lsW = 0, lsH = 0;
        if (SUCCEEDED(decoder->GetMetadataQueryReader(&gmeta))) {
            PROPVARIANT pv; PropVariantInit(&pv);
            if (SUCCEEDED(gmeta->GetMetadataByName(L"/logscrdesc/Width", &pv)))
                lsW = pv.uiVal;
            PropVariantClear(&pv);
            if (SUCCEEDED(gmeta->GetMetadataByName(L"/logscrdesc/Height", &pv)))
                lsH = pv.uiVal;
            PropVariantClear(&pv);
            gmeta->Release();
        }

        // Composite buffer for disposal handling
        std::vector<uint8_t> composite;
        if (lsW > 0 && lsH > 0) composite.resize(lsW * lsH * 4, 0);

        for (UINT fi = 0; fi < frameCount; fi++) {
            IWICBitmapFrameDecode* frameSrc = nullptr;
            if (FAILED(decoder->GetFrame(fi, &frameSrc))) continue;

            IWICMetadataQueryReader* fmeta = nullptr;
            float delay = 0.1f;
            int offX = 0, offY = 0;
            int disposal = 0;

            if (SUCCEEDED(frameSrc->GetMetadataQueryReader(&fmeta))) {
                PROPVARIANT pv; PropVariantInit(&pv);
                if (SUCCEEDED(fmeta->GetMetadataByName(L"/grctlext/Delay", &pv)))
                    delay = pv.uiVal / 100.0f;
                PropVariantClear(&pv);
                if (SUCCEEDED(fmeta->GetMetadataByName(L"/imgdesc/Left", &pv)))
                    offX = pv.uiVal;
                PropVariantClear(&pv);
                if (SUCCEEDED(fmeta->GetMetadataByName(L"/imgdesc/Top", &pv)))
                    offY = pv.uiVal;
                PropVariantClear(&pv);
                if (SUCCEEDED(fmeta->GetMetadataByName(L"/grctlext/Disposal", &pv)))
                    disposal = pv.uiVal;
                PropVariantClear(&pv);
                fmeta->Release();
            }

            IWICFormatConverter* conv = nullptr;
            if (SUCCEEDED(factory->CreateFormatConverter(&conv)) &&
                SUCCEEDED(conv->Initialize(frameSrc, GUID_WICPixelFormat32bppBGRA,
                                           WICBitmapDitherTypeNone, nullptr, 0.f,
                                           WICBitmapPaletteTypeCustom))) {
                UINT fw = 0, fh = 0;
                conv->GetSize(&fw, &fh);

                if (lsW == 0) { lsW = fw; lsH = fh; composite.resize(lsW * lsH * 4, 0); }

                // Decode frame pixels
                std::vector<uint8_t> fpx(fw * fh * 4);
                conv->CopyPixels(nullptr, fw * 4, (UINT)fpx.size(), fpx.data());

                // Composite onto the persistent canvas
                for (UINT fy = 0; fy < fh; fy++) {
                    for (UINT fx = 0; fx < fw; fx++) {
                        int cx = offX + (int)fx;
                        int cy = offY + (int)fy;
                        if (cx < 0 || cx >= (int)lsW || cy < 0 || cy >= (int)lsH) continue;
                        const uint8_t* src = &fpx[(fy * fw + fx) * 4];
                        uint8_t* dst = &composite[(cy * lsW + cx) * 4];
                        // src-over composite (straight alpha from WIC)
                        uint8_t sa = src[3];
                        if (sa == 255) {
                            std::memcpy(dst, src, 4);
                        } else if (sa > 0) {
                            uint8_t da = dst[3];
                            uint32_t oa = sa + (uint32_t)da * (255 - sa) / 255;
                            if (oa > 0) {
                                for (int ch = 0; ch < 3; ch++)
                                    dst[ch] = (uint8_t)(((uint32_t)src[ch]*sa + (uint32_t)dst[ch]*da*(255-sa)/255) / oa);
                                dst[3] = (uint8_t)oa;
                            }
                        }
                    }
                }

                // Snapshot the composite as this frame's pixels (premultiply for OBS)
                ImageFrame frame;
                frame.width  = (int)lsW;
                frame.height = (int)lsH;
                frame.delay  = delay > 0.01f ? delay : 0.1f;
                frame.bgra.resize(lsW * lsH * 4);
                for (size_t i = 0; i < lsW * lsH; i++) {
                    uint8_t b = composite[i*4+0];
                    uint8_t g = composite[i*4+1];
                    uint8_t r = composite[i*4+2];
                    uint8_t a = composite[i*4+3];
                    frame.bgra[i*4+0] = (uint8_t)((uint32_t)b * a / 255);
                    frame.bgra[i*4+1] = (uint8_t)((uint32_t)g * a / 255);
                    frame.bgra[i*4+2] = (uint8_t)((uint32_t)r * a / 255);
                    frame.bgra[i*4+3] = a;
                }
                el.imageFrames.push_back(std::move(frame));
                el.gifTotalDuration += frame.delay;

                // Handle disposal
                if (disposal == 2) {  // restore to background
                    for (UINT fy = 0; fy < fh; fy++)
                        for (UINT fx = 0; fx < fw; fx++) {
                            int cx = offX+(int)fx, cy = offY+(int)fy;
                            if (cx >= 0 && cx < (int)lsW && cy >= 0 && cy < (int)lsH)
                                std::memset(&composite[(cy*lsW+cx)*4], 0, 4);
                        }
                }
            }
            if (conv) conv->Release();
            frameSrc->Release();
        }
        ok = !el.imageFrames.empty();
        decoder->Release();
    }
    factory->Release();
    return ok;
}

// ── Audio PCM loading ─────────────────────────────────────────────────────────
// Decodes audio file to float32 interleaved stereo 44100Hz using Media Foundation.
// Stored in el.pcmSamples once at parse time; zero alloc during playback ticks.
void LayoutRenderer::LoadAudioPCM(LayoutElement& el)
{
    el.pcmSamples.clear();
    el.pcmDurationSec = 0.f;
    if (el.filePath.empty()) return;

    static const uint32_t RATE = 44100;
    static const uint32_t CH   = 2;

    IMFSourceReader* reader = nullptr;
    if (FAILED(MFCreateSourceReaderFromURL(el.filePath.c_str(), nullptr, &reader))) return;

    IMFMediaType* t = nullptr;
    MFCreateMediaType(&t);
    t->SetGUID  (MF_MT_MAJOR_TYPE,              MFMediaType_Audio);
    t->SetGUID  (MF_MT_SUBTYPE,                 MFAudioFormat_Float);
    t->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE,  32);
    t->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS,      CH);
    t->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, RATE);
    HRESULT hrFmt = reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, nullptr, t);
    t->Release();
    if (FAILED(hrFmt)) {
        reader->Release();
        blog(LOG_WARNING, "[Steaming] Audio clip: MF could not set float32 stereo 44100 format for %S (hr=0x%08X)",
             el.filePath.c_str(), (unsigned)hrFmt);
        return;
    }

    while (true) {
        DWORD flags = 0;
        IMFSample* sample = nullptr;
        HRESULT hr = reader->ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                                        0, nullptr, &flags, nullptr, &sample);
        if (FAILED(hr) || (flags & MF_SOURCE_READERF_ENDOFSTREAM)) {
            if (sample) sample->Release(); // MF can return a sample alongside ENDOFSTREAM
            break;
        }
        if (!sample) continue;
        IMFMediaBuffer* buf = nullptr;
        if (SUCCEEDED(sample->ConvertToContiguousBuffer(&buf))) {
            BYTE* data = nullptr; DWORD len = 0;
            if (SUCCEEDED(buf->Lock(&data, nullptr, &len))) {
                size_t n = len / sizeof(float);
                float* f = reinterpret_cast<float*>(data);
                el.pcmSamples.insert(el.pcmSamples.end(), f, f + n);
                buf->Unlock();
            }
            buf->Release();
        }
        sample->Release();
    }
    reader->Release();

    if (!el.pcmSamples.empty())
        el.pcmDurationSec = (float)(el.pcmSamples.size() / CH) / (float)RATE;

    blog(LOG_INFO, "[Steaming] Audio clip decoded: %.2fs  %zu frames  path=%S",
         el.pcmDurationSec, el.pcmSamples.size() / CH, el.filePath.c_str());
}

// ── ScaleToFit ────────────────────────────────────────────────────────────────

void LayoutRenderer::ScaleToFit(int targetW, int targetH)
{
    if (!m_data.valid || targetW <= 0 || targetH <= 0) return;
    if (m_data.canvasW == targetW && m_data.canvasH == targetH) return;

    float sx = (float)targetW / m_data.canvasW;
    float sy = (float)targetH / m_data.canvasH;
    float avgS = (sx + sy) * 0.5f;

    for (auto& el : m_data.elements) {
        el.x *= sx; el.y *= sy;
        el.w *= sx; el.h *= sy;
        if (el.type == ElemType::Text) {
            el.shadowOffX = (int)std::round(el.shadowOffX * sx);
            el.shadowOffY = (int)std::round(el.shadowOffY * sy);
            for (auto& sp : el.spans)
                sp.fontSize = std::max(1, (int)std::round(sp.fontSize * avgS));
        }
        for (auto& kf : el.keyframes) {
            if (kf.mask & 0x01) kf.x *= sx;
            if (kf.mask & 0x02) kf.y *= sy;
            if (kf.mask & 0x04) kf.w *= sx;
            if (kf.mask & 0x08) kf.h *= sy;
            // Scale font sizes in span KFs too
            for (auto& sp : kf.kfSpans)
                sp.fontSize = std::max(1, (int)std::round(sp.fontSize * avgS));
        }
    }

    delete m_bitmap;
    m_bitmap = new RenderBitmap(targetW, targetH);
    m_data.canvasW = targetW;
    m_data.canvasH = targetH;
}

// ── Render ────────────────────────────────────────────────────────────────────

void LayoutRenderer::RenderFrame(float elapsed, std::vector<uint8_t>& out)
{
    if (!m_bitmap || !m_data.valid) { out.clear(); return; }

    m_bitmap->ClearPublic();

    for (const auto& el : m_data.elements) {
        if (el.type == ElemType::Audio) continue;  // audio-only, no visual
        // Elements with keyframes only exist between their first and last keyframe
        if (!el.keyframes.empty()) {
            if (elapsed < el.keyframes.front().time || elapsed > el.keyframes.back().time) continue;
        }
        ElemState state = EvalElemState(el, elapsed);
        if (state.opacity < 0.004f) continue;

        switch (el.type) {
        case ElemType::Rect:
            RenderElement(el, state, elapsed);
            break;
        case ElemType::GoalBar: {
            // Scale width by goal progress: message=current (int), amount=target (int)
            ElemState gs = state;
            if (m_data.amount > 0) {
                float progress = 0.f;
                try { progress = (float)std::stoi(m_data.message) / (float)m_data.amount; }
                catch (...) {}
                if (progress < 0.f) progress = 0.f;
                if (progress > 1.f) progress = 1.f;
                gs.w = state.w * progress;
            } else {
                gs.w = 0.f;
            }
            RenderElement(el, gs, elapsed);
            break;
        }
        case ElemType::Text:
            RenderElement(el, state, elapsed);
            break;
        case ElemType::Image:
            if (!el.imageFrames.empty())
                BlitFrame(el.imageFrames[0], state);
            break;
        case ElemType::Gif:
            RenderGifElement(el, state, elapsed);
            break;
        case ElemType::Video:
            RenderVideoElement(el, state, elapsed);
            break;
        default: break;
        }
    }

    m_bitmap->GetPixels(1.0f, out);
}

void LayoutRenderer::RenderElement(const LayoutElement& el, const ElemState& state, float elapsed)
{
    // abs() so a negative width/height (used for image flip) does not collapse the element to
    // nothing. Rect/GoalBar are symmetric so they render correctly; image mirroring is handled
    // in BlitFrame. Positive values are unaffected.
    int iw = (int)std::round(std::fabs(state.w * state.scaleX));
    int ih = (int)std::round(std::fabs(state.h * state.scaleY));
    if (iw <= 0 || ih <= 0) return;

    uint8_t alpha = (uint8_t)(std::min(state.opacity, 1.0f) * 255.f + 0.5f);

    // If rotated, render into a temp bitmap then rotate-blit onto the main bitmap.
    bool hasRotation = fabsf(state.rotation) > 0.01f;
    if (hasRotation) {
        RenderBitmap tmp(iw, ih);
        ElemState flat = state;
        flat.x = 0.f; flat.y = 0.f; flat.rotation = 0.f; flat.scaleX = 1.f; flat.scaleY = 1.f; flat.opacity = 1.f;
        // Temporarily redirect m_bitmap to the temp, render at origin, restore.
        RenderBitmap* saved = m_bitmap;
        m_bitmap = &tmp;
        RenderElement(el, flat, 0.f);
        m_bitmap = saved;

        std::vector<uint8_t> tmpPixels;
        tmp.GetPixels(1.0f, tmpPixels);
        float cx = state.x + (float)iw / 2.f;
        float cy = state.y + (float)ih / 2.f;
        m_bitmap->RotateBlitPublic(tmpPixels.data(), iw, ih, cx, cy, state.rotation, 1.f, 1.f, alpha);
        return;
    }

    int ix = (int)std::round(state.x);
    int iy = (int)std::round(state.y);

    if (el.type == ElemType::Rect || el.type == ElemType::GoalBar) {
        uint32_t col = (state.fillColor != 0) ? state.fillColor : el.fillColor;
        uint8_t fa = (uint8_t)((argbA(col) * (uint32_t)alpha) / 255);
        uint8_t r  = (uint8_t)((col >> 16) & 0xFF);
        uint8_t g  = (uint8_t)((col >>  8) & 0xFF);
        uint8_t b  = (uint8_t)( col        & 0xFF);
        int cornerR = (state.cornerRadius >= 0) ? state.cornerRadius : el.cornerRadius;
        m_bitmap->FillRoundRectPublic(ix, iy, iw, ih, cornerR, b, g, r, fa);
        return;
    }

    // Evaluate keyframed text properties at the current render time
    std::vector<TextSpan> spanBuf;
    const std::vector<TextSpan>* spansPtr = EvalSpansAt(el, elapsed, spanBuf);
    if (!spansPtr || spansPtr->empty()) return;
    if (el.type != ElemType::Text) return;

    // Check for dual-pass transitions (Fade, SlideLeft, SlideRight, Morph)
    TextTransitionInfo trans = EvalTextTransitionAt(el, elapsed);
    if (trans.inTransition) {
        RenderTextTransition(el, state, trans, ix, iy, iw, ih, alpha, elapsed);
        return;
    }

    RenderTextWithSpans(el, state, *spansPtr, ix, iy, iw, ih, alpha, elapsed);
}

void LayoutRenderer::RenderTextWithSpans(const LayoutElement& el, const ElemState& state,
                                         const std::vector<TextSpan>& spans,
                                         int ix, int iy, int iw, int ih, uint8_t alpha, float elapsed)
{
    if (spans.empty()) return;

    KfShadowState  kfSh  = EvalShadowKfAt(el, elapsed);
    KfOutlineState kfOut = EvalOutlineKfAt(el, elapsed);
    bool     effectShadow    = kfSh.has  ? kfSh.on   : el.shadow;
    bool     effectOutline   = kfOut.has ? kfOut.on  : el.outline;
    uint32_t effectShadowCol = kfSh.has  ? kfSh.color : el.shadowColor;
    uint32_t effectOutlineCol= kfOut.has ? kfOut.color : el.outlineColor;
    int      effectOutlineW  = kfOut.has ? (int)kfOut.width : el.outlineWidth;

    int effShadowX   = (state.shadowOffX != INT_MIN) ? state.shadowOffX : el.shadowOffX;
    int effShadowY   = (state.shadowOffY != INT_MIN) ? state.shadowOffY : el.shadowOffY;
    int effShadowBl  = (state.shadowBlur  != INT_MIN) ? state.shadowBlur  : el.shadowBlur;
    int effTextAlign = (state.textAlign >= 0) ? state.textAlign : el.textAlign;
    int effVertAlign = (state.vertAlign >= 0) ? state.vertAlign : el.vertAlign;

    uint8_t dummy;
    bool singleSpan = (spans.size() == 1);

    if (singleSpan) {
        const auto& sp = spans[0];
        COLORREF cr = ArgbToColorref(sp.color, dummy);
        uint8_t textAlpha = (uint8_t)((dummy * (uint32_t)alpha) / 255);

        bool isMultiline = sp.text.find(L'\n') != std::wstring::npos || sp.text.length() > 40;
        DWORD vFlag = isMultiline ? DT_TOP
                    : (effVertAlign == 0 ? DT_TOP
                    :  effVertAlign == 2 ? DT_BOTTOM
                    :                      DT_VCENTER);
        DWORD dtFlags = vFlag | DT_END_ELLIPSIS |
                        (effTextAlign == 0 ? DT_LEFT : effTextAlign == 2 ? DT_RIGHT : DT_CENTER) |
                        (isMultiline ? DT_WORDBREAK : DT_SINGLELINE);

        int padLeft = 0, padTop = 0, padRight = 0, padBottom = 0;
        if (effectOutline && effectOutlineW > 0) {
            padLeft = padTop = padRight = padBottom = effectOutlineW;
        }
        if (effectShadow) {
            int blurPad = std::max(0, effShadowBl);
            padLeft   = std::max(padLeft,   std::max(0, -effShadowX) + blurPad);
            padTop    = std::max(padTop,    std::max(0, -effShadowY) + blurPad);
            padRight  = std::max(padRight,  std::max(0,  effShadowX) + blurPad);
            padBottom = std::max(padBottom, std::max(0,  effShadowY) + blurPad);
        }

        if (padLeft > 0 || padTop > 0 || padRight > 0 || padBottom > 0) {
            int tmpW = iw + padLeft + padRight;
            int tmpH = ih + padTop + padBottom;
            RenderBitmap tmp(tmpW, tmpH);
            RenderBitmap* saved = m_bitmap;
            m_bitmap = &tmp;

            if (effectOutline && effectOutlineW > 0) {
                COLORREF ocr = ArgbToColorref(effectOutlineCol, dummy);
                uint8_t outAlpha = (uint8_t)((dummy * (uint32_t)alpha) / 255);
                int ow = effectOutlineW;
                for (int dy2 = -ow; dy2 <= ow; dy2++)
                    for (int dx2 = -ow; dx2 <= ow; dx2++) {
                        if (dx2 == 0 && dy2 == 0) continue;
                        m_bitmap->DrawTextGDIPublic(sp.text, padLeft+dx2, padTop+dy2, iw, ih,
                            ocr, sp.fontFamily, sp.fontSize, sp.bold, sp.italic, outAlpha, dtFlags);
                    }
            }

            if (effectShadow) {
                COLORREF scr = ArgbToColorref(effectShadowCol, dummy);
                uint8_t shAlpha = (uint8_t)((dummy * (uint32_t)alpha) / 255);
                if (effShadowBl > 0) {
                    int bR = effShadowBl;
                    int shW = iw + bR * 2, shH = ih + bR * 2;
                    RenderBitmap shBmp(shW, shH);
                    RenderBitmap* shSaved = m_bitmap;
                    m_bitmap = &shBmp;
                    m_bitmap->DrawTextGDIPublic(sp.text, bR, bR, iw, ih,
                        scr, sp.fontFamily, sp.fontSize, sp.bold, sp.italic, 255, dtFlags);
                    m_bitmap = shSaved;
                    std::vector<uint8_t> shPx;
                    shBmp.GetPixels(1.0f, shPx);
                    BoxBlur(shPx.data(), shW, shH, bR);
                    m_bitmap->BlitImagePublic(shPx.data(), shW, shH,
                        padLeft+effShadowX-bR, padTop+effShadowY-bR, shW, shH, shAlpha);
                } else {
                    m_bitmap->DrawTextGDIPublic(sp.text, padLeft+effShadowX, padTop+effShadowY, iw, ih,
                        scr, sp.fontFamily, sp.fontSize, sp.bold, sp.italic, shAlpha, dtFlags);
                }
            }

            m_bitmap->DrawTextGDIPublic(sp.text, padLeft, padTop, iw, ih, cr,
                sp.fontFamily, sp.fontSize, sp.bold, sp.italic, textAlpha, dtFlags);

            m_bitmap = saved;
            std::vector<uint8_t> tmpPixels;
            tmp.GetPixels(1.0f, tmpPixels);
            m_bitmap->BlitImagePublic(tmpPixels.data(), tmpW, tmpH, ix-padLeft, iy-padTop, tmpW, tmpH, 255);
            return;
        }

        if (effectOutline && effectOutlineW > 0) {
            COLORREF ocr = ArgbToColorref(effectOutlineCol, dummy);
            uint8_t outAlpha = (uint8_t)((dummy * (uint32_t)alpha) / 255);
            int ow = effectOutlineW;
            for (int dy2 = -ow; dy2 <= ow; dy2++)
                for (int dx2 = -ow; dx2 <= ow; dx2++) {
                    if (dx2 == 0 && dy2 == 0) continue;
                    m_bitmap->DrawTextGDIPublic(sp.text, ix+dx2, iy+dy2, iw, ih,
                        ocr, sp.fontFamily, sp.fontSize, sp.bold, sp.italic, outAlpha, dtFlags);
                }
        }

        if (effectShadow) {
            COLORREF scr = ArgbToColorref(effectShadowCol, dummy);
            uint8_t shAlpha = (uint8_t)((dummy * (uint32_t)alpha) / 255);
            if (effShadowBl > 0) {
                int bR = effShadowBl;
                int tmpW = iw + bR * 2, tmpH = ih + bR * 2;
                RenderBitmap shBmp(tmpW, tmpH);
                RenderBitmap* saved = m_bitmap;
                m_bitmap = &shBmp;
                m_bitmap->DrawTextGDIPublic(sp.text, bR, bR, iw, ih,
                    scr, sp.fontFamily, sp.fontSize, sp.bold, sp.italic, 255, dtFlags);
                m_bitmap = saved;
                std::vector<uint8_t> shPx;
                shBmp.GetPixels(1.0f, shPx);
                BoxBlur(shPx.data(), tmpW, tmpH, bR);
                m_bitmap->BlitImagePublic(shPx.data(), tmpW, tmpH,
                    ix+effShadowX-bR, iy+effShadowY-bR, tmpW, tmpH, shAlpha);
            } else {
                m_bitmap->DrawTextGDIPublic(sp.text, ix+effShadowX, iy+effShadowY, iw, ih,
                    scr, sp.fontFamily, sp.fontSize, sp.bold, sp.italic, shAlpha, dtFlags);
            }
        }

        m_bitmap->DrawTextGDIPublic(sp.text, ix, iy, iw, ih, cr,
            sp.fontFamily, sp.fontSize, sp.bold, sp.italic, textAlpha, dtFlags);

    } else {
        // Multi-span
        int totalW = 0;
        for (const auto& sp : spans)
            if (!sp.text.empty())
                totalW += m_bitmap->MeasureTextWidthPublic(sp.text, sp.fontFamily,
                                                            sp.fontSize, sp.bold, sp.italic);

        int startX = (effTextAlign == 0) ? ix
                   : (effTextAlign == 2) ? ix + iw - totalW
                   :                       ix + (iw - totalW) / 2;

        auto drawPass = [&](int offX, int offY, bool useSpanColor,
                            COLORREF overrideColor, uint8_t overrideAlpha) {
            int curX = startX;
            for (const auto& sp : spans) {
                if (sp.text.empty()) continue;
                int sw = m_bitmap->MeasureTextWidthPublic(sp.text, sp.fontFamily,
                                                           sp.fontSize, sp.bold, sp.italic);
                COLORREF col; uint8_t a;
                if (useSpanColor) {
                    col = ArgbToColorref(sp.color, a);
                    a   = (uint8_t)((a * (uint32_t)alpha) / 255);
                } else { col = overrideColor; a = overrideAlpha; }
                m_bitmap->DrawTextGDIPublic(sp.text, curX+offX, iy+offY, sw+4, ih,
                    col, sp.fontFamily, sp.fontSize, sp.bold, sp.italic, a,
                    DT_SINGLELINE | DT_VCENTER | DT_LEFT);
                curX += sw;
            }
        };

        if (effectOutline && effectOutlineW > 0) {
            COLORREF ocr = ArgbToColorref(effectOutlineCol, dummy);
            uint8_t outAlpha = (uint8_t)((dummy * (uint32_t)alpha) / 255);
            int ow = effectOutlineW;
            for (int dy2 = -ow; dy2 <= ow; dy2++)
                for (int dx2 = -ow; dx2 <= ow; dx2++) {
                    if (dx2 == 0 && dy2 == 0) continue;
                    drawPass(dx2, dy2, false, ocr, outAlpha);
                }
        }

        if (effectShadow) {
            COLORREF scr = ArgbToColorref(effectShadowCol, dummy);
            uint8_t shAlpha = (uint8_t)((dummy * (uint32_t)alpha) / 255);
            if (effShadowBl > 0) {
                int bR = effShadowBl;
                int tmpW = iw + bR * 2, tmpH = ih + bR * 2;
                RenderBitmap shBmp(tmpW, tmpH);
                RenderBitmap* saved = m_bitmap;
                m_bitmap = &shBmp;
                int curX2 = startX - ix + bR;
                for (const auto& sp2 : spans) {
                    if (sp2.text.empty()) continue;
                    int sw2 = m_bitmap->MeasureTextWidthPublic(sp2.text, sp2.fontFamily,
                                                                sp2.fontSize, sp2.bold, sp2.italic);
                    m_bitmap->DrawTextGDIPublic(sp2.text, curX2, bR, sw2+4, ih,
                        scr, sp2.fontFamily, sp2.fontSize, sp2.bold, sp2.italic, 255,
                        DT_SINGLELINE | DT_VCENTER | DT_LEFT);
                    curX2 += sw2;
                }
                m_bitmap = saved;
                std::vector<uint8_t> shPx;
                shBmp.GetPixels(1.0f, shPx);
                BoxBlur(shPx.data(), tmpW, tmpH, bR);
                m_bitmap->BlitImagePublic(shPx.data(), tmpW, tmpH,
                    ix+effShadowX-bR, iy+effShadowY-bR, tmpW, tmpH, shAlpha);
            } else {
                drawPass(effShadowX, effShadowY, false, scr, shAlpha);
            }
        }

        drawPass(0, 0, true, 0, 255);
    }
}

void LayoutRenderer::RenderTextTransition(const LayoutElement& el, const ElemState& state,
                                          const TextTransitionInfo& trans,
                                          int ix, int iy, int iw, int ih, uint8_t alpha, float elapsed)
{
    // Render each span set into an iw x ih temp bitmap at x=0, y=0 with full opacity,
    // then blit to m_bitmap with the appropriate alpha/offset for the transition type.
    auto makeTmp = [&](const std::vector<TextSpan>& spans) -> RenderBitmap {
        RenderBitmap tmp(iw, ih);
        RenderBitmap* saved = m_bitmap;
        m_bitmap = &tmp;
        ElemState flat = state; flat.x = 0.f; flat.y = 0.f; flat.opacity = 1.f;
        flat.scaleX = 1.f; flat.scaleY = 1.f; flat.rotation = 0.f;
        RenderTextWithSpans(el, flat, spans, 0, 0, iw, ih, 255, elapsed);
        m_bitmap = saved;
        return tmp;
    };

    auto getPixels = [](RenderBitmap& bmp) {
        std::vector<uint8_t> px;
        bmp.GetPixels(1.0f, px);
        return px;
    };

    if (trans.type == TextTransition::Fade) {
        uint8_t fromA = (uint8_t)std::min(255.f, alpha * (1.f - trans.frac) + 0.5f);
        uint8_t toA   = (uint8_t)std::min(255.f, alpha * trans.frac + 0.5f);
        RenderBitmap fromTmp = makeTmp(*trans.fromSpans);
        RenderBitmap toTmp   = makeTmp(*trans.toSpans);
        auto fromPx = getPixels(fromTmp);
        auto toPx   = getPixels(toTmp);
        if (fromA > 0) m_bitmap->BlitImagePublic(fromPx.data(), iw, ih, ix, iy, iw, ih, fromA);
        if (toA   > 0) m_bitmap->BlitImagePublic(toPx.data(),   iw, ih, ix, iy, iw, ih, toA);

    } else if (trans.type == TextTransition::Morph) {
        RenderBitmap fromTmp = makeTmp(*trans.fromSpans);
        RenderBitmap toTmp   = makeTmp(*trans.toSpans);
        auto fromPx = getPixels(fromTmp);
        auto toPx   = getPixels(toTmp);
        // Pixel-blend: out = from*(1-frac) + to*frac
        std::vector<uint8_t> blended(iw * ih * 4);
        for (int i = 0; i < iw * ih * 4; i++) {
            blended[i] = (uint8_t)(fromPx[i] * (1.f - trans.frac) + toPx[i] * trans.frac + 0.5f);
        }
        m_bitmap->BlitImagePublic(blended.data(), iw, ih, ix, iy, iw, ih, alpha);

    } else {
        // SlideLeft or SlideRight
        // FROM slides out, TO slides in. Clip the blit to the element rect [ix, ix+iw].
        float frac = trans.frac;
        int fromXOff, toXOff;
        if (trans.type == TextTransition::SlideLeft) {
            fromXOff = -(int)(frac * iw);           // FROM moves left (off screen left)
            toXOff   =  (int)((1.f - frac) * iw);   // TO comes from right
        } else { // SlideRight
            fromXOff =  (int)(frac * iw);
            toXOff   = -(int)((1.f - frac) * iw);
        }
        RenderBitmap fromTmp = makeTmp(*trans.fromSpans);
        RenderBitmap toTmp   = makeTmp(*trans.toSpans);
        auto fromPx = getPixels(fromTmp);
        auto toPx   = getPixels(toTmp);
        m_bitmap->BlitRawClippedPublic(fromPx.data(), iw, ih, ix+fromXOff, iy, ix, iy, iw, ih, alpha);
        m_bitmap->BlitRawClippedPublic(toPx.data(),   iw, ih, ix+toXOff,   iy, ix, iy, iw, ih, alpha);
    }
}

void LayoutRenderer::RenderGifElement(const LayoutElement& el, const ElemState& state, float elapsed)
{
    if (el.imageFrames.empty()) return;

    // Find the current GIF frame — animate from frame 0 when element first appears
    float relElapsed = std::max(0.f, elapsed - el.gifAnimStartTime);
    float t = el.gifTotalDuration > 0.f ? std::fmod(relElapsed, el.gifTotalDuration) : 0.f;
    int   fi = 0;
    float acc = 0.f;
    for (int i = 0; i < (int)el.imageFrames.size(); i++) {
        acc += el.imageFrames[i].delay;
        if (t < acc) { fi = i; break; }
    }

    BlitFrame(el.imageFrames[fi], state);
}

// Mirrors a BGRA buffer in place. flipX = horizontal, flipY = vertical.
static void MirrorBGRA(std::vector<uint8_t>& px, int w, int h, bool flipX, bool flipY)
{
    if ((!flipX && !flipY) || w <= 0 || h <= 0) return;
    std::vector<uint8_t> out(px.size());
    for (int y = 0; y < h; y++) {
        int sy = flipY ? (h - 1 - y) : y;
        for (int x = 0; x < w; x++) {
            int sx = flipX ? (w - 1 - x) : x;
            const uint8_t* s = &px[((size_t)sy * w + sx) * 4];
            uint8_t* dpx     = &out[((size_t)y  * w + x ) * 4];
            dpx[0] = s[0]; dpx[1] = s[1]; dpx[2] = s[2]; dpx[3] = s[3];
        }
    }
    px.swap(out);
}

void LayoutRenderer::BlitFrame(const ImageFrame& frame, const ElemState& state)
{
    if (frame.bgra.empty() || frame.width <= 0 || frame.height <= 0) return;
    BlitFrameRaw(frame.bgra.data(), frame.width, frame.height, state);
}

void LayoutRenderer::BlitFrameRaw(const uint8_t* srcBgra, int srcW, int srcH, const ElemState& state)
{
    if (!srcBgra || srcW <= 0 || srcH <= 0) return;

    // Negative width/height = flip/mirror the image. Render at abs size; mirror the pixels.
    float effW = state.w * state.scaleX;
    float effH = state.h * state.scaleY;
    int dstW = (int)std::round(std::fabs(effW));
    int dstH = (int)std::round(std::fabs(effH));
    if (dstW <= 0 || dstH <= 0) return;
    bool flipX = effW < 0.f;
    bool flipY = effH < 0.f;

    uint8_t opacity = (uint8_t)(std::min(state.opacity, 1.0f) * 255.f + 0.5f);

    if (fabsf(state.rotation) > 0.01f || flipX || flipY) {
        // Scale to dstW/dstH via a temp buffer, optionally mirror, then blit (rotated or straight).
        RenderBitmap tmp(dstW, dstH);
        tmp.BlitImagePublic(srcBgra, srcW, srcH, 0, 0, dstW, dstH, 255);
        std::vector<uint8_t> tmpPx;
        tmp.GetPixels(1.0f, tmpPx);
        if (flipX || flipY) MirrorBGRA(tmpPx, dstW, dstH, flipX, flipY);

        if (fabsf(state.rotation) > 0.01f) {
            float cx = state.x + (float)dstW / 2.f;
            float cy = state.y + (float)dstH / 2.f;
            m_bitmap->RotateBlitPublic(tmpPx.data(), dstW, dstH, cx, cy, state.rotation, 1.f, 1.f, opacity);
        } else {
            int dstX = (int)std::round(state.x);
            int dstY = (int)std::round(state.y);
            m_bitmap->BlitImagePublic(tmpPx.data(), dstW, dstH, dstX, dstY, dstW, dstH, opacity);
        }
    } else {
        int dstX = (int)std::round(state.x);
        int dstY = (int)std::round(state.y);
        m_bitmap->BlitImagePublic(srcBgra, srcW, srcH, dstX, dstY, dstW, dstH, opacity);
    }
}

void LayoutRenderer::RenderVideoElement(const LayoutElement& el, const ElemState& state, float elapsed)
{
    if (!el.videoDecoder || !el.videoDecoder->Valid()) return;

    double tSec = (double)elapsed - el.videoStartTime;
    if (tSec < 0.0) tSec = 0.0;

    int fw = 0, fh = 0;
    const uint8_t* px = el.videoDecoder->GetFrameAt(tSec, el.videoEnd, fw, fh);
    if (!px || fw <= 0 || fh <= 0) return;

    ElemState s = state;
    // EndFade: fade opacity out over a short tail once the clip is done, then stop drawing.
    if (el.videoEnd == VideoEndBehavior::EndFade) {
        double dur = el.videoDecoder->Duration();
        const double tail = 0.5;
        if (dur > 0.0 && tSec > dur) {
            double k = 1.0 - (tSec - dur) / tail;
            if (k <= 0.0) return;
            s.opacity *= (float)k;
        }
    }

    BlitFrameRaw(px, fw, fh, s);
}
