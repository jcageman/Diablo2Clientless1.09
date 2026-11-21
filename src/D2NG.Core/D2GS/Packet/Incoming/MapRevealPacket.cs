using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Exceptions;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class MapRevealPacket : D2gsPacket
{
    public MapRevealPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.MapReveal)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        X = reader.ReadUInt16();
        Y = reader.ReadUInt16();
        Area = (Area)reader.ReadByte();
    }

    public ushort X { get; }
    public ushort Y { get; }
    public Area Area { get; }
}
