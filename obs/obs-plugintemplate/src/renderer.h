#pragma once
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>
#include <cstdint>

class RenderBitmap
{
public:
    RenderBitmap(int width, int height);
    ~RenderBitmap();

    void DrawAlertText(const std::wstring& username, const std::wstring& message);

    struct EmotePos {
        int         start;
        int         end;
        std::string filePath;
    };

    struct ChatLine {
        std::wstring platform;
        std::vector<std::wstring> platformIcons;
        std::wstring timestamp;
        std::wstring username;
        std::wstring text;
        std::wstring color;
        bool         isBroadcaster = false;
        bool         isModerator   = false;
        bool         isSubscriber  = false;
        bool         isVip         = false;
        bool         isHighlighted = false;
        int          bitsAmount    = 0;
        int          subMonths     = 0;
        std::vector<std::string> badgePaths;
        std::vector<EmotePos>    emotes;
        float        ageSeconds    = 0.f;
    };

    struct ChatRenderSettings {
        uint8_t      backgroundR     = 0;
        uint8_t      backgroundG     = 0;
        uint8_t      backgroundB     = 0;
        uint8_t      backgroundAlpha = 160;
        uint8_t      textR           = 225;
        uint8_t      textG           = 225;
        uint8_t      textB           = 225;
        uint8_t      bitsR           = 255;
        uint8_t      bitsG           = 215;
        uint8_t      bitsB           = 0;
        std::wstring fontFamily      = L"Segoe UI";
        int          fontSize        = 20;
        int          fontWeight      = 700;
        int          textAlign       = 0; // 0=left,1=center,2=right
        int          textShadow      = 0;
        int          outlineSize     = 0;
        int          margin          = 8;
        int          lineSpacing     = 6;
        int          messagePadding  = 8;
        int          maxLinesShown   = 20;
        bool         showChatMessages = true;
        bool         topDownStyle    = false;
        bool         showPlatformIcon = true;
        bool         showTimestamps = false;
        bool         badgesAfterUsername = false;
        int          displayNameColorMode = 0; // 0=user color, 1=platform color, 2=text color
        bool         disappearMessages = false;
        int          disappearAfterSeconds = 360;
        bool         fadeMessages = false;
        int          fadeSeconds = 30;
        int          platformFilter = 0; // 0=all, 1=twitch, 2=kick, 3=youtube
    };

    void DrawChatMessages(const std::vector<ChatLine>& lines, float elapsed,
                          float timeSinceLastMessage,
                          const ChatRenderSettings& settings);

    void GetPixels(float alphaScale, std::vector<uint8_t>& out) const;

    int Width()  const { return m_width; }
    int Height() const { return m_height; }

    void ClearPublic() { Clear(); }
    void FillRoundRectPublic(int x, int y, int w, int h, int radius,
                             uint8_t b, uint8_t g, uint8_t r, uint8_t a)
    {
        FillRoundRect(x, y, w, h, radius, b, g, r, a);
    }

    void DrawTextGDIPublic(const std::wstring& text, int x, int y, int w, int h,
                           COLORREF col, const std::wstring& fontFamily, int sz,
                           bool bold, bool italic, uint8_t alpha, DWORD flags)
    {
        DrawTextGDI(text, x, y, w, h, col, fontFamily, sz, bold, italic, alpha, flags);
    }

    int MeasureTextWidthPublic(const std::wstring& text, const std::wstring& fontFamily,
                               int fontSize, bool bold, bool italic)
    {
        return MeasureTextWidth(text, fontFamily, fontSize, bold, italic);
    }

    void BlitImagePublic(const uint8_t* srcBgra, int srcW, int srcH,
                         int dstX, int dstY, int dstW, int dstH, uint8_t opacity);

    // 1:1 pixel blit of srcBgra (srcW x srcH) at (dstX, dstY), clipped to [clipX, clipX+clipW) x [clipY, clipY+clipH).
    void BlitRawClippedPublic(const uint8_t* srcBgra, int srcW, int srcH,
                              int dstX, int dstY,
                              int clipX, int clipY, int clipW, int clipH,
                              uint8_t opacity);

    // Blit srcBgra rotated by rotationDeg degrees around the destination center (dstCx, dstCy).
    // srcW/srcH are the source pixel dimensions; scaleX/scaleY scale the element before rotation.
    void RotateBlitPublic(const uint8_t* srcBgra, int srcW, int srcH,
                          float dstCx, float dstCy, float rotationDeg,
                          float scaleX, float scaleY, uint8_t opacity);

private:
    int       m_width, m_height;
    uint32_t* m_pixels = nullptr;

    void Clear();
    void FillRoundRect(int x, int y, int w, int h, int radius,
                       uint8_t b, uint8_t g, uint8_t r, uint8_t a);
    void DrawTextGDI(const std::wstring& text, int x, int y, int w, int h,
                     COLORREF colorRef, const std::wstring& fontFamily, int fontHeight,
                     bool bold, bool italic, uint8_t textAlpha, DWORD dtFlags);
    int MeasureTextWidth(const std::wstring& text, const std::wstring& fontFamily,
                         int fontSize, bool bold, bool italic);
};

std::wstring Utf8ToWide(const std::string& s);
COLORREF HexToColorRef(const std::wstring& hex);
