using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// What a client fought with: every attack or skill packet sent at a unit or a point (Teleport left out,
/// as in the activity), with the skill selected on that hand at the time and the client's mana; the
/// hits (NPCHit) on a unit this client sent an attack at within the last second, credited to that
/// attack's skill - a hit by a party member on the same unit in that second is credited too; a hit on
/// another unit goes to the latest cast at a point meant for it, or to the client's skill move beside it (see <see cref="PointCastHit"/>).
/// </summary>
public sealed class FightObserver : ICachedObserver
{
    public string CacheKey => "FightObserver";

    public void Save(System.IO.BinaryWriter w)
    {
        foreach (var casts in new[] { Casts, Buffs })
        {
            w.WriteList(casts, (x, c) => { x.Write(c.At); x.WriteText(c.Skill); x.Write(c.Mana); });
        }

        w.WriteList(Hits, (x, h) => { x.Write(h.At); x.WriteText(h.Skill); });
        w.WriteList(Mana, (x, m) => { x.Write(m.At); x.Write(m.Mana); });
    }

    public void Load(System.IO.BinaryReader r)
    {
        foreach (var casts in new[] { Casts, Buffs })
        {
            r.ReadList(casts, x => new Cast(x.ReadTime(), x.ReadText(), x.ReadNullableDouble()));
        }

        r.ReadList(Hits, x => (x.ReadTime(), x.ReadText()));
        r.ReadList(Mana, x => (x.ReadTime(), x.ReadDouble()));
    }

    public static readonly TimeSpan HitWindow = TimeSpan.FromSeconds(1);

    /// <summary>Below this share of the maximum, mana counts as low.</summary>
    public const double LowMana = 0.15;

    private readonly Dictionary<uint, (TimeSpan At, string Skill)> _lastAt = [];
    private int _maxMana;
    private TimeSpan _maxManaAt = TimeSpan.FromDays(-1000);

    public sealed record Cast(TimeSpan At, string Skill, double? Mana);

    public List<Cast> Casts { get; } = [];

    /// <summary>Buff casts (<see cref="ActivityObserver.IsBuff"/>), kept apart from the attacks.</summary>
    public List<Cast> Buffs { get; } = [];

    public List<(TimeSpan At, string Skill)> Hits { get; } = [];

