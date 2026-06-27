#include "renderer.h"
#include <wincodec.h>
#include <propvarutil.h>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <unordered_map>
#include <cwctype>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "propsys.lib")

static int ResolveFontWeight(const std::wstring& fontFamily, bool bold)
{
    std::wstring face = fontFamily;
    std::transform(face.begin(), face.end(), face.begin(),
                   [](wchar_t ch) { return (wchar_t)towlower(ch); });

    int weight = FW_NORMAL;
    if (face.find(L"thin") != std::wstring::npos)
        weight = FW_THIN;
    else if (face.find(L"extralight") != std::wstring::npos || face.find(L"ultralight") != std::wstring::npos)
        weight = FW_EXTRALIGHT;
    else if (face.find(L"light") != std::wstring::npos)
        weight = FW_LIGHT;
    else if (face.find(L"medium") != std::wstring::npos)
        weight = FW_MEDIUM;
    else if (face.find(L"semibold") != std::wstring::npos || face.find(L"demibold") != std::wstring::npos)
        weight = FW_SEMIBOLD;
    else if (face.find(L"extrabold") != std::wstring::npos || face.find(L"ultrabold") != std::wstring::npos)
        weight = FW_EXTRABOLD;
    else if (face.find(L"black") != std::wstring::npos || face.find(L"heavy") != std::wstring::npos)
        weight = FW_BLACK;
    else if (face.find(L"bold") != std::wstring::npos)
        weight = FW_BOLD;

    if (bold && weight < FW_BOLD)
        weight = FW_BOLD;

    return weight;
}

RenderBitmap::RenderBitmap(int width, int height)
    : m_width(width), m_height(height)
{
    m_pixels = new uint32_t[width * height]();
}

RenderBitmap::~RenderBitmap()
{
    delete[] m_pixels;
}

void RenderBitmap::DrawAlertText(const std::wstring& username, const std::wstring& message)
{
    Clear();

    const int padX = 40;
    const int pillH = 130;
    const int pillY = (m_height - pillH) / 2;
    const int pillW = m_width - padX * 2;

    FillRoundRect(padX, pillY, pillW, pillH, 14, 18, 18, 18, 200);

    DrawTextGDI(username,
                padX + 8, pillY + 12, pillW - 16, 44,
                RGB(255, 255, 255), L"Segoe UI", 30, true, false, 255,
                DT_CENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_VCENTER);

    if (!message.empty()) {
        DrawTextGDI(message,
                    padX + 8, pillY + 64, pillW - 16, 56,
                    RGB(200, 200, 200), L"Segoe UI", 20, false, false, 255,
                    DT_CENTER | DT_WORDBREAK | DT_END_ELLIPSIS);
    }
}

struct EmoteFrame {
    std::vector<uint8_t> bgra;
    int w = 0;
    int h = 0;
    float delay = 0.1f;
};

struct CachedEmote {
    std::vector<EmoteFrame> frames;
    float totalDuration = 0.f;
    uint64_t lastUsed = 0;   // LRU clock value, updated on every access
    size_t   byteSize = 0;   // total decoded bytes, for the cache budget

    const EmoteFrame* GetFrame(float elapsed) const
    {
        if (frames.empty())
            return nullptr;
        if (frames.size() == 1 || totalDuration <= 0.f)
            return &frames[0];

        float t = std::fmod(elapsed, totalDuration);
        float acc = 0.f;
        for (const auto& frame : frames) {
            acc += frame.delay;
            if (t < acc)
                return &frame;
        }
        return &frames.back();
    }
};

static std::unordered_map<std::string, CachedEmote> s_emoteCache;
static uint64_t s_emoteClock = 0;
static size_t   s_emoteCacheBytes = 0;
// Cap the emote/badge cache so a long session can't grow it without bound (the cache used to be
// keep-forever). Evicts least-recently-used entries. Only ever called at the very start of a chat
// render pass — before any EmoteFrame* pointers are taken for the frame — so an eviction can never
// dangle a pointer that the same render is about to use.
static const size_t kEmoteCacheBudgetBytes = 128ull * 1024 * 1024;

static void PruneEmoteCache()
{
    while (s_emoteCacheBytes > kEmoteCacheBudgetBytes && s_emoteCache.size() > 1) {
        auto oldest = s_emoteCache.end();
        for (auto it = s_emoteCache.begin(); it != s_emoteCache.end(); ++it)
            if (oldest == s_emoteCache.end() || it->second.lastUsed < oldest->second.lastUsed)
                oldest = it;
        if (oldest == s_emoteCache.end())
            break;
        s_emoteCacheBytes -= oldest->second.byteSize;
        s_emoteCache.erase(oldest);
    }
}

static bool LoadStaticFrame(IWICImagingFactory* factory, IWICBitmapDecoder* decoder,
                            UINT frameIdx, float delay, CachedEmote& out)
{
    IWICBitmapFrameDecode* src = nullptr;
    IWICFormatConverter* conv = nullptr;
    bool ok = false;

    if (SUCCEEDED(decoder->GetFrame(frameIdx, &src)) &&
        SUCCEEDED(factory->CreateFormatConverter(&conv)) &&
        SUCCEEDED(conv->Initialize(src, GUID_WICPixelFormat32bppPBGRA,
                                   WICBitmapDitherTypeNone, nullptr, 0.f,
                                   WICBitmapPaletteTypeCustom)))
    {
        UINT w = 0;
        UINT h = 0;
        conv->GetSize(&w, &h);

        EmoteFrame frame;
        frame.w = (int)w;
        frame.h = (int)h;
        frame.delay = delay;
        frame.bgra.resize(w * h * 4);

        if (SUCCEEDED(conv->CopyPixels(nullptr, w * 4, (UINT)frame.bgra.size(), frame.bgra.data()))) {
            out.frames.push_back(std::move(frame));
            out.totalDuration += delay;
            ok = true;
        }
    }

    if (conv)
        conv->Release();
    if (src)
        src->Release();
    return ok;
}

