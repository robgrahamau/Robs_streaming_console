#define NOMINMAX
#include <obs-module.h>
#include "chat_source.h"
#include "renderer.h"
#include <algorithm>
#include <deque>
#include <mutex>
#include <vector>
#include <string>
#include <cstring>
#include <chrono>
#include <unordered_map>
#include "pipe_client.h"

PipeClient* steaming_get_pipe();

// Wire format v5, matches ChatPayload.Serialize() in C#:
//   [2+N] platform        primary platform for routing/filtering
//   [2+N] platformIcons   display platforms joined with "|" (for example "Twitch|Kick")
//   [2+N] username  [2+N] message  [2+N] color  [2+N] timestamp
//   [1]   flags      bit0=broadcaster bit1=mod bit2=sub bit3=vip bit4=highlighted bit5=hasBits
//   [4]   bitsAmount [2] subMonths
//   [1]   badgeCount
//   each badge: [2+N] cachedFilePath
//   [1]   emoteCount
//   each emote: [2] startChar [2] endChar [2+N] cachedFilePath

static const int MAX_LINES = 30;

static const char* PROP_WIDTH = "width";
static const char* PROP_HEIGHT = "height";
static const char* PROP_MARGIN = "margin";
static const char* PROP_BG_COLOR = "background_color";
static const char* PROP_BG_OPACITY = "background_opacity";
static const char* PROP_TEXT_COLOR = "text_color";
static const char* PROP_BITS_COLOR = "bits_color";
static const char* PROP_FONT_FAMILY = "font_family";
static const char* PROP_FONT_SIZE = "font_size";
static const char* PROP_FONT_WEIGHT = "font_weight";
static const char* PROP_TEXT_ALIGN = "text_align";
static const char* PROP_TEXT_SHADOW = "text_shadow";
static const char* PROP_OUTLINE_SIZE = "outline_size";
static const char* PROP_LINE_SPACING = "line_spacing";
static const char* PROP_MESSAGE_PADDING = "message_padding";
static const char* PROP_MAX_LINES = "max_lines_shown";
static const char* PROP_SHOW_CHAT_MESSAGES = "show_chat_messages";
static const char* PROP_MESSAGE_STYLE = "message_style";
static const char* PROP_SHOW_PLATFORM_ICON = "show_platform_icon";
static const char* PROP_SHOW_TIMESTAMPS = "show_timestamps";
static const char* PROP_BADGE_PLACEMENT = "badge_placement";
static const char* PROP_NAME_COLOR_MODE = "display_name_color_mode";
static const char* PROP_DISAPPEAR_MESSAGES = "disappear_messages";
static const char* PROP_DISAPPEAR_AFTER = "disappear_after_seconds";
static const char* PROP_FADE_MESSAGES = "fade_messages";
static const char* PROP_FADE_SECONDS = "fade_seconds";
static const char* PROP_PLATFORM_FILTER = "platform_filter";

struct ChatEntry {
    std::wstring platform;
    std::vector<std::wstring> platformIcons;
    std::wstring timestamp;
    std::wstring username;
    std::wstring text;
    std::wstring color;
    bool isBroadcaster = false;
    bool isModerator = false;
    bool isSubscriber = false;
    bool isVip = false;
    bool isHighlighted = false;
    int bitsAmount = 0;
    int subMonths = 0;
    std::vector<std::string> badgePaths;
    std::vector<RenderBitmap::EmotePos> emotes;
    std::chrono::steady_clock::time_point receivedAt;
};

static std::deque<ChatEntry> s_lines;
static std::mutex s_linesMutex;
static std::chrono::steady_clock::time_point s_lastMessageTime = std::chrono::steady_clock::now();

struct ChatData {
    gs_texture_t* texture = nullptr;
    RenderBitmap* bitmap = nullptr;
    bool dirty = true;
    std::mutex mutex;
    bool pendingSettingsDirty = false;
    RenderBitmap::ChatRenderSettings pendingRenderSettings;
    int lastLineCount = -1;
    float elapsed = 0.f;
    float lastRenderElapsed = 0.f;
    int chatW = 400;
    int chatH = 600;
    std::string sourceName;
    RenderBitmap::ChatRenderSettings renderSettings;
    // Reused pixel buffer — grows as needed, never shrinks
    std::vector<uint8_t> pixels;
    // Cached texture dimensions — texture updated in-place when size unchanged
    int texW = 0;
    int texH = 0;
};

static std::vector<ChatData*> s_instances;
static std::mutex s_instancesMutex;
struct ChatSourceSettings {
    int chatW = 400;
    int chatH = 600;
    RenderBitmap::ChatRenderSettings renderSettings;
};
static std::unordered_map<std::string, ChatSourceSettings> s_sourceSettings;

static bool readStr(const std::vector<uint8_t>& payload, size_t& off, std::string& out)
{
    if (off + 2 > payload.size())
        return false;
    uint16_t len = 0;
    std::memcpy(&len, payload.data() + off, 2);
    off += 2;
    if (off + len > payload.size())
        return false;
    out.assign(reinterpret_cast<const char*>(payload.data() + off), len);
    off += len;
    return true;
}

