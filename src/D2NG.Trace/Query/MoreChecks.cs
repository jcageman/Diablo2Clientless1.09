using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>Every threshold of the incident checks, in one place.</summary>
public static class CheckThresholds
{
    /// <summary>A live monster this close that has had no packet this long is likely gone from the server.</summary>
    public static readonly TimeSpan StaleUnit = TimeSpan.FromSeconds(30);

    /// <summary>Subtiles. The server removes monsters (0x0A) from about 45 subtiles out (measured on the leveling and Mephisto traces: none under 40, median 79).</summary>
    public const double SightRange = 40;

    /// <summary>Standing off the walkable map this long after the area last changed.</summary>
    public static readonly TimeSpan NotWalkable = TimeSpan.FromSeconds(2);

    /// <summary>Subtiles: the map's walkable edge is often a subtile off where characters stand, so a position this close to ground counts as standing on its edge - unless it is inside a one-subtile wall.</summary>
    public const int WalkableReach = 2;

    /// <summary>Walkable ground this long ends a not-walkable episode; shorter, the episode goes on, so a walk across unwalkable ground is one incident.</summary>
    public static readonly TimeSpan NotWalkableEpisodeGap = TimeSpan.FromSeconds(1);

    /// <summary>Subtiles: a server move shorter than this is a nudge, not a correction that answers a stand on unwalkable ground.</summary>
    public const double ServerCorrection = 4;

    /// <summary>A server move this soon after a waypoint, door or other object interaction is that warp, not a correction.</summary>
    public static readonly TimeSpan WarpExplains = TimeSpan.FromSeconds(1);

    /// <summary>Subtiles between where the client held itself and where the server put it.</summary>
    public const double PositionHop = 15;

    /// <summary>Walking or running this long with no position from the server, then corrected by more than <see cref="UnconfirmedCorrection"/>.</summary>
    public static readonly TimeSpan UnconfirmedMoving = TimeSpan.FromSeconds(10);

    /// <summary>Subtiles between where the client held itself and where the server put it, after <see cref="UnconfirmedMoving"/>.</summary>
    public const double UnconfirmedCorrection = 15;

    /// <summary>The client's own position outside its revealed map while the server's is inside it, this long.</summary>
    public static readonly TimeSpan OffMap = TimeSpan.FromSeconds(2);

    /// <summary>Subtiles: own positions the server states this close to the first of them are one held position.</summary>
    public const double HeldRadius = 3;

    /// <summary>Statements at one held position, over at least <see cref="HeldSpan"/>.</summary>
    public const int HeldStatements = 5;

    public static readonly TimeSpan HeldSpan = TimeSpan.FromSeconds(5);

    /// <summary>Subtiles the believed position got from the held one.</summary>
    public const double HeldBelievedDistance = 30;

    /// <summary>Subtiles from the held position to a walk or run target that counts as moving away.</summary>
    public const double HeldMoveTarget = 10;

    /// <summary>Cursor packets with no item packet back, and over how long, before the cursor counts as stuck.</summary>
    public const int UnansweredCursorPackets = 3;

    public static readonly TimeSpan UnansweredCursorSpan = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan LifeAboveMax = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan Stuck = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan UnansweredAttacks = TimeSpan.FromSeconds(20);

    public const int PathFailures = 3;

    public const double PathFailureRadius = 10;

    public static readonly TimeSpan PathFailureWindow = TimeSpan.FromSeconds(60);

    public const int HotLoopLines = 20;

    public static readonly TimeSpan QuickFailure = TimeSpan.FromSeconds(10);

    public const double SlowStepFactor = 3;

    public const int SlowStepHistory = 3;

    public static readonly TimeSpan PartySplit = TimeSpan.FromSeconds(60);

    public const double GoldDropShare = 0.5;
}

/// <summary>What a check sees once a client's replay is done: the game, the client's activity, the steps and the history of earlier games.</summary>
public sealed class CheckContext
{
    public GameQuery Query { get; init; }

    public string Client { get; init; }

    public ActivityObserver Activity { get; init; }

    public IReadOnlyList<StepSpan> Steps { get; init; }

    public TraceHistory History { get; init; }

    public MapSource Maps { get; init; }

    public uint MapId { get; init; }

    public Difficulty Difficulty { get; init; }

    /// <summary>The client's attacks, hits and skill moves; null when the replay did not collect them.</summary>
    public CombatObserver Combat { get; init; }

    public List<string> Notes { get; } = [];
}

/// <summary>A check of the "state" family (the client's state disagrees with the server) or the "bot" family (the bot is stuck or being hurt).</summary>
public interface IFamily
{
    string Family { get; }
}

/// <summary>
/// A live monster in sight that the client went for at least three times, but that has had no packet
/// for a long time. When its last skill was Submerge (watchers) or burrowing (maggots, and Death Maulers'
/// Deathmaul, skill 308, which travels under the ground), it is under the
/// ground: the server still has it but sends nothing until it comes up, and the attacks are wasted -
/// reported as "attacking a submerged unit". Otherwise the client's state likely holds a unit the
/// server no longer has (died out of sight, behind a wall): "stale unit". A monster the client never
/// attacked is left out - sleeping monsters in sight get no packets either.
/// </summary>
public sealed class StaleUnitCheck : IncidentCheck, IFamily
{
    public const string SubmergedKind = "attacking a submerged unit";

    public const int MinimumPackets = 3;

    private readonly Dictionary<uint, TimeSpan> _lastPacket = [];
    private readonly Dictionary<uint, (TimeSpan Last, int Count)> _attacked = [];
    private readonly Dictionary<uint, (byte[] Payload, bool Cast)> _lastWhat = [];

    /// <summary>What a unit's last packet was, as the report words it; worked out only for a unit that is reported.</summary>
    private string LastWhat(uint id)
    {
        if (!_lastWhat.TryGetValue(id, out var last))
        {
            return null;
        }

        var name = PacketDecoder.Name(TraceRecordKind.D2gsReceived, last.Payload);
        return last.Cast ? $"{name} {Names.Skill(PacketDecoder.U16(last.Payload, 6))}" : name;
    }
    private readonly HashSet<uint> _submerged = [];
    private readonly Dictionary<uint, (string Skill, TimeSpan At)> _wentUnder = [];
    private readonly HashSet<uint> _reported = [];

    /// <summary>Skills a monster uses to go under the ground, and to come back up.</summary>
    public static bool GoesUnder(Skill skill) => skill is Skill.Submerge or Skill.Magottdown or Skill.Deathmaul;

    public static bool ComesUp(Skill skill) => skill is Skill.Emerge or Skill.Magottup;
    private TimeSpan _scannedAt = TimeSpan.FromDays(-1000);

    public string Family => "state";

