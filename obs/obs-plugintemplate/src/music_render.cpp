#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "music_render.h"
#include <wincodec.h>
#include <algorithm>
#include <cmath>

namespace music_render {

bool LoadImageBGRA(const std::wstring& path, std::vector<uint8_t>& outBgra, int& outW, int& outH)
{
    outBgra.clear(); outW = outH = 0;
    if (path.empty()) return false;

    IWICImagingFactory* factory = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IWICImagingFactory, (void**)&factory)))
        return false;

    bool ok = false;
    IWICBitmapDecoder* decoder = nullptr;
    if (SUCCEEDED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                                     WICDecodeMetadataCacheOnLoad, &decoder))) {
        IWICBitmapFrameDecode* src = nullptr;
        if (SUCCEEDED(decoder->GetFrame(0, &src))) {
            IWICFormatConverter* conv = nullptr;
            if (SUCCEEDED(factory->CreateFormatConverter(&conv)) &&
                SUCCEEDED(conv->Initialize(src, GUID_WICPixelFormat32bppPBGRA,
                                           WICBitmapDitherTypeNone, nullptr, 0.f,
                                           WICBitmapPaletteTypeCustom))) {
                UINT w = 0, h = 0;
                conv->GetSize(&w, &h);
                if (w > 0 && h > 0) {
                    outBgra.resize((size_t)w * h * 4);
                    ok = SUCCEEDED(conv->CopyPixels(nullptr, w * 4, (UINT)outBgra.size(), outBgra.data()));
                    if (ok) { outW = (int)w; outH = (int)h; }
                    else outBgra.clear();
                }
            }
            if (conv) conv->Release();
            src->Release();
        }
        decoder->Release();
    }
    factory->Release();
    return ok;
}

void ScaleBGRA(const std::vector<uint8_t>& src, int sw, int sh,
               std::vector<uint8_t>& dst, int dstW, int dstH)
{
    dst.assign((size_t)dstW * dstH * 4, 0);
    if (sw <= 0 || sh <= 0 || dstW <= 0 || dstH <= 0) return;

    for (int y = 0; y < dstH; ++y) {
        float fy = (y + 0.5f) * sh / dstH - 0.5f;
        int y0 = (int)std::floor(fy);
        float wy = fy - y0;
        int y0c = std::clamp(y0, 0, sh - 1);
        int y1c = std::clamp(y0 + 1, 0, sh - 1);
        for (int x = 0; x < dstW; ++x) {
            float fx = (x + 0.5f) * sw / dstW - 0.5f;
            int x0 = (int)std::floor(fx);
            float wx = fx - x0;
            int x0c = std::clamp(x0, 0, sw - 1);
            int x1c = std::clamp(x0 + 1, 0, sw - 1);

            const uint8_t* p00 = &src[((size_t)y0c * sw + x0c) * 4];
            const uint8_t* p01 = &src[((size_t)y0c * sw + x1c) * 4];
            const uint8_t* p10 = &src[((size_t)y1c * sw + x0c) * 4];
            const uint8_t* p11 = &src[((size_t)y1c * sw + x1c) * 4];
            uint8_t* o = &dst[((size_t)y * dstW + x) * 4];
            for (int c = 0; c < 4; ++c) {
                float top = p00[c] * (1 - wx) + p01[c] * wx;
                float bot = p10[c] * (1 - wx) + p11[c] * wx;
                o[c] = (uint8_t)std::clamp(top * (1 - wy) + bot * wy + 0.5f, 0.f, 255.f);
            }
        }
    }
}

