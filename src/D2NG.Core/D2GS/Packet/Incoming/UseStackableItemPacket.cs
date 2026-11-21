using D2NG.Core.D2GS.Exceptions;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class UseStackableItemPacket : D2gsPacket
{
    public UseStackableItemPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if (InComingPacket.UseStackableItem != (InComingPacket)id)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        reader.ReadByte();
        ItemId = reader.ReadUInt32();
        reader.ReadUInt16();
    }
    public uint ItemId { get; }
}
