using D2NG.Core.BNCS.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.BNCS.Packet;

/// <summary>
/// Answer to SID_CREATEACCOUNT2: a status word, 0 when the account was created, followed by an optional message.
/// The success case captured on 6 Sept 2026 carried no message at all.
/// </summary>
public class CreateAccountResponsePacket : BncsPacket
{
    public uint Status { get; }

    public string Message { get; }

    public bool Success => Status == 0;

    public CreateAccountResponsePacket(BncsPacket packet) : this(packet.Raw)
    {
    }

    public CreateAccountResponsePacket(byte[] packet) : base(packet)
    {
        var reader = new BinaryReader(new MemoryStream(packet), Encoding.ASCII);
        if (PrefixByte != reader.ReadByte())
        {
            throw new BncsPacketException("Not a valid BNCS Packet");
        }
        if ((byte)Sid.CREATEACCOUNT2 != reader.ReadByte())
        {
            throw new BncsPacketException("Expected type was not found");
        }
        if (packet.Length != reader.ReadUInt16())
        {
            throw new BncsPacketException("Packet length does not match");
        }

        Status = reader.ReadUInt32();
        Message = reader.BaseStream.Position < packet.Length ? ReadCString(reader) : string.Empty;
    }

    private static string ReadCString(BinaryReader reader)
    {
        var builder = new StringBuilder();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var c = reader.ReadByte();
            if (c == 0)
            {
                break;
            }
            builder.Append((char)c);
        }
        return builder.ToString();
    }
}
