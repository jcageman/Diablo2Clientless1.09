using D2NG.Core.D2GS.Exceptions;
using System;
using System.Buffers.Binary;

namespace D2NG.Core.D2GS.Packet.Incoming;

/// <summary>
/// 0x6B, 16 bytes: id u32 @1, action @5, a point x,y u16 @6 ((0,0) for most actions), @10 and @11 always 0,
/// current x,y u16 @12.
/// </summary>
public class NPCActionPacket : D2gsPacket
{
    public const int Length = 16;

    public uint EntityId { get; }

    public byte Action { get; }

    /// <summary>The point the action aims at, or null when the packet names none.</summary>
    public Point TargetLocation { get; }

    /// <summary>Where the monster is now.</summary>
    public Point Location { get; }

    public NPCActionPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var raw = packet.Raw;
        if (raw.Length < Length || (InComingPacket)raw[0] != InComingPacket.NPCAction)
        {
            throw new D2GSPacketException($"Invalid NPC action packet of {raw.Length} bytes");
        }

        var span = raw.AsSpan();
        EntityId = BinaryPrimitives.ReadUInt32LittleEndian(span[1..]);
        Action = raw[5];
        var targetX = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        var targetY = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        TargetLocation = targetX == 0 && targetY == 0 ? null : new Point(targetX, targetY);
        Location = new Point(BinaryPrimitives.ReadUInt16LittleEndian(span[12..]), BinaryPrimitives.ReadUInt16LittleEndian(span[14..]));
    }
}