    public override void After(ReplayContext context, TraceRecord record)
    {
        var units = PacketQueries.UnitsIn(record, out var first, out var second);
        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            for (var i = 0; i < units; i++)
            {
                var (type, id, _) = i == 0 ? first : second;
                if (type == EntityType.NPC && _lastPacket.TryGetValue(id, out var lastPacket))
                {
                    var known = _attacked.TryGetValue(id, out var attacked) && attacked.Last > lastPacket ? attacked.Count : 0;
                    _attacked[id] = (context.Now, known + 1);
                }
            }

            return;
        }

        if (record.Kind != TraceRecordKind.D2gsReceived)
        {
            return;
        }

        for (var i = 0; i < units; i++)
        {
            var (type, id, _) = i == 0 ? first : second;
            if (type == EntityType.NPC)
            {
                _lastPacket[id] = context.Now;
                var cast = record.Payload[0] is 0x4C or 0x4D && record.Payload.Length >= 8 && PacketDecoder.U32(record.Payload, 2) == id;
                _lastWhat[id] = (record.Payload, cast);
                if (cast)
                {
                    var skill = (Skill)PacketDecoder.U16(record.Payload, 6);
                    if (GoesUnder(skill))
                    {
                        _submerged.Add(id);
                        _wentUnder[id] = (Names.Skill(PacketDecoder.U16(record.Payload, 6)), context.Now);
                    }
                    else
                    {
                        _submerged.Remove(id);
                    }
                }
                else if (record.Payload[0] is 0x67 or 0x68 or 0x69)
                {
                    _submerged.Remove(id);
                }

                _reported.Remove(id);
            }
        }

        if (context.Now - _scannedAt < TimeSpan.FromSeconds(1) || context.Game.Me == null || context.Position == null)
        {
            return;
        }

        _scannedAt = context.Now;
        List<WorldObject> stale = null;
        foreach (var attackedId in _attacked.Keys)
        {
            if (context.Npc(attackedId) is not { } unit)
            {
                continue;
            }

            if (unit.Type != EntityType.NPC || unit.State is EntityState.Dead or EntityState.Dieing || unit.LifePercentage <= 0
                || !_lastPacket.TryGetValue(unit.Id, out var last) || context.Now - last < CheckThresholds.StaleUnit
                || context.DistanceTo(unit.Location) > CheckThresholds.SightRange
                || !_attacked.TryGetValue(unit.Id, out var attacked) || attacked.Last <= last || attacked.Count < MinimumPackets || _reported.Contains(unit.Id))
            {
                continue;
            }

            (stale ??= []).Add(unit);
        }

        foreach (var unit in stale?.OrderBy(u => u.Id) ?? Enumerable.Empty<WorldObject>())
        {
            var last = _lastPacket[unit.Id];
            var attacked = _attacked[unit.Id];
            _reported.Add(unit.Id);

            var name = context.UnitName(unit.Id);
            var where = $"at {Names.Point(unit.Location)}, {context.DistanceTo(unit.Location):0} away, life {unit.LifePercentage:0}%";
            var silent = $"none back for {(context.Now - last).TotalSeconds:0}s";
            ReportUnit(context, last, _submerged.Contains(unit.Id) ? SubmergedKind : "stale unit", name, attacked.Count,
                _submerged.Contains(unit.Id)
                    ? $"{name} {where}: went under ({_wentUnder[unit.Id].Skill}) at {TraceTime.Format(_wentUnder[unit.Id].At)} and has not come up since; last packet ({LastWhat(unit.Id)}) at {TraceTime.Format(last)}, {silent}; the server sends nothing about a unit while it is under, so attacks at it are wasted"
                    : $"{name} {where}: last packet ({LastWhat(unit.Id)}) at {TraceTime.Format(last)}, {silent}; the client still counts it alive, the server likely no longer has it",
                context.Now);
        }
    }

    private void ReportUnit(ReplayContext context, TimeSpan offset, string kind, string subject, int count, string detail, TimeSpan end)
        => AddFound(new Incident(offset, context.Client, kind, detail) { End = end, Subject = subject, Count = count });

    public override void Finished(ReplayContext context)
    {
        _lastPacket.Clear();
        _lastWhat.Clear();
        _attacked.Clear();
        _submerged.Clear();
        _wentUnder.Clear();
        _reported.Clear();
    }
}

/// <summary>The server moved the client somewhere other than where it held itself, with no teleport, interaction or area change to explain it.</summary>
public sealed class PositionMismatchCheck : IncidentCheck, IFamily
{
    private static readonly TimeSpan Explained = TimeSpan.FromSeconds(3);
    private Point _held;
    private TimeSpan _lastExplanation = TimeSpan.FromDays(-1000);
    private TimeSpan _lastReport = TimeSpan.FromDays(-1000);
    private int _repeats;

    public string Family => "state";

    public override void Before(ReplayContext context, TraceRecord record)
    {
        _held = context.Position == null ? null : new Point(context.Position.X, context.Position.Y);
        if (record.Kind == TraceRecordKind.D2gsSent && record.Payload.Length > 0 && record.Payload[0] is 0x0C or 0x0F or 0x05 or 0x08 or 0x13 or 0x49)
        {
            _lastExplanation = context.Now;
        }

        if (record.Kind == TraceRecordKind.D2gsReceived && record.Payload.Length > 0 && record.Payload[0] is 0x03 or 0x07 or 0x09)
        {
            _lastExplanation = context.Now;
        }
    }

    public override void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (record.Kind != TraceRecordKind.D2gsReceived || p.Length < 10 || _held == null || (_held.X == 0 && _held.Y == 0) || context.SelfId == null)
        {
            return;
        }

        Point confirmed;
        string how;
        if (p[0] == 0x15 && p[1] == (byte)EntityType.Player && PacketDecoder.U32(p, 2) == context.SelfId)
        {
            confirmed = new Point(PacketDecoder.U16(p, 6), PacketDecoder.U16(p, 8));
            how = "reassign";
        }
        else if (p[0] == 0x96)
        {
            try
            {
                confirmed = new Core.D2GS.Packet.Incoming.WalkVerifyPacket(new Core.D2GS.Packet.D2gsPacket(p)).Location;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return;
            }

            how = "walk verify";
        }
        else
        {
            return;
        }

        var distance = confirmed.Distance(_held);
        if (distance <= CheckThresholds.PositionHop || context.Now - _lastExplanation <= Explained)
        {
            return;
        }

        if (context.Now - _lastReport < TimeSpan.FromSeconds(10))
        {
            _repeats++;
            return;
        }

        _lastReport = context.Now;
        Report(context, context.Now, "position mismatch",
            $"server {how} to {Names.Point(confirmed)} while the client held {Names.Point(_held)}, {distance:0} apart, with no teleport, interaction or warp in the {Explained.TotalSeconds:0}s before");
    }
}

