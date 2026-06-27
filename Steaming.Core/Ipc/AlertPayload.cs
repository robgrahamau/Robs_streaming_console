using System.Text;

namespace Steaming.Core.Ipc;

public enum AlertType : byte
{
    Follow           = 0x01,
    Subscribe        = 0x02,
    GiftSub          = 0x03,
    Bits             = 0x04,
    Raid             = 0x05,
    RewardRedemption = 0x06,
}

// Wire format (must match parse_alert in alert_source.cpp):
//   [1]  AlertType
//   [2]  usernameLen (LE uint16)
//   [N]  username (UTF-8)
//   [2]  messageLen (LE uint16)
//   [M]  message (UTF-8)
//   [4]  amount (LE int32)
//   [4]  duration (LE float32, seconds)
public record AlertPayload(AlertType Type, string Username, string Message, int Amount = 0, float Duration = 5.0f)
{
    public byte[] Serialize()
    {
        var un = Encoding.UTF8.GetBytes(Username);
        var mg = Encoding.UTF8.GetBytes(Message);
        var buf = new byte[1 + 2 + un.Length + 2 + mg.Length + 4 + 4];
        int off = 0;

        buf[off++] = (byte)Type;

        BitConverter.TryWriteBytes(buf.AsSpan(off), (ushort)un.Length); off += 2;
        un.CopyTo(buf, off); off += un.Length;

        BitConverter.TryWriteBytes(buf.AsSpan(off), (ushort)mg.Length); off += 2;
        mg.CopyTo(buf, off); off += mg.Length;

        BitConverter.TryWriteBytes(buf.AsSpan(off), Amount);   off += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(off), Duration);

        return buf;
    }
}
