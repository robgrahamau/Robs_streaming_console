using System.Text;
using Steaming.Core.Services;

namespace Steaming.Core.Ipc;

// Wire format matches chat_source_apply_settings() in chat_source.cpp:
// [2+N] sourceName
// [4]   margin
// NOTE: width/height are NOT sent — OBS Properties is the only source of canvas size.
// [2+N] backgroundColor
// [4]   backgroundOpacity
// [2+N] textColor
// [2+N] bitsColor
// [2+N] fontFamily
// [4]   fontSize
// [4]   fontWeight
// [2+N] textAlign
// [4]   textShadow
// [4]   outlineSize
// [4]   lineSpacing
// [4]   messagePadding
// [4]   maxLinesShown
// [1]   showChatMessages
// [2+N] messageStyle
// [1]   showPlatformIcon
// [2+N] badgePlacement
// [2+N] displayNameColorMode
// [1]   disappearMessages
// [4]   disappearAfterSeconds
// [1]   fadeMessages
// [4]   fadeSeconds
// [2+N] platformFilter
// [1]   showTimestamps
public record ChatOverlaySettingsPayload(
    string SourceName,
    int Margin,
    string BackgroundColor,
    int BackgroundOpacity,
    string TextColor,
    string BitsColor,
    string FontFamily,
    int FontSize,
    int FontWeight,
    string TextAlign,
    int TextShadow,
    int OutlineSize,
    int LineSpacing,
    int MessagePadding,
    int MaxLinesShown,
    bool ShowChatMessages,
    string MessageStyle,
    bool ShowPlatformIcon,
    string BadgePlacement,
    string DisplayNameColorMode,
    bool DisappearMessages,
    int DisappearAfterSeconds,
    bool FadeMessages,
    int FadeSeconds,
    string PlatformFilter,
    bool ShowTimestamps)
{
    public static ChatOverlaySettingsPayload FromConfig(ChatOverlayConfig config) => new(
        config.SourceName,
        config.Margin,
        config.BackgroundColor,
        config.BackgroundOpacity,
        config.TextColor,
        config.BitsColor,
        config.FontFamily,
        config.FontSize,
        config.FontWeight,
        config.TextAlign,
        config.TextShadow,
        config.OutlineSize,
        config.LineSpacing,
        config.MessagePadding,
        config.MaxLinesShown,
        config.ShowChatMessages,
        config.MessageStyle,
        config.ShowPlatformIcon,
        config.BadgePlacement,
        config.DisplayNameColorMode,
        config.DisappearMessages,
        config.DisappearAfterSeconds,
        config.FadeMessages,
        config.FadeSeconds,
        config.PlatformFilter,
        config.ShowTimestamps);

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, true);

        void WriteStr(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }

        WriteStr(SourceName);
        w.Write(Margin);
        WriteStr(BackgroundColor);
        w.Write(BackgroundOpacity);
        WriteStr(TextColor);
        WriteStr(BitsColor);
        WriteStr(FontFamily);
        w.Write(FontSize);
        w.Write(FontWeight);
        WriteStr(TextAlign);
        w.Write(TextShadow);
        w.Write(OutlineSize);
        w.Write(LineSpacing);
        w.Write(MessagePadding);
        w.Write(MaxLinesShown);
        w.Write(ShowChatMessages);
        WriteStr(MessageStyle);
        w.Write(ShowPlatformIcon);
        WriteStr(BadgePlacement);
        WriteStr(DisplayNameColorMode);
        w.Write(DisappearMessages);
        w.Write(DisappearAfterSeconds);
        w.Write(FadeMessages);
        w.Write(FadeSeconds);
        WriteStr(PlatformFilter);
        w.Write(ShowTimestamps);
        return ms.ToArray();
    }
}