/// <summary>
/// The cursor holds an item while the client keeps sending item packets that nothing answers - the
/// emptied quiver the client still believes it holds. An item packet that changes what the cursor holds
/// is the answer; one about another item is not. A brief hold while moving items is answered at once.
/// </summary>
public sealed class StuckCursorCheck : IncidentCheck, IFamily
{
    /// <summary>The packets that move an item to, from or through the cursor, which the server answers with an item packet. Attacks and interactions are not among them.</summary>
    public static readonly IReadOnlySet<byte> CursorPackets = new[]
    {
        "PickItem", "DropItem", "InsertItemToBuffer", "RemoveItemFromBuffer", "EquipItem", "Swap2HItem", "RemoveBodyItem", "SwapCursorWithBody",
        "Swap1HItemWith2HItem", "SwapCursorAndBufferItem", "StackItems", "UnstackItems", "ItemToBelt", "ItemFromBelt", "SwapBeltItem",
        "SocketItem", "ScrollToBook", "ItemToCube", "SendItemToBelt", "DropOrEquipMercItem",
    }.Select(n => (byte)Enum.Parse<Core.D2GS.Packet.OutGoingPacket>(n)).ToHashSet();

    private int _unanswered;
    private TimeSpan _firstUnanswered;
    private TimeSpan _lastUnanswered;
    private int _attacks;
    private string _item;
    private uint _itemId;
    private uint? _heldId;
    private TimeSpan _heldSince;
    private readonly Dictionary<byte, int> _types = [];

    public string Family => "state";

    public override void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (p.Length == 0)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsReceived && p[0] is 0x9C or 0x9D)
        {
            var held = context.Game.CursorItem;
            if (held?.Id != _heldId)
            {
                _heldId = held?.Id;
                _heldSince = context.Now;
            }

            if (_unanswered > 0 && (held == null || held.Id != _itemId))
            {
                Close(context);
            }

            return;
        }

        if (record.Kind != TraceRecordKind.D2gsSent)
        {
            return;
        }

        if (p[0] is >= 0x05 and <= 0x11)
        {
            if (_unanswered > 0)
            {
                _attacks++;
            }

            return;
        }

        if (!CursorPackets.Contains(p[0]))
        {
            return;
        }

        var cursor = context.Game.CursorItem;
        if (cursor == null)
        {
            Close(context);
            return;
        }

        if (_unanswered > 0 && cursor.Id != _itemId)
        {
            Close(context);
        }

        if (_heldId != cursor.Id)
        {
            _heldId = cursor.Id;
            _heldSince = context.Now;
        }

        if (_unanswered == 0)
        {
            _firstUnanswered = context.Now;
            _item = $"{cursor.Name} {Names.Id(cursor.Id)}";
            _itemId = cursor.Id;
        }

        _unanswered++;
        _lastUnanswered = context.Now;
        _types[p[0]] = _types.GetValueOrDefault(p[0]) + 1;
    }

    public override void Finished(ReplayContext context)
    {
        Close(context);
        _heldId = null;
    }

    private void Close(ReplayContext context)
    {
        var span = _lastUnanswered - _firstUnanswered;
        if (_unanswered >= CheckThresholds.UnansweredCursorPackets && span >= CheckThresholds.UnansweredCursorSpan)
        {
            Report(context, _firstUnanswered, "stuck cursor",
                $"cursor held {_item} for {(context.Now - _heldSince).TotalSeconds:0}s; {_unanswered} cursor packets went unanswered over {span.TotalSeconds:0}s, one every {span.TotalSeconds / (_unanswered - 1):0.0}s ({string.Join(", ", _types.OrderByDescending(t => t.Value).Select(t => $"{Names.Outgoing(t.Key)} x{t.Value}"))}); no item packet came back"
                + (_attacks > 0 ? $"; the client kept attacking meanwhile ({_attacks} attack packets)" : ""),
                _lastUnanswered);
        }

        _unanswered = 0;
        _attacks = 0;
        _types.Clear();
    }
}

/// <summary>Life above the computed maximum for longer than a moment: the maximum, or the life, is wrong.</summary>
public sealed class LifeAboveMaxCheck : IncidentCheck, IFamily
{
    private TimeSpan? _since;
    private int _worst;
    private int _max;

    public string Family => "state";

    public override void After(ReplayContext context, TraceRecord record)
    {
        if (record.Kind != TraceRecordKind.D2gsReceived || record.Payload.Length == 0 || record.Payload[0] is not (0x18 or 0x95))
        {
            return;
        }

        var max = context.MaxLife;
        if (max > 0 && context.Life > max)
        {
            _since ??= context.Now;
            _worst = Math.Max(_worst, context.Life);
            _max = max;
            return;
        }

        Close(context);
    }

    public override void Finished(ReplayContext context) => Close(context);

    private void Close(ReplayContext context)
    {
        if (_since is { } since && context.Now - since >= CheckThresholds.LifeAboveMax)
        {
            Report(context, since, "life above max", $"life up to {_worst} above the computed maximum {_max} for {(context.Now - since).TotalSeconds:0.0}s", context.Now);
        }

        _since = null;
        _worst = 0;
    }
}

/// <summary>
/// A client attacking one monster for more than <see cref="CheckThresholds.UnansweredAttacks"/> while the server says
/// nothing happened to it - no NPCHit, no NPCState, no state of the attacking skill's element (Cold after Ice Blast,
/// Poison after Poison Nova) - and its life stays at 100% in every packet that carries it (the assign, NPCStop,
/// NPCState, NPCHit; the replayed Game keeps no life from a stop): a unit that cannot be damaged (the
/// friendly angel at the River of Flame waypoint, class 408, NPCCode.Hadriel) or one the attacks never
/// reach. Attacks are skills sent at the unit and skills sent at a point within a few subtiles of it (Concentrate and
/// Blessed Hammer are cast at a point). One incident per unit and client per episode, written when the episode ends -
/// five seconds without an attack at it, a hit, or the end of the file - with its whole length; the same unit
/// attacked by several clients at once is one line.
/// </summary>
public sealed class AttackingWithoutHitsCheck : IncidentCheck, IFamily
{
    public const string Kind = "attacking without hits";

    private const double PointReach = 4;
    private static readonly TimeSpan EpisodeGap = TimeSpan.FromSeconds(5);

    private static readonly HashSet<Skill> ColdSkills =
        [Skill.ColdArrow, Skill.IceArrow, Skill.FreezingArrow, Skill.IceBolt, Skill.FrostNova, Skill.IceBlast, Skill.GlacialSpike, Skill.Blizzard, Skill.FrozenOrb];

    private static readonly HashSet<Skill> PoisonSkills = [Skill.PoisonJavelin, Skill.PlagueJavelin, Skill.PoisonDagger, Skill.PoisonExplosion, Skill.PoisonNova];

