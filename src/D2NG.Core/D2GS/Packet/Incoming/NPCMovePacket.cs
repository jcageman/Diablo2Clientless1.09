using D2NG.Core.D2GS.Exceptions;

namespace D2NG.Core.D2GS.Packet.Incoming;

/// <summary>
/// 0x67, 15 bytes on 1.09: id u32 @1, move type @5, destination x,y u16 @6, @10 always 1, @11 always 0,
/// path type @12, run bonus @13, max distance @14. One byte shorter than 1.10's: the bonus is a byte.
/// </summary>
public class NPCMovePacket : D2gsPacket
{
    public const int Length = 15;

    public uint EntityId { get; }

    /// <summary>Where the monster is going, not where it is: the packet carries no start position.</summary>
    public Point Location { get; }

    public byte MoveType { get; }

    /// <summary>1 A*, 2 toward, 7 straight, 13 toward-finish, 15 wall follow.</summary>
    public byte PathType { get; }

    /// <summary>Percent added to the walk speed: 0 walking, (Run - Velocity) / Velocity * 100 running.</summary>
    public byte RunBonus { get; }

    public byte MaxDistance { get; }

    public NPCMovePacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.NPCMove)
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
            PathType = packet.Raw[12];
            RunBonus = packet.Raw[13];
            MaxDistance = packet.Raw[14];
        }
    }
}
