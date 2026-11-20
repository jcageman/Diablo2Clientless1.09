using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.IO;
using System.Text;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class AllyPartyInfoPacket : D2gsPacket
{
    public AllyPartyInfoPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        if ((InComingPacket)reader.ReadByte() != InComingPacket.AllyPartyInfo)
        {
            throw new D2GSPacketException("Expected Packet Type Not Found");
        }
        EntityType = reader.ReadByte() == 1 ? EntityType.Player : EntityType.NPC;
        LifePercentage = reader.ReadUInt16();
        EntityId = reader.ReadUInt32();
        Area = (Area)reader.ReadUInt16();

    }

    public EntityType EntityType { get; }
    public double LifePercentage { get; }
    public uint EntityId { get; }
    public Area Area { get; }
}
