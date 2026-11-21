using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class NpcStatePacket : D2gsPacket
{
    public uint EntityId { get; }
    public EntityState EntityState { get; }
    /// <summary>Exact for get-hit and dead (the corpse); for dying it is (0,0) or a point the monster is not at.</summary>
    public Point Location { get; }
    public double LifePercentage { get; }

    public NpcStatePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.NPCState)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        EntityId = reader.ReadUInt32();
        EntityState = (EntityState)reader.ReadByte();
        var x = reader.ReadUInt16();
        var y = reader.ReadUInt16();
        Location = new Point(x, y);
        // Only the get-hit state carries the 0x80 monster type flag of NpcHitPacket; in the others 0x80
        // is full life. Dying and dead put the direction (0-63 in all 3,787 traced) there instead.
        var life = reader.ReadByte();
        if (EntityState is EntityState.Dieing or EntityState.Dead)
        {
            Direction = life;
            LifePercentage = 0;
        }
        else
        {
            LifePercentage = (EntityState == EntityState.BeingHit ? life & 0x7F : life) / 1.28;
        }
    }

    /// <summary>The direction a dying or dead monster faces; 0 in the other states.</summary>
    public byte Direction { get; }
}