    /// <summary>The client's mana as a share of its maximum, whenever a life and mana packet came.</summary>
    public List<(TimeSpan At, double Mana)> Mana { get; } = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        var me = context.Game.Me;
        if (p.Length == 0 || me == null)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsSent && p[0] is >= 0x05 and <= 0x11 and not 0x0B)
        {
            var hand = p[0] <= 0x0A ? Hand.Left : Hand.Right;
            var skill = me.ActiveSkills.TryGetValue(hand, out var selected) ? selected : (Skill?)null;
            if (skill != null && ActivityObserver.IsBuff(skill.Value))
            {
                Buffs.Add(new Cast(context.Now, skill.Value.ToString(), ManaShare(context)));
                return;
            }

            if (skill != null && !ActivityObserver.IsAttack(skill.Value))
            {
                return;
            }

            var name = skill == null ? $"{hand.ToString().ToLowerInvariant()} skill (unknown)" : skill.ToString();
            Casts.Add(new Cast(context.Now, name, ManaShare(context)));
            if (p.Length >= 9 && p[0] is 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11 && PacketDecoder.U32(p, 1) == (uint)EntityType.NPC)
            {
                _lastAt[PacketDecoder.U32(p, 5)] = (context.Now, name);
            }
            else if (p.Length >= 5 && p[0] is 0x05 or 0x08 or 0x0C or 0x0F && me.Location is { } from)
            {
                var aim = new Point(PacketDecoder.U16(p, 1), PacketDecoder.U16(p, 3));
                _pointCasts.AddLast((context.Now, name, CombatObserver.MeantFor(context, from, aim).Id));
                while (_pointCasts.First != null && context.Now - _pointCasts.First.Value.At > PointCastMemory)
                {
                    _pointCasts.RemoveFirst();
                }
            }

            return;
        }

        if (record.Kind != TraceRecordKind.D2gsReceived)
        {
            return;
        }

        if (p[0] == 0x0C && p.Length >= 6 && p[1] == (byte)EntityType.NPC)
        {
            var id = PacketDecoder.U32(p, 2);
            if (_lastAt.TryGetValue(id, out var last) && context.Now - last.At <= HitWindow)
            {
                Hits.Add((context.Now, last.Skill));
            }
            else if (PointCastHit(context, id) is { } skill)
            {
                Hits.Add((context.Now, skill));
            }
        }
        else if (p[0] is 0x18 or 0x95 && ManaShare(context) is { } share)
        {
            Mana.Add((context.Now, share));
        }
    }

    public void Finished(ReplayContext context)
    {
        _lastAt.Clear();
        _pointCasts.Clear();
    }

    private static readonly TimeSpan PointCastMemory = TimeSpan.FromSeconds(3);

    private readonly LinkedList<(TimeSpan At, string Skill, uint Meant)> _pointCasts = new();

    /// <summary>
    /// The skill of the latest cast at a point meant for the unit hit - the nearest monster in its direction
    /// (<see cref="CombatObserver.MeantFor"/>) - within that skill's hit window; while this client's own skill move plays out,
    /// that skill for a unit within 5 of it.
    /// </summary>
    private string PointCastHit(ReplayContext context, uint unit)
    {
        for (var node = _pointCasts.Last; node != null; node = node.Previous)
        {
            var (at, skill, meant) = node.Value;
            if (context.Now - at > CombatObserver.HitWindow(skill))
            {
                continue;
            }

            if (meant == unit)
            {
                return skill;
            }

            if (CombatObserver.IsSkillMove(skill) && context.Game.Me.Effects.ContainsKey(Core.D2GS.Enums.EntityEffect.Skillmove)
                && context.Position is { } self && context.Npc(unit)?.Location is { } where && self.Distance(where) <= 5)
            {
                return skill;
            }
        }

        return null;
    }

    private double? ManaShare(ReplayContext context)
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

        return _maxMana > 0 ? Math.Clamp((double)context.Game.Me.Mana / _maxMana, 0, 1) : null;
    }

    public double? ManaAt(TimeSpan t)
    {
        int low = 0, high = Mana.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (Mana[middle].At <= t)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found < 0 ? null : Mana[found].Mana;
    }

    public string SkillAt(TimeSpan t) => LastBefore(Casts, t);

    public string BuffAt(TimeSpan t) => LastBefore(Buffs, t);

    private static string LastBefore(List<Cast> casts, TimeSpan t)
    {
        int low = 0, high = casts.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (casts[middle].At <= t)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found < 0 ? null : casts[found].Skill;
    }
}

/// <summary>Fighting time split by skill: casts, seconds (each fighting slice goes to the skill of the last cast before it), hits, and time with low mana.</summary>
public sealed class SkillSplit
{
    public sealed class Row
    {
        public int Casts;
        public TimeSpan Time;
        public int Hits;
        public TimeSpan LowMana;
    }

    public Dictionary<string, Row> Skills { get; } = new(StringComparer.Ordinal);

    /// <summary>Buffs, apart from the attacks: casts and the seconds of the buffing state, each slice to the buff cast last before it.</summary>
    public Dictionary<string, Row> Buffs { get; } = new(StringComparer.Ordinal);

    public TimeSpan Fighting { get; private set; }

    public TimeSpan Buffing { get; private set; }

    public TimeSpan LowMana { get; private set; }

    public TimeSpan ManaKnown { get; private set; }

    private Row RowOf(string skill) => Skills.TryGetValue(skill, out var row) ? row : Skills[skill] = new Row();

    private Row BuffOf(string skill) => Buffs.TryGetValue(skill, out var row) ? row : Buffs[skill] = new Row();

