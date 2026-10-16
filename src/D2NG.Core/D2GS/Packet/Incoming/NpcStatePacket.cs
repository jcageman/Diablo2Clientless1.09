using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class NpcStatePacket : D2gsPacket
{
    public uint EntityId { get; }
    public EntityState EntityState { get; }
    public Point Location { get; }
    public double LifePercentage { get; }

    public NpcStatePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
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
        // is full life.
        var life = reader.ReadByte();
        LifePercentage = (EntityState == EntityState.BeingHit ? life & 0x7F : life) / 1.28;
    }
}
