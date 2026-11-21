using D2NG.Core.D2GS.Objects;
using D2NG.Core.MonsterData;
using System;

namespace D2NG.Core.D2GS.Motion;

/// <summary>How the run bonus v of 0x67 and 0x68 enters the speed; the traces cannot tell the two apart yet.</summary>
public enum RunBonusFormula
{
    /// <summary>75% * (1 + v/100): the base percentage scaled by v.</summary>
    Multiplied,

    /// <summary>(75 + v)%: v added to the base percentage.</summary>
    Added,
}

/// <summary>What the motion rules need to know about a monster besides its packets.</summary>
public readonly record struct MotionUnit(NPCCode Code, bool ExtraFast, bool Champion, bool Minion, bool Named);

/// <summary>
/// The monster movement model: how each packet changes a <see cref="MotionSnapshot"/> and where the
/// snapshot puts the monster some time later. Pure functions of their arguments, so one replay can run
/// several variants side by side.
/// </summary>
public sealed class MotionRules
{
    /// <summary>STAT_VELOCITYPERCENT every monster starts with (D2MOO); players do not have it.</summary>
    public const int BaseVelocityPercent = 75;

    /// <summary>
    /// Extra Fast adds to the base percentage: 75 + 50 fits the 1.61-1.70x measured on one Council
    /// Member pack (~27 walks, one game). Low sample; D2MOO's +100 is contradicted by it.
    /// </summary>
    public const int ExtraFastBonusPercent = 50;

    public const RunBonusFormula DefaultRunBonus = RunBonusFormula.Multiplied;

    /// <summary>A chasing monster stops this many subtiles short of its target.</summary>
    public const double DefaultChaseStopDistance = 2;

    /// <summary>25 frames a second; Velocity is in sixteenths of a subtile per frame (trace-fitted, 0.95-1.00 for 15 of 17 classes).</summary>
    private const double SubtilesPerSecondPerVelocity = 25.0 / 16;

    public static MotionRules Default { get; } = new();

    /// <summary>Null reads <see cref="MonsterSpeedTable.Shared"/>.</summary>
    public MonsterSpeedTable Speeds { get; init; }

    public RunBonusFormula RunBonus { get; init; } = DefaultRunBonus;

    public int ExtraFastBonus { get; init; } = ExtraFastBonusPercent;

    public double ChaseStopDistance { get; init; } = DefaultChaseStopDistance;

    /// <summary>Adds the class's MeleeRng (monstats.bin +457, unconfirmed offset) to <see cref="ChaseStopDistance"/>.</summary>
    public bool ChaseStopsAtMeleeRange { get; init; }

    /// <summary>
    /// The one place a speed factor applies: a multiplier on the computed speed, 1 when null. Chill,
    /// Holy Freeze, Decrepify, Slow Missiles and champions are not measured yet; a factor for them goes
    /// here once a recording settles its value.
    /// </summary>
    public Func<MotionUnit, double> SpeedFactor { get; init; }

    public MonsterSpeedTable SpeedTable => Speeds ?? MonsterSpeedTable.Shared;

    /// <summary>Subtiles per second; 0 when the class's speed is unknown, which leaves the monster at its anchor.</summary>
    public double Speed(MotionUnit unit, int runBonus)
    {
        if (!SpeedTable.TryGet(unit.Code, out var speed) || speed.Velocity <= 0)
        {
            return 0;
        }

        var basePercent = BaseVelocityPercent + (unit.ExtraFast ? ExtraFastBonus : 0);
        var percent = RunBonus == RunBonusFormula.Multiplied
            ? basePercent * (1 + runBonus / 100.0)
            : basePercent + runBonus;
        var factor = SpeedFactor?.Invoke(unit) ?? 1;
        return speed.Velocity * percent / 100 * SubtilesPerSecondPerVelocity * factor;
    }

    public static MotionSnapshot Start(Point position, long now, byte packet)
        => new(position, now, position.X, position.Y, now, MotionMode.Standing, null, EntityType.Player, 0, 0, 0, 0, packet);