    public void Add(FightObserver fights, IReadOnlyList<Stretch> stretches, TimeSpan from, TimeSpan to)
    {
        foreach (var cast in fights.Casts.Where(c => c.At >= from && c.At < to))
        {
            RowOf(cast.Skill).Casts++;
        }

        foreach (var cast in fights.Buffs.Where(c => c.At >= from && c.At < to))
        {
            BuffOf(cast.Skill).Casts++;
        }

        foreach (var stretch in stretches.Where(s => s.State == ActivityState.Buffing && s.End > from && s.Start < to))
        {
            var start = stretch.Start > from ? stretch.Start : from;
            var end = stretch.End < to ? stretch.End : to;
            for (var t = start; t < end; t += ActivityObserver.Slice)
            {
                var slice = t + ActivityObserver.Slice < end ? ActivityObserver.Slice : end - t;
                BuffOf(fights.BuffAt(t + slice) ?? "?").Time += slice;
                Buffing += slice;
            }
        }

        foreach (var (_, skill) in fights.Hits.Where(h => h.At >= from && h.At < to))
        {
            RowOf(skill).Hits++;
        }

        foreach (var stretch in stretches.Where(s => s.State == ActivityState.Fighting && s.End > from && s.Start < to))
        {
            var start = stretch.Start > from ? stretch.Start : from;
            var end = stretch.End < to ? stretch.End : to;
            for (var t = start; t < end; t += ActivityObserver.Slice)
            {
                var slice = t + ActivityObserver.Slice < end ? ActivityObserver.Slice : end - t;
                var skill = fights.SkillAt(t + slice) ?? "?";
                var row = RowOf(skill);
                row.Time += slice;
                Fighting += slice;
                if (fights.ManaAt(t + slice) is { } mana)
                {
                    ManaKnown += slice;
                    if (mana < FightObserver.LowMana)
                    {
                        row.LowMana += slice;
                        LowMana += slice;
                    }
                }
            }
        }
    }

    public void Add(SkillSplit other)
    {
        foreach (var (skill, row) in other.Skills)
        {
            var mine = RowOf(skill);
            mine.Casts += row.Casts;
            mine.Time += row.Time;
            mine.Hits += row.Hits;
            mine.LowMana += row.LowMana;
        }

        foreach (var (skill, row) in other.Buffs)
        {
            var mine = BuffOf(skill);
            mine.Casts += row.Casts;
            mine.Time += row.Time;
        }

        Fighting += other.Fighting;
        Buffing += other.Buffing;
        LowMana += other.LowMana;
        ManaKnown += other.ManaKnown;
    }

    /// <summary>"BlessedHammer 62% 300s, Attack 30% 140s; mana under 15% for 25% of fighting" - top skills by time.</summary>
    public string Short(int shown = 3)
    {
        if (Fighting <= TimeSpan.Zero)
        {
            return Skills.Count == 0 ? "no attacks" : $"{Skills.Sum(s => s.Value.Casts)} casts outside fighting time";
        }

        var top = Skills.Where(s => s.Value.Time > TimeSpan.Zero).OrderByDescending(s => s.Value.Time).Take(shown)
            .Select(s => $"{s.Key} {s.Value.Time.TotalSeconds / Fighting.TotalSeconds:0%}");
        return $"{string.Join(", ", top)}{(ManaKnown > TimeSpan.Zero ? $"; mana under {FightObserver.LowMana:0%} for {LowMana.TotalSeconds / Fighting.TotalSeconds:0%} of it" : "")}";
    }

    public IEnumerable<string> Lines()
    {
        foreach (var (skill, row) in Skills.OrderByDescending(s => s.Value.Time).ThenByDescending(s => s.Value.Casts))
        {
            var share = Fighting > TimeSpan.Zero ? row.Time.TotalSeconds / Fighting.TotalSeconds : 0;
            yield return $"{skill,-18} {row.Casts,6} casts {row.Time.TotalSeconds,7:0}s ({share,4:0%} of fighting) {row.Hits,6} hits{(row.Time > TimeSpan.Zero ? $"  mana under {FightObserver.LowMana:0%} for {row.LowMana.TotalSeconds / row.Time.TotalSeconds:0%} of its time" : "")}";
        }

        if (Buffs.Count > 0)
        {
            yield return $"buffs (not fighting; the buffing state, {Buffing.TotalSeconds:0}s):";
            foreach (var (skill, row) in Buffs.OrderByDescending(s => s.Value.Time).ThenByDescending(s => s.Value.Casts))
            {
                yield return $"  {skill,-16} {row.Casts,6} casts {row.Time.TotalSeconds,7:0}s";
            }
        }
    }
}

