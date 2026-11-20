using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>What a client was doing, in precedence order: when two apply, the earlier one wins.</summary>
public enum ActivityState
{
    Out,
    Loading,
    Fighting,
    Picking,
    Npc,
    Buffing,
    Town,
    Teleporting,
    Walking,
    StandingWaiting,
    StandingIdle,
}

public sealed record Stretch(ActivityState State, TimeSpan Start, TimeSpan End, Point At, string Area)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Splits a client's wall time into mutually exclusive states, decided every 100 ms at the end of the
/// slice (the last slice of an interval is shorter, so the states sum exactly to it):
///   out          no trace file of the client is open: before its first, between a leave and a rejoin, after its last
///   loading      in a file, but the server has not placed the character yet
///   fighting     an attack or skill other than Teleport, town portal or identify sent at a unit or a point
///                within the last 1.5 s; buffs are not fighting
///   picking      an item packet sent (pick up, drop, move between containers, belt, cube) within the last 1.5 s
///   npc          talking to an NPC (between initiate and terminate chat), or an interact, trade, buy, sell,
///                repair, hire or waypoint packet within the last 2 s
///   buffing      a self or party buff (<see cref="IsBuff"/>: armors, shields, shouts and orders) cast within the last 1.5 s
///   town         in a town area (from the revealed tile holding the position, see <see cref="AreaTracker"/>; unknown is not town)
///   teleporting  moved more than 2 subtiles over the last second, with Teleport cast within it
///   walking      moved more than 2 subtiles over the last second otherwise
///   standing-waiting  none of the above, inside a typed wait event the bot reported
///   standing-idle     none of the above
/// Precedence is the order above.
/// </summary>
public sealed class ActivityObserver : ICachedObserver
{
    public string CacheKey => "ActivityObserver";

    public void Save(System.IO.BinaryWriter w)
    {
        w.WriteList(_positions, (x, p) => { x.Write(p.At); x.Write(p.Position); });
        w.WriteList(_areas, (x, a) => { x.Write(a.At); x.Write(a.Area.HasValue ? (int)a.Area.Value : -1); x.Write(a.LastKnown.HasValue ? (int)a.LastKnown.Value : -1); x.Write(a.FromServer); });
        foreach (var times in new[] { _fights, _buffs, _picks, _npcs, _teleports, _warps, _placed })
        {
            w.WriteTimes(times);
        }

        foreach (var spans in new[] { _chats, _files })
        {
            w.WriteList(spans, (x, s) => { x.Write(s.Start); x.Write(s.End); });
        }

        w.WriteList(_corrections, (x, c) => { x.Write(c.At); x.Write(c.Held); x.Write(c.To); });
        w.WriteList(_sends, (x, s) => { x.Write(s.At); x.Write(s.Type); });
        w.WriteText(Client);
        w.Write(MapId);
        w.Write((int)Difficulty);
        Server.Save(w);
    }

    public void Load(System.IO.BinaryReader r)
    {
        static Area? AreaOf(int value) => value < 0 ? null : (Area)value;
        r.ReadList(_positions, x => (x.ReadTime(), x.ReadPoint()));
        r.ReadList(_areas, x => (x.ReadTime(), AreaOf(x.ReadInt32()), AreaOf(x.ReadInt32()), x.ReadBoolean()));
        foreach (var times in new[] { _fights, _buffs, _picks, _npcs, _teleports, _warps, _placed })
        {
            r.ReadTimes(times);
        }

        foreach (var spans in new[] { _chats, _files })
        {
            r.ReadList(spans, x => (x.ReadTime(), x.ReadTime()));
        }

        r.ReadList(_corrections, x => (x.ReadTime(), x.ReadPoint(), x.ReadPoint()));
        r.ReadList(_sends, x => (x.ReadTime(), x.ReadByte()));
        Client = r.ReadText();
        MapId = r.ReadUInt32();
        Difficulty = (Difficulty)r.ReadInt32();
        Server.Load(r);
    }

    public static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan FightMemory = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan BuffMemory = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan PickMemory = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan NpcMemory = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MoveWindow = TimeSpan.FromSeconds(1);
    public const double MoveDistance = 2;
    private static readonly TimeSpan AreaInterval = TimeSpan.FromMilliseconds(500);