    private readonly Dictionary<uint, Episode> _episodes = [];

    private readonly Dictionary<uint, double> _life = [];

    private ReplayContext _context;

    public string Family => "state";

    private sealed class Episode
    {
        public TimeSpan First;
        public TimeSpan Last;
        public int Count;
        public bool Answered;
        public readonly Dictionary<string, int> Skills = [];
        public readonly HashSet<Skill> Used = [];
    }

    /// <summary>
    /// The NPC and its life, 0-100, that a received packet states; null for a packet that states none. A stop stating 0
    /// states none: town folk and other units that take no damage stop with 0 while alive.
    /// </summary>
    internal static (uint Id, double Life)? LifeOf(byte[] p)
    {
        if (p[0] is not ((byte)InComingPacket.NPCStop or (byte)InComingPacket.NPCState or (byte)InComingPacket.NPCHit or (byte)InComingPacket.AssignNPC1 or (byte)InComingPacket.AssignNPC2))
        {
            return null;
        }

        try
        {
            var packet = new D2gsPacket(p);
            return p[0] switch
            {
                (byte)InComingPacket.NPCStop => new NPCStopPacket(packet) is { LifePercentage: > 0 } stop ? (stop.EntityId, stop.LifePercentage) : null,
                (byte)InComingPacket.NPCState => new NpcStatePacket(packet) is { EntityState: not (EntityState.Dieing or EntityState.Dead) } state ? (state.EntityId, state.LifePercentage) : null,
                (byte)InComingPacket.NPCHit when p.Length > 1 && p[1] == (byte)EntityType.NPC => new NpcHitPacket(packet) is var hit ? (hit.EntityId, hit.LifePercentage) : null,
                (byte)InComingPacket.AssignNPC1 or (byte)InComingPacket.AssignNPC2 => new AssignNpcPacket(packet) is var assign ? (assign.EntityId, assign.LifePercentage) : null,
                _ => null,
            };
        }
        catch (Exception e) when (e is D2GSPacketException or ArgumentException or IndexOutOfRangeException or System.IO.EndOfStreamException)
        {
            return null;
        }
    }

    private static bool Matches(EntityEffect effect, IEnumerable<Skill> used)
        => used.Any(skill => (ColdSkills.Contains(skill) && effect is EntityEffect.Cold or EntityEffect.Freeze)
            || (PoisonSkills.Contains(skill) && effect is EntityEffect.Poison));

    public override void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (p.Length == 0)
        {
            return;
        }

        if (!ReferenceEquals(context, _context))
        {
            _context = context;
            _life.Clear();
        }

        List<KeyValuePair<uint, Episode>> ended = null;
        foreach (var open in _episodes)
        {
            if (context.Now - open.Value.Last >= EpisodeGap)
            {
                (ended ??= []).Add(open);
            }
        }

        foreach (var (endedId, endedEpisode) in ended ?? [])
        {
            Close(context, endedId, endedEpisode);
        }

        if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            if (LifeOf(p) is var (lifeOf, life))
            {
                _life[lifeOf] = life;
                if (life < 99.5 && _episodes.TryGetValue(lifeOf, out var damaged))
                {
                    damaged.Answered = true;
                }
            }

            if (p[0] is (byte)InComingPacket.AddEntityEffect or (byte)InComingPacket.AddEntityEffect2 && p.Length >= 10 && p[1] == (byte)EntityType.NPC
                && _episodes.TryGetValue(PacketDecoder.U32(p, 2), out var affected) && Matches((EntityEffect)PacketDecoder.U32(p, 6), affected.Used))
            {
                affected.Answered = true;
            }

            var answered = p[0] switch
            {
                0x0C when p.Length >= 6 && p[1] == (byte)EntityType.NPC => PacketDecoder.U32(p, 2),
                0x69 when p.Length >= 5 => PacketDecoder.U32(p, 1),
                _ => (uint?)null,
            };
            if (answered is { } hit && _episodes.TryGetValue(hit, out var touched))
            {
                touched.Answered = true;
            }