/// <summary>
/// What a client did that explains a town stay: NPC chats (who, and whether a trade happened in it),
/// trades, waypoint and portal use, the stash, item moves and deaths. A chat runs from its initiate
/// (0x2F) to its terminate (0x30) or its last trade, and ends when the client walks or runs off.
/// </summary>
public sealed class TownObserver : ICachedObserver
{
    public string CacheKey => "TownObserver";

    public void Save(System.IO.BinaryWriter w)
    {
        w.WriteList(Chats, (x, c) => { x.Write(c.Start); x.Write(c.End); x.WriteText(c.Npc); x.Write(c.Healer); x.Write(c.Trades); });
        foreach (var list in new[] { Trades, Uses })
        {
            w.WriteList(list, (x, t) => { x.Write(t.At); x.WriteText(t.What); });
        }

        w.WriteTimes(ItemMoves);
        _deaths.Save(w);
        w.WriteList(FileEnds, (x, f) => { x.Write(f.End); x.Write(f.Life); x.Write(f.LifeAt); x.Write(f.SentLeave); x.Write(f.LeaveAt); });
    }

    public void Load(System.IO.BinaryReader r)
    {
        r.ReadList(Chats, x => new Chat { Start = x.ReadTime(), End = x.ReadTime(), Npc = x.ReadText(), Healer = x.ReadBoolean(), Trades = x.ReadInt32() });
        foreach (var list in new[] { Trades, Uses })
        {
            r.ReadList(list, x => (x.ReadTime(), x.ReadText()));
        }

        r.ReadTimes(ItemMoves);
        _deaths.Load(r);
        r.ReadList(FileEnds, x => new FileEnd(x.ReadTime(), x.ReadNullableDouble(), x.ReadTime(), x.ReadBoolean()) { LeaveAt = x.ReadNullableTime() });
    }

    private static readonly HashSet<NPCCode> Healers = [NPCCode.Akara, NPCCode.Fara, NPCCode.Ormus, NPCCode.JamellaAct4, NPCCode.Malah];

    private readonly DeathCheck _deaths = new();
    private Chat _open;

    public sealed class Chat
    {
        public TimeSpan Start;
        public TimeSpan End;
        public string Npc;
        public bool Healer;
        public int Trades;
    }

    public List<Chat> Chats { get; } = [];

    /// <summary>Buys, sells, repairs, identifies at a vendor, hires: when, and which.</summary>
    public List<(TimeSpan At, string What)> Trades { get; } = [];

    /// <summary>Waypoint menus taken (0x49), town portals and waypoints clicked, the stash opened.</summary>
    public List<(TimeSpan At, string What)> Uses { get; } = [];

    public List<TimeSpan> ItemMoves { get; } = [];

    public IEnumerable<TimeSpan> Deaths => _deaths.Found.Where(d => d.Kind == "death").Select(d => d.Offset);

    /// <summary>How each file of the client ended, in file order: its last life share before the end, and whether the client sent leave-game.</summary>
    public sealed record FileEnd(TimeSpan End, double? Life, TimeSpan LifeAt, bool SentLeave)
    {
        /// <summary>When the client sent leave-game, the moment it left; otherwise null and the file's end is.</summary>
        public TimeSpan? LeaveAt { get; init; }

        public TimeSpan Left => LeaveAt ?? End;
    }

    public List<FileEnd> FileEnds { get; } = [];

    private double? _life;
    private TimeSpan _lifeAt;
    private bool _sentLeave;
    private TimeSpan? _leaveAt;