    private readonly List<(TimeSpan At, Point Position)> _positions = [];
    private readonly List<(TimeSpan At, Area? Area, Area? LastKnown, bool FromServer)> _areas = [];
    private readonly List<TimeSpan> _fights = [];
    private readonly List<TimeSpan> _buffs = [];
    private readonly List<TimeSpan> _picks = [];
    private readonly List<TimeSpan> _npcs = [];
    private readonly List<TimeSpan> _teleports = [];
    private readonly List<TimeSpan> _warps = [];
    private readonly List<(TimeSpan Start, TimeSpan End)> _chats = [];
    private readonly List<(TimeSpan Start, TimeSpan End)> _files = [];
    private readonly List<TimeSpan> _placed = [];
    private readonly List<(TimeSpan At, Point Held, Point To)> _corrections = [];
    private readonly List<(TimeSpan At, byte Type)> _sends = [];
    private readonly AreaTracker _tracker = new();
    private TimeSpan? _chatOpen;
    private TimeSpan? _fileStart;
    private bool _placedThisFile;
    private TimeSpan _areaCheckedAt = TimeSpan.FromDays(-1000);

    public string Client { get; private set; }

    /// <summary>The map seed and difficulty of the last file, for the map service.</summary>
    public uint MapId { get; private set; }

    public Difficulty Difficulty { get; private set; }

    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> Files => _files;

