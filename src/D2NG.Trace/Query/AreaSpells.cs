using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>A monster's life falling, as this client saw it: percentages before and after, and where the monster stood.</summary>
public sealed record MonsterLifeDrop(TimeSpan At, uint Id, double Before, double After, Point Where);

/// <summary>Every fall in a monster's life percentage this client saw, deaths included (after 0).</summary>
public sealed class MonsterLifeObserver : IReplayObserver
{
    public List<MonsterLifeDrop> Drops { get; } = [];

    private readonly Dictionary<uint, double> _life = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (record.Kind != TraceRecordKind.D2gsReceived || p.Length == 0)
        {
            return;
        }

        uint id = 0;
        if (p[0] is >= 0x67 and <= 0x6D)
        {
            id = Core.D2GS.Motion.MotionUpdate.Read(p).EntityId;
        }
        else if (p[0] == 0x0C && p.Length >= 6 && p[1] == (byte)EntityType.NPC)
        {
            id = PacketDecoder.U32(p, 2);
        }

        if (id == 0 || context.Npc(id) is not { } unit)
        {
            return;
        }

        var dead = p[0] == 0x69 && p.Length >= 6 && p[5] is (byte)EntityState.Dieing or (byte)EntityState.Dead;
        var life = dead ? 0 : unit.LifePercentage;
        if (_life.TryGetValue(id, out var was) && life < was)
        {
            Drops.Add(new MonsterLifeDrop(context.Now, id, was, life, unit.Location));
        }

        _life[id] = life;
    }
}

/// <summary>
/// What an area spell of the client's own took off the monsters around it: each life drop goes to the client's most recent
/// area cast at most <see cref="Window"/> before it, when the monster stood within that skill's radius of where the client
/// cast from. A party member's damage there in the same moment counts too, alike for every skill, so the skills compare
/// with each other better than they measure on their own.
/// </summary>
public static class AreaSpells
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(0.6);

    public static readonly IReadOnlyDictionary<string, double> Radius = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["StaticField"] = 20,
        ["Nova"] = 10,
        ["FrostNova"] = 10,
    };

    public sealed record SkillTotal(string Skill, int Casts, int CastsWithEffect, double LifeRemoved, int Kills, TimeSpan CastTime)
    {
        public double PerCast => Casts > 0 ? LifeRemoved / Casts : 0;

        public double PerSecond => CastTime > TimeSpan.Zero ? LifeRemoved / CastTime.TotalSeconds : 0;
    }

    public static List<SkillTotal> Measure(IReadOnlyList<AttackCast> casts, IReadOnlyList<MonsterLifeDrop> drops)
    {
        var area = casts.Where(c => Radius.ContainsKey(c.Skill) && c.From != null).OrderBy(c => c.At).ToList();
        var removed = new double[area.Count];
        var kills = new int[area.Count];
        var touched = new bool[area.Count];
        foreach (var drop in drops.Where(d => d.Where != null).OrderBy(d => d.At))
        {
            var i = LastCastBefore(area, drop.At);
            if (i < 0 || drop.At - area[i].At > Window || area[i].From.Distance(drop.Where) > Radius[area[i].Skill])
            {
                continue;
            }

            removed[i] += drop.Before - drop.After;
            touched[i] = true;
            if (drop.After <= 0)
            {
                kills[i]++;
            }
        }

        return area.Select((cast, i) => (cast, i)).GroupBy(x => x.cast.Skill)
            .Select(g =>
            {
                var time = TimeSpan.FromTicks(g.Sum(x => x.i + 1 < area.Count ? Math.Min((area[x.i + 1].At - x.cast.At).Ticks, Window.Ticks) : Window.Ticks));
                return new SkillTotal(g.Key, g.Count(), g.Count(x => touched[x.i]), g.Sum(x => removed[x.i]), g.Sum(x => kills[x.i]), time);
            })
            .OrderByDescending(s => s.Casts)
            .ToList();
    }

    /// <summary>
    /// The life drops after each cast of <paramref name="skill"/> by distance from where it was cast, in bins of
    /// <paramref name="bin"/> out to <paramref name="farthest"/>: where a skill really reaches. Static Field's drops are
    /// told apart by their shape, a quarter of the life the monster had (20-30% here); for the others every drop counts.
    /// </summary>
    public static List<(double From, int Drops, double Life)> ByDistance(IReadOnlyList<AttackCast> casts, IReadOnlyList<MonsterLifeDrop> drops, string skill, double bin = 2.5, double farthest = 30)
    {
        var area = casts.Where(c => Radius.ContainsKey(c.Skill) && c.From != null).OrderBy(c => c.At).ToList();
        var bins = Enumerable.Range(0, (int)Math.Ceiling(farthest / bin)).Select(i => (From: i * bin, Drops: 0, Life: 0.0)).ToList();
        var isStatic = string.Equals(skill, "StaticField", StringComparison.OrdinalIgnoreCase);
        foreach (var drop in drops.Where(d => d.Where != null))
        {
            var i = LastCastBefore(area, drop.At);
            if (i < 0 || !string.Equals(area[i].Skill, skill, StringComparison.OrdinalIgnoreCase) || drop.At - area[i].At > Window)
            {
                continue;
            }

            var share = drop.Before > 0 ? (drop.Before - drop.After) / drop.Before : 0;
            if (isStatic && (share < 0.2 || share > 0.3))
            {
                continue;
            }

            var b = (int)(area[i].From.Distance(drop.Where) / bin);
            if (b < bins.Count)
            {
                bins[b] = (bins[b].From, bins[b].Drops + 1, bins[b].Life + drop.Before - drop.After);
            }
        }

        return bins;
    }

    private static int LastCastBefore(List<AttackCast> casts, TimeSpan at)
    {
        int lo = 0, hi = casts.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (casts[mid].At <= at)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}
