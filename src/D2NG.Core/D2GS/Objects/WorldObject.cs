using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Motion;
using System;
using System.Collections.Generic;

namespace D2NG.Core.D2GS.Objects;

public class WorldObject : Entity
{
    public EntityCode Code { get; internal set; }

    public NPCCode NPCCode { get; internal set; }
    public EntityState State { get; internal set; }
    public byte InteractionType { get; }

    public double LifePercentage { get; internal set; } = 100;

    public Area TownPortalArea { get; internal set; }

    public uint TownPortalOwnerId { get; internal set; }

    public HashSet<EntityEffect> Effects { get; internal set; } = [];

    public HashSet<MonsterEnchantment> MonsterEnchantments { get; internal set; } = [];

    /// <summary>A unique monster (the boss flag of the NPC assignment), super uniques included.</summary>
    public bool IsBoss { get; internal set; }

    public bool IsChampion { get; internal set; }

    public bool IsBossMinion { get; internal set; }

    private volatile Point _legacyLocation;
    private volatile MotionSnapshot _motion;
    private MotionContext _motionContext;
    private MotionUnit _motionUnit;

    /// <summary>
    /// For a monster, what <see cref="Game.EffectiveMonsterMotion"/> selects: <see cref="LegacyLocation"/>,
    /// <see cref="LastKnownLocation"/> or <see cref="SimulatedLocation"/>. Setting it sets the legacy value.
    /// </summary>
    public override Point Location
    {
        get
        {
            var context = _motionContext;
            return context == null ? _legacyLocation : LocationIn(context.EffectiveMode);
        }
        internal set => _legacyLocation = value;
    }

    /// <summary>What <see cref="Location"/> returns in <paramref name="mode"/>, Simulated falling back to Legacy without speed data.</summary>
    internal Point LocationIn(MonsterMotion mode)
    {
        var context = _motionContext;
        if (context == null)
        {
            return _legacyLocation;
        }

        if (mode == MonsterMotion.Simulated)
        {
            mode = MonsterMotionSettings.Effective(mode, context.Rules.SpeedTable);
        }

        return mode switch
        {
            MonsterMotion.Anchored => LastKnownLocation,
            MonsterMotion.Simulated => SimulatedLocation,
            _ => _legacyLocation,
        };
    }

    /// <summary>The position as handled before motion tracking: a 0x67 destination counts as the position.</summary>
    public Point LegacyLocation => _legacyLocation;

    /// <summary>Everything known about the monster's movement, published whole after each packet; null for other units.</summary>
    public MotionSnapshot Motion => _motion;

    /// <summary>The last position a packet said the monster was at.</summary>
    public Point LastKnownLocation => _motion?.Exact ?? _legacyLocation;

    /// <summary>How long ago <see cref="LastKnownLocation"/> arrived; zero for other units.</summary>
    public TimeSpan LocationAge
    {
        get
        {
            var (motion, context) = (_motion, _motionContext);
            return motion == null || context == null ? TimeSpan.Zero : TimeSpan.FromSeconds(context.Seconds(motion.ExactAt, context.Now));
        }
    }

    /// <summary>Where a 0x67 sends the monster, while it walks there.</summary>
    public Point Destination => _motion?.Destination;

    public MotionMode MotionMode => _motion?.Mode ?? MotionMode.Standing;

    /// <summary>Subtiles per second of the current move.</summary>
    public double Speed => _motion?.Speed ?? 0;

    public byte PathType => _motion?.PathType ?? 0;

    /// <summary>The unit a 0x68 has the monster chasing.</summary>
    public uint? ChaseTargetId => _motion is { Mode: MotionMode.MovingToUnit } motion ? motion.TargetId : null;

    /// <summary>The last known position moved along the current walk or chase to now.</summary>
    public Point SimulatedLocation => _motion == null ? LastKnownLocation : MotionRules.ToPoint(SimulatedPosition);

    public (double X, double Y) SimulatedPosition
    {
        get
        {
            var (motion, context) = (_motion, _motionContext);
            if (motion == null || context == null)
            {
                var location = LastKnownLocation;
                return location == null ? default : (location.X, location.Y);
            }

            return PositionAt(motion, context, context.Now);
        }
    }

    private (double X, double Y) PositionAt(MotionSnapshot motion, MotionContext context, long now)
    {
        var target = motion.Mode == MotionMode.MovingToUnit ? context.Target(motion.TargetType, motion.TargetId) : null;
        return context.Rules.PositionAt(motion, context.Seconds(motion.AnchorAt, now), target, _motionUnit);
    }

    internal MotionUnit MotionUnit => _motionUnit;

    /// <summary>Called on the assigning packet, before the object is published to other threads.</summary>
    internal void AttachMotion(MotionContext context, in MotionUpdate assign)
    {
        _motionContext = context;
        _motionUnit = new MotionUnit(NPCCode, MonsterEnchantments.Contains(MonsterEnchantment.ExtraFast), IsChampion, IsBossMinion, IsBoss && !IsChampion);
        _motion = context.Rules.Next(null, assign, context.Now, default, _motionUnit);
    }

    /// <summary>Runs on the receive thread only, so a snapshot is never replaced by two writers at once.</summary>
    internal void ApplyMotion(in MotionUpdate update)
    {
        var context = _motionContext;
        var previous = _motion;
        if (context == null || previous == null)
        {
            return;
        }

        var now = context.Now;
        var next = context.Rules.Next(previous, update, now, PositionAt(previous, context, now), _motionUnit);
        if (next != null)
        {
            _motion = next;
        }
    }

    public WorldObject(EntityType objectType, uint objectId, EntityCode objectCode, Point location, EntityState state, byte interactionType)
    {
        Type = objectType;
        Id = objectId;
        Code = objectCode;
        Location = location;
        State = state;
        InteractionType = interactionType;
    }
}