    public void Before(ReplayContext context, TraceRecord record)
    {
        Client ??= context.Client;
        if (_fileStart == null)
        {
            _fileStart = context.File.Header.StartUtc - context.GameTrace.StartUtc;
            _placedThisFile = false;
            SetArea(_fileStart.Value, null, null, false);
        }

        var p = record.Payload;
        if (record.Kind != TraceRecordKind.D2gsReceived || p.Length < 10 || context.Game.Me?.Location is not { } held || context.SelfId is not { } self)
        {
            return;
        }

        if (p[0] == 0x15 && p[1] == (byte)D2NG.Core.D2GS.Objects.EntityType.Player && PacketDecoder.U32(p, 2) == self)
        {
            _corrections.Add((context.Now, new Point(held.X, held.Y), new Point(PacketDecoder.U16(p, 6), PacketDecoder.U16(p, 8))));
        }
        else if (p[0] == 0x96)
        {
            try
            {
                var to = new Core.D2GS.Packet.Incoming.WalkVerifyPacket(new Core.D2GS.Packet.D2gsPacket(p)).Location;
                _corrections.Add((context.Now, new Point(held.X, held.Y), to));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
        }
    }

    /// <summary>Where the server put the client (a reassign or a walk verify), and where the client held itself just before.</summary>
    public IReadOnlyList<(TimeSpan At, Point Held, Point To)> Corrections => _corrections;

    /// <summary>Where the server said the client stood, as opposed to where the client held itself.</summary>
    public ServerPositions Server { get; } = new();

    public void After(ReplayContext context, TraceRecord record)
    {
        var now = context.Now;
        var payload = record.Payload;
        _tracker.Observe(context, record);
        Server.Observe(context, record);
        if (record.Kind == TraceRecordKind.D2gsSent && payload.Length > 0)
        {
            Sent(context, payload, now);
        }

        var me = context.Game.Me;
        var position = me?.Location;
        if (position != null && (position.X != 0 || position.Y != 0))
        {
            if (!_placedThisFile)
            {
                _placedThisFile = true;
                _placed.Add(now);
            }

            if (_positions.Count == 0 || !SamePoint(_positions[^1].Position, position))
            {
                _positions.Add((now, new Point(position.X, position.Y)));
            }
        }

        var forced = record.Kind == TraceRecordKind.D2gsReceived && payload.Length > 0 && payload[0] is 0x03 or 0x07 or 0x15;
        if (me != null && (forced || now - _areaCheckedAt >= AreaInterval))
        {
            _areaCheckedAt = now;
            var (area, source) = context.Game.KnownArea;
            SetArea(now, area, _tracker.LastKnown, source == AreaSource.ServerConfirmed);
        }
    }

    private void SetArea(TimeSpan at, Area? area, Area? lastKnown, bool fromServer)
    {
        if (_areas.Count > 0 && _areas[^1].Area == area && _areas[^1].FromServer == fromServer && (area != null || _areas[^1].LastKnown == lastKnown))
        {
            return;
        }

        _areas.Add((at, area, lastKnown, fromServer));
    }

    public void Finished(ReplayContext context)
    {
        if (context.Game.Me != null)
        {
            try
            {
                MapId = context.Game.MapId;
                Difficulty = context.Game.Difficulty;
            }
            catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
            {
            }
        }

        var start = _fileStart ?? context.File.Header.StartUtc - context.GameTrace.StartUtc;
        _files.Add((start, context.Now < start ? start : context.Now));
        if (_chatOpen is { } open)
        {
            _chats.Add((open, context.Now));
            _chatOpen = null;
        }

        if (!_placedThisFile)
        {
            _placed.Add(TimeSpan.MaxValue);
        }

        _fileStart = null;
    }

    private void Sent(ReplayContext context, byte[] payload, TimeSpan now)
    {
        _sends.Add((now, payload[0]));
        switch (payload[0])
        {
            case 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0A or 0x0C or 0x0D or 0x0E or 0x0F or 0x10 or 0x11:
                var hand = payload[0] <= 0x0A ? Hand.Left : Hand.Right;
                var skill = Skill.Attack;
                var known = context.Game.Me != null && context.Game.Me.ActiveSkills.TryGetValue(hand, out skill);
                if (known && skill == Skill.Teleport)
                {
                    _teleports.Add(now);
                }
                else if (known && IsBuff(skill))
                {
                    _buffs.Add(now);
                }
                else if (!known || IsAttack(skill))
                {
                    _fights.Add(now);
                }

                break;
            case >= 0x16 and <= 0x25 and not 0x20:
            case 0x27 or 0x28 or 0x29 or 0x2A or 0x4C:
                _picks.Add(now);
                break;
            case 0x13 or 0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37 or 0x38 or 0x39 or 0x49 or 0x4F:
                _npcs.Add(now);
                if (payload[0] == 0x49 || (payload[0] == 0x13 && payload.Length >= 5 && PacketDecoder.U32(payload, 1) is (uint)D2NG.Core.D2GS.Objects.EntityType.Object or (uint)D2NG.Core.D2GS.Objects.EntityType.Doorway))
                {
                    _warps.Add(now);
                }

                break;
            case 0x2F:
                _npcs.Add(now);
                _chatOpen ??= now;
                break;
            case 0x30:
                _npcs.Add(now);
                if (_chatOpen is { } open)
                {
                    _chats.Add((open, now));
                    _chatOpen = null;
                }

                break;
        }
    }

    private static bool SamePoint(Point a, Point b) => a.X == b.X && a.Y == b.Y;

    /// <summary>Whether casting a skill is fighting: everything but Teleport, the town portal and identify books and scrolls, and buffs.</summary>
    public static bool IsAttack(Skill skill)
        => !IsBuff(skill) && skill is not (Skill.Teleport or Skill.BookOfTownportal or Skill.ScrollOfTownportal or Skill.BookOfIdentify or Skill.ScrollOfIdentify);

    /// <summary>
    /// Self and party buffs: armors, shields, shouts and orders. skills.bin marks none of them apart from
    /// attacks, so they are listed here.
    /// </summary>
    public static readonly IReadOnlySet<Skill> Buffs = new HashSet<Skill>
    {
        Skill.FrozenArmor, Skill.ShiverArmor, Skill.ChillingArmor, Skill.EnergyShield, Skill.ThunderStorm, Skill.Enchant,
        Skill.BoneArmor, Skill.HolyShield, Skill.Shout, Skill.BattleOrders, Skill.BattleCommand,
        Skill.CycloneArmor, Skill.Quickness, Skill.Fade, Skill.Venom, Skill.BladeShield,
    };

    public static bool IsBuff(Skill skill) => Buffs.Contains(skill);

    public static bool IsTown(Area area) => area is Area.RogueEncampment or Area.LutGholein or Area.KurastDocks or Area.ThePandemoniumFortress or Area.Harrogath;

    public static string Name(ActivityState state) => state switch
    {
        ActivityState.StandingWaiting => "standing-waiting",
        ActivityState.StandingIdle => "standing-idle",
        _ => state.ToString().ToLowerInvariant(),
    };

    public static bool Unproductive(ActivityState state) => state is ActivityState.StandingIdle or ActivityState.StandingWaiting or ActivityState.Out or ActivityState.Loading;

    /// <summary>The client's stretches over [from, to], contiguous and covering it exactly; waits are the typed wait intervals of this client.</summary>
    public List<Stretch> Stretches(TimeSpan from, TimeSpan to, IReadOnlyList<(TimeSpan Start, TimeSpan End)> waits)
    {
        var stretches = new List<Stretch>();
        if (to <= from)
        {
            return stretches;
        }

        var cursor = new Cursors();
        var current = (State: (ActivityState?)null, Start: from, At: (Point)null, Area: (string)null);
        for (var start = from; start < to; start += Slice)
        {
            var end = start + Slice < to ? start + Slice : to;
            var state = StateAt(end, waits, cursor);
            if (state != current.State)
            {
                if (current.State != null)
                {
                    stretches.Add(new Stretch(current.State.Value, current.Start, start, current.At, current.Area));
                }

                current = (state, start, PositionAtOrBefore(start), AreaName(start));
            }
        }

        stretches.Add(new Stretch(current.State.Value, current.Start, to, current.At, current.Area));
        return stretches;
    }

    private sealed class Cursors
    {
        public int Position;
        public int Area;
        public int Fight;
        public int Buff;
        public int Pick;
        public int Npc;
        public int Teleport;
        public int File;
    }

    private ActivityState StateAt(TimeSpan t, IReadOnlyList<(TimeSpan Start, TimeSpan End)> waits, Cursors cursor)
    {
        while (cursor.File < _files.Count && _files[cursor.File].End < t)
        {
            cursor.File++;
        }

        if (cursor.File >= _files.Count || _files[cursor.File].Start > t)
        {
            return ActivityState.Out;
        }

        if (t < _placed[cursor.File])
        {
            return ActivityState.Loading;
        }

        if (Recent(_fights, ref cursor.Fight, t, FightMemory))
        {
            return ActivityState.Fighting;
        }

        if (Recent(_picks, ref cursor.Pick, t, PickMemory))
        {
            return ActivityState.Picking;
        }

        if (Recent(_npcs, ref cursor.Npc, t, NpcMemory) || Inside(_chats, t))
        {
            return ActivityState.Npc;
        }

        if (Recent(_buffs, ref cursor.Buff, t, BuffMemory))
        {
            return ActivityState.Buffing;
        }

        if (AreaAt(t, cursor) is { } area && IsTown(area))
        {
            return ActivityState.Town;
        }

        var here = PositionAt(t, cursor);
        var before = PositionAtOrBefore(t - MoveWindow);
        if (here != null && before != null && here.Distance(before) > MoveDistance)
        {
            return Recent(_teleports, ref cursor.Teleport, t, MoveWindow) ? ActivityState.Teleporting : ActivityState.Walking;
        }

        return Inside(waits, t) ? ActivityState.StandingWaiting : ActivityState.StandingIdle;
    }

    private static bool Inside(IReadOnlyList<(TimeSpan Start, TimeSpan End)> spans, TimeSpan t)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            if (spans[i].Start <= t && t <= spans[i].End)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Recent(List<TimeSpan> times, ref int index, TimeSpan t, TimeSpan memory)
    {
        while (index + 1 < times.Count && times[index + 1] <= t)
        {
            index++;
        }

        return index < times.Count && times[index] <= t && t - times[index] <= memory;
    }

    private Point PositionAt(TimeSpan t, Cursors cursor)
    {
        while (cursor.Position + 1 < _positions.Count && _positions[cursor.Position + 1].At <= t)
        {
            cursor.Position++;
        }

        return cursor.Position < _positions.Count && _positions[cursor.Position].At <= t ? _positions[cursor.Position].Position : null;
    }

    private Point PositionAtOrBefore(TimeSpan t)
    {
        int low = 0, high = _positions.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_positions[middle].At <= t)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found < 0 ? null : _positions[found].Position;
    }

