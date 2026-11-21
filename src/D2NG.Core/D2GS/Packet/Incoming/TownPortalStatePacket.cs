using D2NG.Core.D2GS.Exceptions;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class TownPortalStatePacket : D2gsPacket
{
    public TownPortalStatePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.TownPortalState)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        reader.ReadByte();
        Area = (Act.Area)reader.ReadByte();
        TeleportId = reader.ReadUInt32();
    }

    public Act.Area Area { get; }
    public uint TeleportId { get; }
}