static bool readStr(const std::vector<uint8_t>& payload, size_t& off, std::wstring& out)
{
    std::string tmp;
    if (!readStr(payload, off, tmp))
        return false;
    out = Utf8ToWide(tmp);
    return true;
}

static bool readU16(const std::vector<uint8_t>& payload, size_t& off, uint16_t& out)
{
    if (off + 2 > payload.size())
        return false;
    std::memcpy(&out, payload.data() + off, 2);
    off += 2;
    return true;
}

static bool readU32(const std::vector<uint8_t>& payload, size_t& off, uint32_t& out)
{
    if (off + 4 > payload.size())
        return false;
    std::memcpy(&out, payload.data() + off, 4);
    off += 4;
    return true;
}

static bool readI32(const std::vector<uint8_t>& payload, size_t& off, int32_t& out)
{
    if (off + 4 > payload.size())
        return false;
    std::memcpy(&out, payload.data() + off, 4);
    off += 4;
    return true;
}

static bool readBool(const std::vector<uint8_t>& payload, size_t& off, bool& out)
{
    if (off + 1 > payload.size())
        return false;
    out = payload[off++] != 0;
    return true;
}

static std::string wide_to_utf8(const std::wstring& value)
{
    if (value.empty())
        return {};
    int needed = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    if (needed <= 0)
        return {};
    std::string out(needed, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), out.data(), needed, nullptr, nullptr);
    return out;
}

static void set_rgb_from_hex(const std::wstring& hex, uint8_t& r, uint8_t& g, uint8_t& b)
{
    auto color = HexToColorRef(hex);
    r = GetRValue(color);
    g = GetGValue(color);
    b = GetBValue(color);
}

static void parse_platform_icons(const std::wstring& value, const std::wstring& fallback, std::vector<std::wstring>& out)
{
    out.clear();
    std::wstring source = value.empty() ? fallback : value;
    size_t start = 0;
    while (start <= source.size()) {
        size_t sep = source.find(L'|', start);
        std::wstring part = source.substr(start, sep == std::wstring::npos ? std::wstring::npos : sep - start);
        if (!part.empty())
            out.push_back(std::move(part));
        if (sep == std::wstring::npos)
            break;
        start = sep + 1;
    }
    if (out.empty() && !fallback.empty())
        out.push_back(fallback);
}

static bool parse_chat(const std::vector<uint8_t>& payload, ChatEntry& out)
{
    size_t off = 0;
    std::wstring platformIconsText;
    if (!readStr(payload, off, out.platform))
        return false;
    if (!readStr(payload, off, platformIconsText))
        return false;
    if (!readStr(payload, off, out.username))
        return false;
    if (!readStr(payload, off, out.text))
        return false;
    if (!readStr(payload, off, out.color))
        return false;
    if (!readStr(payload, off, out.timestamp))
        return false;

    if (out.color.empty())
        out.color = L"#FFFFFF";
    parse_platform_icons(platformIconsText, out.platform, out.platformIcons);

    if (off < payload.size()) {
        uint8_t flags = payload[off++];
        out.isBroadcaster = (flags & 0x01) != 0;
        out.isModerator = (flags & 0x02) != 0;
        out.isSubscriber = (flags & 0x04) != 0;
        out.isVip = (flags & 0x08) != 0;
        out.isHighlighted = (flags & 0x10) != 0;

        uint32_t bits = 0;
        readU32(payload, off, bits);
        out.bitsAmount = (int)bits;

        uint16_t subMonths = 0;
        readU16(payload, off, subMonths);
        out.subMonths = (int)subMonths;

        if (off < payload.size()) {
            uint8_t badgeCount = payload[off++];
            for (int i = 0; i < (int)badgeCount; i++) {
                std::string path;
                if (!readStr(payload, off, path))
                    break;
                out.badgePaths.push_back(std::move(path));
            }
        }

        if (off < payload.size()) {
            uint8_t emoteCount = payload[off++];
            for (int i = 0; i < (int)emoteCount; i++) {
                uint16_t start = 0;
                uint16_t end = 0;
                if (!readU16(payload, off, start))
                    break;
                if (!readU16(payload, off, end))
                    break;
                std::string path;
                if (!readStr(payload, off, path))
                    break;
                out.emotes.push_back({ (int)start, (int)end, path });
            }
        }
    }

    return true;
}

