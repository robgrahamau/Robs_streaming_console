#pragma once
#define NOMINMAX
#include <windows.h>
#include <vector>
#include <string>
#include <cstdint>

// Shared rendering helpers for the music Now-Playing + Lyrics overlay sources.
// Everything works on top-down, premultiplied BGRA buffers (what OBS expects via GS_BGRA).
namespace music_render {

struct TextStyle {
    std::wstring font   = L"Segoe UI";
    int          sizePx = 32;
    int          weight = FW_SEMIBOLD;  // GDI weight (FW_NORMAL=400, FW_BOLD=700)
    uint32_t     argb   = 0xFFFFFFFF;   // 0xAARRGGBB
};

enum class Align { Left, Center, Right };

// Decode an image file to premultiplied BGRA via WIC. Returns false on failure.
bool LoadImageBGRA(const std::wstring& path, std::vector<uint8_t>& outBgra, int& outW, int& outH);

// Bilinear-scale a premultiplied BGRA source into a dstW x dstH premultiplied BGRA buffer.
void ScaleBGRA(const std::vector<uint8_t>& src, int sw, int sh,
               std::vector<uint8_t>& dst, int dstW, int dstH);

// src-over composite a premultiplied BGRA tile into dst at (dx,dy), clipped to dst bounds.
void BlitOver(std::vector<uint8_t>& dst, int dw, int dh,
              const std::vector<uint8_t>& src, int sw, int sh, int dx, int dy);

// Pixel width a line of text would occupy (for centring / fitting).
int MeasureTextWidth(const std::wstring& text, const TextStyle& st);

// Render one line of text (grayscale-AA, tinted to st.argb, premultiplied) into a tile of size
// (boxW x lineHeight) and src-over composite it into dst at (x,y). align positions within boxW.
// Returns the line height used.
int DrawTextLine(std::vector<uint8_t>& dst, int dw, int dh,
                 int x, int y, int boxW, const std::wstring& text,
                 const TextStyle& st, Align align);

// Convenience: the natural line height for a font size.
inline int LineHeight(int sizePx) { return (int)(sizePx * 1.35f) + 2; }

} // namespace music_render
