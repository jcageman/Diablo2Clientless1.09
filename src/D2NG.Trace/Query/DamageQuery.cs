using D2NG.Core.D2GS.Enums;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// Who hurt a client. The protocol never says who caused a loss of life: 0x95/0x18 only carry the
/// new life. What it does say is who went for the client - a monster attack (0x6C) or a skill cast
/// on it (0x4C) naming it as the target. A drop is put on the units such packets named in the second
/// before it, shared evenly when there were several; failing those, on units that cast at a point
/// beside the client (0x4D) or were closing in on it (0x68); the rest is unattributed (missiles fired
/// earlier, auras, area skills, poison, units out of sight).
/// </summary>
public sealed class DamageObserver(TimeSpan from, TimeSpan to) : IReplayObserver
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    public const string Named = "named as target";
    public const string Nearby = "aimed nearby or closing in";
    public const string Unattributed = "unattributed";

    public sealed class Attacker
    {
        public string Name { get; init; }

        public string Basis { get; init; }

        public double Lost { get; set; }

        public int Drops { get; set; }

        public HashSet<string> Hows { get; } = [];

        public TimeSpan First { get; set; }

        public TimeSpan Last { get; set; }
    }

    public Dictionary<string, Attacker> Attackers { get; } = [];

    public int TotalLost { get; private set; }

    public int TotalGained { get; private set; }

    public int Drops { get; private set; }

    public int MaxLife { get; private set; }

    public List<LifeDrop> Each { get; } = [];

    /// <summary>Unattributed life per inferred cause; the unattributed row keeps the same life.</summary>
    public Dictionary<string, (double Lost, int Drops)> Inferred { get; } = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        if (record.Kind != TraceRecordKind.D2gsReceived || record.Payload.Length == 0 || record.Payload[0] is not (0x18 or 0x95)
            || context.Now < from || context.Now > to || context.Game.Me == null || context.PreviousLife == 0)
        {
            return;
        }

        var change = context.Life - context.PreviousLife;
        if (change >= 0)
        {
            TotalGained += change;
            return;
        }

        var drop = LifeDrop.Of(context);
        Each.Add(drop);
        var lost = drop.Lost;
        TotalLost += lost;
        Drops++;
        MaxLife = Math.Max(MaxLife, context.MaxLife);
        if (drop.Credited.Count == 0)
        {
            Charge(Unattributed, Unattributed, lost, null, context.Now);
            foreach (var cause in drop.Inferred)
            {
                var key = LifeDrop.Cause(cause);
                var (sum, count) = Inferred.GetValueOrDefault(key);
                Inferred[key] = (sum + lost, count + 1);
            }

            return;
        }

        var units = drop.Credited.GroupBy(a => a.Name).ToList();
        foreach (var unit in units)
        {
            Charge($"{unit.Key}|{drop.Basis}", drop.Basis, (double)lost / units.Count, unit.Select(a => a.How), context.Now, unit.Key);
        }
    }

    private void Charge(string key, string basis, double lost, IEnumerable<string> hows, TimeSpan at, string name = null)
    {
        if (!Attackers.TryGetValue(key, out var attacker))
        {
            attacker = new Attacker { Name = name ?? key, Basis = basis, First = at };
            Attackers[key] = attacker;
        }

        attacker.Lost += lost;
        attacker.Drops++;
        attacker.Last = at;
        foreach (var how in hows ?? [])
        {
            attacker.Hows.Add(how);
        }
    }

    public IEnumerable<string> Report(string client)
    {
        yield return $"{client}: lost {TotalLost} life in {Drops} drops{(MaxLife > 0 ? $" (max life {MaxLife})" : "")}, gained {TotalGained}";
        yield return "attribution: a drop goes to the units an attack packet named this client as target of (0x6C, 0x4C) in the 1s before it,";
        yield return "  split evenly; failing that to units casting at a point within 5 (0x4D) or closing in (0x68) - inference; else unattributed";
        foreach (var attacker in Attackers.Values.OrderByDescending(a => a.Lost))
        {
            var share = TotalLost > 0 ? attacker.Lost / TotalLost : 0;
            yield return $"  {attacker.Lost,7:0} ({share,4:P0}) {attacker.Name,-24} {attacker.Basis,-28} {attacker.Drops} drops {TraceTime.Format(attacker.First)}..{TraceTime.Format(attacker.Last)}{(attacker.Hows.Count > 0 ? "  " + string.Join(", ", attacker.Hows.Take(4)) : "")}";
        }

        if (Inferred.Count > 0)
        {
            yield return "INFERENCE for the unattributed drops (likely causes, not attribution; the unattributed row above keeps this life):";
            foreach (var (cause, (lost, drops)) in Inferred.OrderByDescending(i => i.Value.Lost))
            {
                yield return $"  {lost,7:0} likely: {cause} ({drops} drop{(drops == 1 ? "" : "s")})";
            }
        }

        yield return "drops (time = the life packet; an attack credited shows its own packet's time):";
        foreach (var drop in Each)
        {
            yield return $"  {drop.Describe()}";
        }
    }
}