static void LoadGifFrames(IWICImagingFactory* factory, IWICBitmapDecoder* decoder,
                          CachedEmote& out)
{
    UINT frameCount = 0;
    decoder->GetFrameCount(&frameCount);
    if (frameCount == 0)
        return;

    IWICMetadataQueryReader* globalMeta = nullptr;
    UINT logicalWidth = 0;
    UINT logicalHeight = 0;
    if (SUCCEEDED(decoder->GetMetadataQueryReader(&globalMeta))) {
        PROPVARIANT pv;
        PropVariantInit(&pv);
        if (SUCCEEDED(globalMeta->GetMetadataByName(L"/logscrdesc/Width", &pv)))
            logicalWidth = pv.uiVal;
        PropVariantClear(&pv);
        if (SUCCEEDED(globalMeta->GetMetadataByName(L"/logscrdesc/Height", &pv)))
            logicalHeight = pv.uiVal;
        PropVariantClear(&pv);
        globalMeta->Release();
    }

    std::vector<uint8_t> composite;
    if (logicalWidth > 0 && logicalHeight > 0)
        composite.resize(logicalWidth * logicalHeight * 4, 0);

    for (UINT fi = 0; fi < frameCount; fi++) {
        IWICBitmapFrameDecode* frameSrc = nullptr;
        if (FAILED(decoder->GetFrame(fi, &frameSrc)))
            continue;

        IWICMetadataQueryReader* frameMeta = nullptr;
        float delay = 0.1f;
        int offX = 0;
        int offY = 0;
        int disposal = 0;

        if (SUCCEEDED(frameSrc->GetMetadataQueryReader(&frameMeta))) {
            PROPVARIANT pv;
            PropVariantInit(&pv);
            if (SUCCEEDED(frameMeta->GetMetadataByName(L"/grctlext/Delay", &pv)))
                delay = pv.uiVal / 100.0f;
            PropVariantClear(&pv);
            if (SUCCEEDED(frameMeta->GetMetadataByName(L"/imgdesc/Left", &pv)))
                offX = pv.uiVal;
            PropVariantClear(&pv);
            if (SUCCEEDED(frameMeta->GetMetadataByName(L"/imgdesc/Top", &pv)))
                offY = pv.uiVal;
            PropVariantClear(&pv);
            if (SUCCEEDED(frameMeta->GetMetadataByName(L"/grctlext/Disposal", &pv)))
                disposal = pv.uiVal;
            PropVariantClear(&pv);
            frameMeta->Release();
        }

        IWICFormatConverter* conv = nullptr;
        if (SUCCEEDED(factory->CreateFormatConverter(&conv)) &&
            SUCCEEDED(conv->Initialize(frameSrc, GUID_WICPixelFormat32bppBGRA,
                                       WICBitmapDitherTypeNone, nullptr, 0.f,
                                       WICBitmapPaletteTypeCustom)))
        {
            UINT fw = 0;
            UINT fh = 0;
            conv->GetSize(&fw, &fh);
            if (logicalWidth == 0) {
                logicalWidth = fw;
                logicalHeight = fh;
                composite.resize(logicalWidth * logicalHeight * 4, 0);
            }

            std::vector<uint8_t> framePixels(fw * fh * 4);
            conv->CopyPixels(nullptr, fw * 4, (UINT)framePixels.size(), framePixels.data());

            for (UINT fy = 0; fy < fh; fy++) {
                for (UINT fx = 0; fx < fw; fx++) {
                    int cx = offX + (int)fx;
                    int cy = offY + (int)fy;
                    if (cx < 0 || cx >= (int)logicalWidth || cy < 0 || cy >= (int)logicalHeight)
                        continue;

                    const uint8_t* src = &framePixels[(fy * fw + fx) * 4];
                    uint8_t* dst = &composite[(cy * logicalWidth + cx) * 4];
                    uint8_t sa = src[3];
                    if (sa == 255) {
                        std::memcpy(dst, src, 4);
                    } else if (sa > 0) {
                        uint8_t da = dst[3];
                        uint32_t oa = sa + (uint32_t)da * (255 - sa) / 255;
                        if (oa > 0) {
                            for (int ch = 0; ch < 3; ch++)
                                dst[ch] = (uint8_t)(((uint32_t)src[ch] * sa +
                                                     (uint32_t)dst[ch] * da * (255 - sa) / 255) / oa);
                            dst[3] = (uint8_t)oa;
                        }
                    }
                }
            }

            EmoteFrame frame;
            frame.w = (int)logicalWidth;
            frame.h = (int)logicalHeight;
            frame.delay = delay > 0.01f ? delay : 0.1f;
            frame.bgra.resize(logicalWidth * logicalHeight * 4);
            for (size_t i = 0; i < logicalWidth * logicalHeight; i++) {
                uint8_t b = composite[i * 4 + 0];
                uint8_t g = composite[i * 4 + 1];
                uint8_t r = composite[i * 4 + 2];
                uint8_t a = composite[i * 4 + 3];
                frame.bgra[i * 4 + 0] = (uint8_t)((uint32_t)b * a / 255);
                frame.bgra[i * 4 + 1] = (uint8_t)((uint32_t)g * a / 255);
                frame.bgra[i * 4 + 2] = (uint8_t)((uint32_t)r * a / 255);
                frame.bgra[i * 4 + 3] = a;
            }
            out.frames.push_back(std::move(frame));
            out.totalDuration += frame.delay;

            if (disposal == 2) {
                for (UINT fy = 0; fy < fh; fy++) {
                    for (UINT fx = 0; fx < fw; fx++) {
                        int cx = offX + (int)fx;
                        int cy = offY + (int)fy;
                        if (cx >= 0 && cx < (int)logicalWidth && cy >= 0 && cy < (int)logicalHeight)
                            std::memset(&composite[(cy * logicalWidth + cx) * 4], 0, 4);
                    }
                }
            }
        }

        if (conv)
            conv->Release();
        frameSrc->Release();
    }
}

static const CachedEmote* LoadCachedEmote(const std::string& path)
{
    if (path.empty())
        return nullptr;

    auto it = s_emoteCache.find(path);
    if (it != s_emoteCache.end()) {
        it->second.lastUsed = ++s_emoteClock;
        return &it->second;
    }

    IWICImagingFactory* factory = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IWICImagingFactory, (void**)&factory)))
        return nullptr;

    CachedEmote emote;
    std::wstring wpath(path.begin(), path.end());
    IWICBitmapDecoder* decoder = nullptr;

    if (SUCCEEDED(factory->CreateDecoderFromFilename(wpath.c_str(), nullptr, GENERIC_READ,
                                                     WICDecodeMetadataCacheOnLoad, &decoder)))
    {
        GUID containerFormat = GUID_NULL;
        decoder->GetContainerFormat(&containerFormat);
        if (containerFormat == GUID_ContainerFormatGif)
            LoadGifFrames(factory, decoder, emote);
        else
            LoadStaticFrame(factory, decoder, 0, 0.f, emote);
        decoder->Release();
    }

    factory->Release();

    if (emote.frames.empty())
        return nullptr;

    size_t bytes = 0;
    for (const auto& f : emote.frames)
        bytes += f.bgra.size();
    emote.byteSize = bytes;
    emote.lastUsed = ++s_emoteClock;
    s_emoteCacheBytes += bytes;

    s_emoteCache[path] = std::move(emote);
    return &s_emoteCache[path];
}

