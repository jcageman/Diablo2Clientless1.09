using D2NG.Core.D2GS.Exceptions;


using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class AssignMercPacket : D2gsPacket
{
    public AssignMercPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new LegacyBitReader(packet.Raw);
        var id = (InComingPacket)reader.ReadByte();
        if (InComingPacket.AssignMerc != id )
        {
            throw new D2GSPacketException("Invalid Packet Id");
        }
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        PlayerEntityId = reader.ReadUInt32();
        MercEntityId = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
    }

    public uint MercEntityId { get; }
    public uint PlayerEntityId { get; }
}
