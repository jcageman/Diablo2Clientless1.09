using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// One attack or skill packet a client sent: at a unit, or at a point. <see cref="Meant"/> is the unit it was meant for -
/// the unit named, or for a point the nearest monster in the cast's direction - and where that unit stood then.
/// </summary>
public sealed record AttackCast(TimeSpan At, string Skill, bool AtPoint, Point From, Point Aim, uint MeantId, Point Meant, string MeantName)
{
    /// <summary>Where the cast was judged to go: the unit it was meant for, else the point aimed at.</summary>
    public Point Target => Meant ?? Aim;
}

/// <summary>A monster the client saw get hit (NPCHit, or its life lower in a later packet - a monster that does not flinch gets no NPCHit) or die (NPCState dying or dead), and where it stood.</summary>
public sealed record HitMark(TimeSpan At, uint Id, Point Where, bool Death);

/// <summary>
/// A skill move the server performed for the client: the Skillmove effect on its own player from added to removed
/// (Whirlwind, Charge, Leap, Leap Attack), or a Teleport cast answered by a reassign. The asks are the cast packets of
/// that skill since the previous skill move ended; <paramref name="LastAskAt"/> is the last of them, the one the server answered.
/// </summary>
public sealed record SkillMove(TimeSpan Start, TimeSpan End, string Skill, Point Asked, TimeSpan AskedAt, int Asks, TimeSpan LastAskAt);

/// <summary>A skill move as the server played it out: its displacement, the monsters hit along it, and the mana it took.</summary>
public sealed record SkillMoveView(SkillMove Move, Point From, Point To, double Moved, IReadOnlyList<HitMark> Hits, int? ManaBefore, int? ManaAfter)
{
    public int? ManaSpent => ManaBefore is { } before && ManaAfter is { } after ? before - after : null;
}

