using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>One change of the client's mana with what happened around it, before a cause is chosen.</summary>
public sealed record ManaChange(TimeSpan At, int Before, int After, int Max, string Doing, Point Position,
    string Cast, TimeSpan? CastAgo, string Burner, TimeSpan? BurnAgo, string NearestBurner, double? BurnerDistance,
    string Potion, TimeSpan? PotionAgo, int PreviousMax)
{
    public int Delta => After - Before;

    /// <summary>How long before (positive) or after (negative) the change the latest hit of the client's own casts was; set by <see cref="ManaObserver.Attribute"/>.</summary>
    public TimeSpan? HitAgo { get; set; }

    /// <summary>The causes chosen for it, each with its share of the change; see <see cref="ManaObserver"/>.</summary>
    public List<(string Cause, string Detail, int Amount)> Causes { get; } = [];
}

/// <summary>
/// Every fall and rise of a client's mana, as the life and mana packets (0x95, 0x18) state it, put on its causes.
/// A fall goes to the client's own cast when it sent an attack, skill or buff packet in the second before (the skill
/// selected on that hand), and to mana burn when a monster with the ManaBurn enchantment named the client in an attack
/// in the second before; a fall to a lower maximum is the maximum falling (a buff ending, an item taken off); when both did, the cast takes the skill's typical cost (the median fall it alone explained in
/// this replay) and the burn the rest. A rise goes to a mana or rejuvenation potion the server consumed in the
/// refill, which runs <see cref="Refill"/> from the drink or from the end of the refill it queued behind, else to leech when a hit the client's own casts may have landed
/// (<see cref="CombatObserver.OwnHitsBetween"/>) came from half a second before to 0.1 s after the packet (hits arrive in
/// the same burst), else to regeneration when it is small (<see cref="RegenerationMax"/>); the rest of either
/// way is unattributed.
/// </summary>
public sealed class ManaObserver(TimeSpan from, TimeSpan to, CombatObserver combat) : IReplayObserver
{
    /// <summary>
    /// How long a mana potion keeps refilling: a super mana potion was measured rising for 4.5 to 5 s, and two or three drunk
    /// moments apart kept mana rising at the same rate for 8 to 9 s - a potion drunk while one refills queues behind it.
    /// </summary>
    public static readonly TimeSpan Refill = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan CastWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan BurnWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LeechWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>A hit stated in the same burst just after the life and mana packet still counts for leech.</summary>
    public static readonly TimeSpan LeechAfter = TimeSpan.FromMilliseconds(100);

    public const string OwnCast = "own casts";
    public const string ManaBurn = "mana burn";
    public const string Potions = "potions";
    public const string Leech = "leech";
    public const string Regeneration = "regeneration";
    public const string Unattributed = "unattributed";
    public const string MaxFell = "maximum fell";

    /// <summary>The largest rise that counts as regeneration: 2% of the maximum, at least 3.</summary>
    public static int RegenerationMax(int max) => Math.Max(3, max / 50);

    private int _mana = -1;
    private int _previousMax;
    private (TimeSpan At, string Skill)? _cast;
    private bool _stated;
    private readonly Dictionary<uint, (TimeSpan At, string Name)> _drinks = [];
    private (TimeSpan At, string Name)? _potion;
    private TimeSpan _refillEnd = TimeSpan.MinValue;
    private readonly LinkedList<(TimeSpan At, Point Position)> _positions = new();

    public List<ManaChange> Changes { get; } = [];

