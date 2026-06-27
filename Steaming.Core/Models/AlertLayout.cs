using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steaming.Core.Models;

public enum AlertElementType { Rect, Text, Image, Gif, Audio, GoalBar, Video }
public enum AlertTextAlign { Left, Center, Right }
public enum AlertEasing { Linear, EaseIn, EaseOut, EaseInOut, Bounce }
// What a video element does once it has played through and the alert is still showing.
// Numeric values are part of the ALT3 wire format; keep existing values stable across C# and C++.
public enum VideoEndBehavior { Loop = 0, Hold = 1, EndHide = 2, EndFade = 3, HoldFirst = 4 }
public enum TextTransitionType { Cut = 0, TypeOn = 1, SlideLeft = 2, SlideRight = 3, Fade = 4, Morph = 5 }

// ── Rich text span ────────────────────────────────────────────────────────────
public class TextSpan
{
    public string Text       { get; set; } = "";
    public string FontFamily { get; set; } = "Segoe UI";
    public int    FontSize   { get; set; } = 24;
    public bool   Bold       { get; set; }
    public bool   Italic     { get; set; }
    public string Color      { get; set; } = "#FFFFFFFF";  // ARGB hex

    public TextSpan Clone() => new() { Text = Text, FontFamily = FontFamily, FontSize = FontSize, Bold = Bold, Italic = Italic, Color = Color };
}

// ── Audio volume envelope keyframe ────────────────────────────────────────────
public class AudioVolumeKeyframe
{
    public float Time   { get; set; }
    public float Volume { get; set; } = 1.0f; // 0.0–2.0 (>1 = boost)
}

// ── Keyframe ──────────────────────────────────────────────────────────────────
public class AlertKeyframe
{
    public float Time     { get; set; }
    public AlertEasing Easing { get; set; } = AlertEasing.EaseInOut;
    public float? X        { get; set; }
    public float? Y        { get; set; }
    public float? Width    { get; set; }
    public float? Height   { get; set; }
    public float? Opacity  { get; set; }
    public float? ScaleX   { get; set; }
    public float? ScaleY   { get; set; }
    public float? Rotation { get; set; } // degrees, 0x80 mask bit
    // Extended properties (extMask byte in wire format)
    // bit 0: fillColor
    public string? FillColor { get; set; } // Rect-only animated fill colour (ARGB hex)
    // bit 1: text spans — null = no span override at this keyframe
    public List<TextSpan>? Spans { get; set; }
    // bit 2: shadow override — null = no shadow override at this keyframe
    public bool?   KfShadow      { get; set; }
    public string? KfShadowColor { get; set; } // ARGB hex
    // bit 3: outline override — null = no outline override at this keyframe
    public bool?   KfOutline      { get; set; }
    public string? KfOutlineColor { get; set; } // ARGB hex
    public int?    KfOutlineWidth { get; set; }
    // bit 4: corner radius override (Rect/GoalBar) — null = not animated
    public int?    KfCornerRadius  { get; set; }
    // bit 5: shadow geometry override — null = not animated (angle+dist+blur; serialized as X+Y+blur)
    public float?  KfShadowAngle    { get; set; }
    public float?  KfShadowDistance { get; set; }
    public float?  KfShadowBlur     { get; set; }
    // bit 6: text alignment override — null = not animated
    public int?    KfAlign          { get; set; } // 0=left 1=center 2=right
    public int?    KfVertAlign      { get; set; } // 0=top 1=middle 2=bottom
    // bit 7: span transition type — null = Cut (default)
    public TextTransitionType? SpanTransition { get; set; }
}

