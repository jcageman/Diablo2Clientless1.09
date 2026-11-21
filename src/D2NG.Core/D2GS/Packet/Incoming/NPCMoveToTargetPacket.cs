using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;

namespace D2NG.Core.D2GS.Packet.Incoming;

/// <summary>
/// 0x68, 20 bytes on 1.09: id u32 @1, move type @5, current x,y u16 @6, target type @10, target id u32 @11,
/// @15 always 1, @16 always 0, path type @17, run bonus @18, max distance @19.
/// </summary>
public class NPCMoveToTargetPacket : D2gsPacket
{
    public const int Length = 20;

    public uint EntityId { get; }

    /// <summary>Where the monster is now.</summary>
    public Point Location { get; }

    public byte MoveType { get; }

    public EntityType TargetType { get; }

    public uint TargetId { get; }

    public byte PathType { get; }

    public byte RunBonus { get; }

    public byte MaxDistance { get; }

    public NPCMoveToTargetPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.NPCMoveToTarget)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        EntityId = reader.ReadUInt32();
        MoveType = reader.ReadByte();
        var x = reader.ReadUInt16();
        var y = reader.ReadUInt16();
        Location = new Point(x, y);
        if (packet.Raw.Length >= Length)
        {
            TargetType = (EntityType)reader.ReadByte();
            TargetId = reader.ReadUInt32();
            PathType = packet.Raw[17];
            RunBonus = packet.Raw[18];
            MaxDistance = packet.Raw[19];
        }
    }
}