    /// <summary>Mana and rejuvenation potions the server consumed: when sent, which, and how long after the previous one.</summary>
    public List<(TimeSpan At, string Name, TimeSpan? AfterPrevious, bool WhileRefilling)> Drinks { get; } = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        var me = context.Game.Me;
        if (p.Length == 0 || me == null)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            Sent(context, me, p);
        }
        else if (record.Kind == TraceRecordKind.D2gsReceived)
        {
            Received(context, p);
        }

        if (me.Location is { } here && (_positions.Last == null || _positions.Last.Value.Position != here))
        {
            _positions.AddLast((context.Now, here));
        }

        while (_positions.Count > 1 && context.Now - _positions.First.Next.Value.At > TimeSpan.FromSeconds(0.6))
        {
            _positions.RemoveFirst();
        }

        if (record.Kind == TraceRecordKind.D2gsReceived && p[0] is 0x95 or 0x18 && !_stated)
        {
            _stated = true;
            _mana = me.Mana;
            _previousMax = Max(context);
            return;
        }

        if (me.Mana == _mana || !_stated)
        {
            return;
        }

        if (record.Kind != TraceRecordKind.D2gsReceived || p[0] is not (0x95 or 0x18))
        {
            _mana = me.Mana;
            _previousMax = Max(context);
            return;
        }

        var before = _mana;
        _mana = me.Mana;
        var previousMax = _previousMax;
        _previousMax = Max(context);
        if (before < 0 || context.Now < from || context.Now > to)
        {
            return;
        }

        var now = context.Now;
        var burn = context.AttacksBetween(now - BurnWindow, now).Where(a => a.Named && IsBurner(context.Npc(a.Attacker))).LastOrDefault();
        var nearest = context.Game.WorldObjects.Values
            .Where(o => o.Type == EntityType.NPC && o.State is not (EntityState.Dieing or EntityState.Dead) && IsBurner(o) && o.Location is not null)
            .Select(o => (Unit: o, Distance: context.DistanceTo(o.Location)))
            .OrderBy(u => u.Distance).FirstOrDefault();
        Changes.Add(new ManaChange(now, before, _mana, Max(context), Doing(context, me), me.Location,
            _cast?.Skill, _cast is { } cast && now - cast.At <= CastWindow ? now - cast.At : null,
            burn == null ? null : context.UnitName(burn.Attacker), burn == null ? null : now - burn.Offset,
            nearest.Unit == null ? null : context.UnitName(nearest.Unit.Id), nearest.Unit == null ? null : nearest.Distance,
            _potion?.Name, _potion is { } potion && now <= _refillEnd ? now - potion.At : null, previousMax));
    }

    private static bool IsBurner(WorldObject unit) => unit?.MonsterEnchantments?.Contains(MonsterEnchantment.ManaBurn) == true;

    private string Doing(ReplayContext context, Self me)
    {
        if (me.Effects.ContainsKey(EntityEffect.Skillmove))
        {
            return $"skill move ({_cast?.Skill ?? "?"})";
        }

        if (_cast is { Skill: nameof(Skill.Teleport) } teleport && context.Now - teleport.At <= TimeSpan.FromSeconds(0.5))
        {
            return "teleporting";
        }

        var first = _positions.First?.Value.Position;
        return first is not null && me.Location is { } here && first.Distance(here) > 1 ? "walking" : _cast is { } c && context.Now - c.At <= CastWindow ? $"casting {c.Skill}" : "standing";
    }

    private static int Max(ReplayContext context) => PotionWasteCheck.SafeMaxMana(context.Game.Me);

    private void Sent(ReplayContext context, Self me, byte[] p)
    {
        if (p[0] is >= 0x05 and <= 0x11 and not 0x0B)
        {
            var hand = p[0] <= 0x0A ? Hand.Left : Hand.Right;
            var skill = me.ActiveSkills.TryGetValue(hand, out var selected) ? selected.ToString() : $"{hand.ToString().ToLowerInvariant()} skill (unknown)";
            _cast = (context.Now, skill);
            return;
        }

        if (p.Length < 5 || p[0] is not (0x26 or 0x20) || (p[0] == 0x26 && p.Length >= 9 && PacketDecoder.U32(p, 5) != 0))
        {
            return;
        }

        var id = PacketDecoder.U32(p, 1);
        var item = PotionWasteCheck.Find(context, id, p[0] == 0x26);
        if (item?.Classification is ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion && !_drinks.ContainsKey(id))
        {
            _drinks[id] = (context.Now, item.Name.ToString());
        }
    }

    private void Received(ReplayContext context, byte[] p)
    {
        uint id;
        if (p[0] == 0x3F && p.Length >= 6)
        {
            id = PacketDecoder.U32(p, 2);
        }
        else if (p[0] is 0x9C or 0x9D && p.Length >= 8 && p[1] == (byte)Core.D2GS.Items.Action.RemoveFromBelt)
        {
            id = PacketDecoder.U32(p, 4);
        }
        else
        {
            return;
        }

        if (_drinks.Remove(id, out var drink) && context.Now - drink.At <= TimeSpan.FromSeconds(2))
        {
            Drinks.Add((drink.At, drink.Name, _potion is { } previous ? drink.At - previous.At : null, _refillEnd > drink.At));
            _refillEnd = (_refillEnd > drink.At ? _refillEnd : drink.At) + Refill;
            _potion = drink;
        }
    }

    public void Finished(ReplayContext context)
    {
        _mana = -1;
        _cast = null;
        _stated = false;
        _potion = null;
        _refillEnd = TimeSpan.MinValue;
        _drinks.Clear();
        _positions.Clear();
    }

    /// <summary>Puts every change on its causes; the typical cost of a skill is the median fall a cast of it alone explained.</summary>
    public void Attribute()
    {
        var typical = Changes.Where(c => c.Delta < 0 && c.CastAgo != null && c.BurnAgo == null)
            .GroupBy(c => c.Cast)
            .ToDictionary(g => g.Key, g => Median(g.Select(c => -c.Delta)));
        foreach (var change in Changes)
        {
            change.Causes.Clear();
            change.HitAgo = LeechHit(change.At);
            var amount = Math.Abs(change.Delta);
            if (change.Delta < 0)
            {
                if (change.Max > 0 && (change.PreviousMax > change.Max || change.Before > change.Max) && change.After >= change.Max)
                {
                    change.Causes.Add((MaxFell, $"{Math.Max(change.PreviousMax, change.Before)} -> {change.Max}", amount));
                }
                else if (change.CastAgo != null && change.BurnAgo != null && typical.TryGetValue(change.Cast, out var cost) && amount > cost * 1.5)
                {
                    change.Causes.Add((OwnCast, change.Cast, cost));
                    change.Causes.Add((ManaBurn, change.Burner, amount - cost));
                }
                else if (change.CastAgo != null)
                {
                    change.Causes.Add((OwnCast, change.Cast, amount));
                }
                else if (change.BurnAgo != null)
                {
                    change.Causes.Add((ManaBurn, change.Burner, amount));
                }
                else
                {
                    change.Causes.Add((Unattributed, null, amount));
                }

                continue;
            }

            if (change.PotionAgo != null)
            {
                change.Causes.Add((Potions, change.Potion, amount));
            }
            else if (change.HitAgo != null)
            {
                change.Causes.Add((Leech, null, amount));
            }
            else if (amount <= RegenerationMax(change.Max))
            {
                change.Causes.Add((Regeneration, null, amount));
            }
            else
            {
                change.Causes.Add((Unattributed, null, amount));
            }
        }
    }

    /// <summary>
    /// The time from the latest hit one of the client's own casts may have landed (<see cref="CombatObserver.OwnHitsBetween"/>:
    /// an NPCHit, a life drop or a death of the unit it was meant for or near its target, in that skill's window) to
    /// <paramref name="at"/>, within <see cref="LeechWindow"/> before or <see cref="LeechAfter"/> after.
    /// </summary>
    private TimeSpan? LeechHit(TimeSpan at)
        => combat.OwnHitsBetween(at - LeechWindow, at + LeechAfter).Select(h => (TimeSpan?)(at - h.At)).LastOrDefault();

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    public IEnumerable<string> Report(string client, bool all)
    {
        Attribute();
        var falls = Changes.Where(c => c.Delta < 0).ToList();
        var rises = Changes.Where(c => c.Delta > 0).ToList();
        var fell = falls.Sum(c => -c.Delta);
        var rose = rises.Sum(c => c.Delta);
        var max = Changes.Select(c => c.Max).DefaultIfEmpty(0).Max();
        yield return $"{client}: mana fell {fell} in {falls.Count} falls and rose {rose} in {rises.Count} rises{(max > 0 ? $" (max mana {max})" : "")}";
        yield return "falls by cause:";
        foreach (var line in ByCause(falls, fell))
        {
            yield return line;
        }

        yield return "rises by cause:";
        foreach (var line in ByCause(rises, rose))
        {
            yield return line;
        }

        var inWindow = Drinks.Where(d => d.At >= from && d.At <= to).ToList();
        if (inWindow.Count > 0)
        {
            var soon = inWindow.Count(d => d.WhileRefilling);
            yield return $"potions: {inWindow.Count} mana or rejuvenation potions drunk ({string.Join(", ", inWindow.GroupBy(d => d.Name).Select(g => $"{g.Key} x{g.Count()}"))}); "
                + $"{soon} of them while an earlier one was still refilling (its refill then starts when the earlier one's ends)";
        }

        var burners = falls.Where(c => c.NearestBurner != null).ToList();
        yield return burners.Count == 0
            ? "no monster with the ManaBurn enchantment was in sight at any fall"
            : $"a ManaBurn monster was in sight at {burners.Count} of {falls.Count} falls, nearest {burners.Min(c => c.BurnerDistance):0.0} away ({burners.MinBy(c => c.BurnerDistance).NearestBurner})";
        yield return $"changes (falls, and rises other than regeneration{(all ? "; --all: every change" : "; --all for every change")}); time = the life and mana packet that showed it:";
        foreach (var change in Changes.Where(c => all || c.Delta < 0 || c.Causes.Any(k => k.Cause != Regeneration)))
        {
            yield return "  " + Describe(change);
        }
    }

    private static IEnumerable<string> ByCause(List<ManaChange> changes, int total)
    {
        var rows = changes.SelectMany(c => c.Causes.Select(k => (k.Cause, k.Detail, k.Amount, c)))
            .GroupBy(r => (r.Cause, r.Detail))
            .Select(g => (g.Key.Cause, g.Key.Detail, Amount: g.Sum(r => r.Amount), Count: g.Count(), Typical: Median(g.Select(r => r.Amount))))
            .OrderByDescending(r => r.Amount)
            .ToList();
        if (rows.Count == 0)
        {
            yield return "  none";
        }

        foreach (var row in rows)
        {
            var share = total > 0 ? (double)row.Amount / total : 0;
            yield return $"  {row.Amount,6} ({share,4:P0}) {row.Cause,-14} {row.Detail ?? "",-24} {row.Count} change{(row.Count == 1 ? "" : "s")}, median {row.Typical}";
        }
    }

    private static string Describe(ManaChange c)
    {
        var causes = string.Join(" + ", c.Causes.Select(k => k.Cause switch
        {
            OwnCast => $"own cast {k.Detail} ({c.CastAgo?.TotalSeconds:0.00}s before){(c.Causes.Count > 1 ? $" {k.Amount}" : "")}",
            ManaBurn => $"mana burn by {k.Detail} ({c.BurnAgo?.TotalSeconds:0.00}s before){(c.Causes.Count > 1 ? $" {k.Amount}" : "")}",
            Potions => $"{k.Detail} drunk {c.PotionAgo?.TotalSeconds:0.0}s before",
            Leech => $"leech (a monster hit {c.HitAgo?.TotalSeconds:0.00}s before)",
            MaxFell => $"maximum fell {k.Detail}",
            _ => k.Cause,
        }));
        var burner = c.NearestBurner == null ? "" : $"; ManaBurn {c.NearestBurner} {c.BurnerDistance:0.0} away";
        return $"{TraceTime.Format(c.At)} {(c.Delta > 0 ? "+" : "")}{c.Delta} ({c.Before} -> {c.After}{(c.Max > 0 ? $" of {c.Max}" : "")}) {causes}; {c.Doing} at {Names.Point(c.Position)}{burner}";
    }
}
