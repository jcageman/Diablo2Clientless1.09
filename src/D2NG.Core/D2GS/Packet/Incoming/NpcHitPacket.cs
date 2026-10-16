using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class NpcHitPacket : D2gsPacket
{
    public uint EntityId { get; }

    public EntityType EntityType { get; }
    public EntityState EntityState { get; }

    public double LifePercentage { get; }

    public NpcHitPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.NPCHit)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        EntityType = (EntityType)reader.ReadByte();
        EntityId = reader.ReadUInt32();
        EntityState = (EntityState)reader.ReadByte();
        reader.ReadByte();
        // Life is 0-127 of 128 and the server ORs 0x80 in as a monster type flag (D2MOO MonsterMsg.cpp):
        // Pindleskin's hits read 0xFB, 0x77, 0xF3, 0x6F, which is 96, 93, 90, 87%.
        LifePercentage = (reader.ReadByte() & 0x7F) / 1.28;
    }
}
