using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class UseStackableItemPacket : D2gsPacket
{
    public UseStackableItemPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if (InComingPacket.UseStackableItem != (InComingPacket)id)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        reader.ReadByte();
        ItemId = reader.ReadUInt32();
        reader.ReadUInt16();
        reader.Close();
    }
    public uint ItemId { get; }
}