static int DrawBadgePill(RenderBitmap* bm, int x, int y, int lineH,
                         const wchar_t* label, uint8_t br, uint8_t bg, uint8_t bb, uint8_t alpha = 220)
{
    const int pw = 22;
    const int ph = 14;
    int py = y + (lineH - ph) / 2;
    bm->FillRoundRectPublic(x, py, pw, ph, 4, bb, bg, br, alpha);
    bm->DrawTextGDIPublic(label, x, py, pw, ph,
                          RGB(255, 255, 255), L"Segoe UI", 10, true, false, alpha,
                          DT_CENTER | DT_SINGLELINE | DT_VCENTER);
    return pw + 2;
}

static int MeasureBadgePillWidth()
{
    return 24;
}

namespace {

struct InlineToken {
    enum class Kind { Text, Emote };

    Kind kind = Kind::Text;
    std::wstring text;
    COLORREF color = RGB(225, 225, 225);
    const EmoteFrame* frame = nullptr;
};

struct WrappedRow {
    std::vector<InlineToken> tokens;
};

struct EntryLayout {
    const RenderBitmap::ChatLine* line = nullptr;
    std::vector<WrappedRow> rows;
    int height = 0;
};

static bool IsWhitespaceToken(const InlineToken& token)
{
    return token.kind == InlineToken::Kind::Text &&
           !token.text.empty() &&
           std::all_of(token.text.begin(), token.text.end(),
                       [](wchar_t ch) { return iswspace(ch) != 0; });
}

static int MeasureTextToken(RenderBitmap* bm, const InlineToken& token,
                            const std::wstring& fontFamily, int fontSize)
{
    if (token.text.empty())
        return 0;
    return bm->MeasureTextWidthPublic(token.text, fontFamily, fontSize, false, false);
}

static int RowPixelWidth(RenderBitmap* bm, const WrappedRow& row,
                         const std::wstring& fontFamily, int fontSize, int emoteSize)
{
    int width = 0;
    for (const auto& token : row.tokens)
        width += token.kind == InlineToken::Kind::Emote ? emoteSize + 2 : MeasureTextToken(bm, token, fontFamily, fontSize);
    return width;
}

static COLORREF PlatformColor(const std::wstring& platform)
{
    if (platform == L"Twitch")
        return RGB(145, 71, 255);
    if (platform == L"YouTube")
        return RGB(255, 0, 0);
    return RGB(83, 252, 24);
}

static COLORREF BaseTextColor(const RenderBitmap::ChatRenderSettings& settings)
{
    return RGB(settings.textR, settings.textG, settings.textB);
}

static COLORREF ResolveDisplayNameColor(const RenderBitmap::ChatLine& line,
                                        const RenderBitmap::ChatRenderSettings& settings)
{
    if (settings.displayNameColorMode == 1)
        return PlatformColor(!line.platformIcons.empty() ? line.platformIcons.front() : line.platform);
    if (settings.displayNameColorMode == 2)
        return BaseTextColor(settings);
    return HexToColorRef(line.color);
}

static int MeasurePlatformIconWidth(const RenderBitmap::ChatLine& line)
{
    int count = (int)line.platformIcons.size();
    return std::max(0, count) * MeasureBadgePillWidth();
}

static int DrawPlatformBadge(RenderBitmap* bm, const std::wstring& platform, int x, int y, int lineH, uint8_t alpha)
{
    if (platform == L"Twitch")
        return DrawBadgePill(bm, x, y, lineH, L"T", 100, 65, 165, alpha);
    if (platform == L"YouTube")
        return DrawBadgePill(bm, x, y, lineH, L"Y", 220, 30, 30, alpha);
    return DrawBadgePill(bm, x, y, lineH, L"K", 26, 156, 62, alpha);
}

static int MeasurePrefixWidth(RenderBitmap* bm, const RenderBitmap::ChatLine& line,
                              const RenderBitmap::ChatRenderSettings& settings,
                              const std::wstring& fontFamily, int fontSize, int lineH, int badgeSize)
{
    int width = 0;
    if (settings.showTimestamps && !line.timestamp.empty()) {
        width += bm->MeasureTextWidthPublic(line.timestamp + L" ", fontFamily, fontSize, false, false);
    }
    if (settings.showPlatformIcon)
        width += MeasurePlatformIconWidth(line);

    if (!line.badgePaths.empty()) {
        int visibleBadges = 0;
        for (const auto& badgePath : line.badgePaths) {
            if (!badgePath.empty())
                visibleBadges++;
        }
        width += visibleBadges * (badgeSize + 2);
    } else {
        if (line.isBroadcaster)
            width += MeasureBadgePillWidth();
        if (line.isModerator)
            width += MeasureBadgePillWidth();
        if (line.isVip)
            width += MeasureBadgePillWidth();
        if (line.isSubscriber)
            width += MeasureBadgePillWidth();
    }

    width += 2;
    width += bm->MeasureTextWidthPublic(line.username, fontFamily, fontSize, true, false);
    width += 4;
    return width;
}

static void AppendTextPieces(std::vector<InlineToken>& out, const std::wstring& text, COLORREF color)
{
    size_t i = 0;
    while (i < text.size()) {
        if (text[i] == L'\r') {
            i++;
            continue;
        }
        if (text[i] == L'\n') {
            InlineToken token;
            token.text = L"\n";
            out.push_back(std::move(token));
            i++;
            continue;
        }

        const bool whitespace = iswspace(text[i]) != 0;
        size_t start = i;
        while (i < text.size() && text[i] != L'\r' && text[i] != L'\n' &&
               (iswspace(text[i]) != 0) == whitespace)
        {
            i++;
        }

        InlineToken token;
        token.kind = InlineToken::Kind::Text;
        token.text = text.substr(start, i - start);
        token.color = color;
        out.push_back(std::move(token));
    }
}

static std::vector<InlineToken> BuildMessageTokens(const RenderBitmap::ChatLine& line, float elapsed,
                                                   const RenderBitmap::ChatRenderSettings& settings)
{
    std::vector<InlineToken> tokens;
    std::vector<RenderBitmap::EmotePos> emotes = line.emotes;
    std::sort(emotes.begin(), emotes.end(),
              [](const RenderBitmap::EmotePos& a, const RenderBitmap::EmotePos& b) {
                  return a.start < b.start;
              });

    const std::wstring& message = line.text;
    int messageLength = (int)message.size();
    int charPos = 0;

    for (const auto& emotePos : emotes) {
        if (messageLength <= 0)
            break;

        int eStart = std::max(0, std::min(emotePos.start, messageLength - 1));
        int eEnd = std::max(eStart, std::min(emotePos.end, messageLength - 1));

        if (eStart > charPos)
            AppendTextPieces(tokens, message.substr(charPos, eStart - charPos),
                             RGB(settings.textR, settings.textG, settings.textB));

        const CachedEmote* emote = emotePos.filePath.empty() ? nullptr : LoadCachedEmote(emotePos.filePath);
        const EmoteFrame* frame = emote ? emote->GetFrame(elapsed) : nullptr;
        if (frame && !frame->bgra.empty()) {
            InlineToken token;
            token.kind = InlineToken::Kind::Emote;
            token.frame = frame;
            tokens.push_back(token);
        } else {
            AppendTextPieces(tokens, message.substr(eStart, eEnd - eStart + 1),
                             RGB(settings.textR, settings.textG, settings.textB));
        }

        charPos = eEnd + 1;
    }

    if (charPos < messageLength)
        AppendTextPieces(tokens, message.substr(charPos),
                         RGB(settings.textR, settings.textG, settings.textB));

    if (line.bitsAmount > 0) {
        wchar_t bitsBuf[32];
        _snwprintf_s(bitsBuf, _countof(bitsBuf), L" (%d bits)", line.bitsAmount);
        AppendTextPieces(tokens, bitsBuf, RGB(settings.bitsR, settings.bitsG, settings.bitsB));
    }

    return tokens;
}

static std::vector<WrappedRow> WrapMessage(RenderBitmap* bm, const std::vector<InlineToken>& tokens,
                                           const std::wstring& fontFamily, int fontSize,
                                           int firstRowWidth, int nextRowWidth, int emoteSize)
{
    std::vector<WrappedRow> rows;
    rows.push_back(WrappedRow{});

    int rowIndex = 0;
    int rowWidth = 0;

    auto maxWidthForRow = [&](int index) {
        return index == 0 ? std::max(20, firstRowWidth) : std::max(20, nextRowWidth);
    };

    for (const auto& token : tokens) {
        if (token.kind == InlineToken::Kind::Text && token.text == L"\n") {
            rows.push_back(WrappedRow{});
            rowIndex++;
            rowWidth = 0;
            continue;
        }

        int tokenWidth = token.kind == InlineToken::Kind::Emote
            ? emoteSize + 2
            : MeasureTextToken(bm, token, fontFamily, fontSize);

        bool whitespace = IsWhitespaceToken(token);
        int maxWidth = maxWidthForRow(rowIndex);
        bool rowHasContent = !rows[rowIndex].tokens.empty();

        if (whitespace && !rowHasContent)
            continue;

        if (rowHasContent && rowWidth + tokenWidth > maxWidth) {
            rows.push_back(WrappedRow{});
            rowIndex++;
            rowWidth = 0;
            maxWidth = maxWidthForRow(rowIndex);
            if (whitespace)
                continue;
        }

        if (token.kind == InlineToken::Kind::Text && !whitespace && tokenWidth > maxWidth) {
            std::wstring remaining = token.text;
            while (!remaining.empty()) {
                size_t split = remaining.size();
                while (split > 1) {
                    int width = bm->MeasureTextWidthPublic(remaining.substr(0, split), fontFamily,
                                                           fontSize, false, false);
                    if (width <= maxWidth)
                        break;
                    split--;
                }

                InlineToken piece = token;
                piece.text = remaining.substr(0, split);
                int pieceWidth = MeasureTextToken(bm, piece, fontFamily, fontSize);

                if (!rows[rowIndex].tokens.empty() && rowWidth + pieceWidth > maxWidth) {
                    rows.push_back(WrappedRow{});
                    rowIndex++;
                    rowWidth = 0;
                    maxWidth = maxWidthForRow(rowIndex);
                }

                rows[rowIndex].tokens.push_back(piece);
                rowWidth += pieceWidth;

                remaining.erase(0, split);
                while (!remaining.empty() && iswspace(remaining.front()) != 0)
                    remaining.erase(remaining.begin());

                if (!remaining.empty()) {
                    rows.push_back(WrappedRow{});
                    rowIndex++;
                    rowWidth = 0;
                    maxWidth = maxWidthForRow(rowIndex);
                }
            }
            continue;
        }

        rows[rowIndex].tokens.push_back(token);
        rowWidth += tokenWidth;
    }

    while (rows.size() > 1 && rows.back().tokens.empty())
        rows.pop_back();
    if (rows.empty())
        rows.push_back(WrappedRow{});

    return rows;
}

static int DrawPrefix(RenderBitmap* bm, const RenderBitmap::ChatLine& line, float elapsed,
                      int x, int y, int lineH, int badgeSize,
                      const RenderBitmap::ChatRenderSettings& settings,
                      const std::wstring& fontFamily, int fontSize, uint8_t alpha)
{
    int curX = x;
    if (settings.showTimestamps && !line.timestamp.empty()) {
        auto timestampText = line.timestamp + L" ";
        int timestampWidth = bm->MeasureTextWidthPublic(timestampText, fontFamily, fontSize, false, false);
        bm->DrawTextGDIPublic(timestampText, curX, y, timestampWidth + 2, lineH,
                              BaseTextColor(settings), fontFamily, fontSize, false, false, alpha,
                              DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
        curX += timestampWidth + 2;
    }
    auto drawBadges = [&](int startX) {
        int localX = startX;
        if (!line.badgePaths.empty()) {
            for (const auto& badgePath : line.badgePaths) {
                if (badgePath.empty())
                    continue;
                const CachedEmote* emote = LoadCachedEmote(badgePath);
                const EmoteFrame* frame = emote ? emote->GetFrame(elapsed) : nullptr;
                if (!frame || frame->bgra.empty())
                    continue;
                int by = y + (lineH - badgeSize) / 2;
                bm->BlitImagePublic(frame->bgra.data(), frame->w, frame->h,
                                    localX, by, badgeSize, badgeSize, alpha);
                localX += badgeSize + 2;
            }
        } else {
            if (line.isBroadcaster)
                localX += DrawBadgePill(bm, localX, y, lineH, L"*", 180, 30, 30, alpha);
            if (line.isModerator)
                localX += DrawBadgePill(bm, localX, y, lineH, L"MOD", 30, 140, 50, alpha);
            if (line.isVip)
                localX += DrawBadgePill(bm, localX, y, lineH, L"VIP", 150, 50, 150, alpha);
            if (line.isSubscriber) {
                wchar_t subLabel[16] = L"SUB";
                if (line.subMonths > 0)
                    _snwprintf_s(subLabel, _countof(subLabel), L"%d", line.subMonths);
                localX += DrawBadgePill(bm, localX, y, lineH, subLabel, 90, 50, 160, alpha);
            }
        }
        return localX;
    };

    if (settings.showPlatformIcon) {
        for (const auto& platformIcon : line.platformIcons)
            curX += DrawPlatformBadge(bm, platformIcon, curX, y, lineH, alpha);
    }

    if (!settings.badgesAfterUsername)
        curX = drawBadges(curX);

    if (curX > x)
        curX += 2;
    int usernameWidth = bm->MeasureTextWidthPublic(line.username, fontFamily, fontSize, true, false);
    bm->DrawTextGDIPublic(line.username, curX, y, usernameWidth + 2, lineH,
                          ResolveDisplayNameColor(line, settings), fontFamily, fontSize, true, false, alpha,
                          DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
    curX += usernameWidth + 4;

    if (settings.badgesAfterUsername) {
        curX = drawBadges(curX);
        if (curX > x)
            curX += 2;
    }
    return curX;
}

static void DrawWrappedTokens(RenderBitmap* bm, const WrappedRow& row, int x, int y, int lineH,
                              const RenderBitmap::ChatRenderSettings& settings,
                              const std::wstring& fontFamily, int fontSize, int emoteSize,
                              int availableWidth, uint8_t alpha)
{
    int contentWidth = RowPixelWidth(bm, row, fontFamily, fontSize, emoteSize);
    int curX = x;
    if (settings.textAlign == 1)
        curX += std::max(0, (availableWidth - contentWidth) / 2);
    else if (settings.textAlign == 2)
        curX += std::max(0, availableWidth - contentWidth);

    bool bold = settings.fontWeight >= 600;
    for (const auto& token : row.tokens) {
        if (token.kind == InlineToken::Kind::Emote) {
            if (token.frame && !token.frame->bgra.empty()) {
                int ey = y + (lineH - emoteSize) / 2;
                bm->BlitImagePublic(token.frame->bgra.data(), token.frame->w, token.frame->h,
                                    curX, ey, emoteSize, emoteSize, alpha);
            }
            curX += emoteSize + 2;
            continue;
        }

        int width = MeasureTextToken(bm, token, fontFamily, fontSize);
        if (width <= 0)
            continue;

        if (settings.outlineSize > 0) {
            for (int oy = -settings.outlineSize; oy <= settings.outlineSize; ++oy) {
                for (int ox = -settings.outlineSize; ox <= settings.outlineSize; ++ox) {
                    if (ox == 0 && oy == 0)
                        continue;
                    bm->DrawTextGDIPublic(token.text, curX + ox, y + oy, width + 2, lineH,
                                          RGB(0, 0, 0), fontFamily, fontSize, bold, false, alpha,
                                          DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
                }
            }
        }
        if (settings.textShadow > 0) {
            bm->DrawTextGDIPublic(token.text, curX + settings.textShadow, y + settings.textShadow, width + 2, lineH,
                                  RGB(0, 0, 0), fontFamily, fontSize, bold, false, alpha,
                                  DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
        }
        bm->DrawTextGDIPublic(token.text, curX, y, width + 2, lineH,
                              token.color, fontFamily, fontSize, bold, false, alpha,
                              DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
        curX += width;
    }
}

}

void RenderBitmap::DrawChatMessages(const std::vector<ChatLine>& lines, float elapsed,
                                    float timeSinceLastMessage,
                                    const ChatRenderSettings& settings)
{
    // Evict stale emotes before any EmoteFrame* pointers are taken for this pass (see PruneEmoteCache).
    PruneEmoteCache();
    Clear();
    if (lines.empty())
        return;

    ChatRenderSettings cfg = settings;
    if (cfg.fontFamily.empty())
        cfg.fontFamily = L"Segoe UI";
    cfg.fontSize = std::max(10, cfg.fontSize);
    cfg.fontWeight = std::clamp(cfg.fontWeight, 100, 900);
    cfg.lineSpacing = std::max(0, cfg.lineSpacing);
    cfg.messagePadding = std::max(2, cfg.messagePadding);
    cfg.maxLinesShown = std::max(1, cfg.maxLinesShown);
    if (!cfg.showChatMessages)
        return;

    const int outerMargin = cfg.margin;
    const int innerPad = cfg.messagePadding;
    const int messageGap = cfg.lineSpacing;
    const int badgeSize = std::max(cfg.fontSize + 2, 18);
    const int lineH = std::max(cfg.fontSize + 8, badgeSize) + innerPad * 2;
    const int rowWidth = std::max(40, m_width - outerMargin * 2 - innerPad * 2);
    const int rowBackgroundWidth = std::max(20, m_width - outerMargin * 2);

    std::vector<EntryLayout> visibleEntries;
    int usedVisualRows = 0;
    int usedHeight = 0;

    for (int i = (int)lines.size() - 1; i >= 0; --i) {
        const ChatLine& line = lines[i];
        int prefixWidth = MeasurePrefixWidth(this, line, cfg, cfg.fontFamily, cfg.fontSize, lineH, badgeSize);
        int firstRowWidth = std::max(20, rowWidth - prefixWidth);
        auto wrappedRows = WrapMessage(this, BuildMessageTokens(line, elapsed, cfg), cfg.fontFamily,
                                       cfg.fontSize, firstRowWidth, rowWidth, badgeSize);

        int entryRows = (int)wrappedRows.size();
        int entryHeight = entryRows * lineH;
        if (!visibleEntries.empty())
            entryHeight += messageGap;

        if ((usedVisualRows > 0 && usedVisualRows + entryRows > cfg.maxLinesShown) ||
            (usedHeight > 0 && usedHeight + entryHeight > m_height))
        {
            break;
        }

        EntryLayout layout;
        layout.line = &line;
        layout.rows = std::move(wrappedRows);
        layout.height = entryRows * lineH;
        visibleEntries.push_back(std::move(layout));
        usedVisualRows += entryRows;
        usedHeight += entryHeight;
    }

    std::reverse(visibleEntries.begin(), visibleEntries.end());

    // Fade all messages together — based on idle time since last new message.
    // When anyone chats, timeSinceLastMessage resets to 0 → full opacity.
    uint8_t globalFadeAlpha = 255;
    if (cfg.fadeMessages && cfg.fadeSeconds > 0) {
        float remaining = std::max(0.0f, (float)cfg.fadeSeconds - timeSinceLastMessage);
        globalFadeAlpha = (uint8_t)std::clamp((int)(255.0f * remaining / (float)cfg.fadeSeconds), 0, 255);
    }

    int y = cfg.topDownStyle ? outerMargin : std::max(outerMargin, m_height - usedHeight - outerMargin);
    for (size_t entryIndex = 0; entryIndex < visibleEntries.size(); ++entryIndex) {
        const EntryLayout& layout = visibleEntries[entryIndex];
        const ChatLine& line = *layout.line;

        uint8_t bgR = cfg.backgroundR;
        uint8_t bgG = cfg.backgroundG;
        uint8_t bgB = cfg.backgroundB;
        uint8_t bgA = cfg.backgroundAlpha;

        if (line.isHighlighted) {
            bgR = (uint8_t)std::min(255, (int)bgR + 40);
            bgG = (uint8_t)std::min(255, (int)bgG + 26);
            bgB = (uint8_t)std::min(255, (int)bgB + 6);
            bgA = (uint8_t)std::min(255, (int)bgA + 24);
        }

        // Fade is global: all messages fade together based on time since last chat message.
        // New message resets the timer → all messages snap back to full opacity.
        uint8_t entryAlpha = globalFadeAlpha;
        bgA = (uint8_t)((uint32_t)bgA * entryAlpha / 255);

        FillRoundRect(outerMargin, y, rowBackgroundWidth, layout.height, 8, bgB, bgG, bgR, bgA);

        int entryTop = y;
        for (size_t rowIndex = 0; rowIndex < layout.rows.size(); ++rowIndex) {
            int contentX = outerMargin + innerPad;
            if (rowIndex == 0) {
                contentX = DrawPrefix(this, line, elapsed, contentX, y, lineH, badgeSize,
                                      cfg, cfg.fontFamily, cfg.fontSize, entryAlpha);
            }

            int availableWidth = outerMargin + rowBackgroundWidth - innerPad - contentX;
            DrawWrappedTokens(this, layout.rows[rowIndex], contentX, y, lineH,
                              cfg, cfg.fontFamily, cfg.fontSize, badgeSize, availableWidth, entryAlpha);

            y += lineH;
        }

        if (entryIndex + 1 < visibleEntries.size())
            y += messageGap;

        if (!cfg.topDownStyle)
            entryTop = y;
    }
}

void RenderBitmap::GetPixels(float alphaScale, std::vector<uint8_t>& out) const
{
    int n = m_width * m_height;
    out.resize(n * 4);

    uint32_t fa = (uint32_t)(alphaScale * 256.0f + 0.5f);
    const uint8_t* src = reinterpret_cast<const uint8_t*>(m_pixels);
    uint8_t* dst = out.data();

    for (int i = 0; i < n; i++) {
        uint8_t b = src[i * 4 + 0];
        uint8_t g = src[i * 4 + 1];
        uint8_t r = src[i * 4 + 2];
        uint8_t a = (uint8_t)((src[i * 4 + 3] * fa) >> 8);

        dst[i * 4 + 0] = (uint8_t)((uint32_t)b * a / 255);
        dst[i * 4 + 1] = (uint8_t)((uint32_t)g * a / 255);
        dst[i * 4 + 2] = (uint8_t)((uint32_t)r * a / 255);
        dst[i * 4 + 3] = a;
    }
}

void RenderBitmap::Clear()
{
    std::memset(m_pixels, 0, m_width * m_height * 4);
}

void RenderBitmap::FillRoundRect(int x, int y, int w, int h, int radius,
                                 uint8_t b, uint8_t g, uint8_t r, uint8_t a)
{
    uint32_t fill = ((uint32_t)a << 24) | ((uint32_t)r << 16) | ((uint32_t)g << 8) | b;

    for (int py = y; py < y + h && py < m_height; py++) {
        for (int px = x; px < x + w && px < m_width; px++) {
            if (px < 0 || py < 0)
                continue;

            int lx = px - x;
            int ly = py - y;

            bool inCorner = false;
            int cx = -1;
            int cy = -1;
            if (lx < radius && ly < radius) {
                cx = radius;
                cy = radius;
                inCorner = true;
            } else if (lx >= w - radius && ly < radius) {
                cx = w - radius;
                cy = radius;
                inCorner = true;
            } else if (lx < radius && ly >= h - radius) {
                cx = radius;
                cy = h - radius;
                inCorner = true;
            } else if (lx >= w - radius && ly >= h - radius) {
                cx = w - radius;
                cy = h - radius;
                inCorner = true;
            }

            if (inCorner) {
                float dx = (float)(lx - cx);
                float dy = (float)(ly - cy);
                if (dx * dx + dy * dy > (float)(radius * radius))
                    continue;
            }

            m_pixels[py * m_width + px] = fill;
        }
    }
}

void RenderBitmap::DrawTextGDI(const std::wstring& text, int x, int y, int w, int h,
                               COLORREF colorRef, const std::wstring& fontFamily,
                               int fontHeight, bool bold, bool italic,
                               uint8_t textAlpha, DWORD dtFlags)
{
    if (text.empty() || w <= 0 || h <= 0)
        return;

    BITMAPINFO bmi = {};
    auto& hdr = bmi.bmiHeader;
    hdr.biSize = sizeof(BITMAPINFOHEADER);
    hdr.biWidth = w;
    hdr.biHeight = -h;
    hdr.biPlanes = 1;
    hdr.biBitCount = 32;
    hdr.biCompression = BI_RGB;

    uint32_t* dibPx = nullptr;
    HDC hdc = CreateCompatibleDC(nullptr);
    if (!hdc)
        return;

    HBITMAP hbm = CreateDIBSection(hdc, &bmi, DIB_RGB_COLORS, (void**)&dibPx, nullptr, 0);
    if (!hbm) {
        DeleteDC(hdc);
        return;
    }

    std::memset(dibPx, 0, w * h * 4);

    HBITMAP oldBm = (HBITMAP)SelectObject(hdc, hbm);
    SetBkMode(hdc, TRANSPARENT);
    // Rasterize coverage in white, then tint during compositing. If we draw directly
    // in black, the old brightness-based alpha extraction sees zero and drops outline/shadow.
    SetTextColor(hdc, RGB(255, 255, 255));

    LOGFONTW lf = {};
    lf.lfHeight = -fontHeight;
    lf.lfWeight = ResolveFontWeight(fontFamily, bold);
    lf.lfItalic = italic ? TRUE : FALSE;
    lf.lfQuality = CLEARTYPE_QUALITY;
    lf.lfCharSet = DEFAULT_CHARSET;

    const wchar_t* faceName = !fontFamily.empty() ? fontFamily.c_str() : L"Segoe UI";
    wcsncpy_s(lf.lfFaceName, faceName, LF_FACESIZE - 1);

    HFONT font = CreateFontIndirectW(&lf);
    HFONT oldFont = (HFONT)SelectObject(hdc, font);

    RECT rect = { 0, 0, w, h };
    DrawTextW(hdc, text.c_str(), -1, &rect, dtFlags);
    GdiFlush();

    uint8_t cr = GetRValue(colorRef);
    uint8_t cg = GetGValue(colorRef);
    uint8_t cb = GetBValue(colorRef);

    for (int py = 0; py < h; py++) {
        for (int px = 0; px < w; px++) {
            uint32_t src = dibPx[py * w + px];
            uint8_t sb = (uint8_t)(src & 0xFF);
            uint8_t sg = (uint8_t)((src >> 8) & 0xFF);
            uint8_t sr = (uint8_t)((src >> 16) & 0xFF);

            uint8_t bright = (uint8_t)std::max({ (int)sr, (int)sg, (int)sb });
            if (bright == 0)
                continue;

            uint8_t a = (uint8_t)((uint32_t)bright * textAlpha / 255);
            if (a == 0)
                continue;

            int dstX = x + px;
            int dstY = y + py;
            if (dstX < 0 || dstX >= m_width || dstY < 0 || dstY >= m_height)
                continue;

            uint32_t& dst = m_pixels[dstY * m_width + dstX];
            uint8_t da = (uint8_t)((dst >> 24) & 0xFF);
            uint8_t dr = (uint8_t)((dst >> 16) & 0xFF);
            uint8_t dg = (uint8_t)((dst >> 8) & 0xFF);
            uint8_t db = (uint8_t)(dst & 0xFF);

            uint32_t oa = a + (uint32_t)da * (255 - a) / 255;
            if (oa == 0)
                continue;

            uint32_t or_ = ((uint32_t)cr * a + (uint32_t)dr * da * (255 - a) / 255) / oa;
            uint32_t og = ((uint32_t)cg * a + (uint32_t)dg * da * (255 - a) / 255) / oa;
            uint32_t ob = ((uint32_t)cb * a + (uint32_t)db * da * (255 - a) / 255) / oa;

            dst = (oa << 24) | (or_ << 16) | (og << 8) | ob;
        }
    }

    SelectObject(hdc, oldFont);
    SelectObject(hdc, oldBm);
    DeleteObject(font);
    DeleteObject(hbm);
    DeleteDC(hdc);
}

int RenderBitmap::MeasureTextWidth(const std::wstring& text, const std::wstring& fontFamily,
                                   int fontSize, bool bold, bool italic)
{
    if (text.empty())
        return 0;

    HDC hdc = CreateCompatibleDC(nullptr);
    if (!hdc)
        return fontSize * (int)text.size() / 2;

    LOGFONTW lf = {};
    lf.lfHeight = -fontSize;
    lf.lfWeight = ResolveFontWeight(fontFamily, bold);
    lf.lfItalic = italic ? TRUE : FALSE;
    lf.lfQuality = CLEARTYPE_QUALITY;
    lf.lfCharSet = DEFAULT_CHARSET;

    const wchar_t* faceName = !fontFamily.empty() ? fontFamily.c_str() : L"Segoe UI";
    wcsncpy_s(lf.lfFaceName, faceName, LF_FACESIZE - 1);

    HFONT font = CreateFontIndirectW(&lf);
    HFONT oldFont = (HFONT)SelectObject(hdc, font);

    SIZE sz = {};
    GetTextExtentPoint32W(hdc, text.c_str(), (int)text.size(), &sz);

    SelectObject(hdc, oldFont);
    DeleteObject(font);
    DeleteDC(hdc);
    return sz.cx;
}

void RenderBitmap::BlitImagePublic(const uint8_t* srcBgra, int srcW, int srcH,
                                   int dstX, int dstY, int dstW, int dstH, uint8_t opacity)
{
    if (!srcBgra || srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
        return;

    for (int dy = 0; dy < dstH; dy++) {
        int py = dstY + dy;
        if (py < 0 || py >= m_height)
            continue;
        int sy = dy * srcH / dstH;
        if (sy >= srcH)
            sy = srcH - 1;

        for (int dx = 0; dx < dstW; dx++) {
            int px = dstX + dx;
            if (px < 0 || px >= m_width)
                continue;
            int sx = dx * srcW / dstW;
            if (sx >= srcW)
                sx = srcW - 1;

            const uint8_t* src = srcBgra + (sy * srcW + sx) * 4;
            uint8_t sb = src[0];
            uint8_t sg = src[1];
            uint8_t sr = src[2];
            uint8_t saRaw = src[3];

            uint32_t sa = ((uint32_t)saRaw * opacity) / 255;
            if (sa == 0)
                continue;

            uint32_t scaledB = (uint32_t)sb * opacity / 255;
            uint32_t scaledG = (uint32_t)sg * opacity / 255;
            uint32_t scaledR = (uint32_t)sr * opacity / 255;

            uint32_t& dst = m_pixels[py * m_width + px];
            uint8_t da = (uint8_t)((dst >> 24) & 0xFF);
            uint8_t dr = (uint8_t)((dst >> 16) & 0xFF);
            uint8_t dg = (uint8_t)((dst >> 8) & 0xFF);
            uint8_t db = (uint8_t)(dst & 0xFF);

            uint32_t inv = 255 - sa;
            uint32_t ob = scaledB + (uint32_t)db * inv / 255;
            uint32_t og = scaledG + (uint32_t)dg * inv / 255;
            uint32_t or_ = scaledR + (uint32_t)dr * inv / 255;
            uint32_t oa = sa + (uint32_t)da * inv / 255;

            dst = (oa << 24) | (or_ << 16) | (og << 8) | ob;
        }
    }
}

void RenderBitmap::BlitRawClippedPublic(const uint8_t* srcBgra, int srcW, int srcH,
                                        int dstX, int dstY,
                                        int clipX, int clipY, int clipW, int clipH,
                                        uint8_t opacity)
{
    if (!srcBgra || srcW <= 0 || srcH <= 0 || clipW <= 0 || clipH <= 0) return;
    int clipR = clipX + clipW, clipB = clipY + clipH;

    for (int sy = 0; sy < srcH; sy++) {
        int py = dstY + sy;
        if (py < 0 || py >= m_height || py < clipY || py >= clipB) continue;
        const uint8_t* srcRow = srcBgra + sy * srcW * 4;
        for (int sx = 0; sx < srcW; sx++) {
            int px = dstX + sx;
            if (px < 0 || px >= m_width || px < clipX || px >= clipR) continue;

            const uint8_t* src = srcRow + sx * 4;
            uint8_t sb = src[0], sg = src[1], sr = src[2], saRaw = src[3];
            uint32_t sa = ((uint32_t)saRaw * opacity) / 255;
            if (sa == 0) continue;

            uint32_t scaledB = (uint32_t)sb * opacity / 255;
            uint32_t scaledG = (uint32_t)sg * opacity / 255;
            uint32_t scaledR = (uint32_t)sr * opacity / 255;

            uint32_t& dst = m_pixels[py * m_width + px];
            uint8_t da = (uint8_t)((dst >> 24) & 0xFF);
            uint8_t dr = (uint8_t)((dst >> 16) & 0xFF);
            uint8_t dg = (uint8_t)((dst >>  8) & 0xFF);
            uint8_t db = (uint8_t)( dst         & 0xFF);

            uint32_t inv = 255 - sa;
            uint32_t ob = scaledB + (uint32_t)db * inv / 255;
            uint32_t og = scaledG + (uint32_t)dg * inv / 255;
            uint32_t or_ = scaledR + (uint32_t)dr * inv / 255;
            uint32_t oa = sa + (uint32_t)da * inv / 255;
            dst = (oa << 24) | (or_ << 16) | (og << 8) | ob;
        }
    }
}

void RenderBitmap::RotateBlitPublic(const uint8_t* srcBgra, int srcW, int srcH,
                                     float dstCx, float dstCy, float rotDeg,
                                     float scaleX, float scaleY, uint8_t opacity)
{
    if (!srcBgra || srcW <= 0 || srcH <= 0 || scaleX <= 0.f || scaleY <= 0.f) return;

    const float PI = 3.14159265f;
    float rad  = rotDeg * PI / 180.0f;
    float cosA = cosf(rad);
    float sinA = sinf(rad);
    float invCosA =  cosA;
    float invSinA = -sinA; // inverse rotation

    float scaledW = srcW * scaleX;
    float scaledH = srcH * scaleY;
    float hw = scaledW / 2.0f;
    float hh = scaledH / 2.0f;

    // Compute axis-aligned bounding box of the rotated scaled rectangle
    float absCosHw = fabsf(cosA) * hw, absSinHw = fabsf(sinA) * hw;
    float absCosHh = fabsf(cosA) * hh, absSinHh = fabsf(sinA) * hh;
    float bboxHW = absCosHw + absSinHh;
    float bboxHH = absSinHw + absCosHh;

    int x0 = std::max(0,         (int)(dstCx - bboxHW - 1));
    int y0 = std::max(0,         (int)(dstCy - bboxHH - 1));
    int x1 = std::min(m_width-1, (int)(dstCx + bboxHW + 1));
    int y1 = std::min(m_height-1,(int)(dstCy + bboxHH + 1));

    for (int py = y0; py <= y1; py++) {
        for (int px = x0; px <= x1; px++) {
            // Translate to center-origin, apply inverse rotation
            float dx = (float)px - dstCx;
            float dy = (float)py - dstCy;
            float lx = (invCosA * dx - invSinA * dy) / scaleX + (float)srcW / 2.0f;
            float ly = (invSinA * dx + invCosA * dy) / scaleY + (float)srcH / 2.0f;

            if (lx < 0.f || lx >= (float)srcW || ly < 0.f || ly >= (float)srcH) continue;

            // Bilinear sample
            int   sx0 = (int)lx,              sy0 = (int)ly;
            int   sx1 = std::min(sx0+1, srcW-1), sy1 = std::min(sy0+1, srcH-1);
            float tx  = lx - (float)sx0,      ty  = ly - (float)sy0;

            auto sample = [&](int sx, int sy) -> const uint8_t* {
                return srcBgra + (sy * srcW + sx) * 4;
            };
            const uint8_t* p00 = sample(sx0, sy0);
            const uint8_t* p10 = sample(sx1, sy0);
            const uint8_t* p01 = sample(sx0, sy1);
            const uint8_t* p11 = sample(sx1, sy1);

            auto lerp4 = [&](int ch) -> uint8_t {
                float v = (p00[ch] * (1.f-tx) + p10[ch] * tx) * (1.f-ty)
                        + (p01[ch] * (1.f-tx) + p11[ch] * tx) * ty;
                return (uint8_t)std::clamp((int)v, 0, 255);
            };

            uint8_t sb = lerp4(0), sg = lerp4(1), sr = lerp4(2), saRaw = lerp4(3);
            uint32_t sa = ((uint32_t)saRaw * opacity) / 255;
            if (sa == 0) continue;

            // src pixels are premultiplied — scale RGB by opacity/255
            uint32_t scaledB = (uint32_t)sb * opacity / 255;
            uint32_t scaledG = (uint32_t)sg * opacity / 255;
            uint32_t scaledR = (uint32_t)sr * opacity / 255;

            uint32_t& dst = m_pixels[py * m_width + px];
            uint8_t da = (uint8_t)((dst >> 24) & 0xFF);
            uint8_t dr = (uint8_t)((dst >> 16) & 0xFF);
            uint8_t dg = (uint8_t)((dst >>  8) & 0xFF);
            uint8_t db = (uint8_t)( dst         & 0xFF);

            uint32_t inv = 255 - sa;
            uint32_t ob  = scaledB + (uint32_t)db * inv / 255;
            uint32_t og  = scaledG + (uint32_t)dg * inv / 255;
            uint32_t or_ = scaledR + (uint32_t)dr * inv / 255;
            uint32_t oa  = sa      + (uint32_t)da * inv / 255;

            dst = (oa << 24) | (or_ << 16) | (og << 8) | ob;
        }
    }
}

std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty())
        return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    if (n <= 0)
        return {};
    std::wstring out(n - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, out.data(), n);
    return out;
}

COLORREF HexToColorRef(const std::wstring& hex)
{
    if (hex.size() < 7 || hex[0] != L'#')
        return RGB(255, 255, 255);

    auto h = [](wchar_t c) -> int {
        if (c >= L'0' && c <= L'9')
            return c - L'0';
        if (c >= L'A' && c <= L'F')
            return c - L'A' + 10;
        if (c >= L'a' && c <= L'f')
            return c - L'a' + 10;
        return 0;
    };

    int r = (h(hex[1]) << 4) | h(hex[2]);
    int g = (h(hex[3]) << 4) | h(hex[4]);
    int b = (h(hex[5]) << 4) | h(hex[6]);
    return RGB(r, g, b);
}