void BlitOver(std::vector<uint8_t>& dst, int dw, int dh,
              const std::vector<uint8_t>& src, int sw, int sh, int dx, int dy)
{
    for (int y = 0; y < sh; ++y) {
        int ty = dy + y;
        if (ty < 0 || ty >= dh) continue;
        for (int x = 0; x < sw; ++x) {
            int tx = dx + x;
            if (tx < 0 || tx >= dw) continue;
            const uint8_t* s = &src[((size_t)y * sw + x) * 4];
            uint8_t* d = &dst[((size_t)ty * dw + tx) * 4];
            uint8_t sa = s[3];
            if (sa == 0) continue;
            int inv = 255 - sa;
            d[0] = (uint8_t)(s[0] + (d[0] * inv) / 255);
            d[1] = (uint8_t)(s[1] + (d[1] * inv) / 255);
            d[2] = (uint8_t)(s[2] + (d[2] * inv) / 255);
            d[3] = (uint8_t)(s[3] + (d[3] * inv) / 255);
        }
    }
}

static HFONT MakeFont(const TextStyle& st)
{
    return CreateFontW(-st.sizePx, 0, 0, 0, st.weight, FALSE, FALSE, FALSE,
                       DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
                       ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_DONTCARE, st.font.c_str());
}

int MeasureTextWidth(const std::wstring& text, const TextStyle& st)
{
    HDC dc = CreateCompatibleDC(nullptr);
    HFONT font = MakeFont(st);
    HGDIOBJ old = SelectObject(dc, font);
    SIZE sz{ 0, 0 };
    GetTextExtentPoint32W(dc, text.c_str(), (int)text.size(), &sz);
    SelectObject(dc, old);
    DeleteObject(font);
    DeleteDC(dc);
    return sz.cx;
}

int DrawTextLine(std::vector<uint8_t>& dst, int dw, int dh,
                 int x, int y, int boxW, const std::wstring& text,
                 const TextStyle& st, Align align)
{
    int lineH = LineHeight(st.sizePx);
    if (boxW <= 0 || lineH <= 0 || text.empty()) return lineH;

    // 32-bit top-down DIB, zeroed.
    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = boxW;
    bmi.bmiHeader.biHeight = -lineH;   // top-down
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP dib = CreateDIBSection(dc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!dib) { DeleteDC(dc); return lineH; }
    memset(bits, 0, (size_t)boxW * lineH * 4);

    HGDIOBJ oldBmp = SelectObject(dc, dib);
    HFONT font = MakeFont(st);
    HGDIOBJ oldFont = SelectObject(dc, font);
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(255, 255, 255));  // white → coverage; tinted below

    RECT rc{ 0, 0, boxW, lineH };
    UINT fmt = DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER | DT_END_ELLIPSIS;
    fmt |= (align == Align::Center) ? DT_CENTER : (align == Align::Right) ? DT_RIGHT : DT_LEFT;
    DrawTextW(dc, text.c_str(), (int)text.size(), &rc, fmt);
    GdiFlush();

    // Tint coverage → premultiplied BGRA, then src-over into dst.
    uint8_t tb = (uint8_t)(st.argb & 0xFF);
    uint8_t tg = (uint8_t)((st.argb >> 8) & 0xFF);
    uint8_t tr = (uint8_t)((st.argb >> 16) & 0xFF);
    uint8_t ta = (uint8_t)((st.argb >> 24) & 0xFF);
    const uint8_t* sp = (const uint8_t*)bits;

    std::vector<uint8_t> tile((size_t)boxW * lineH * 4, 0);
    for (int yy = 0; yy < lineH; ++yy) {
        for (int xx = 0; xx < boxW; ++xx) {
            uint8_t cov = sp[((size_t)yy * boxW + xx) * 4]; // grayscale AA coverage (B==G==R)
            if (cov == 0) continue;
            uint8_t a = (uint8_t)((cov * ta) / 255);
            uint8_t* o = &tile[((size_t)yy * boxW + xx) * 4];
            o[0] = (uint8_t)((tb * a) / 255);
            o[1] = (uint8_t)((tg * a) / 255);
            o[2] = (uint8_t)((tr * a) / 255);
            o[3] = a;
        }
    }
    BlitOver(dst, dw, dh, tile, boxW, lineH, x, y);

    SelectObject(dc, oldFont);
    DeleteObject(font);
    SelectObject(dc, oldBmp);
    DeleteObject(dib);
    DeleteDC(dc);
    return lineH;
}

} // namespace music_render