    private Area? AreaAt(TimeSpan t, Cursors cursor)
    {
        while (cursor.Area + 1 < _areas.Count && _areas[cursor.Area + 1].At <= t)
        {
            cursor.Area++;
        }

        return cursor.Area < _areas.Count && _areas[cursor.Area].At <= t ? _areas[cursor.Area].Area : null;
    }

    private int AreaIndex(TimeSpan t)
    {
        int low = 0, high = _areas.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_areas[middle].At <= t)
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

    public Point PositionAt(TimeSpan t) => PositionAtOrBefore(t);

    /// <summary>The resolved area at a time; null when unknown (no revealed tile holds the position, see <see cref="AreaTracker"/>).</summary>
    public Area? AreaAt(TimeSpan t) => AreaIndex(t) is var i and >= 0 ? _areas[i].Area : null;

    /// <summary>"Travincal", "Travincal (server position)" when the client's own position was outside its revealed map, or "unknown (last known: Travincal)".</summary>
    public string AreaName(TimeSpan t)
    {
        var i = AreaIndex(t);
        return i < 0 ? null : _areas[i].FromServer && _areas[i].Area is { } server ? $"{Names.Area(server)} (server position)" : AreaTracker.Describe(_areas[i].Area, _areas[i].LastKnown);
    }

    /// <summary>Every packet the client sent to the game server: when, and its type.</summary>
    public IReadOnlyList<(TimeSpan At, byte Type)> Sends => _sends;

    /// <summary>Waypoint menus taken and objects or doorways interacted with: a warp or a door that moves the client.</summary>
    public IReadOnlyList<TimeSpan> Warps => _warps;