    public void After(ReplayContext context, TraceRecord record)
    {
        _deaths.After(context, record);
        var p = record.Payload;
        if (record.Kind == TraceRecordKind.D2gsReceived && p.Length > 0 && p[0] is 0x18 or 0x95 && context.Game.Me != null && context.MaxLife > 0 && !_sentLeave)
        {
            _life = context.LifeFraction;
            _lifeAt = context.Now;
        }

        if (record.Kind != TraceRecordKind.D2gsSent || p.Length == 0)
        {
            return;
        }

        if (p[0] == (byte)Core.D2GS.Packet.OutGoingPacket.LeaveGame)
        {
            _leaveAt ??= context.Now;
            _sentLeave = true;
        }

        switch (p[0])
        {
            case 0x2F when p.Length >= 9:
                var id = PacketDecoder.U32(p, 5);
                var code = context.CodeOf(id);
                _open = new Chat { Start = context.Now, End = context.Now, Npc = code == null ? context.UnitName(id) : Names.Npc(code.Value), Healer = code != null && Healers.Contains(code.Value) };
                Chats.Add(_open);
                break;
            case 0x30:
                if (_open != null)
                {
                    _open.End = context.Now;
                    _open = null;
                }

                break;
            case 0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37 or 0x39:
                var what = p[0] switch
                {
                    0x32 => "buy",
                    0x33 => "sell",
                    0x34 or 0x37 => "identify",
                    0x35 => "repair",
                    0x36 => "hire",
                    _ => "heal",
                };
                Trades.Add((context.Now, what));
                if (_open != null)
                {
                    _open.Trades++;
                    _open.End = context.Now;
                }

                break;
            case 0x49:
                Uses.Add((context.Now, "waypoint"));
                break;
            case 0x13 when p.Length >= 9 && PacketDecoder.U32(p, 1) == (uint)EntityType.Object:
                var target = context.Game.WorldObjects.GetValueOrDefault((PacketDecoder.U32(p, 5), EntityType.Object));
                var use = target?.Code switch
                {
                    EntityCode.TownPortal or EntityCode.RedTownPortal => "portal",
                    { } c when c.ToString().Contains("Portal", StringComparison.Ordinal) => "act portal",
                    EntityCode.Stash => "stash",
                    { } c when c.ToString().StartsWith("Waypoint", StringComparison.Ordinal) => "waypoint",
                    _ => null,
                };
                if (use != null)
                {
                    Uses.Add((context.Now, use));
                }

                break;
            case >= 0x16 and <= 0x2A and not 0x20 and not 0x26:
                ItemMoves.Add(context.Now);
                break;
            case <= 0x04:
                _open = null;
                break;
        }
    }

    public void Finished(ReplayContext context)
    {
        _deaths.Finished(context);
        _open = null;
        FileEnds.Add(new FileEnd(context.Now, _life, _lifeAt, _sentLeave) { LeaveAt = _leaveAt });
        _life = null;
        _sentLeave = false;
        _leaveAt = null;
    }
}

/// <summary>One stay in town: why it began, the step around its start, and where its time went, slice by slice.</summary>
public sealed record TownStay(string Client, TimeSpan Start, TimeSpan End, string Cause, string Step)
{
    public TimeSpan Duration => End - Start;

    /// <summary>What the time inside went to, as contiguous runs covering the stay.</summary>
    public List<(TimeSpan Start, TimeSpan End, string Part)> Runs { get; init; } = [];

    public Dictionary<string, TimeSpan> Split => Runs.GroupBy(r => r.Part).ToDictionary(g => g.Key, g => g.Aggregate(TimeSpan.Zero, (sum, r) => sum + (r.End - r.Start)), StringComparer.Ordinal);

    public int Trades { get; init; }

    /// <summary>For a rejoin: what the packets say about how the previous file ended.</summary>
    public string Evidence { get; init; }

    /// <summary>The part of the stay within [from, to], or null when none of it is.</summary>
    public TownStay Within(TimeSpan from, TimeSpan to)
    {
        var start = Start > from ? Start : from;
        var end = End < to ? End : to;
        if (end <= start)
        {
            return null;
        }

        if (start == Start && end == End)
        {
            return this;
        }

        return this with
        {
            Start = start,
            End = end,
            Runs = [.. Runs.Where(r => r.End > start && r.Start < end).Select(r => (r.Start > start ? r.Start : start, r.End < end ? r.End : end, r.Part))],
        };
    }
}

