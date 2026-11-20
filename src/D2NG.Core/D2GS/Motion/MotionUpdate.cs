using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System;
using System.Buffers.Binary;

namespace D2NG.Core.D2GS.Motion;

public enum MotionUpdateKind : byte
{
    None,
    Assign,
    MoveToPoint,
    MoveToUnit,
    Action,
    Attack,
    Stop,
    Hit,
    Dying,
    Dead,
    Snap,
}

/// <summary>What one game server packet says about where a monster is and where it is going.</summary>
public readonly record struct MotionUpdate(
    MotionUpdateKind Kind,
    byte Packet,
    uint EntityId,
    Point Position,
    EntityType TargetType = EntityType.Player,
    uint TargetId = 0,
    byte RunBonus = 0,
    byte PathType = 0)
{
    /// <summary>Whether <see cref="Position"/> is where the monster is now. A 0x67 carries the destination instead.</summary>
    public bool HasExactPosition => Kind is MotionUpdateKind.Assign or MotionUpdateKind.MoveToUnit or MotionUpdateKind.Action
        or MotionUpdateKind.Attack or MotionUpdateKind.Stop or MotionUpdateKind.Hit or MotionUpdateKind.Dead or MotionUpdateKind.Snap;

    public static MotionUpdate From(NPCMovePacket p) => new(MotionUpdateKind.MoveToPoint, 0x67, p.EntityId, p.Location, RunBonus: p.RunBonus, PathType: p.PathType);

    public static MotionUpdate From(NPCMoveToTargetPacket p) => new(MotionUpdateKind.MoveToUnit, 0x68, p.EntityId, p.Location, p.TargetType, p.TargetId, p.RunBonus, p.PathType);

    public static MotionUpdate From(NPCActionPacket p) => new(MotionUpdateKind.Action, 0x6B, p.EntityId, p.Location);

    public static MotionUpdate From(NPCAttackPacket p) => new(MotionUpdateKind.Attack, 0x6C, p.EntityId, p.Location, p.TargetType, p.TargetId);

    public static MotionUpdate From(NPCStopPacket p) => new(MotionUpdateKind.Stop, 0x6D, p.EntityId, p.Location);

    public static MotionUpdate From(NpcStatePacket p) => p.EntityState switch
    {
        EntityState.BeingHit => new(MotionUpdateKind.Hit, 0x69, p.EntityId, p.Location),
        EntityState.Dieing => new(MotionUpdateKind.Dying, 0x69, p.EntityId, null),
        EntityState.Dead => new(MotionUpdateKind.Dead, 0x69, p.EntityId, p.Location),
        _ => new(MotionUpdateKind.None, 0x69, p.EntityId, null),
    };

    public static MotionUpdate From(AssignNpcPacket p) => new(MotionUpdateKind.Assign, (byte)p.Type, p.EntityId, p.Location);

    /// <summary>A 0x15 for a monster: it blinked or jumped and stands where the packet says.</summary>
    public static MotionUpdate From(ReassignPlayerPacket p) => new(MotionUpdateKind.Snap, 0x15, p.UnitId, p.Location);

    /// <summary>The update a received packet carries for a monster, or <see cref="MotionUpdateKind.None"/>.</summary>
    public static MotionUpdate Read(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
        {
            return default;
        }

        if (payload[0] is not (0x15 or 0x55 or 0x56) and not (>= 0x67 and <= 0x6D))
        {
            return default;
        }

        ReadOnlySpan<byte> p = payload;
        var type = payload[0];
        return (InComingPacket)type switch
        {
            InComingPacket.NPCMove when p.Length >= NPCMovePacket.Length
                => new(MotionUpdateKind.MoveToPoint, type, U32(p, 1), At(p, 6), RunBonus: p[13], PathType: p[12]),
            InComingPacket.NPCMoveToTarget when p.Length >= NPCMoveToTargetPacket.Length
                => new(MotionUpdateKind.MoveToUnit, type, U32(p, 1), At(p, 6), (EntityType)p[10], U32(p, 11), p[18], p[17]),
            InComingPacket.NPCAction when p.Length >= NPCActionPacket.Length
                => new(MotionUpdateKind.Action, type, U32(p, 1), At(p, 12)),
            InComingPacket.NPCAttack when p.Length >= NPCAttackPacket.Length
                => new(MotionUpdateKind.Attack, type, U32(p, 1), At(p, 12), (EntityType)p[6], U32(p, 7)),
            InComingPacket.NPCStop when p.Length >= 10
                => new(MotionUpdateKind.Stop, type, U32(p, 1), At(p, 5)),
            InComingPacket.AssignNPC1 when p.Length >= 12
                => new(MotionUpdateKind.Assign, type, U32(p, 1), At(p, 7)),
            InComingPacket.AssignNPC2 when p.Length >= 46
                => new(MotionUpdateKind.Assign, type, U32(p, 1), At(p, 7)),
            InComingPacket.NPCState when p.Length >= 11 => (EntityState)p[5] switch
            {
                EntityState.BeingHit => new(MotionUpdateKind.Hit, type, U32(p, 1), At(p, 6)),
                EntityState.Dieing => new(MotionUpdateKind.Dying, type, U32(p, 1), null),
                EntityState.Dead => new(MotionUpdateKind.Dead, type, U32(p, 1), At(p, 6)),
                _ => new(MotionUpdateKind.None, type, U32(p, 1), null),
            },
            InComingPacket.ReassignPlayer when p.Length >= 11 && p[1] == (byte)EntityType.NPC
                => new(MotionUpdateKind.Snap, type, U32(p, 2), At(p, 6)),
            _ => default,
        };
    }

    private static uint U32(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(p[offset..]);

    private static Point At(ReadOnlySpan<byte> p, int offset)
        => new(BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]), BinaryPrimitives.ReadUInt16LittleEndian(p[(offset + 2)..]));
}