static RenderBitmap::ChatRenderSettings read_render_settings(obs_data_t* settings)
{
    RenderBitmap::ChatRenderSettings out;

    uint32_t bgColor = (uint32_t)obs_data_get_int(settings, PROP_BG_COLOR);
    out.backgroundR = (uint8_t)(bgColor & 0xFF);
    out.backgroundG = (uint8_t)((bgColor >> 8) & 0xFF);
    out.backgroundB = (uint8_t)((bgColor >> 16) & 0xFF);
    out.backgroundAlpha = (uint8_t)std::clamp((int)obs_data_get_int(settings, PROP_BG_OPACITY), 0, 255);

    uint32_t textColor = (uint32_t)obs_data_get_int(settings, PROP_TEXT_COLOR);
    out.textR = (uint8_t)(textColor & 0xFF);
    out.textG = (uint8_t)((textColor >> 8) & 0xFF);
    out.textB = (uint8_t)((textColor >> 16) & 0xFF);

    uint32_t bitsColor = (uint32_t)obs_data_get_int(settings, PROP_BITS_COLOR);
    out.bitsR = (uint8_t)(bitsColor & 0xFF);
    out.bitsG = (uint8_t)((bitsColor >> 8) & 0xFF);
    out.bitsB = (uint8_t)((bitsColor >> 16) & 0xFF);

    out.fontFamily = Utf8ToWide(obs_data_get_string(settings, PROP_FONT_FAMILY));
    if (out.fontFamily.empty())
        out.fontFamily = L"Segoe UI";

    out.margin = std::clamp((int)obs_data_get_int(settings, PROP_MARGIN), 0, 128);
    out.fontSize = std::clamp((int)obs_data_get_int(settings, PROP_FONT_SIZE), 10, 96);
    out.fontWeight = std::clamp((int)obs_data_get_int(settings, PROP_FONT_WEIGHT), 100, 900);
    const char* textAlign = obs_data_get_string(settings, PROP_TEXT_ALIGN);
    out.textAlign = std::strcmp(textAlign, "Center") == 0 ? 1 : (std::strcmp(textAlign, "Right") == 0 ? 2 : 0);
    out.textShadow = std::clamp((int)obs_data_get_int(settings, PROP_TEXT_SHADOW), 0, 32);
    out.outlineSize = std::clamp((int)obs_data_get_int(settings, PROP_OUTLINE_SIZE), 0, 8);
    out.lineSpacing = std::clamp((int)obs_data_get_int(settings, PROP_LINE_SPACING), 0, 48);
    out.messagePadding = std::clamp((int)obs_data_get_int(settings, PROP_MESSAGE_PADDING), 2, 48);
    out.maxLinesShown = std::clamp((int)obs_data_get_int(settings, PROP_MAX_LINES), 1, MAX_LINES);
    out.showChatMessages = obs_data_get_bool(settings, PROP_SHOW_CHAT_MESSAGES);
    out.topDownStyle = std::strcmp(obs_data_get_string(settings, PROP_MESSAGE_STYLE), "TopDown") == 0;
    out.showPlatformIcon = obs_data_get_bool(settings, PROP_SHOW_PLATFORM_ICON);
    out.showTimestamps = obs_data_get_bool(settings, PROP_SHOW_TIMESTAMPS);
    out.badgesAfterUsername = std::strcmp(obs_data_get_string(settings, PROP_BADGE_PLACEMENT), "AfterUsername") == 0;

    const char* colorMode = obs_data_get_string(settings, PROP_NAME_COLOR_MODE);
    out.displayNameColorMode = std::strcmp(colorMode, "PlatformColor") == 0 ? 1 :
                               std::strcmp(colorMode, "BaseTextColor") == 0 ? 2 : 0;

    out.disappearMessages = obs_data_get_bool(settings, PROP_DISAPPEAR_MESSAGES);
    out.disappearAfterSeconds = std::clamp((int)obs_data_get_int(settings, PROP_DISAPPEAR_AFTER), 1, 3600);
    out.fadeMessages = obs_data_get_bool(settings, PROP_FADE_MESSAGES);
    out.fadeSeconds = std::clamp((int)obs_data_get_int(settings, PROP_FADE_SECONDS), 1, 600);

    const char* platformFilter = obs_data_get_string(settings, PROP_PLATFORM_FILTER);
    out.platformFilter = std::strcmp(platformFilter, "Twitch") == 0 ? 1 :
                         std::strcmp(platformFilter, "Kick") == 0 ? 2 :
                         std::strcmp(platformFilter, "YouTube") == 0 ? 3 : 0;
    return out;
}

static const char* chat_get_name(void*)
{
    return "Streaming Chat Overlay";
}