/// <summary>
/// The town stays of a client and what each was for. A stay is every 100 ms slice in which the client
/// has a trace file open and is in a town area (the slice decided at its end, as the activity states
/// are); it begins with:
///   game start         placed in town on joining the game (its first file)
///   rejoin after chicken / rejoin after leave / rejoin after disconnect   placed in town on joining
///                      again (a later file): chicken when the bot logged a chicken decision for it in the
///                      15 s before the previous file ended, leave when that file ended with the client
///                      sending leave-game, disconnect when it ended without (a dropped connection or a crash)
///   rejoin (no bot events recorded: cause unknown)   the same when the game has no typed bot events to
///                      tell a chicken by; the stay carries the packet evidence: the life share last seen
///                      before the previous file ended, and whether it ended with leave-game
///   death              in the 20 s after its corpse was assigned
///   waypoint back / portal back / act portal   a waypoint, a town portal or another portal object (the one to the next act) taken in the 10 s before
///   other              none of these (an area change the packets do not explain)
/// Inside, each 100 ms slice is, first that applies: vendor (in a chat with an NPC in which something
/// was bought, sold, repaired, identified or hired), healing (a chat with a healer and no trade), npc
/// talk (any other chat), waypoint/portal (one used in the last 2 s), stash (an item moved, the stash
/// opened in the last 30 s), items (an item moved in the last 1.5 s otherwise), waiting for others (a
/// typed wait), walking (moved over 2 subtiles in the last second), idle.
/// </summary>
public static class TownStays
{
    public const string UnknownRejoin = "rejoin (no bot events recorded: cause unknown)";

    public static readonly string[] Causes = ["game start", "rejoin after chicken", "rejoin after leave", "rejoin after disconnect", UnknownRejoin, "death", "waypoint back", "portal back", "act portal", "other"];

    public static readonly string[] Parts = ["vendor", "healing", "npc talk", "waypoint/portal", "stash", "items", "waiting for others", "walking", "idle"];

    /// <param name="chickens">The bot's chicken decisions for the client; null when the game has no typed bot events at all, so a rejoin's cause cannot be told.</param>
    public static List<TownStay> Of(string client, ActivityObserver activity, TownObserver town, IReadOnlyList<(TimeSpan Start, TimeSpan End)> waits,
        IReadOnlyList<StepSpan> steps, IReadOnlyList<TimeSpan> chickens, TimeSpan end)
    {
        var stays = new List<TownStay>();
        TimeSpan? start = null;
        for (var t = TimeSpan.Zero; t < end; t += ActivityObserver.Slice)
        {
            var at = t + ActivityObserver.Slice < end ? t + ActivityObserver.Slice : end;
            var inTown = activity.Files.Any(f => f.Start <= at && at <= f.End) && activity.AreaAt(at) is { } area && ActivityObserver.IsTown(area);
            if (inTown)
            {
                start ??= t;
                continue;
            }

            if (start != null)
            {
                stays.Add(Stay(client, activity, town, waits, steps, chickens, start.Value, t));
                start = null;
            }
        }

        if (start != null && end > start)
        {
            stays.Add(Stay(client, activity, town, waits, steps, chickens, start.Value, end));
        }

        for (var i = 1; i < stays.Count; i++)
        {
            if (stays[i].Cause == "other" && OnFootAcrossTheEdge(activity, town, stays[i - 1].End, stays[i].Start))
            {
                stays[i] = stays[i] with { Cause = stays[i - 1].Cause, Step = stays[i - 1].Step ?? stays[i].Step, Evidence = stays[i - 1].Evidence };
            }
        }

        return stays;
    }

    /// <summary>How long a client may step out of town on foot and still be on the same stay when it walks back in.</summary>
    public static readonly TimeSpan EdgeCrossing = TimeSpan.FromSeconds(5);

    private static bool OnFootAcrossTheEdge(ActivityObserver activity, TownObserver town, TimeSpan left, TimeSpan back)
        => back - left <= EdgeCrossing
            && activity.Files.Any(f => f.Start <= left && back <= f.End)
            && !town.Uses.Any(u => u.What is "waypoint" or "portal" or "act portal" && u.At >= left - TimeSpan.FromSeconds(10) && u.At <= back)
            && !activity.Warps.Any(w => w >= left - TimeSpan.FromSeconds(2) && w <= back);