/// <summary>
/// What a client attacked with and what it hit, for the combat checks, the skill moves and the hits of point casts:
/// every attack packet with its target, every hit or death of a monster it saw, every skill move the server played out,
/// every change of its mana and the server's statements of its position.
/// </summary>
public sealed class CombatObserver : ICachedObserver
{
    /// <summary>How long after a cast its hits may still land, per skill: a travelling or lingering skill takes longer. Others take <see cref="DefaultHitWindow"/>.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> HitWindows = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        [nameof(Skill.FrozenOrb)] = TimeSpan.FromSeconds(2),
        [nameof(Skill.Blizzard)] = TimeSpan.FromSeconds(2.5),
        [nameof(Skill.Meteor)] = TimeSpan.FromSeconds(2),
        [nameof(Skill.Hydra)] = TimeSpan.FromSeconds(3),
        [nameof(Skill.GuidedArrow)] = TimeSpan.FromSeconds(2),
        [nameof(Skill.BoneSpirit)] = TimeSpan.FromSeconds(2),
        [nameof(Skill.BlessedHammer)] = TimeSpan.FromSeconds(1.5),
        [nameof(Skill.ChargedBolt)] = TimeSpan.FromSeconds(1.5),
        [nameof(Skill.FireBall)] = TimeSpan.FromSeconds(1.5),
        [nameof(Skill.ImmolationArrow)] = TimeSpan.FromSeconds(1.5),
        [nameof(Skill.FreezingArrow)] = TimeSpan.FromSeconds(1.5),
        [nameof(Skill.Whirlwind)] = TimeSpan.FromSeconds(1.5),
    };

    public static readonly TimeSpan DefaultHitWindow = TimeSpan.FromSeconds(1.2);

    /// <summary>Skills cast at a corpse or that only use one: they hit nothing by design.</summary>
    public static readonly IReadOnlySet<Skill> CorpseSkills = new HashSet<Skill>
    {
        Skill.FindItem, Skill.FindPotion, Skill.CorpseExplosion, Skill.RaiseSkeleton, Skill.RaiseSkeletalMage, Skill.Revive, Skill.GrimWard,
    };

    /// <summary>Skills the server plays out as a skill move (the Skillmove effect), which do their damage along the way.</summary>
    public static readonly IReadOnlySet<string> SkillMoveSkills = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Skill.Whirlwind), nameof(Skill.Charge), nameof(Skill.Leap), nameof(Skill.LeapAttack),
    };

    public static bool IsSkillMove(string skill) => skill != null && SkillMoveSkills.Contains(skill);

    public static TimeSpan HitWindow(string skill) => skill != null && HitWindows.TryGetValue(skill, out var window) ? window : DefaultHitWindow;

    /// <summary>A point cast is meant for the nearest monster within this angle of its direction...</summary>
    public const double MeantAngle = 30;

    /// <summary>...and this far from the caster.</summary>
    public const double MeantReach = 30;

    private static readonly TimeSpan TeleportAnswer = TimeSpan.FromSeconds(1.5);

    public string CacheKey => "CombatObserver";

    public List<AttackCast> Casts { get; } = [];

    public List<HitMark> Hits { get; } = [];

    public List<SkillMove> Moves { get; } = [];

    /// <summary>The client's mana whenever it changed, and its maximum then (0 when unknown).</summary>
    public List<(TimeSpan At, int Mana, int Max)> Mana { get; } = [];

    public ServerPositions Server { get; } = new();

    private int _lastMana = -1;
    private int _maxMana;
    private TimeSpan _maxManaAt = TimeSpan.FromDays(-1000);
    private (TimeSpan At, string Skill, Point Aim)? _lastAsk;
    private int _asks;
    private TimeSpan _firstAsk;
    private TimeSpan? _moveStart;
    private (TimeSpan At, string Skill, Point Aim, int Asks, TimeSpan Last)? _moveAsk;
    private (TimeSpan First, Point Aim, int Asks, TimeSpan Last)? _teleport;

    public void Save(System.IO.BinaryWriter w)
    {
        w.WriteList(Casts, (x, c) =>
        {
            x.Write(c.At);
            x.WriteText(c.Skill);
            x.Write(c.AtPoint);
            x.Write(c.From);
            x.Write(c.Aim);
            x.Write(c.MeantId);
            x.Write(c.Meant);
            x.WriteText(c.MeantName);
        });
        w.WriteList(Hits, (x, h) => { x.Write(h.At); x.Write(h.Id); x.Write(h.Where); x.Write(h.Death); });
        w.WriteList(Moves, (x, m) => { x.Write(m.Start); x.Write(m.End); x.WriteText(m.Skill); x.Write(m.Asked); x.Write(m.AskedAt); x.Write(m.Asks); x.Write(m.LastAskAt); });
        w.WriteList(Mana, (x, m) => { x.Write(m.At); x.Write(m.Mana); x.Write(m.Max); });
        Server.Save(w);
    }

    public void Load(System.IO.BinaryReader r)
    {
        r.ReadList(Casts, x => new AttackCast(x.ReadTime(), x.ReadText(), x.ReadBoolean(), x.ReadPoint(), x.ReadPoint(), x.ReadUInt32(), x.ReadPoint(), x.ReadText()));
        r.ReadList(Hits, x => new HitMark(x.ReadTime(), x.ReadUInt32(), x.ReadPoint(), x.ReadBoolean()));
        r.ReadList(Moves, x => new SkillMove(x.ReadTime(), x.ReadTime(), x.ReadText(), x.ReadPoint(), x.ReadTime(), x.ReadInt32(), x.ReadTime()));
        r.ReadList(Mana, x => (x.ReadTime(), x.ReadInt32(), x.ReadInt32()));
        Server.Load(r);
    }

    public void After(ReplayContext context, TraceRecord record)
    {
        Server.Observe(context, record);
        var p = record.Payload;
        var me = context.Game.Me;
        if (p.Length == 0 || me == null)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            if (p[0] is >= 0x05 and <= 0x11 and not 0x0B)
            {
                Sent(context, me, p);
            }
        }
        else if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            Received(context, me, p);
        }

        if (me.Mana != _lastMana)
        {
            _lastMana = me.Mana;
            Mana.Add((context.Now, me.Mana, MaxMana(context)));
        }

        var moving = me.Effects.ContainsKey(EntityEffect.Skillmove);
        if (moving && _moveStart == null)
        {
            _moveStart = context.Now;
            _moveAsk = _lastAsk is { } ask && context.Now - ask.At <= TimeSpan.FromSeconds(2) ? (_firstAsk, ask.Skill, ask.Aim, _asks, ask.At) : null;
        }
        else if (!moving && _moveStart is { } start)
        {
            CloseMove(start, context.Now);
        }
    }

    private void CloseMove(TimeSpan start, TimeSpan end)
    {
        var ask = _moveAsk;
        Moves.Add(new SkillMove(start, end, ask?.Skill ?? "skill move", ask?.Aim, ask?.At ?? start, ask?.Asks ?? 0, ask?.Last ?? start));
        _moveStart = null;
        _moveAsk = null;
        _asks = 0;
    }

    private int MaxMana(ReplayContext context)
    {
        if (context.Now - _maxManaAt > TimeSpan.FromSeconds(1))
        {
            _maxManaAt = context.Now;
            try
            {
                _maxMana = context.Game.Me.MaxMana;
            }
            catch (Exception e) when (e is NullReferenceException or InvalidOperationException or KeyNotFoundException)
            {
                _maxMana = 0;
            }
        }

        return _maxMana;
    }

    private void Sent(ReplayContext context, Self me, byte[] p)
    {
        var hand = p[0] <= 0x0A ? Hand.Left : Hand.Right;
        if (!me.ActiveSkills.TryGetValue(hand, out var skill) || CorpseSkills.Contains(skill))
        {
            return;
        }

        var atPoint = p[0] is 0x05 or 0x08 or 0x0C or 0x0F;
        Point aim = null;
        uint meantId = 0;
        Point meant = null;
        string meantName = null;
        if (atPoint && p.Length >= 5)
        {
            aim = new Point(PacketDecoder.U16(p, 1), PacketDecoder.U16(p, 3));
        }
        else if (!atPoint && p.Length >= 9)
        {
            var type = (EntityType)PacketDecoder.U32(p, 1);
            var id = PacketDecoder.U32(p, 5);
            if (type == EntityType.NPC)
            {
                var unit = context.Npc(id);
                if (unit?.State is EntityState.Dieing or EntityState.Dead)
                {
                    return;
                }

                aim = unit?.Location;
                (meantId, meant, meantName) = (id, aim, context.UnitName(id));
            }
            else if (type == EntityType.Player)
            {
                aim = context.Game.Players.FirstOrDefault(pl => pl.Id == id)?.Location;
            }
        }

        var name = skill.ToString();
        if (skill == Skill.Teleport)
        {
            if (aim != null)
            {
                _teleport = _teleport is { } open && context.Now - open.First <= TeleportAnswer ? open with { Aim = aim, Asks = open.Asks + 1, Last = context.Now } : (context.Now, aim, 1, context.Now);
            }

            return;
        }

        if (!ActivityObserver.IsAttack(skill))
        {
            return;
        }

        if (_lastAsk?.Skill != name || _asks == 0)
        {
            _asks = 0;
            _firstAsk = context.Now;
        }

        _asks++;
        _lastAsk = (context.Now, name, aim);
        var from = me.Location;
        if (atPoint && aim != null && from != null)
        {
            (meantId, meant, meantName) = MeantFor(context, from, aim);
        }

        Casts.Add(new AttackCast(context.Now, name, atPoint, from, aim, meantId, meant, meantName));
    }

    private readonly Dictionary<uint, double> _life = [];

    private void Received(ReplayContext context, Self me, byte[] p)
    {
        if (p[0] is >= 0x67 and <= 0x6D or 0x0C)
        {
            var id = p[0] == 0x0C ? (p.Length >= 6 && p[1] == (byte)EntityType.NPC ? PacketDecoder.U32(p, 2) : 0) : Core.D2GS.Motion.MotionUpdate.Read(p).EntityId;
            if (id != 0 && context.Npc(id) is { } unit)
            {
                var life = unit.LifePercentage;
                if (p[0] != 0x0C && _life.TryGetValue(id, out var was) && life < was && life > 0)
                {
                    Hits.Add(new HitMark(context.Now, id, unit.Location, false));
                }

                _life[id] = life;
            }
        }

        switch (p[0])
        {
            case 0x0C when p.Length >= 6 && p[1] == (byte)EntityType.NPC:
                var hit = PacketDecoder.U32(p, 2);
                Hits.Add(new HitMark(context.Now, hit, context.Npc(hit)?.Location, false));
                break;
            case 0x69 when p.Length >= 10 && p[5] is (byte)EntityState.Dieing or (byte)EntityState.Dead:
                var died = PacketDecoder.U32(p, 1);
                var at = new Point(PacketDecoder.U16(p, 6), PacketDecoder.U16(p, 8));
                Hits.Add(new HitMark(context.Now, died, at.X != 0 || at.Y != 0 ? at : context.Npc(died)?.Location, true));
                break;
            case 0x15 when p.Length >= 11 && p[1] == (byte)EntityType.Player && PacketDecoder.U32(p, 2) == me.Id:
                if (_teleport is { } teleport && context.Now - teleport.First <= TeleportAnswer + TimeSpan.FromSeconds(1))
                {
                    Moves.Add(new SkillMove(teleport.First, context.Now, nameof(Skill.Teleport), teleport.Aim, teleport.First, teleport.Asks, teleport.Last));
                    _teleport = null;
                }

                break;
        }
    }

    public void Finished(ReplayContext context)
    {
        if (_moveStart is { } start)
        {
            CloseMove(start, context.Now);
        }

        _lastMana = -1;
        _life.Clear();
        _lastAsk = null;
        _teleport = null;
    }

    private static readonly ConcurrentDictionary<NPCCode, bool> Allies = new();

    /// <summary>Hirelings, summons and traps: units on the players' side, which a cast is never meant for.</summary>
    public static bool IsAlly(NPCCode code) => Allies.GetOrAdd(code, static c =>
    {
        var name = c.ToString();
        return c is NPCCode.RogueScout or NPCCode.Guard or NPCCode.IronWolf or NPCCode.Hireling1 or NPCCode.Hireling2
            || name.Contains("Golem", StringComparison.Ordinal) || name.Contains("Valkyrie", StringComparison.Ordinal) || name.Contains("Necro", StringComparison.Ordinal)
            || name.Contains("Sentry", StringComparison.Ordinal) || name.Contains("Hydra", StringComparison.Ordinal);
    });

    /// <summary>The nearest live monster (the lowest id of equally near ones) within <see cref="MeantAngle"/> of the direction from the caster to the point and <see cref="MeantReach"/> of the caster; a point on the caster takes the nearest within 6.</summary>
    public static (uint Id, Point At, string Name) MeantFor(ReplayContext context, Point from, Point aim)
    {
        var game = context.Game;
        var mercs = new HashSet<uint>(game.Players.Where(pl => pl.MercId != null).Select(pl => pl.MercId.Value));
        if (game.Me?.MercId is { } own)
        {
            mercs.Add(own);
        }

        double dx = aim.X - from.X, dy = aim.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var cos = Math.Cos(MeantAngle * Math.PI / 180);
        WorldObject best = null;
        var bestDistance = double.MaxValue;
        foreach (var unit in game.WorldObjects.Values)
        {
            if (unit.Type != EntityType.NPC || unit.Location is not { } at || unit.State is EntityState.Dieing or EntityState.Dead
                || mercs.Contains(unit.Id) || IsAlly(unit.NPCCode))
            {
                continue;
            }

            double ux = at.X - from.X, uy = at.Y - from.Y;
            var distance = Math.Sqrt(ux * ux + uy * uy);
            if (distance > MeantReach || distance > bestDistance || (distance == bestDistance && unit.Id > best.Id))
            {
                continue;
            }

            var inDirection = length < 1 ? distance <= 6 : distance < 0.5 || (ux * dx + uy * dy) / (distance * length) >= cos;
            if (inDirection)
            {
                best = unit;
                bestDistance = distance;
            }
        }

        return best == null ? (0, null, null) : (best.Id, best.Location, context.UnitName(best.Id));
    }

    /// <summary>The mana at or before <paramref name="t"/>, or null before the first.</summary>
    public int? ManaAt(TimeSpan t)
    {
        var i = LastAtOrBefore(Mana, t, m => m.At);
        return i < 0 ? null : Mana[i].Mana;
    }

    /// <summary>The mana lost in falls from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public int ManaSpent(TimeSpan from, TimeSpan to)
    {
        var spent = 0;
        for (var i = Math.Max(1, LastAtOrBefore(Mana, from, m => m.At) + 1); i < Mana.Count && Mana[i].At <= to; i++)
        {
            spent += Math.Max(0, Mana[i - 1].Mana - Mana[i].Mana);
        }

        return spent;
    }

    /// <summary>The hits from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public IEnumerable<HitMark> HitsBetween(TimeSpan from, TimeSpan to)
    {
        for (var i = LastAtOrBefore(Hits, from - TimeSpan.FromTicks(1), h => h.At) + 1; i < Hits.Count && Hits[i].At <= to; i++)
        {
            yield return Hits[i];
        }
    }

    /// <summary>How near a hit must land to the target of a cast to count as its hit.</summary>
    public const double HitReach = 6;

    /// <summary>
    /// Whether the unit the cast was meant for, or a monster near its target, was hit or died within the skill's hit window;
    /// for a skill move, a monster near its path from the caster to the point asked.
    /// </summary>
    public bool Landed(AttackCast cast) => HitsBetween(cast.At, cast.At + HitWindow(cast.Skill)).Any(hit => Credits(cast, hit));

    /// <summary>Whether a hit is one the cast may have landed: on the unit it was meant for, or near its target (its path, for a skill move).</summary>
    public static bool Credits(AttackCast cast, HitMark hit)
    {
        if (cast.MeantId != 0 && hit.Id == cast.MeantId)
        {
            return true;
        }

        if (hit.Where is null)
        {
            return false;
        }

        if (IsSkillMove(cast.Skill) && cast.From is not null && cast.Aim is not null)
        {
            return Geometry.DistanceToSegment(hit.Where, cast.From, cast.Aim) <= HitReach;
        }

        return cast.Target is { } target && hit.Where.Distance(target) <= HitReach;
    }

    /// <summary>The hits from <paramref name="from"/> to <paramref name="to"/> that one of this client's own casts may have landed, within that cast's window.</summary>
    public IEnumerable<HitMark> OwnHitsBetween(TimeSpan from, TimeSpan to)
    {
        foreach (var hit in HitsBetween(from, to))
        {
            for (var i = LastAtOrBefore(Casts, hit.At, c => c.At); i >= 0 && hit.At - Casts[i].At <= LongestHitWindow; i--)
            {
                if (hit.At - Casts[i].At <= HitWindow(Casts[i].Skill) && Credits(Casts[i], hit))
                {
                    yield return hit;
                    break;
                }
            }
        }
    }

    private static readonly TimeSpan LongestHitWindow = HitWindows.Values.Append(DefaultHitWindow).Max();

    private Dictionary<TimeSpan, int> _charges;
    private readonly Dictionary<TimeSpan, TimeSpan> _lastCharged = [];
    private readonly Dictionary<TimeSpan, SkillMoveView> _views = [];

    /// <summary>
    /// Each mana fall charged once to skill moves. The life and mana packets come every 0.4 s and lag the casts, so a fall
    /// can be stated after the next move began, and one packet can state two moves' cost. A fall is charged among the moves
    /// started before its packet that ended at most 0.8 s before it: to the earliest of them with no charge yet - split
    /// evenly over all of those when it is at least 1.5 times the median such fall, a lump - else to the latest.
    /// </summary>
    private Dictionary<TimeSpan, int> Charges()
    {
        if (_charges != null)
        {
            return _charges;
        }

        _charges = [];
        var falls = new List<(TimeSpan At, int Fall, List<int> Moves)>();
        for (var i = 1; i < Mana.Count; i++)
        {
            var fall = Mana[i - 1].Mana - Mana[i].Mana;
            var at = Mana[i].At;
            var candidates = new List<int>();
            for (var m = LastAtOrBefore(Moves, at - TimeSpan.FromTicks(1), x => x.Start); m >= 0 && at - Moves[m].Start <= TimeSpan.FromSeconds(5); m--)
            {
                if (at - Moves[m].End <= TimeSpan.FromMilliseconds(800))
                {
                    candidates.Insert(0, m);
                }
            }

            if (fall > 0 && candidates.Count > 0)
            {
                falls.Add((at, fall, candidates));
            }
        }

        var sorted = falls.Select(f => f.Fall).OrderBy(f => f).ToList();
        var lump = sorted.Count == 0 ? int.MaxValue : sorted[sorted.Count / 2] * 1.5;
        void Charge(int move, int amount, TimeSpan at)
        {
            _charges[Moves[move].Start] = _charges.GetValueOrDefault(Moves[move].Start) + amount;
            _lastCharged[Moves[move].Start] = at;
        }

        foreach (var (at, fall, moves) in falls)
        {
            var open = moves.Where(m => !_charges.ContainsKey(Moves[m].Start)).ToList();
            if (open.Count == 0)
            {
                Charge(moves[^1], fall, at);
            }
            else if (open.Count > 1 && fall >= lump)
            {
                for (var k = 0; k < open.Count; k++)
                {
                    Charge(open[k], (fall / open.Count) + (k == open.Count - 1 ? fall % open.Count : 0), at);
                }
            }
            else
            {
                Charge(open[0], fall, at);
            }
        }

        return _charges;
    }

    /// <summary>How the server played a skill move out: from its last stated position at the start to the first it stated after the end (within 1 s), the monsters hit within 5 of that path until 0.3 s after, the mana before it (the previous move's mana after while a fall charged to that one is still to come) and that less the falls charged to it (see <see cref="Charges"/>).</summary>
    public SkillMoveView View(SkillMove move)
    {
        if (_views.TryGetValue(move.Start, out var known))
        {
            return known;
        }

        var view = MakeView(move);
        _views[move.Start] = view;
        return view;
    }

    private SkillMoveView MakeView(SkillMove move)
    {
        var from = Server.At(move.Start)?.Position;
        Point to = null;
        var positions = Server.Positions;
        var i = LastAtOrBefore(positions, move.End, p => p.At);
        if (move.Skill == nameof(Skill.Teleport) && i >= 0)
        {
            to = positions[i].Position;
        }
        else if (i + 1 < positions.Count && positions[i + 1].At <= move.End + TimeSpan.FromSeconds(1))
        {
            to = positions[i + 1].Position;
        }
        else if (i >= 0)
        {
            to = positions[i].Position;
        }

        if (move.Skill == nameof(Skill.Teleport))
        {
            from = Server.At(move.Start - TimeSpan.FromTicks(1))?.Position ?? from;
        }

        var moved = from is not null && to is not null ? from.Distance(to) : 0;
        var hits = from is null ? [] : HitsBetween(move.Start, move.End + TimeSpan.FromMilliseconds(300))
            .Where(h => h.Where is not null && Geometry.DistanceToSegment(h.Where, from, to ?? from) <= 5).ToList();
        var before = ManaAt(move.Start - TimeSpan.FromTicks(1));
        var previous = LastAtOrBefore(Moves, move.Start - TimeSpan.FromTicks(1), m => m.Start);
        if (previous >= 0 && Charges().ContainsKey(Moves[previous].Start) && _lastCharged[Moves[previous].Start] > move.Start)
        {
            before = View(Moves[previous]).ManaAfter ?? before;
        }

        int? after = before is { } b ? b - Charges().GetValueOrDefault(move.Start) : null;
        return new SkillMoveView(move, from, to, moved, hits, before, after);
    }

    internal static int LastAtOrBefore<T>(IReadOnlyList<T> list, TimeSpan t, Func<T, TimeSpan> at)
    {
        int low = 0, high = list.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (at(list[middle]) <= t)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}

public static class Geometry
{
    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>
    /// The subtiles a straight line from <paramref name="from"/> to <paramref name="to"/> passes over, leaving out the
    /// first and last <paramref name="skip"/> - the caster and the target stand there, often against a wall.
    /// </summary>
    public static IEnumerable<(int X, int Y)> Between(Point from, Point to, int skip = 1)
    {
        var steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        (int, int)? last = null;
        for (var i = skip; i <= steps - skip; i++)
        {
            var t = (double)i / steps;
            var cell = ((int)Math.Round(from.X + (to.X - from.X) * t), (int)Math.Round(from.Y + (to.Y - from.Y) * t));
            if (cell != last)
            {
                last = cell;
                yield return cell;
            }
        }
    }
}