/// <summary>
/// One fall in life as the client saw it: the life packet that showed it, the attack packets credited with
/// it (each with its own time, since the life packet arrives later), and the inferred causes when none is.
/// </summary>
/// <param name="Curses">The curses on the client when the drop showed - an Amplify Damage from a Cursed pack doubles every hit.</param>
public sealed record LifeDrop(TimeSpan At, int Before, int After, int MaxLife, string Basis, IReadOnlyList<(string Name, string How, TimeSpan At)> Credited, IReadOnlyList<string> Inferred, IReadOnlyList<string> Curses = null)
{
    private const int CreditedShown = 4;

    private static readonly HashSet<EntityEffect> CurseEffects =
    [
        EntityEffect.Amplifydamage, EntityEffect.Weaken, EntityEffect.Dimvision, EntityEffect.Ironmaiden, EntityEffect.Terror,
        EntityEffect.Attract, EntityEffect.Lifetap, EntityEffect.Confuse, EntityEffect.Decrepify, EntityEffect.Lowerresist,
    ];

    public int Lost => Before - After;

    public static LifeDrop Of(ReplayContext context)
    {
        var recent = context.AttacksBetween(context.Now - DamageObserver.Window, context.Now).ToList();
        var named = recent.Where(a => a.Named).ToList();
        var (basis, blamed) = named.Count > 0 ? (DamageObserver.Named, named) : recent.Count > 0 ? (DamageObserver.Nearby, recent) : (DamageObserver.Unattributed, recent);
        var effects = context.Game?.Me?.Effects;
        return new LifeDrop(context.Now, context.PreviousLife, context.Life, context.MaxLife, basis,
            [.. blamed.Select(a => (context.UnitName(a.Attacker, a.AttackerType), a.How, a.Offset))],
            blamed.Count == 0 ? context.Inferred(context.Now) : [],
            effects == null ? [] : [.. effects.Keys.Where(CurseEffects.Contains).Select(e => e.ToString()).OrderBy(n => n, StringComparer.Ordinal)]);
    }

    private string CursedText => Curses is { Count: > 0 } ? $" under {string.Join(", ", Curses)}" : "";

    /// <summary>An inferred cause without its timing, to add up drops of the same cause.</summary>
    public static string Cause(string inferred)
    {
        var cut = inferred.IndexOf(", which ", StringComparison.Ordinal);
        return cut < 0 ? inferred : inferred[..cut];
    }

    public string Describe()
    {
        var text = $"{TraceTime.Format(At)} -{Lost} ({Before} -> {After}{(MaxLife > 0 ? $" of {MaxLife}" : "")}){CursedText}";
        if (Credited.Count > 0)
        {
            var units = Credited.GroupBy(c => c.Name).OrderByDescending(g => g.Max(c => c.At)).ToList();
            return text + $" {Basis}: " + string.Join(", ", units.Take(CreditedShown).Select(g =>
                $"{g.Key} ({string.Join(", ", g.Select(c => $"{c.How} at {TraceTime.Format(c.At)}, {(At - c.At).TotalSeconds:0.00}s before"))})"))
                + (units.Count > CreditedShown ? $" and {units.Count - CreditedShown} more" : "");
        }

        return text + " unattributed" + (Inferred.Count > 0 ? $"; INFERENCE likely: {string.Join("; ", Inferred)}" : "");
    }
}
