using D2NG.Core.BNCS.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.BNCS.Packet;

/// <summary>
/// Answer to SID_CHANGEPASSWORD: a single status word, 1 when the password was changed. Captured 6 Sept 2026.
/// </summary>
public class ChangePasswordResponsePacket : BncsPacket
{
    public uint Status { get; }

    public bool Success => Status == 1;

    public ChangePasswordResponsePacket(BncsPacket packet) : this(packet.Raw)
    {
    }

    public ChangePasswordResponsePacket(byte[] packet) : base(packet)
    {
        var reader = new BinaryReader(new MemoryStream(packet), Encoding.ASCII);
        if (PrefixByte != reader.ReadByte())
        {
            throw new BncsPacketException("Not a valid BNCS Packet");
        }
        if ((byte)Sid.CHANGEPASSWORD != reader.ReadByte())
        {
            throw new BncsPacketException("Expected type was not found");
        }
        if (packet.Length != reader.ReadUInt16())
        {
            throw new BncsPacketException("Packet length does not match");
        }

        Status = reader.ReadUInt32();
    }
}