    private static TownStay Stay(string client, ActivityObserver activity, TownObserver town, IReadOnlyList<(TimeSpan Start, TimeSpan End)> waits,
        IReadOnlyList<StepSpan> steps, IReadOnlyList<TimeSpan> chickens, TimeSpan start, TimeSpan end)
    {
        var step = steps.Where(s => s.Covers(client) && s.Start <= start && s.End >= start && s.Duration > TimeSpan.Zero).OrderBy(s => s.Duration).FirstOrDefault();
        var (cause, evidence) = Cause(activity, town, chickens, start);
        var runs = new List<(TimeSpan Start, TimeSpan End, string Part)>();
        for (var t = start; t < end; t += ActivityObserver.Slice)
        {
            var slice = t + ActivityObserver.Slice < end ? ActivityObserver.Slice : end - t;
            var part = Part(activity, town, waits, t + slice);
            if (runs.Count > 0 && runs[^1].Part == part)
            {
                runs[^1] = (runs[^1].Start, t + slice, part);
            }
            else
            {
                runs.Add((t, t + slice, part));
            }
        }

        return new TownStay(client, start, end, cause, step?.Name)
        {
            Trades = town.Trades.Count(tr => tr.At >= start && tr.At < end),
            Evidence = evidence,
            Runs = runs,
        };
    }

    private static (string Cause, string Evidence) Cause(ActivityObserver activity, TownObserver town, IReadOnlyList<TimeSpan> chickens, TimeSpan start)
    {
        var files = activity.Files;
        var index = -1;
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Start <= start && start <= files[i].End)
            {
                index = i;
                break;
            }
        }

        if (index >= 0 && start - files[index].Start <= TimeSpan.FromSeconds(5))
        {
            if (index == 0)
            {
                return ("game start", null);
            }

            var left = files[index - 1].End;
            var ended = index - 1 < town.FileEnds.Count ? town.FileEnds[index - 1] : null;
            var evidence = ended == null ? null : Evidence(ended);
            if (chickens == null)
            {
                return (UnknownRejoin, evidence);
            }

            if (chickens.Any(c => c >= left - TimeSpan.FromSeconds(15) && c <= left + TimeSpan.FromSeconds(2)))
            {
                return ("rejoin after chicken", evidence);
            }

            return (ended == null || ended.SentLeave ? "rejoin after leave" : "rejoin after disconnect", evidence);
        }

        var decided = start + ActivityObserver.Slice;
        if (town.Deaths.Any(d => d <= decided && decided - d <= TimeSpan.FromSeconds(20)))
        {
            return ("death", null);
        }

        var used = town.Uses.Where(u => u.What is "waypoint" or "portal" or "act portal" && u.At <= decided && decided - u.At <= TimeSpan.FromSeconds(10)).Select(u => u.What).LastOrDefault();
        return (used switch
        {
            "waypoint" => "waypoint back",
            "portal" => "portal back",
            "act portal" => "act portal",
            _ => "other",
        }, null);
    }

    /// <summary>"life 27% 0.002s before leaving; the client sent leave-game" - how the previous file ended.</summary>
    public static string Evidence(TownObserver.FileEnd ended)
    {
        var life = ended.Life is { } share
            ? $"life {share:0%} {(ended.Left - ended.LifeAt).TotalSeconds:0.###}s before leaving"
            : "no life seen before leaving";
        return $"{life}; {(ended.SentLeave ? "the client sent leave-game" : "the file ended without the client sending leave-game (connection dropped)")}";
    }

    private static string Part(ActivityObserver activity, TownObserver town, IReadOnlyList<(TimeSpan Start, TimeSpan End)> waits, TimeSpan t)
    {
        var chat = town.Chats.LastOrDefault(c => c.Start <= t && t <= c.End);
        if (chat != null)
        {
            return chat.Trades > 0 ? "vendor" : chat.Healer ? "healing" : "npc talk";
        }

        if (town.Uses.Any(u => u.What is "waypoint" or "portal" or "act portal" && u.At <= t && t - u.At <= TimeSpan.FromSeconds(2)))
        {
            return "waypoint/portal";
        }

        if (Recent(town.ItemMoves, t, ActivityObserver.PickMemory))
        {
            return town.Uses.Any(u => u.What == "stash" && u.At <= t && t - u.At <= TimeSpan.FromSeconds(30)) ? "stash" : "items";
        }

        if (waits.Any(w => w.Start <= t && t <= w.End))
        {
            return "waiting for others";
        }

        var here = activity.PositionAt(t);
        var before = activity.PositionAt(t - ActivityObserver.MoveWindow);
        return here != null && before != null && here.Distance(before) > ActivityObserver.MoveDistance ? "walking" : "idle";
    }

    /// <summary>Whether a sorted list holds a time in (t - window, t].</summary>
    private static bool Recent(List<TimeSpan> sorted, TimeSpan t, TimeSpan window)
    {
        var index = sorted.BinarySearch(t);
        if (index >= 0)
        {
            return true;
        }

        var before = ~index - 1;
        return before >= 0 && t - sorted[before] <= window;
    }

    /// <summary>The times the bot logged a chicken decision for a client; null when the game has no typed bot events, so a missing chicken says nothing.</summary>
    public static List<TimeSpan> Chickens(GameQuery query, string client)
        => query.GameEntries().Any(e => !e.IsLog && e.Kind is not ("gap" or "trace-failed"))
            ? [.. query.GameEntries().Where(e => e.Kind == "decision" && e.Str("name") == "chicken" && e.Client == client).Select(query.Offset)]
            : null;
}