    /// <summary>The typed wait intervals of a client: from "entered" to the next "left" of the same reason (or the end).</summary>
    public static List<(TimeSpan Start, TimeSpan End)> Waits(GameQuery query, string client, TimeSpan end)
        => [.. WaitsWithReason(query, client, end).Select(w => (w.Start, w.End))];

    /// <summary>The typed wait intervals of a client with the reason the bot gave.</summary>
    public static List<(TimeSpan Start, TimeSpan End, string Reason)> WaitsWithReason(GameQuery query, string client, TimeSpan end)
    {
        var open = new Dictionary<string, TimeSpan>();
        var waits = new List<(TimeSpan, TimeSpan, string)>();
        foreach (var entry in query.GameEntries().Where(e => e.Kind == "wait" && e.Client == client))
        {
            var reason = entry.Str("reason") ?? "";
            var at = query.Offset(entry);
            if (entry.Str("state") == "entered")
            {
                open.TryAdd(reason, at);
            }
            else if (open.Remove(reason, out var started))
            {
                waits.Add((started, at, reason));
            }
        }

        waits.AddRange(open.Select(o => (o.Value, end, o.Key)));
        return waits;
    }

    /// <summary>Standing-waiting time within [from, to] per typed wait reason; time inside two waits goes to the one entered first.</summary>
    public static Dictionary<string, TimeSpan> WaitingByReason(IReadOnlyList<Stretch> stretches, IReadOnlyList<(TimeSpan Start, TimeSpan End, string Reason)> waits, TimeSpan from, TimeSpan to)
    {
        var byReason = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var ordered = waits.OrderBy(w => w.Start).ToList();
        foreach (var stretch in stretches.Where(s => s.State == ActivityState.StandingWaiting && s.End > from && s.Start < to))
        {
            var start = stretch.Start > from ? stretch.Start : from;
            var end = stretch.End < to ? stretch.End : to;
            for (var t = start; t < end; t += Slice)
            {
                var slice = (end - t) < Slice ? end - t : Slice;
                var reason = ordered.FirstOrDefault(w => w.Start <= t && t <= w.End).Reason ?? "?";
                byReason[reason] = byReason.GetValueOrDefault(reason) + slice;
            }
        }

        return byReason;
    }

    /// <summary>"party 120s, low life 8s", largest first; empty when there was no standing-waiting.</summary>
    public static string RenderWaiting(Dictionary<string, TimeSpan> byReason)
        => string.Join(", ", byReason.Where(r => r.Value >= TimeSpan.FromSeconds(0.5)).OrderByDescending(r => r.Value).Select(r => $"{(r.Key.Length == 0 ? "no reason" : r.Key)} {r.Value.TotalSeconds:0}s"));
}

/// <summary>Seconds per state over a set of stretches, clipped to a window.</summary>
public sealed class ActivitySplit
{
    public Dictionary<ActivityState, TimeSpan> Totals { get; } = Enum.GetValues<ActivityState>().ToDictionary(s => s, _ => TimeSpan.Zero);

    public TimeSpan Total => Totals.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    public Stretch LongestIdle { get; private set; }

    public void Add(IReadOnlyList<Stretch> stretches, TimeSpan from, TimeSpan to)
    {
        int low = 0, high = stretches.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (stretches[middle].End <= from)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var i = low; i < stretches.Count && stretches[i].Start < to; i++)
        {
            var stretch = stretches[i];
            var start = stretch.Start > from ? stretch.Start : from;
            var end = stretch.End < to ? stretch.End : to;
            if (end <= start)
            {
                continue;
            }

            Totals[stretch.State] += end - start;
            if (stretch.State is ActivityState.StandingIdle && (LongestIdle == null || end - start > LongestIdle.Duration))
            {
                LongestIdle = stretch with { Start = start, End = end };
            }
        }
    }

    public void Add(ActivitySplit other)
    {
        foreach (var (state, time) in other.Totals)
        {
            Totals[state] += time;
        }

        if (other.LongestIdle != null && (LongestIdle == null || other.LongestIdle.Duration > LongestIdle.Duration))
        {
            LongestIdle = other.LongestIdle;
        }
    }

    /// <summary>"town 40% 120s, walking 30% 90s, ..." - states with time only, largest first.</summary>
    public string Render()
    {
        var total = Total.TotalSeconds;
        return total <= 0
            ? "no time"
            : string.Join(", ", Totals.Where(t => t.Value > TimeSpan.Zero).OrderByDescending(t => t.Value)
                .Select(t => $"{ActivityObserver.Name(t.Key)} {t.Value.TotalSeconds / total:0%} {t.Value.TotalSeconds:0}s"));
    }
}
