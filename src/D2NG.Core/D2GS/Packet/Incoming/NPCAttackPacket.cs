using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System;
using System.Buffers.Binary;

namespace D2NG.Core.D2GS.Packet.Incoming;

/// <summary>
/// 0x6C, 16 bytes: id u32 @1, attack type @5, target type @6, target id u32 @7, @11 always 0,
/// current x,y u16 @12.
/// </summary>
public class NPCAttackPacket : D2gsPacket
{
    public const int Length = 16;

    public uint EntityId { get; }

    public byte AttackType { get; }

    public EntityType TargetType { get; }

    public uint TargetId { get; }

    /// <summary>Where the monster is now.</summary>
    public Point Location { get; }

    public NPCAttackPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var raw = packet.Raw;
        if (raw.Length < Length || (InComingPacket)raw[0] != InComingPacket.NPCAttack)
        {
            throw new D2GSPacketException($"Invalid NPC attack packet of {raw.Length} bytes");
        }

        var span = raw.AsSpan();
        EntityId = BinaryPrimitives.ReadUInt32LittleEndian(span[1..]);
        AttackType = raw[5];
        TargetType = (EntityType)raw[6];
        TargetId = BinaryPrimitives.ReadUInt32LittleEndian(span[7..]);
        Location = new Point(BinaryPrimitives.ReadUInt16LittleEndian(span[12..]), BinaryPrimitives.ReadUInt16LittleEndian(span[14..]));
    }
}