/// <summary>
/// Town stays summed per cause, and the time inside town per part. <see cref="Total"/> is the time in town
/// areas; the headline's town state is less, since fighting, picking, npc and buffing outrank it there.
/// </summary>
public sealed class TownSummary
{
    public Dictionary<string, List<double>> ByCause { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TimeSpan> Parts { get; } = new(StringComparer.Ordinal);

    /// <summary>Per activity state, the time it held inside the stays.</summary>
    public Dictionary<ActivityState, TimeSpan> States { get; } = [];

    public TimeSpan Total { get; private set; }

    public void Add(IEnumerable<TownStay> stays, IReadOnlyList<Stretch> stretches = null)
    {
        foreach (var stay in stays)
        {
            (ByCause.TryGetValue(stay.Cause, out var list) ? list : ByCause[stay.Cause] = []).Add(stay.Duration.TotalSeconds);
            foreach (var (part, time) in stay.Split)
            {
                Parts[part] = Parts.GetValueOrDefault(part) + time;
            }

            Total += stay.Duration;
            foreach (var stretch in stretches ?? [])
            {
                var start = stretch.Start > stay.Start ? stretch.Start : stay.Start;
                var end = stretch.End < stay.End ? stretch.End : stay.End;
                if (end > start)
                {
                    States[stretch.State] = States.GetValueOrDefault(stretch.State) + (end - start);
                }
            }
        }
    }

    public void Add(TownSummary other)
    {
        foreach (var (cause, list) in other.ByCause)
        {
            (ByCause.TryGetValue(cause, out var mine) ? mine : ByCause[cause] = []).AddRange(list);
        }

        foreach (var (part, time) in other.Parts)
        {
            Parts[part] = Parts.GetValueOrDefault(part) + time;
        }

        foreach (var (state, time) in other.States)
        {
            States[state] = States.GetValueOrDefault(state) + time;
        }

        Total += other.Total;
    }

    /// <summary>"time in town areas: 276s (headline town 217s + npc 40s, picking 15s, fighting 4s there)".</summary>
    public string Reconcile()
    {
        var town = States.GetValueOrDefault(ActivityState.Town);
        var others = States.Where(s => s.Key != ActivityState.Town && s.Value > TimeSpan.Zero).OrderByDescending(s => s.Value).ToList();
        return $"time in town areas: {Total.TotalSeconds:0}s (headline town {town.TotalSeconds:0}s"
            + (others.Count == 0 ? "" : $" + {string.Join(", ", others.Select(s => $"{ActivityObserver.Name(s.Key)} {s.Value.TotalSeconds:0}s"))} there")
            + "); the causes below sum to it";
    }

    /// <summary>"portal back 5x median 40s total 210s, game start 1x ..." - by total, largest first.</summary>
    public string Causes()
        => Total <= TimeSpan.Zero
            ? "no time in town"
            : string.Join(", ", ByCause.OrderByDescending(c => c.Value.Sum()).Select(c => $"{c.Key} {c.Value.Count}x median {Steps.Percentiles(c.Value).Median:0}s total {c.Value.Sum():0}s"));

    public string Inside()
        => Total <= TimeSpan.Zero
            ? ""
            : string.Join(", ", Parts.Where(p => p.Value > TimeSpan.Zero).OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value.TotalSeconds / Total.TotalSeconds:0%} {p.Value.TotalSeconds:0}s"));
}
