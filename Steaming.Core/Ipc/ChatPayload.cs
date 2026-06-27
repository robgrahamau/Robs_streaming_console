using System.Text;

namespace Steaming.Core.Ipc;

// Wire format (ChatPayload v5):
//   [2+N] platform        UTF-8  primary platform for routing/filtering
//   [2+N] platformIcons   UTF-8  display platforms joined with "|" (for example "Twitch|Kick")
//   [2+N] username        UTF-8
//   [2+N] message         UTF-8  (clean text; emote words still present)
//   [2+N] color           UTF-8  "#RRGGBB"
//   [2+N] timestamp       UTF-8  "HH:mm:ss" or empty
//   [1]   flags      bit0=broadcaster bit1=moderator bit2=subscriber
//                    bit3=vip bit4=highlighted bit5=hasBits
//   [4]   bitsAmount int32 LE
//   [2]   subMonths  uint16 LE
//   [1]   badgeCount (0–255)  — actual badge images in display order
//   each badge:  [2+N] cachedFilePath  (empty → use coloured pill fallback)
//   [1]   emoteCount (0–255)
//   each emote:  [2] startCharIndex uint16 LE
//                [2] endCharIndex   uint16 LE (inclusive)
//                [2+N] cachedFilePath  (empty → render as text)

public record EmoteSegment(int Start, int End, string CachedPath);

public record ChatPayload(
    string Platform,
    string PlatformIcons,
    string Username,
    string Message,
    string Color,
    string TimestampText,
    bool   IsBroadcaster = false,
    bool   IsModerator   = false,
    bool   IsSubscriber  = false,
    bool   IsVip         = false,
    bool   IsHighlighted = false,
    int    BitsAmount    = 0,
    int    SubMonths     = 0,
    List<string>?       BadgePaths = null,
    List<EmoteSegment>? Emotes     = null)
{
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, true);

        void WriteStr(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        WriteStr(Platform);
        WriteStr(PlatformIcons);
        WriteStr(Username);
        WriteStr(Message);
        WriteStr(Color);
        WriteStr(TimestampText);

        byte flags = 0;
        if (IsBroadcaster) flags |= 0x01;
        if (IsModerator)   flags |= 0x02;
        if (IsSubscriber)  flags |= 0x04;
        if (IsVip)         flags |= 0x08;
        if (IsHighlighted) flags |= 0x10;
        if (BitsAmount > 0) flags |= 0x20;
        w.Write(flags);
        w.Write(BitsAmount);
        w.Write((ushort)Math.Clamp(SubMonths, 0, 65535));

        var badges = BadgePaths ?? new List<string>();
        w.Write((byte)Math.Min(255, badges.Count));
        foreach (var bp in badges)
            WriteStr(bp ?? "");

        var emotes = Emotes ?? new List<EmoteSegment>();
        w.Write((byte)Math.Min(255, emotes.Count));
        foreach (var em in emotes)
        {
            w.Write((ushort)Math.Clamp(em.Start, 0, 65535));
            w.Write((ushort)Math.Clamp(em.End,   0, 65535));
            WriteStr(em.CachedPath ?? "");
        }

        return ms.ToArray();
    }
}