// ── Element ───────────────────────────────────────────────────────────────────
public class AlertElement
{
    public string           Id           { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public AlertElementType Type         { get; set; }
    public bool             Hidden       { get; set; }
    public bool             Locked       { get; set; }
    public float            X            { get; set; }
    public float            Y            { get; set; }
    public float            Width        { get; set; }
    public float            Height       { get; set; }
    public float            Rotation     { get; set; } = 0f; // degrees
    public int              ZOrder       { get; set; }
    public List<AlertKeyframe> Keyframes { get; set; } = new();

    // Rect
    public string FillColor    { get; set; } = "#CC000000";
    public int    CornerRadius { get; set; }

    // Text — legacy single-style fields kept for JSON backward-compat
    public string         Content       { get; set; } = "";
    public string         FontFamily    { get; set; } = "Segoe UI";
    public int            FontSize      { get; set; } = 24;
    public bool           Bold          { get; set; }
    public bool           Italic        { get; set; }
    public string         Color         { get; set; } = "#FFFFFFFF";
    public AlertTextAlign Align         { get; set; } = AlertTextAlign.Center;
    public int            VertAlign     { get; set; } = 1; // 0=top 1=middle 2=bottom
    public bool           Shadow         { get; set; }
    public string         ShadowColor    { get; set; } = "#AA000000";
    public float          ShadowAngle    { get; set; } = 135f; // degrees, 0=right, 90=down (Photoshop-style)
    public float          ShadowDistance { get; set; } = 3f;   // pixels
    public float          ShadowBlur     { get; set; } = 4f;   // pixels (WPF preview only; C++ has no blur)
    // Rich text spans (ALT3). If empty, Serialize falls back to legacy fields.
    public List<TextSpan> Spans         { get; set; } = new();
    // Outline effect
    public bool           Outline       { get; set; }
    public string         OutlineColor  { get; set; } = "#FF000000";
    public int            OutlineWidth  { get; set; } = 1;

    // Image / Gif / Video
    public string? FilePath { get; set; }

    // Video (Type == Video) — streamed at render time, not pre-decoded
    public VideoEndBehavior VideoEnd    { get; set; } = VideoEndBehavior.Loop;
    public bool             VideoMuted  { get; set; }
    public float            VideoVolume { get; set; } = 1f; // 0–2

    // Audio clip (Type == Audio)
    public float   StartTime      { get; set; } = 0f;
    public float   VolumeL        { get; set; } = 1f; // left channel 0–2
    public float   VolumeR        { get; set; } = 1f; // right channel 0–2
    public float   FadeIn         { get; set; } = 0f; // seconds
    public float   FadeOut        { get; set; } = 0f; // seconds
    public List<AudioVolumeKeyframe> VolumeEnvelope { get; set; } = new();
}

// ── Layout ────────────────────────────────────────────────────────────────────
public class AlertLayout
{
    public int                Width     { get; set; } = 800;
    public int                Height    { get; set; } = 200;
    public float              Duration  { get; set; } = 0f; // 0 = use event default; >0 = override
    public List<AlertElement> Elements  { get; set; } = new();
    public string?            SoundFile      { get; set; }
    public float              Volume         { get; set; } = 1.0f;
    public List<AudioVolumeKeyframe> VolumeEnvelope { get; set; } = new();

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    public static AlertLayout? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<AlertLayout>(json); }
        catch { return null; }
    }

    public static AlertLayout CreateDefault(float duration = 10.0f) => new()
    {
        Width = 800, Height = 200,
        Elements = new List<AlertElement>
        {
            new()
            {
                Type = AlertElementType.Rect,
                X = 40, Y = 35, Width = 720, Height = 130,
                CornerRadius = 14, FillColor = "#CC121212", ZOrder = 0,
                Keyframes = new()
                {
                    new() { Time = 0.00f,     Opacity = 0.0f, Easing = AlertEasing.EaseOut },
                    new() { Time = 0.35f,     Opacity = 1.0f, Easing = AlertEasing.Linear  },
                    new() { Time = duration,  Opacity = 1.0f, Easing = AlertEasing.Linear  },
                }
            },
            new()
            {
                Type = AlertElementType.Text,
                X = 48, Y = 47, Width = 704, Height = 44,
                Content = "{user}", Align = AlertTextAlign.Center, ZOrder = 1,
                Spans = new() { new TextSpan { Text = "{user}", FontFamily = "Segoe UI", FontSize = 30, Bold = true, Color = "#FFFFFFFF" } },
                Keyframes = new()
                {
                    new() { Time = 0.00f,     Y = 15f, Opacity = 0.0f, Easing = AlertEasing.EaseOut },
                    new() { Time = 0.40f,     Y = 47f, Opacity = 1.0f, Easing = AlertEasing.Linear  },
                    new() { Time = duration,  Y = 47f, Opacity = 1.0f, Easing = AlertEasing.Linear  },
                }
            },
            new()
            {
                Type = AlertElementType.Text,
                X = 48, Y = 99, Width = 704, Height = 56,
                Content = "{message}", Align = AlertTextAlign.Center, ZOrder = 2,
                Spans = new() { new TextSpan { Text = "{message}", FontFamily = "Segoe UI", FontSize = 20, Color = "#FFC8C8C8" } },
                Keyframes = new()
                {
                    new() { Time = 0.00f,     Y = 120f, Opacity = 0.0f, Easing = AlertEasing.EaseOut },
                    new() { Time = 0.50f,     Y = 99f,  Opacity = 1.0f, Easing = AlertEasing.Linear  },
                    new() { Time = duration,  Y = 99f,  Opacity = 1.0f, Easing = AlertEasing.Linear  },
                }
            },
        }
    };

    // Simple label: dark bg + white text showing {value}.  Used when no custom layout is set.
    public static AlertLayout CreateDefaultLabel() => new()
    {
        Width = 400, Height = 60,
        Elements = new List<AlertElement>
        {
            new()
            {
                Type = AlertElementType.Rect, X = 0, Y = 0, Width = 400, Height = 60,
                CornerRadius = 8, FillColor = "#CC121212", ZOrder = 0,
            },
            new()
            {
                Type = AlertElementType.Text, X = 10, Y = 5, Width = 380, Height = 50,
                Align = AlertTextAlign.Left, ZOrder = 1,
                Spans = new() { new TextSpan { Text = "{value}", FontFamily = "Segoe UI", FontSize = 22, Color = "#FFFFFFFF" } },
            },
        }
    };

    // Simple goal: title + progress bar + count text.  Used when no custom layout is set.
    public static AlertLayout CreateDefaultGoal(string title, int target) => new()
    {
        Width = 500, Height = 80,
        Elements = new List<AlertElement>
        {
            // Background
            new() { Type = AlertElementType.Rect, X = 0, Y = 0, Width = 500, Height = 80, CornerRadius = 8, FillColor = "#CC121212", ZOrder = 0 },
            // Title
            new()
            {
                Type = AlertElementType.Text, X = 10, Y = 5, Width = 480, Height = 26,
                Align = AlertTextAlign.Left, ZOrder = 1,
                Spans = new() { new TextSpan { Text = title, FontFamily = "Segoe UI", FontSize = 16, Bold = true, Color = "#FFE0E0E0" } },
            },
            // Bar background
            new() { Type = AlertElementType.Rect, X = 10, Y = 36, Width = 480, Height = 20, CornerRadius = 4, FillColor = "#FF333333", ZOrder = 2 },
            // Goal bar (auto-scales width to {current}/{target})
            new() { Type = AlertElementType.GoalBar, X = 10, Y = 36, Width = 480, Height = 20, CornerRadius = 4, FillColor = "#FF2196F3", ZOrder = 3 },
            // Count text
            new()
            {
                Type = AlertElementType.Text, X = 10, Y = 58, Width = 480, Height = 18,
                Align = AlertTextAlign.Left, ZOrder = 4,
                Spans = new() { new TextSpan { Text = "{current} / {target}", FontFamily = "Segoe UI", FontSize = 13, Color = "#FFAAAAAA" } },
            },
        }
    };

    // ── Binary wire serialization (ALT3) ──────────────────────────────────────
    // Format: [4]magic [4]W [4]H [2]elemCount [elements...] [2+N]username [2+N]message [4]amount [2+N]platform [4]duration
    //
    // Text element (ALT3):
    //   [1]align [1]textFlags [4]shadowColor [2]shadowOffX [2]shadowOffY
    //   [4]outlineColor [1]outlineWidth [2]spanCount
    //   each span: [2+N]text [2+N]fontFamily [2]fontSize [1]spanFlags [4]color
    public byte[] Serialize(string username, string message, int amount, float duration, string platform = "")
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, true);

        w.Write(0x33544C41u); // "ALT3" LE
        w.Write(Width);
        w.Write(Height);

        var elems = Elements;
        w.Write((ushort)elems.Count);

        foreach (var el in elems)
        {
            w.Write((byte)el.Type);
            w.Write(el.X);
            w.Write(el.Y);
            w.Write(el.Width);
            w.Write(el.Height);
            w.Write(el.Rotation);
            w.Write(el.ZOrder);

            switch (el.Type)
            {
                case AlertElementType.Rect:
                case AlertElementType.GoalBar:  // same wire format as Rect; C++ scales width by goal progress
                    w.Write(ParseArgb(el.FillColor));
                    w.Write((ushort)el.CornerRadius);
                    break;

                case AlertElementType.Text:
                {
                    // If no explicit spans, convert legacy single-style to one span
                    var spans = el.Spans.Count > 0
                        ? el.Spans
                        : new List<TextSpan> { new() {
                            Text = el.Content ?? "",
                            FontFamily = el.FontFamily ?? "Segoe UI",
                            FontSize = el.FontSize,
                            Bold = el.Bold,
                            Italic = el.Italic,
                            Color = el.Color
                          }};

                    w.Write((byte)el.Align);
                    byte tf = 0;
                    if (el.Shadow)  tf |= 0x01;
                    if (el.Outline) tf |= 0x02;
                    tf |= (byte)((el.VertAlign & 0x03) << 2); // bits 2-3 = vertAlign (0=top 1=mid 2=bot)
                    w.Write(tf);
                    // Compute X/Y from Photoshop-style angle+distance
                    double radians = el.ShadowAngle * Math.PI / 180.0;
                    short shadowX = (short)Math.Round(Math.Cos(radians) * el.ShadowDistance);
                    short shadowY = (short)Math.Round(Math.Sin(radians) * el.ShadowDistance);
                    w.Write(ParseArgb(el.ShadowColor));
                    w.Write(shadowX);
                    w.Write(shadowY);
                    w.Write((byte)Math.Max(0, Math.Min(30, (int)el.ShadowBlur)));
                    w.Write(ParseArgb(el.OutlineColor));
                    w.Write((byte)Math.Max(0, Math.Min(255, el.OutlineWidth)));
                    w.Write((ushort)spans.Count);
                    foreach (var sp in spans)
                    {
                        WriteStr(w, sp.Text ?? "");
                        WriteStr(w, sp.FontFamily ?? "Segoe UI");
                        w.Write((ushort)Math.Max(1, sp.FontSize));
                        byte sf = 0;
                        if (sp.Bold)   sf |= 0x01;
                        if (sp.Italic) sf |= 0x02;
                        w.Write(sf);
                        w.Write(ParseArgb(sp.Color));
                    }
                    break;
                }

                case AlertElementType.Image:
                case AlertElementType.Gif:
                    WriteStr(w, el.FilePath ?? "");
                    break;

                case AlertElementType.Video:
                    // filePath, endBehavior(u8), muted(u8), volume(f32). Frames are streamed by the
                    // plugin at render time; embedded audio is decoded plugin-side from the same file.
                    WriteStr(w, el.FilePath ?? "");
                    w.Write((byte)el.VideoEnd);
                    w.Write((byte)(el.VideoMuted ? 1 : 0));
                    w.Write(Math.Clamp(el.VideoVolume, 0f, 2f));
                    break;

                case AlertElementType.Audio:
                {
                    // Audio clip: filePath, startTime, volumeL, volumeR, fadeIn, fadeOut, envelope
                    WriteStr(w, el.FilePath ?? "");
                    w.Write(el.StartTime);
                    w.Write(Math.Clamp(el.VolumeL, 0f, 2f));
                    w.Write(Math.Clamp(el.VolumeR, 0f, 2f));
                    w.Write(Math.Max(0f, el.FadeIn));
                    w.Write(Math.Max(0f, el.FadeOut));
                    var clipEnv = el.VolumeEnvelope ?? new();
                    w.Write((ushort)clipEnv.Count);
                    foreach (var ekf in clipEnv.OrderBy(k => k.Time))
                    {
                        w.Write(ekf.Time);
                        w.Write(Math.Clamp(ekf.Volume, 0f, 2f));
                    }
                    break;
                }
            }

            // Keyframes (Audio elements have no visual keyframes — always 0)
            // Wire format per keyframe: time(f32) mask(u8) easing(u8) extMask(u8) [fields...] [ext fields...]
            //   mask bits:    0=x 1=y 2=w 3=h 4=opacity 5=scaleX 6=scaleY 7=rotation
            //   extMask bits: 0=fillColor(u32 ARGB)
            //                 1=spans(u16 count + span records — same layout as element-level spans)
            //                 2=shadow(u8 on, u32 ARGB color)
            //                 3=outline(u8 on, u32 ARGB color, u8 width)
            //                 4=cornerRadius(u8, clamped 0-255)
            //                 5=shadowGeom(i16 shadowX, i16 shadowY, u8 blur)
            //                 6=textAlign(u8: bits0-1=hAlign 0-2, bits2-3=vAlign 0-2)
            //                 7=spanTransition(u8 TextTransitionType, omitted when Cut)
            w.Write((ushort)el.Keyframes.Count);
            foreach (var kf in el.Keyframes)
            {
                w.Write(kf.Time);
                byte mask = 0;
                if (kf.X.HasValue)        mask |= 0x01;
                if (kf.Y.HasValue)        mask |= 0x02;
                if (kf.Width.HasValue)    mask |= 0x04;
                if (kf.Height.HasValue)   mask |= 0x08;
                if (kf.Opacity.HasValue)  mask |= 0x10;
                if (kf.ScaleX.HasValue)   mask |= 0x20;
                if (kf.ScaleY.HasValue)   mask |= 0x40;
                if (kf.Rotation.HasValue) mask |= 0x80;
                w.Write(mask);
                w.Write((byte)kf.Easing);
                byte extMask = 0;
                if (!string.IsNullOrEmpty(kf.FillColor))                           extMask |= 0x01;
                if (kf.Spans != null && kf.Spans.Count > 0)                        extMask |= 0x02;
                if (kf.KfShadow.HasValue && !string.IsNullOrEmpty(kf.KfShadowColor)) extMask |= 0x04;
                if (kf.KfOutline.HasValue && !string.IsNullOrEmpty(kf.KfOutlineColor)) extMask |= 0x08;
                if (kf.KfCornerRadius.HasValue)                                          extMask |= 0x10;
                if (kf.KfShadowAngle.HasValue || kf.KfShadowDistance.HasValue || kf.KfShadowBlur.HasValue) extMask |= 0x20;
                if (kf.KfAlign.HasValue || kf.KfVertAlign.HasValue)                     extMask |= 0x40;
                if (kf.SpanTransition.HasValue && kf.SpanTransition != TextTransitionType.Cut) extMask |= 0x80;
                w.Write(extMask);
                if (kf.X.HasValue)        w.Write(kf.X.Value);
                if (kf.Y.HasValue)        w.Write(kf.Y.Value);
                if (kf.Width.HasValue)    w.Write(kf.Width.Value);
                if (kf.Height.HasValue)   w.Write(kf.Height.Value);
                if (kf.Opacity.HasValue)  w.Write(kf.Opacity.Value);
                if (kf.ScaleX.HasValue)   w.Write(kf.ScaleX.Value);
                if (kf.ScaleY.HasValue)   w.Write(kf.ScaleY.Value);
                if (kf.Rotation.HasValue) w.Write(kf.Rotation.Value);
                if ((extMask & 0x01) != 0) w.Write(ParseArgb(kf.FillColor!));
                if ((extMask & 0x02) != 0)
                {
                    var spans = kf.Spans!;
                    w.Write((ushort)spans.Count);
                    foreach (var sp in spans)
                    {
                        WriteStr(w, sp.Text ?? "");
                        WriteStr(w, sp.FontFamily ?? "Segoe UI");
                        w.Write((ushort)Math.Max(6, (int)sp.FontSize));
                        byte sf = 0;
                        if (sp.Bold)   sf |= 0x01;
                        if (sp.Italic) sf |= 0x02;
                        w.Write(sf);
                        w.Write(ParseArgb(sp.Color ?? "#FFFFFFFF"));
                    }
                }
                if ((extMask & 0x04) != 0)
                {
                    w.Write((byte)(kf.KfShadow == true ? 1 : 0));
                    w.Write(ParseArgb(kf.KfShadowColor!));
                }
                if ((extMask & 0x08) != 0)
                {
                    w.Write((byte)(kf.KfOutline == true ? 1 : 0));
                    w.Write(ParseArgb(kf.KfOutlineColor!));
                    w.Write((byte)Math.Clamp(kf.KfOutlineWidth ?? 1, 0, 255));
                }
                if ((extMask & 0x10) != 0) // bit 4: cornerRadius
                {
                    w.Write((byte)Math.Clamp(kf.KfCornerRadius!.Value, 0, 255));
                }
                if ((extMask & 0x20) != 0) // bit 5: shadow geometry
                {
                    float kfAngle = kf.KfShadowAngle    ?? el.ShadowAngle;
                    float kfDist  = kf.KfShadowDistance ?? el.ShadowDistance;
                    float kfBlur  = kf.KfShadowBlur     ?? el.ShadowBlur;
                    double kfRad  = kfAngle * Math.PI / 180.0;
                    short kfShadX = (short)Math.Round(Math.Cos(kfRad) * kfDist);
                    short kfShadY = (short)Math.Round(Math.Sin(kfRad) * kfDist);
                    w.Write(kfShadX);
                    w.Write(kfShadY);
                    w.Write((byte)Math.Max(0, Math.Min(30, (int)kfBlur)));
                }
                if ((extMask & 0x40) != 0) // bit 6: text alignment
                {
                    int ha = Math.Clamp(kf.KfAlign    ?? (int)el.Align,   0, 2);
                    int va = Math.Clamp(kf.KfVertAlign ?? el.VertAlign,    0, 2);
                    w.Write((byte)((ha & 0x03) | ((va & 0x03) << 2)));
                }
                if ((extMask & 0x80) != 0) // bit 7: span transition type
                {
                    w.Write((byte)(kf.SpanTransition ?? TextTransitionType.Cut));
                }
            }
        }

        WriteStr(w, username);
        WriteStr(w, message);
        w.Write(amount);
        WriteStr(w, platform);
        w.Write(duration);
        WriteStr(w, SoundFile ?? "");
        w.Write(Math.Clamp(Volume, 0f, 2f));
        // Volume envelope keyframes
        var env = VolumeEnvelope ?? new();
        w.Write((ushort)env.Count);
        foreach (var kf in env.OrderBy(k => k.Time))
        {
            w.Write(kf.Time);
            w.Write(Math.Clamp(kf.Volume, 0f, 2f));
        }

        return ms.ToArray();
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    // "#AARRGGBB" or "#RRGGBB" → uint32 ARGB
    private static uint ParseArgb(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return 0xFF000000u;
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length == 8) return Convert.ToUInt32(hex, 16);
        return 0xFF000000u;
    }
}