            return;
        }

        if (record.Kind != TraceRecordKind.D2gsSent || context.Game.Me == null)
        {
            return;
        }

        uint? target = null;
        if (p[0] is 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11 && p.Length >= 9 && PacketDecoder.U32(p, 1) == (uint)EntityType.NPC)
        {
            target = PacketDecoder.U32(p, 5);
        }
        else if (p[0] is 0x05 or 0x08 or 0x0C or 0x0F && p.Length >= 5)
        {
            var point = new Point(PacketDecoder.U16(p, 1), PacketDecoder.U16(p, 3));
            target = NearestMonster(context, point);
        }

        if (target is not { } attacked)
        {
            return;
        }

        if (!_episodes.TryGetValue(attacked, out var episode))
        {
            episode = new Episode { First = context.Now };
            _episodes[attacked] = episode;
        }

        episode.Last = context.Now;
        episode.Count++;
        var hand = p[0] <= 0x0A ? Hand.Left : Hand.Right;
        var skill = context.Game.Me.ActiveSkills.TryGetValue(hand, out var active) ? active.ToString() : $"{hand.ToString().ToLowerInvariant()} skill";
        episode.Skills[skill] = episode.Skills.GetValueOrDefault(skill) + 1;
        if (context.Game.Me.ActiveSkills.TryGetValue(hand, out var used))
        {
            episode.Used.Add(used);
        }
    }

    private static uint? NearestMonster(ReplayContext context, Point point)
    {
        try
        {
            return context.Game.WorldObjects.Values
                .Where(u => u.Type == EntityType.NPC && u.Location != null && u.State is not (EntityState.Dead or EntityState.Dieing) && u.Location.Distance(point) <= PointReach)
                .OrderBy(u => u.Location.Distance(point))
                .ThenBy(u => u.Id)
                .Select(u => (uint?)u.Id)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Close(ReplayContext context, uint id, Episode episode)
    {
        _episodes.Remove(id);
        var length = episode.Last - episode.First;
        var unit = context.Npc(id);
        var life = _life.TryGetValue(id, out var stated) ? stated : unit?.LifePercentage ?? 100;
        if (episode.Answered || length < CheckThresholds.UnansweredAttacks || unit == null || life < 99.5)
        {
            return;
        }

        var code = context.CodeOf(id);
        var name = context.UnitName(id);
        var friendly = code is { } known && Core.MonsterData.FriendlyUnits.IsFriendly(known);
        var skills = string.Join(", ", episode.Skills.OrderByDescending(s => s.Value).Select(s => $"{s.Key} x{s.Value}"));
        AddFound(new Incident(episode.First, context.Client, Kind,
            $"{name} (class {(code is { } c ? ((int)c).ToString(CultureInfo.InvariantCulture) : "?")}{(friendly ? ", in the friendly/neutral list" : ", not in the friendly/neutral list")}) at {Names.Point(unit.Location)}, life {life:0}% throughout, no NPCHit, NPCState or state of the attacking skill for it")
        {
            End = episode.Last,
            Subject = name,
            Count = episode.Count,
            PerClient = $"{episode.Count} attacks ({skills}) from {TraceTime.Format(episode.First)} over {length.TotalSeconds:0}s, {length.TotalSeconds:0}s wasted",
        });
    }

    public override void Finished(ReplayContext context)
    {
        foreach (var (id, episode) in _episodes.ToList())
        {
            Close(context, id, episode);
        }

        _episodes.Clear();
    }
}

/// <summary>Attacks sent at one unit that takes no hit (NPCHit) for a long time: out of reach, immune, behind a wall, or gone.</summary>
public sealed class UnansweredAttackCheck : IncidentCheck, IFamily
{
    private readonly Dictionary<(EntityType, uint), (TimeSpan First, TimeSpan Last, int Count, bool Reported)> _attacks = [];

    public string Family => "bot";

    public override void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (p.Length < 6)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsReceived && p[0] == 0x0C)
        {
            _attacks.Remove(((EntityType)p[1], PacketDecoder.U32(p, 2)));
            _executed.Remove(((EntityType)p[1], PacketDecoder.U32(p, 2)));
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsReceived && p[0] == 0x4C && p.Length >= 14 && p[1] == (byte)EntityType.Player)
        {
            var target = ((EntityType)p[9], PacketDecoder.U32(p, 10));
            (_executed.TryGetValue(target, out var list) ? list : _executed[target] = []).Add(context.Now);
            return;
        }

        if (record.Kind != TraceRecordKind.D2gsSent || p.Length < 9 || p[0] is not (0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11))
        {
            return;
        }

        var key = ((EntityType)PacketDecoder.U32(p, 1), PacketDecoder.U32(p, 5));
        if (key.Item1 != EntityType.NPC)
        {
            return;
        }

        var fresh = !(_attacks.TryGetValue(key, out var known) && context.Now - known.Last < TimeSpan.FromSeconds(5));
        (TimeSpan First, TimeSpan Last, int Count, bool Reported) state = fresh ? (context.Now, context.Now, 0, false) : known;
        if (fresh)
        {
            _lifeAtStart[key] = context.Npc(key.Item2)?.LifePercentage;
        }

        state = (state.First, context.Now, state.Count + 1, state.Reported);
        if (!state.Reported && state.Last - state.First >= CheckThresholds.UnansweredAttacks)
        {
            var unit = context.Npc(key.Item2);
            var executed = _executed.TryGetValue(key, out var times) ? times.Count(t => t >= state.First) : 0;
            var unchanged = unit != null && _lifeAtStart.GetValueOrDefault(key) is { } before && Math.Abs(before - unit.LifePercentage) < 0.5;
            var why = executed > 0 && unchanged
                ? $"; the server carried out {executed} skills of other players on it (0x4C) and its life stayed at {unit.LifePercentage:0}% throughout: immune or invulnerable?"
                : "";
            Report(context, state.First, "unanswered attacks",
                $"{state.Count} attacks at {context.UnitName(key.Item2)}{(unit == null ? " (not in the client's state)" : $" at {Names.Point(unit.Location)}, {context.DistanceTo(unit.Location):0} away, life {unit.LifePercentage:0}%")} over {(state.Last - state.First).TotalSeconds:0}s with no hit on it{why}",
                state.Last);
            state.Reported = true;
        }

        _attacks[key] = state;
    }

    private readonly Dictionary<(EntityType, uint), List<TimeSpan>> _executed = [];
    private readonly Dictionary<(EntityType, uint), double?> _lifeAtStart = [];

    public override void Finished(ReplayContext context)
    {
        _attacks.Clear();
        _executed.Clear();
        _lifeAtStart.Clear();
    }
}

/// <summary>
/// Gold on the character and in the stash falling by more than half over one stay in town, after what
/// the vendors were paid is taken off: each vendor answer (0x2A) carries the gold on the character after
/// its buy, gamble or repair, so the fall from the gold just before it is what was spent.
/// </summary>
public sealed class GoldDropCheck : IncidentCheck, IFamily
{
    private TimeSpan _checkedAt = TimeSpan.FromDays(-1000);
    private bool _inTown;
    private int _goldAtEntry;
    private TimeSpan _entered;
    private int _goldOnPerson;
    private TimeSpan _lastAnswer = TimeSpan.FromDays(-1000);
    private byte _lastTrade;
    private uint _lastTradeItem;
    private int _spent;
    private readonly Dictionary<string, (int Count, int Gold)> _purchases = [];
    private readonly AreaTracker _areas = new();

    public string Family => "bot";

    public override void Before(ReplayContext context, TraceRecord record)
    {
        if (context.Game.Me != null && record.Kind == TraceRecordKind.D2gsReceived && record.Payload.Length > 0 && record.Payload[0] == 0x2A
            && context.Now - _lastAnswer > TimeSpan.FromSeconds(2))
        {
            _goldOnPerson = context.Game.Me.Attributes.GetValueOrDefault(Core.D2GS.Players.Attribute.GoldOnPerson, 0);
        }
    }

    public override void After(ReplayContext context, TraceRecord record)
    {
        _areas.Observe(context, record);
        var p = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsSent && p.Length >= 9 && p[0] is 0x32 or 0x35 or 0x36 or 0x39)
        {
            _lastTrade = p[0];
            _lastTradeItem = PacketDecoder.U32(p, 5);
        }
        else if (record.Kind == TraceRecordKind.D2gsReceived && p.Length >= 15 && p[0] == 0x2A && context.Game.Me != null)
        {
            Answered(context, p);
        }

        if (context.Game.Me == null || context.Now - _checkedAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _checkedAt = context.Now;
        if (_areas.Current(context) is not { } area)
        {
            return;
        }

        var town = ActivityObserver.IsTown(area);
        var gold = Gold(context);
        if (town && !_inTown)
        {
            _goldAtEntry = gold;
            _entered = context.Now;
            _spent = 0;
            _purchases.Clear();
        }
        else if (town && _goldAtEntry == 0)
        {
            _goldAtEntry = gold;
        }
        else if (!town && _inTown && area != Area.None)
        {
            Leave(context, gold);
        }

        _inTown = town;
    }

    public override void Finished(ReplayContext context)
    {
        if (_inTown)
        {
            Leave(context, Gold(context));
        }

        _inTown = false;
    }

