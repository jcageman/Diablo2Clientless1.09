using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Players;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class BaseAttributePacket : D2gsPacket
{
    public Attribute Attribute { get; }
    public int Value { get; }

    public BaseAttributePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        Attribute = (Attribute)reader.ReadByte();
        switch ((InComingPacket)id)
        {
            case InComingPacket.AddAttributeByte:
                Value = reader.ReadByte();
                break;
            case InComingPacket.AddAttributeWord:
                // Unsigned: a sorceress's base maximum mana, 231 in 1/256ths, is 0xE700 and read signed
                // it came out as -25, so her maximum mana was computed at 122 against 1100 in hand.
                Value = reader.ReadUInt16();
                break;
            case InComingPacket.AddAttributeDword:
                Value = reader.ReadInt32();
                break;
            default:
                throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        reader.Close();
    }
}