static obs_properties_t* chat_get_properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_int(props, PROP_WIDTH, "Width", 100, 3840, 10);
    obs_properties_add_int(props, PROP_HEIGHT, "Height", 100, 2160, 10);
    obs_properties_add_int(props, PROP_MARGIN, "Margin", 0, 128, 1);
    obs_properties_add_color(props, PROP_BG_COLOR, "Background Color");
    obs_properties_add_int_slider(props, PROP_BG_OPACITY, "Background Opacity", 0, 255, 1);
    obs_properties_add_color(props, PROP_TEXT_COLOR, "Message Text Color");
    obs_properties_add_color(props, PROP_BITS_COLOR, "Bits Text Color");
    obs_properties_add_text(props, PROP_FONT_FAMILY, "Font Family", OBS_TEXT_DEFAULT);
    obs_properties_add_int(props, PROP_FONT_SIZE, "Font Size", 10, 96, 1);
    obs_properties_add_int(props, PROP_FONT_WEIGHT, "Font Weight", 100, 900, 50);
    auto* textAlign = obs_properties_add_list(props, PROP_TEXT_ALIGN, "Text Align",
                                              OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    obs_property_list_add_string(textAlign, "Left", "Left");
    obs_property_list_add_string(textAlign, "Center", "Center");
    obs_property_list_add_string(textAlign, "Right", "Right");
    obs_properties_add_int(props, PROP_TEXT_SHADOW, "Text Shadow", 0, 32, 1);
    obs_properties_add_int(props, PROP_OUTLINE_SIZE, "Outline Size", 0, 8, 1);
    obs_properties_add_int(props, PROP_LINE_SPACING, "Line Spacing", 0, 48, 1);
    obs_properties_add_int(props, PROP_MESSAGE_PADDING, "Message Padding", 2, 48, 1);
    obs_properties_add_int(props, PROP_MAX_LINES, "Max Lines Shown", 1, MAX_LINES, 1);
    obs_properties_add_bool(props, PROP_SHOW_CHAT_MESSAGES, "Show Chat Messages");
    obs_properties_add_bool(props, PROP_SHOW_TIMESTAMPS, "Show Timestamps");
    auto* messageStyle = obs_properties_add_list(props, PROP_MESSAGE_STYLE, "Message Style",
                                                 OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    obs_property_list_add_string(messageStyle, "Text (Bottom-up)", "BottomUp");
    obs_property_list_add_string(messageStyle, "Text (Top-down)", "TopDown");
    obs_properties_add_bool(props, PROP_SHOW_PLATFORM_ICON, "Show Platform Icon");
    auto* badgePlacement = obs_properties_add_list(props, PROP_BADGE_PLACEMENT, "Badge Placement",
                                                   OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    obs_property_list_add_string(badgePlacement, "Before Username", "BeforeUsername");
    obs_property_list_add_string(badgePlacement, "After Username", "AfterUsername");
    auto* nameColorMode = obs_properties_add_list(props, PROP_NAME_COLOR_MODE, "Display Name Color",
                                                  OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    obs_property_list_add_string(nameColorMode, "User Color", "UserColor");
    obs_property_list_add_string(nameColorMode, "Platform Color", "PlatformColor");
    obs_property_list_add_string(nameColorMode, "Base Text Color", "BaseTextColor");
    obs_properties_add_bool(props, PROP_DISAPPEAR_MESSAGES, "Make Messages Disappear");
    obs_properties_add_int(props, PROP_DISAPPEAR_AFTER, "Disappear After (seconds)", 1, 3600, 1);
    obs_properties_add_bool(props, PROP_FADE_MESSAGES, "Fade Messages");
    obs_properties_add_int(props, PROP_FADE_SECONDS, "Fade Duration (seconds)", 1, 600, 1);
    auto* platformFilter = obs_properties_add_list(props, PROP_PLATFORM_FILTER, "Use From",
                                                   OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    obs_property_list_add_string(platformFilter, "All", "All");
    obs_property_list_add_string(platformFilter, "Twitch", "Twitch");
    obs_property_list_add_string(platformFilter, "Kick", "Kick");
    obs_property_list_add_string(platformFilter, "YouTube", "YouTube");
    return props;
}

static void chat_get_defaults(obs_data_t* settings)
{
    obs_data_set_default_int(settings, PROP_WIDTH, 400);
    obs_data_set_default_int(settings, PROP_HEIGHT, 600);
    obs_data_set_default_int(settings, PROP_MARGIN, 8);
    obs_data_set_default_int(settings, PROP_BG_COLOR, 0x101010);
    obs_data_set_default_int(settings, PROP_BG_OPACITY, 180);
    obs_data_set_default_int(settings, PROP_TEXT_COLOR, 0xE1E1E1);
    obs_data_set_default_int(settings, PROP_BITS_COLOR, 0x00D7FF);
    obs_data_set_default_string(settings, PROP_FONT_FAMILY, "Segoe UI");
    obs_data_set_default_int(settings, PROP_FONT_SIZE, 20);
    obs_data_set_default_int(settings, PROP_FONT_WEIGHT, 700);
    obs_data_set_default_string(settings, PROP_TEXT_ALIGN, "Left");
    obs_data_set_default_int(settings, PROP_TEXT_SHADOW, 0);
    obs_data_set_default_int(settings, PROP_OUTLINE_SIZE, 0);
    obs_data_set_default_int(settings, PROP_LINE_SPACING, 6);
    obs_data_set_default_int(settings, PROP_MESSAGE_PADDING, 8);
    obs_data_set_default_int(settings, PROP_MAX_LINES, 20);
    obs_data_set_default_bool(settings, PROP_SHOW_CHAT_MESSAGES, true);
    obs_data_set_default_bool(settings, PROP_SHOW_TIMESTAMPS, false);
    obs_data_set_default_string(settings, PROP_MESSAGE_STYLE, "BottomUp");
    obs_data_set_default_bool(settings, PROP_SHOW_PLATFORM_ICON, true);
    obs_data_set_default_string(settings, PROP_BADGE_PLACEMENT, "BeforeUsername");
    obs_data_set_default_string(settings, PROP_NAME_COLOR_MODE, "UserColor");
    obs_data_set_default_bool(settings, PROP_DISAPPEAR_MESSAGES, false);
    obs_data_set_default_int(settings, PROP_DISAPPEAR_AFTER, 360);
    obs_data_set_default_bool(settings, PROP_FADE_MESSAGES, false);
    obs_data_set_default_int(settings, PROP_FADE_SECONDS, 30);
    obs_data_set_default_string(settings, PROP_PLATFORM_FILTER, "All");
}

static void recreate_bitmap(ChatData* data)
{
    obs_enter_graphics();
    if (data->texture) {
        gs_texture_destroy(data->texture);
        data->texture = nullptr;
    }
    obs_leave_graphics();

    delete data->bitmap;
    data->bitmap = new RenderBitmap(data->chatW, data->chatH);
    data->dirty = true;
}

static void chat_apply_pending_settings(ChatData* data)
{
    std::lock_guard<std::mutex> lock(data->mutex);
    if (!data->pendingSettingsDirty)
        return;
    data->renderSettings = data->pendingRenderSettings;
    data->pendingSettingsDirty = false;
    data->dirty = true;
}

struct ChatSourceInfo {
    std::string name;
    int chatW = 400;
    int chatH = 600;
};

// Wire format: [2]count, each entry: [2+N]name [4]width [4]height (int32 LE)
static void collect_source_info(std::vector<ChatSourceInfo>& out)
{
    std::lock_guard<std::mutex> lock(s_instancesMutex);
    for (const ChatData* instance : s_instances) {
        if (instance->sourceName.empty())
            continue;
        bool found = false;
        for (auto& info : out) {
            if (info.name == instance->sourceName) {
                found = true;
                break;
            }
        }
        if (!found)
            out.push_back({ instance->sourceName, instance->chatW, instance->chatH });
    }
    std::sort(out.begin(), out.end(),
              [](const ChatSourceInfo& a, const ChatSourceInfo& b) { return a.name < b.name; });
}

static std::vector<uint8_t> serialize_source_list(const std::vector<ChatSourceInfo>& sources)
{
    std::vector<uint8_t> payload;
    uint16_t count = static_cast<uint16_t>(std::min<size_t>(sources.size(), 65535));
    payload.resize(2);
    std::memcpy(payload.data(), &count, sizeof(count));
    for (uint16_t i = 0; i < count; ++i) {
        const auto& src = sources[i];
        uint16_t len = static_cast<uint16_t>(std::min<size_t>(src.name.size(), 65535));
        size_t offset = payload.size();
        payload.resize(offset + 2 + len + 8);
        std::memcpy(payload.data() + offset, &len, 2);
        if (len > 0)
            std::memcpy(payload.data() + offset + 2, src.name.data(), len);
        int32_t w = src.chatW, h = src.chatH;
        std::memcpy(payload.data() + offset + 2 + len, &w, 4);
        std::memcpy(payload.data() + offset + 2 + len + 4, &h, 4);
    }
    return payload;
}

static void chat_update(void* data, obs_data_t* settings)
{
    auto* d = static_cast<ChatData*>(data);
    int width = std::max(100, (int)obs_data_get_int(settings, PROP_WIDTH));
    int height = std::max(100, (int)obs_data_get_int(settings, PROP_HEIGHT));
    RenderBitmap::ChatRenderSettings renderSettings = read_render_settings(settings);

    {
        // OBS Properties is authoritative for ALL settings including fade/disappear.
        // C# app settings arrive via pipe (UpdateChatSettings) and are applied via
        // pendingRenderSettings, bypassing chat_update entirely.
        // chat_update must NOT override with s_sourceSettings — that caused OBS
        // Properties changes to be silently discarded.
        // Keep W/H in s_sourceSettings in sync so chat_create gets correct size on restart.
        std::lock_guard<std::mutex> lock(s_instancesMutex);
        auto found = s_sourceSettings.find(d->sourceName);
        if (found != s_sourceSettings.end()) {
            found->second.chatW = width;
            found->second.chatH = height;
        }
    }

    bool sizeChanged = width != d->chatW || height != d->chatH;
    bool settingsChanged =
        renderSettings.backgroundR != d->renderSettings.backgroundR ||
        renderSettings.backgroundG != d->renderSettings.backgroundG ||
        renderSettings.backgroundB != d->renderSettings.backgroundB ||
        renderSettings.backgroundAlpha != d->renderSettings.backgroundAlpha ||
        renderSettings.textR != d->renderSettings.textR ||
        renderSettings.textG != d->renderSettings.textG ||
        renderSettings.textB != d->renderSettings.textB ||
        renderSettings.bitsR != d->renderSettings.bitsR ||
        renderSettings.bitsG != d->renderSettings.bitsG ||
        renderSettings.bitsB != d->renderSettings.bitsB ||
        renderSettings.margin != d->renderSettings.margin ||
        renderSettings.fontFamily != d->renderSettings.fontFamily ||
        renderSettings.fontSize != d->renderSettings.fontSize ||
        renderSettings.fontWeight != d->renderSettings.fontWeight ||
        renderSettings.textAlign != d->renderSettings.textAlign ||
        renderSettings.textShadow != d->renderSettings.textShadow ||
        renderSettings.outlineSize != d->renderSettings.outlineSize ||
        renderSettings.lineSpacing != d->renderSettings.lineSpacing ||
        renderSettings.messagePadding != d->renderSettings.messagePadding ||
        renderSettings.maxLinesShown != d->renderSettings.maxLinesShown ||
        renderSettings.showChatMessages != d->renderSettings.showChatMessages ||
        renderSettings.topDownStyle != d->renderSettings.topDownStyle ||
        renderSettings.showPlatformIcon != d->renderSettings.showPlatformIcon ||
        renderSettings.showTimestamps != d->renderSettings.showTimestamps ||
        renderSettings.badgesAfterUsername != d->renderSettings.badgesAfterUsername ||
        renderSettings.displayNameColorMode != d->renderSettings.displayNameColorMode ||
        renderSettings.disappearMessages != d->renderSettings.disappearMessages ||
        renderSettings.disappearAfterSeconds != d->renderSettings.disappearAfterSeconds ||
        renderSettings.fadeMessages != d->renderSettings.fadeMessages ||
        renderSettings.fadeSeconds != d->renderSettings.fadeSeconds ||
        renderSettings.platformFilter != d->renderSettings.platformFilter;

    d->chatW = width;
    d->chatH = height;
    d->renderSettings = std::move(renderSettings);

    if (sizeChanged)
        recreate_bitmap(d);
    else if (settingsChanged)
        d->dirty = true;
}

static void* chat_create(obs_data_t* settings, obs_source_t* source)
{
    auto* d = new ChatData();
    d->sourceName = source ? obs_source_get_name(source) : "";
    d->renderSettings = read_render_settings(settings);
    d->chatW = std::max(100, (int)obs_data_get_int(settings, PROP_WIDTH));
    d->chatH = std::max(100, (int)obs_data_get_int(settings, PROP_HEIGHT));

    {
        // s_sourceSettings must be accessed under s_instancesMutex — same lock
        // used by chat_source_apply_settings on the pipe thread.
        std::lock_guard<std::mutex> lock(s_instancesMutex);
        auto found = s_sourceSettings.find(d->sourceName);
        if (found != s_sourceSettings.end()) {
            d->chatW = std::max(100, found->second.chatW);
            d->chatH = std::max(100, found->second.chatH);
            d->renderSettings = found->second.renderSettings;
        } else {
            s_sourceSettings[d->sourceName] = { d->chatW, d->chatH, d->renderSettings };
        }
        s_instances.push_back(d);
    }

    d->bitmap = new RenderBitmap(d->chatW, d->chatH);
    return d;
}

static void chat_destroy(void* data)
{
    auto* d = static_cast<ChatData*>(data);
    {
        std::lock_guard<std::mutex> lock(s_instancesMutex);
        s_instances.erase(std::remove(s_instances.begin(), s_instances.end(), d), s_instances.end());
    }

    obs_enter_graphics();
    if (d->texture)
        gs_texture_destroy(d->texture);
    obs_leave_graphics();

    delete d->bitmap;
    delete d;
}

static uint32_t chat_width(void* data)
{
    return (uint32_t)static_cast<ChatData*>(data)->chatW;
}

static uint32_t chat_height(void* data)
{
    return (uint32_t)static_cast<ChatData*>(data)->chatH;
}

static void chat_tick(void* data, float dt)
{
    auto* d = static_cast<ChatData*>(data);
    chat_apply_pending_settings(d);
    d->elapsed += dt;

    int count = 0;
    {
        std::lock_guard<std::mutex> lock(s_linesMutex);
        count = (int)s_lines.size();
    }

    if (count != d->lastLineCount) {
        d->dirty = true;
        d->lastLineCount = count;
    }

    // Only redraw every frame when content actually changes over time.
    // Fade/disappear need per-frame alpha updates; animated emotes need ~20fps updates.
    // Static messages with no dynamic settings don't need per-frame redraws.
    if (!d->dirty && d->lastLineCount > 0) {
        bool needsDynamic = d->renderSettings.fadeMessages || d->renderSettings.disappearMessages;
        if (needsDynamic || (d->elapsed - d->lastRenderElapsed >= 0.05f))
            d->dirty = true;
    }
}

static void chat_render(void* data, gs_effect_t* effect)
{
    auto* d = static_cast<ChatData*>(data);

    if (d->dirty) {
        std::vector<RenderBitmap::ChatLine> snapshot;
        auto now = std::chrono::steady_clock::now();
        float timeSinceLastMsg = 0.f;
        {
            std::lock_guard<std::mutex> lock(s_linesMutex);
            timeSinceLastMsg = std::chrono::duration<float>(now - s_lastMessageTime).count();
            snapshot.reserve(s_lines.size());
            for (const auto& line : s_lines) {
                float ageSeconds = std::chrono::duration<float>(now - line.receivedAt).count();
                if (d->renderSettings.platformFilter == 1 && line.platform != L"Twitch")
                    continue;
                if (d->renderSettings.platformFilter == 2 && line.platform != L"Kick")
                    continue;
                if (d->renderSettings.platformFilter == 3 && line.platform != L"YouTube")
                    continue;
                if (d->renderSettings.disappearMessages && ageSeconds > (float)d->renderSettings.disappearAfterSeconds)
                    continue;

                snapshot.push_back({
                    line.platform, line.platformIcons, line.timestamp, line.username, line.text, line.color,
                    line.isBroadcaster, line.isModerator, line.isSubscriber, line.isVip,
                    line.isHighlighted, line.bitsAmount, line.subMonths,
                    line.badgePaths, line.emotes, ageSeconds
                });
            }
        }

        d->bitmap->DrawChatMessages(snapshot, d->elapsed, timeSinceLastMsg, d->renderSettings);

        std::vector<uint8_t> pixels;
        d->bitmap->GetPixels(1.0f, pixels);
        obs_enter_graphics();
        if (d->texture) { gs_texture_destroy(d->texture); d->texture = nullptr; }
        const uint8_t* planes[1] = { pixels.data() };
        d->texture = gs_texture_create((uint32_t)d->chatW, (uint32_t)d->chatH, GS_BGRA, 1, planes, 0);
        obs_leave_graphics();

        if (!d->texture) {
            blog(LOG_WARNING, "[Steaming] Chat texture creation failed.");
            return;
        }

        d->dirty = false;
        d->lastRenderElapsed = d->elapsed;
    }

    if (!d->texture)
        return;

    gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), d->texture);
    gs_draw_sprite(d->texture, 0, (uint32_t)d->chatW, (uint32_t)d->chatH);
}

static obs_source_info s_chatInfo = {};

void chat_source_register()
{
    s_chatInfo.id = "steaming_chat";
    s_chatInfo.type = OBS_SOURCE_TYPE_INPUT;
    s_chatInfo.output_flags = OBS_SOURCE_VIDEO;
    s_chatInfo.get_name = chat_get_name;
    s_chatInfo.get_properties = chat_get_properties;
    s_chatInfo.get_defaults = chat_get_defaults;
    s_chatInfo.create = chat_create;
    s_chatInfo.update = chat_update;
    s_chatInfo.destroy = chat_destroy;
    s_chatInfo.get_width = chat_width;
    s_chatInfo.get_height = chat_height;
    s_chatInfo.video_render = chat_render;
    s_chatInfo.video_tick = chat_tick;
    obs_register_source(&s_chatInfo);
    blog(LOG_INFO, "[Steaming] Registered source: steaming_chat");
}

void chat_source_mark_dirty()
{
    std::lock_guard<std::mutex> lock(s_instancesMutex);
    for (ChatData* instance : s_instances) {
        std::lock_guard<std::mutex> instanceLock(instance->mutex);
        instance->dirty = true;
    }
}

void chat_source_enqueue(const std::vector<uint8_t>& payload)
{
    ChatEntry entry;
    if (!parse_chat(payload, entry))
        return;

    entry.receivedAt = std::chrono::steady_clock::now();

    std::lock_guard<std::mutex> lock(s_linesMutex);
    s_lastMessageTime = entry.receivedAt;
    s_lines.push_back(std::move(entry));
    while ((int)s_lines.size() > MAX_LINES)
        s_lines.pop_front();
}

void chat_source_clear()
{
    {
        std::lock_guard<std::mutex> lock(s_linesMutex);
        s_lines.clear();
        s_lastMessageTime = std::chrono::steady_clock::now();
    }

    chat_source_mark_dirty();
}

void chat_source_apply_settings(const std::vector<uint8_t>& payload)
{
    // Wire format matches ChatOverlaySettingsPayload.Serialize() in C#:
    // [2+N] sourceName, [4]margin, [2+N]bgColor, [4]bgOpacity,
    // [2+N]textColor, [2+N]bitsColor, [2+N]fontFamily, [4]fontSize, [4]fontWeight,
    // [2+N]textAlign, [4]textShadow, [4]outlineSize, [4]lineSpacing, [4]messagePadding,
    // [4]maxLines, [1]showChatMessages, [2+N]messageStyle, [1]showPlatformIcon,
    // [2+N]badgePlacement, [2+N]nameColorMode, [1]disappearMessages,
    // [4]disappearAfter, [1]fadeMessages, [4]fadeSeconds, [2+N]platformFilter, [1]showTimestamps
    // NOTE: width/height are NOT in this payload — OBS Properties is the only source of canvas size.
    size_t off = 0;
    std::wstring sourceName;
    int32_t margin = 0, bgOpacity = 0, fontSize = 0, fontWeight = 700;
    int32_t textShadow = 0, outlineSize = 0, lineSpacing = 0, messagePadding = 0, maxLines = 0, disappearAfter = 0, fadeSeconds = 0;
    bool showChatMessages = true, showPlatformIcon = true, disappearMessages = false, fadeMessages = false, showTimestamps = false;
    std::wstring bgColor, textColor, bitsColor, fontFamily, textAlign, badgePlacement, nameColorMode, messageStyle, platformFilter;

    if (!readStr(payload, off, sourceName)) return;
    if (!readI32(payload, off, margin)) return;
    if (!readStr(payload, off, bgColor)) return;
    if (!readI32(payload, off, bgOpacity)) return;
    if (!readStr(payload, off, textColor)) return;
    if (!readStr(payload, off, bitsColor)) return;
    if (!readStr(payload, off, fontFamily)) return;
    if (!readI32(payload, off, fontSize)) return;
    if (!readI32(payload, off, fontWeight)) return;
    if (!readStr(payload, off, textAlign)) return;
    if (!readI32(payload, off, textShadow)) return;
    if (!readI32(payload, off, outlineSize)) return;
    if (!readI32(payload, off, lineSpacing)) return;
    if (!readI32(payload, off, messagePadding)) return;
    if (!readI32(payload, off, maxLines)) return;
    if (!readBool(payload, off, showChatMessages)) return;
    if (!readStr(payload, off, messageStyle)) return;
    if (!readBool(payload, off, showPlatformIcon)) return;
    if (!readStr(payload, off, badgePlacement)) return;
    if (!readStr(payload, off, nameColorMode)) return;
    if (!readBool(payload, off, disappearMessages)) return;
    if (!readI32(payload, off, disappearAfter)) return;
    if (!readBool(payload, off, fadeMessages)) return;
    if (!readI32(payload, off, fadeSeconds)) return;
    if (!readStr(payload, off, platformFilter)) return;
    if (!readBool(payload, off, showTimestamps)) return;

    RenderBitmap::ChatRenderSettings settings = {};
    settings.margin = std::clamp((int)margin, 0, 128);
    settings.backgroundAlpha = (uint8_t)std::clamp((int)bgOpacity, 0, 255);
    set_rgb_from_hex(bgColor, settings.backgroundR, settings.backgroundG, settings.backgroundB);
    set_rgb_from_hex(textColor, settings.textR, settings.textG, settings.textB);
    set_rgb_from_hex(bitsColor, settings.bitsR, settings.bitsG, settings.bitsB);
    settings.fontFamily = fontFamily.empty() ? L"Segoe UI" : fontFamily;
    settings.fontSize = std::clamp((int)fontSize, 10, 96);
    settings.fontWeight = std::clamp((int)fontWeight, 100, 900);
    settings.textAlign = textAlign == L"Center" ? 1 : (textAlign == L"Right" ? 2 : 0);
    settings.textShadow = std::clamp((int)textShadow, 0, 32);
    settings.outlineSize = std::clamp((int)outlineSize, 0, 8);
    settings.lineSpacing = std::clamp((int)lineSpacing, 0, 48);
    settings.messagePadding = std::clamp((int)messagePadding, 2, 48);
    settings.maxLinesShown = std::clamp((int)maxLines, 1, MAX_LINES);
    settings.showChatMessages = showChatMessages;
    settings.topDownStyle = messageStyle == L"TopDown";
    settings.showPlatformIcon = showPlatformIcon;
    settings.showTimestamps = showTimestamps;
    settings.badgesAfterUsername = badgePlacement == L"AfterUsername";
    settings.displayNameColorMode = nameColorMode == L"PlatformColor" ? 1 : (nameColorMode == L"BaseTextColor" ? 2 : 0);
    settings.disappearMessages = disappearMessages;
    settings.disappearAfterSeconds = std::clamp((int)disappearAfter, 1, 3600);
    settings.fadeMessages = fadeMessages;
    settings.fadeSeconds = std::clamp((int)fadeSeconds, 1, 600);
    settings.platformFilter = platformFilter == L"Twitch" ? 1 :
                              (platformFilter == L"Kick" ? 2 :
                              (platformFilter == L"YouTube" ? 3 : 0));

    std::string sourceNameUtf8 = wide_to_utf8(sourceName);

    std::lock_guard<std::mutex> lock(s_instancesMutex);
    // Preserve W/H from OBS Properties (stored in instance or existing s_sourceSettings).
    // Never overwrite canvas size from the pipe — OBS Properties is the only source of size.
    auto& stored = s_sourceSettings[sourceNameUtf8];
    stored.renderSettings = settings;

    for (ChatData* instance : s_instances) {
        if (!sourceNameUtf8.empty() && instance->sourceName != sourceNameUtf8)
            continue;
        // Sync s_sourceSettings W/H from the live instance so chat_create gets it right after restart
        stored.chatW = instance->chatW;
        stored.chatH = instance->chatH;
        std::lock_guard<std::mutex> instanceLock(instance->mutex);
        instance->pendingRenderSettings = settings;
        instance->pendingSettingsDirty = true;
    }
}

void chat_source_send_source_list()
{
    PipeClient* pipe = steaming_get_pipe();
    if (!pipe || !pipe->isConnected())
        return;

    std::vector<ChatSourceInfo> sources;
    collect_source_info(sources);
    auto payload = serialize_source_list(sources);
    pipe->sendMessage(PipeMessageType::ChatSourceList, payload);
}