    private void Answered(ReplayContext context, byte[] p)
    {
        Core.D2GS.Packet.Incoming.NpcTransactionPacket answer;
        try
        {
            answer = new Core.D2GS.Packet.Incoming.NpcTransactionPacket(new Core.D2GS.Packet.D2gsPacket(p));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return;
        }

        var before = _goldOnPerson;
        _goldOnPerson = answer.Gold;
        _lastAnswer = context.Now;
        if (answer.Gold >= before || answer.ItemId == 0xFFFFFFFF && _lastTrade == 0x32)
        {
            return;
        }

        var paid = before - answer.Gold;
        _spent += paid;
        var what = _lastTrade switch
        {
            0x32 => context.Game.Items.TryGetValue(answer.ItemId, out var item) || context.Game.Items.TryGetValue(_lastTradeItem, out item) ? $"bought {item.Name}" : "bought an item",
            0x35 => "repair",
            0x36 => "hired a mercenary",
            0x39 => "healing",
            _ => "a vendor",
        };
        var known = _purchases.GetValueOrDefault(what);
        _purchases[what] = (known.Count + 1, known.Gold + paid);
    }

    private void Leave(ReplayContext context, int gold)
    {
        var unexplained = _goldAtEntry - gold - _spent;
        if (_goldAtEntry > 0 && unexplained > _goldAtEntry * CheckThresholds.GoldDropShare)
        {
            var paid = _spent > 0
                ? $"; vendors were paid {_spent:N0} of it ({string.Join(", ", _purchases.OrderByDescending(p => p.Value.Gold).Take(5).Select(p => $"{p.Key}{(p.Value.Count > 1 ? $" x{p.Value.Count}" : "")} {p.Value.Gold:N0}"))}), {unexplained:N0} is unexplained"
                : "; no vendor was paid";
            Report(context, _entered, "gold drop", $"gold on the character and in the stash fell from {_goldAtEntry:N0} to {gold:N0} during a town stay of {(context.Now - _entered).TotalSeconds:0}s{paid}", context.Now);
        }

        _spent = 0;
        _purchases.Clear();
    }

    private static int Gold(ReplayContext context)
        => context.Game.Me.Attributes.GetValueOrDefault(Core.D2GS.Players.Attribute.GoldOnPerson, 0) + context.Game.Me.Attributes.GetValueOrDefault(Core.D2GS.Players.Attribute.GoldInStash, 0);
}

/// <summary>Checks that need the whole of a client's replay, its activity and the sidecar; they run after the replay.</summary>
public abstract class CompletedCheck
{
    public abstract string Family { get; }

    public abstract IEnumerable<Incident> Run(CheckContext context);
}

/// <summary>
/// Standing idle - not in town, not fighting, no typed wait - for a long time while a step is running.
/// When the bot reports no measured step for the client (none, or only steps whose start was inferred),
/// any such stretch counts.
/// </summary>
public sealed class StuckCheck : CompletedCheck
{
    public override string Family => "bot";

    public override IEnumerable<Incident> Run(CheckContext context)
    {
        var game = context.Query.Game;
        var stretches = context.Activity.Stretches(TimeSpan.Zero, game.EndUtc - game.StartUtc, ActivityObserver.Waits(context.Query, context.Client, game.EndUtc - game.StartUtc));
        var anySteps = context.Steps.Any(s => s.Covers(context.Client) && !s.StartInferred && s.Duration > TimeSpan.Zero);
        foreach (var stretch in stretches.Where(s => s.State == ActivityState.StandingIdle && s.Duration >= CheckThresholds.Stuck))
        {
            var step = context.Steps.Where(s => s.Covers(context.Client) && s.Start <= stretch.Start && s.End >= stretch.Start + CheckThresholds.Stuck).OrderBy(s => s.Duration).FirstOrDefault();
            if (anySteps && step == null)
            {
                continue;
            }

            yield return new Incident(stretch.Start, context.Client, "stuck",
                $"stood idle {stretch.Duration.TotalSeconds:0}s at {Names.Point(stretch.At)} in {stretch.Area ?? "?"}{(step == null ? "" : $" during step {step.Name}")}; not in town, no attack, no typed wait")
            { End = stretch.End };
        }
    }
}

/// <summary>Walks or paths failing three or more times close to one another (typed move events).</summary>
public sealed class PathFailureCheck : CompletedCheck
{
    public override string Family => "bot";

    public override IEnumerable<Incident> Run(CheckContext context)
    {
        var failures = context.Query.GameEntries()
            .Where(e => e.Kind == "move" && e.Client == context.Client && e.Str("outcome") is { } outcome && outcome != "arrived")
            .Select(e => (At: context.Query.Offset(e), Where: Point(e, "from") ?? Point(e, "to"), Entry: e))
            .Where(f => f.Where != null)
            .ToList();
        var reportedUntil = TimeSpan.FromDays(-1000);
        for (var i = 0; i < failures.Count; i++)
        {
            if (failures[i].At <= reportedUntil)
            {
                continue;
            }

            var cluster = failures.Skip(i).TakeWhile(f => f.At - failures[i].At <= CheckThresholds.PathFailureWindow)
                .Where(f => f.Where.Distance(failures[i].Where) <= CheckThresholds.PathFailureRadius).ToList();
            if (cluster.Count < CheckThresholds.PathFailures)
            {
                continue;
            }

            reportedUntil = cluster[^1].At;
            yield return new Incident(failures[i].At, context.Client, "path failures",
                $"{cluster.Count} moves failed within {CheckThresholds.PathFailureRadius:0} subtiles of {Names.Point(failures[i].Where)} in {(cluster[^1].At - cluster[0].At).TotalSeconds:0}s: {string.Join(", ", cluster.GroupBy(c => $"{c.Entry.Str("kind")} {c.Entry.Str("outcome")}").Select(g => $"{g.Key} x{g.Count()}"))}{(cluster[0].Entry.Str("area") is { } area ? $" in {area}" : "")}", "sidecar")
            { End = cluster[^1].At };
        }
    }

    private static Point Point(SidecarEntry entry, string key)
        => entry.Json.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array && value.GetArrayLength() == 2
            ? new Point((ushort)value[0].GetInt32(), (ushort)value[1].GetInt32())
            : null;
}

/// <summary>Game-wide checks over the sidecar and every client's activity together.</summary>
public abstract class GameCheck
{
    public abstract string Family { get; }

    public abstract IEnumerable<Incident> Run(GameQuery query, IReadOnlyList<CheckContext> clients);
}