    /// <summary>
    /// The snapshot after <paramref name="update"/>. <paramref name="positionNow"/> is where
    /// <paramref name="previous"/> puts the monster at <paramref name="now"/>: a 0x67 carries no start
    /// position, so the walk starts from there.
    /// </summary>
    public MotionSnapshot Next(MotionSnapshot previous, in MotionUpdate update, long now, (double X, double Y) positionNow, MotionUnit unit)
    {
        if (update.Kind == MotionUpdateKind.Assign || (previous == null && update.HasExactPosition))
        {
            return Start(update.Position, now, update.Packet) with { Mode = update.Kind == MotionUpdateKind.Dead ? MotionMode.Dead : MotionMode.Standing };
        }

        if (previous == null)
        {
            return null;
        }

        return update.Kind switch
        {
            MotionUpdateKind.MoveToPoint => previous with
            {
                AnchorX = positionNow.X,
                AnchorY = positionNow.Y,
                AnchorAt = now,
                Mode = MotionMode.MovingToPoint,
                Destination = update.Position,
                TargetId = 0,
                Speed = Speed(unit, update.RunBonus),
                PathType = update.PathType,
                RunBonus = update.RunBonus,
                LastPacket = update.Packet,
            },
            MotionUpdateKind.MoveToUnit => Exact(previous, update, now, MotionMode.MovingToUnit) with
            {
                TargetType = update.TargetType,
                TargetId = update.TargetId,
                Speed = Speed(unit, update.RunBonus),
                PathType = update.PathType,
                RunBonus = update.RunBonus,
            },
            MotionUpdateKind.Action or MotionUpdateKind.Attack => Exact(previous, update, now, MotionMode.Acting) with
            {
                TargetType = update.TargetType,
                TargetId = update.TargetId,
            },
            MotionUpdateKind.Stop or MotionUpdateKind.Hit or MotionUpdateKind.Snap => Exact(previous, update, now, MotionMode.Standing),
            MotionUpdateKind.Dead => Exact(previous, update, now, MotionMode.Dead),
            MotionUpdateKind.Dying => previous with
            {
                AnchorX = positionNow.X,
                AnchorY = positionNow.Y,
                AnchorAt = now,
                Mode = MotionMode.Dead,
                Destination = null,
                Speed = 0,
                LastPacket = update.Packet,
            },
            _ => previous,
        };
    }

    private static MotionSnapshot Exact(MotionSnapshot previous, in MotionUpdate update, long now, MotionMode mode) => previous with
    {
        Exact = update.Position,
        ExactAt = now,
        AnchorX = update.Position.X,
        AnchorY = update.Position.Y,
        AnchorAt = now,
        Mode = mode,
        Destination = null,
        TargetId = 0,
        Speed = 0,
        LastPacket = update.Packet,
    };

    /// <summary>
    /// Where <paramref name="snapshot"/> puts the monster <paramref name="seconds"/> after its anchor: on the
    /// straight line toward the destination, or toward <paramref name="target"/> stopping short of it,
    /// capped at the end. A chase whose target is unknown stays at the anchor.
    /// </summary>
    public (double X, double Y) PositionAt(MotionSnapshot snapshot, double seconds, Point target, MotionUnit unit)
    {
        var anchor = (snapshot.AnchorX, snapshot.AnchorY);
        if (!snapshot.IsMoving || snapshot.Speed <= 0)
        {
            return anchor;
        }

        var travel = snapshot.Speed * Math.Max(0, seconds);
        if (snapshot.Mode == MotionMode.MovingToPoint)
        {
            return Step(anchor, (snapshot.Destination.X, snapshot.Destination.Y), travel);
        }

        if (target == null)
        {
            return anchor;
        }

        var goal = ((double)target.X, (double)target.Y);
        var length = Distance(anchor, goal);
        var stop = ChaseStop(unit);
        if (length <= stop)
        {
            return anchor;
        }

        return Step(anchor, Step(anchor, goal, length - stop), travel);
    }

    public double ChaseStop(MotionUnit unit)
        => ChaseStopDistance + (ChaseStopsAtMeleeRange && SpeedTable.TryGet(unit.Code, out var speed) ? speed.MeleeRange : 0);

    public static Point ToPoint((double X, double Y) position)
        => new((ushort)Math.Clamp(Math.Round(position.X), 0, ushort.MaxValue), (ushort)Math.Clamp(Math.Round(position.Y), 0, ushort.MaxValue));

    public static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static (double X, double Y) Step((double X, double Y) from, (double X, double Y) to, double distance)
    {
        var length = Distance(from, to);
        if (length <= distance || length == 0)
        {
            return to;
        }

        return (from.X + (to.X - from.X) * distance / length, from.Y + (to.Y - from.Y) * distance / length);
    }
}