/// <summary>
/// One log message template more than 20 times within a second: a hot loop. Item transactions are one
/// line per item by nature - selling a full inventory logs "Sold item" thirty times in a second - and are
/// left out.
/// </summary>
public sealed class HotLoopCheck : GameCheck
{
    private static readonly System.Text.RegularExpressions.Regex PerItem = new(
        @"\b(sold|sells?|bought|buys?|identif(y|ies|ied)|stash(ed|es)?|picked up|picks? up|gambl(e|es|ed)|cubed|transmut(e|es|ed)|muled?)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsItemTransaction(string template) => PerItem.IsMatch(template ?? "");

    public override string Family => "bot";

    public override IEnumerable<Incident> Run(GameQuery query, IReadOnlyList<CheckContext> clients)
    {
        foreach (var group in query.GameEntries().Where(e => e.IsLog).GroupBy(e => (e.Process, Template(e))).Where(g => !IsItemTransaction(g.Key.Item2)))
        {
            var lines = group.ToList();
            var reportedUntil = DateTime.MinValue;
            for (int start = 0, end = 0; end < lines.Count; end++)
            {
                while (lines[end].Utc - lines[start].Utc > TimeSpan.FromSeconds(1))
                {
                    start++;
                }

                if (end - start + 1 > CheckThresholds.HotLoopLines && lines[start].Utc > reportedUntil)
                {
                    var last = end;
                    while (last + 1 < lines.Count && lines[last + 1].Utc - lines[last].Utc <= TimeSpan.FromSeconds(1))
                    {
                        last++;
                    }

                    reportedUntil = lines[last].Utc;
                    var owners = query.Attribution.ClientsOf(lines[start]);
                    yield return new Incident(query.Offset(lines[start]), owners.Count == 0 ? null : string.Join("+", owners), "hot loop",
                        $"\"{Short(group.Key.Item2)}\" logged {last - start + 1} times in {(lines[last].Utc - lines[start].Utc).TotalSeconds:0.0}s ({lines[start].Level})", "sidecar")
                    { End = query.Offset(lines[last]) };
                }
            }
        }
    }

    /// <summary>The message template; a line logged with its values interpolated has none, so its numbers are blanked to #.</summary>
    internal static string Template(SidecarEntry entry)
        => entry.Template ?? Digits.Replace(entry.Message ?? "", "#");

    private static readonly System.Text.RegularExpressions.Regex Digits = new(@"\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string Short(string text) => text.Length > 100 ? text[..100] + ".." : text;
}

/// <summary>Party members in different areas for a long time while none of them is in town.</summary>
public sealed class PartySplitCheck : GameCheck
{
    public override string Family => "bot";

    public override IEnumerable<Incident> Run(GameQuery query, IReadOnlyList<CheckContext> clients)
    {
        if (clients.Count < 2)
        {
            yield break;
        }

        var end = query.Game.EndUtc - query.Game.StartUtc;
        TimeSpan? since = null;
        string detail = null;
        for (var t = TimeSpan.Zero; t <= end; t += TimeSpan.FromSeconds(1))
        {
            var areas = clients.Where(c => c.Activity.Files.Any(f => f.Start <= t && t <= f.End)).Select(c => (c.Client, Area: c.Activity.AreaAt(t))).ToList();
            var split = areas.Count >= 2 && areas.All(a => a.Area != null && !ActivityObserver.IsTown(a.Area.Value)) && areas.Select(a => a.Area).Distinct().Count() > 1;
            if (split)
            {
                since ??= t;
                detail = string.Join(", ", areas.GroupBy(a => a.Area).Select(g => $"{string.Join("+", g.Select(a => a.Client))} in {g.Key}"));
                continue;
            }

            if (since is { } started && t - started >= CheckThresholds.PartySplit)
            {
                yield return new Incident(started, null, "party split", $"members in different areas for {(t - started).TotalSeconds:0}s, none in town; last: {detail}") { End = t };
            }

            since = null;
        }

        if (since is { } open && end - open >= CheckThresholds.PartySplit)
        {
            yield return new Incident(open, null, "party split", $"members in different areas for {(end - open).TotalSeconds:0}s until the end, none in town; last: {detail}") { End = end };
        }
    }
}

/// <summary>
/// The earlier games of the same bot, when there are enough of them to compare with; otherwise null and,
/// once per game, a note that the comparing checks were skipped.
/// </summary>
internal static class EarlierGames
{
    public const string SkippedNote = "slow-step and new-warning checks skipped for lack of history: fewer than 10 earlier games of the same bot in the 7 days before";

    public const string NoHistoryNote = "slow-step and new-warning checks skipped: no history (--no-history), so nothing is compared with earlier games";

    public static List<TraceHistory.GameRecord> Of(GameQuery query, IReadOnlyList<CheckContext> clients)
    {
        var history = clients.Count > 0 ? clients[0].History : null;
        if (history == null)
        {
            if (clients.Count > 0 && !clients[0].Notes.Contains(NoHistoryNote))
            {
                clients[0].Notes.Add(NoHistoryNote);
            }

            return null;
        }

        var earlier = history.Earlier(query);
        if (earlier.Count >= TraceHistory.MinimumGames)
        {
            return earlier;
        }

        if (!clients[0].Notes.Contains(SkippedNote))
        {
            clients[0].Notes.Add(SkippedNote);
        }

        return null;
    }
}

/// <summary>A step that failed within seconds of starting, or ran over three times the median of the same step in earlier games.</summary>
public sealed class StepDurationCheck : GameCheck
{
    public override string Family => "bot";

    public override IEnumerable<Incident> Run(GameQuery query, IReadOnlyList<CheckContext> clients)
    {
        var steps = clients.Count > 0 ? clients[0].Steps : Steps.Of(query);
        var records = EarlierGames.Of(query, clients);
        foreach (var step in steps)
        {
            if (step.Outcome is "failed" or "timeout" && step.Duration < CheckThresholds.QuickFailure && !step.StartInferred)
            {
                yield return new Incident(step.Start, step.Client, "quick step failure", $"{step.Name} {step.Outcome} after {step.Duration.TotalSeconds:0.0}s", "sidecar") { End = step.End };
            }

            if (step.StartInferred)
            {
                continue;
            }

            List<double> earlier = records == null ? null : [.. records.SelectMany(r => r.Steps.GetValueOrDefault(step.Name) ?? [])];
            if (earlier is { Count: >= CheckThresholds.SlowStepHistory })
            {
                var (median, _) = Steps.Percentiles(earlier);
                if (step.Duration.TotalSeconds > CheckThresholds.SlowStepFactor * median)
                {
                    yield return new Incident(step.Start, step.Client, "slow step",
                        $"{step.Name} took {step.Duration.TotalSeconds:0}s, {step.Duration.TotalSeconds / median:0.0}x the median {median:0}s of {earlier.Count} earlier runs", "sidecar")
                    { End = step.End };
                }
            }
        }
    }
}

/// <summary>A warning or error message template that no earlier game of the same bot logged.</summary>
public sealed class NewWarningCheck : GameCheck
{
    public override string Family => "bot";

    public override IEnumerable<Incident> Run(GameQuery query, IReadOnlyList<CheckContext> clients)
    {
        var records = EarlierGames.Of(query, clients);
        if (records == null)
        {
            yield break;
        }

        var seen = records.SelectMany(r => r.Warnings).ToHashSet(StringComparer.Ordinal);
        foreach (var first in query.GameEntries().Where(e => e.IsLog && e.Level is "Warning" or "Error" or "Fatal").GroupBy(HotLoopCheck.Template).Select(g => g.First()))
        {
            var template = HotLoopCheck.Template(first);
            if (seen.Contains(template))
            {
                continue;
            }

            var owners = query.Attribution.ClientsOf(first);
            yield return new Incident(query.Offset(first), owners.Count == 0 ? null : string.Join("+", owners), "new warning",
                $"first time in {records.Count} earlier games of this bot (last {TraceHistory.Days} days): {first.Level} \"{HotLoopCheck.Short(template)}\"", "sidecar");
        }
    }
}

/// <summary>
/// Standing where the map says the ground is not walkable, longer than a moment after the area changed,
/// and either with no ground at all within <see cref="CheckThresholds.WalkableReach"/> subtiles or with the
/// server moving the client away from there within seconds (a move of at least
/// <see cref="CheckThresholds.ServerCorrection"/> subtiles, not within <see cref="CheckThresholds.WarpExplains"/>
/// of a waypoint, door or object interaction): the client's area or position is wrong. A spot next to
/// ground that the server lets the client stand on is not reported: the map's collision has one-subtile
/// lines on tile boundaries that characters stand on both sides of, and walk across. An episode - checks
/// on unwalkable ground with no walkable second between them - is one incident with its whole length,
/// reported when one of its stands (within a subtile of one spot) qualifies. An unknown area is not checked.
/// </summary>
public sealed class NotWalkableCheck : CompletedCheck
{
    /// <summary>How long after a stand on unwalkable ground a server correction still counts as its answer.</summary>
    public static readonly TimeSpan CorrectionWithin = TimeSpan.FromSeconds(3);

    public override string Family => "state";

    public override IEnumerable<Incident> Run(CheckContext context)
    {
        if (context.Maps == null)
        {
            context.Notes.Add("position-not-walkable check skipped: no map service (pass --map-url, or start the map service)");
            return [];
        }

        var found = new List<Incident>();
        var game = context.Query.Game;
        var activity = context.Activity;
        var step = TimeSpan.FromMilliseconds(500);
        TimeSpan? since = null;
        var lastBad = TimeSpan.Zero;
        var positions = new List<Point>();
        Area? sinceArea = null;
        (TimeSpan Start, Point At, bool NoGround)? stand = null;
        var stands = new List<(TimeSpan Start, TimeSpan End, Point At, bool NoGround, (TimeSpan At, Point Held, Point To) Correction)>();
        TimeSpan lastAreaChange = TimeSpan.FromDays(-1000);
        Area? lastArea = null;
        var missing = new HashSet<Area>();
        for (var t = TimeSpan.Zero; t <= game.EndUtc - game.StartUtc; t += step)
        {
            var area = activity.AreaAt(t);
            var position = activity.PositionAt(t);
            if (area != lastArea)
            {
                lastArea = area;
                lastAreaChange = t;
            }

            var ground = GroundAt.Walkable;
            if (area != null && position != null && t - lastAreaChange > CheckThresholds.NotWalkable && !missing.Contains(area.Value)
                && activity.Files.Any(f => f.Start <= t && t <= f.End))
            {
                try
                {
                    ground = context.Maps.GroundAt(context.MapId, context.Difficulty, area.Value, position, CheckThresholds.WalkableReach);
                }
                catch (MapUnavailableException e)
                {
                    missing.Add(area.Value);
                    context.Notes.Add(e.WholeService ? $"position-not-walkable check skipped in areas with no cached map: {e.Message}" : $"position-not-walkable check skipped in {area}: {e.Message}");
                }
            }

            if (ground == GroundAt.Walkable)
            {
                if (since != null)
                {
                    EndStand(lastBad + step);
                    if (t - lastBad >= CheckThresholds.NotWalkableEpisodeGap)
                    {
                        Close();
                    }
                }

                continue;
            }

            if (since != null && area != sinceArea)
            {
                Close();
            }

            if (since == null)
            {
                since = t;
                sinceArea = area;
                positions.Clear();
                stands.Clear();
            }

            if (stand is { } open && position.Distance(open.At) > 1)
            {
                EndStand(t);
            }

            stand ??= (t, position, false);
            stand = stand.Value with { NoGround = stand.Value.NoGround || ground == GroundAt.NoGround };
            lastBad = t;
            if (positions.Count == 0 || positions[^1].Distance(position) > 0)
            {
                positions.Add(position);
            }
        }

        Close();
        return found;

        void EndStand(TimeSpan end)
        {
            if (stand is { } open && end - open.Start >= CheckThresholds.NotWalkable)
            {
                var correction = activity.Corrections.FirstOrDefault(c => c.At >= open.Start && c.At <= end + CorrectionWithin
                    && c.Held.Distance(open.At) <= CheckThresholds.WalkableReach && c.To.Distance(c.Held) >= CheckThresholds.ServerCorrection
                    && !activity.Warps.Any(w => w <= c.At && c.At - w <= CheckThresholds.WarpExplains));
                if (open.NoGround || correction.To != null)
                {
                    stands.Add((open.Start, end, open.At, open.NoGround, correction));
                }
            }

            stand = null;
        }

        void Close()
        {
            var end = lastBad + step;
            EndStand(end);
            if (since is { } started && stands.Count > 0)
            {
                var longest = stands.MaxBy(s => s.End - s.Start);
                var at = positions.Count <= 1 || positions[0].Distance(positions[^1]) <= 1
                    ? $"at {Names.Point(positions[0])}"
                    : $"from {Names.Point(positions[0])} to {Names.Point(positions[^1])} ({positions.Count} spots, the longest stand {(longest.End - longest.Start).TotalSeconds:0.0}s at {Names.Point(longest.At)})";
                var correction = stands.Select(s => s.Correction).FirstOrDefault(c => c.To != null);
                found.Add(new Incident(started, context.Client, "not walkable", stands.Any(s => s.NoGround)
                    ? $"{at}, with no walkable ground within {CheckThresholds.WalkableReach} subtiles on the {sinceArea} map, for {(end - started).TotalSeconds:0.0}s{(correction.To == null ? "" : $"; the server then put the client at {Names.Point(correction.To)} at {TraceTime.Format(correction.At)}")}"
                    : $"{at}, which the {sinceArea} map marks unwalkable, for {(end - started).TotalSeconds:0.0}s, and the server moved the client away to {Names.Point(correction.To)} at {TraceTime.Format(correction.At)}")
                { End = end });
            }

            since = null;
            stands.Clear();
        }
    }
}
