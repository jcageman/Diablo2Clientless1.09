using D2NG.Core.D2GS.Items;
using D2NG.Mule.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule.Upgrades;

public sealed record StatChange(string Stat, double Before, double After)
{
    public double Delta => After - Before;
}

public enum CandidateStanding
{
    /// <summary>Wearable and keeps every guarded resist where it stands.</summary>
    Fits,

    /// <summary>Wearable, but drops a guarded resist below where it stands.</summary>
    DropsResist,

    /// <summary>Cannot be worn: a requirement is not met.</summary>
    Blocked,
}

public sealed class UpgradeCandidate
{
    public MuleItemDb Item { get; init; }
    public double Score { get; init; }
    public CandidateStanding Standing { get; init; }

    /// <summary>What blocks or weakens the swap, in words; null when it fits.</summary>
    public string Reason { get; init; }

    public GearEvaluation After { get; init; }
    public IReadOnlyList<StatChange> Changes { get; init; }

    /// <summary>For a charm, the carried charm of the same size it takes the place of; null for a worn slot.</summary>
    public MuleItemDb Replaces { get; init; }
}

public sealed class UpgradeResult
{
    public string Slot { get; init; }
    public MuleItemDb Current { get; init; }
    public GearEvaluation Before { get; init; }
    public IReadOnlyList<UpgradeCandidate> Candidates { get; init; }

    /// <summary>Mule items of the right type and game mode, before ranking.</summary>
    public int Considered { get; init; }
}

/// <summary>
/// Ranks the mule items that could go in one slot by how much the swap improves the character
/// under a preset: the score after the swap less the score now, with resists judged after the
/// difficulty penalty. Fitting swaps rank first, then those that drop a guarded resist, then the
/// ones the character cannot wear.
/// </summary>
public static class UpgradeFinder
{
    /// <summary>The charm layout: a top row of ten small charms over three rows of ten grand charms; large charms have no place in it.</summary>
    private static readonly Dictionary<string, int> CharmRows = new() { ["SmallCharm"] = 10, ["GrandCharm"] = 10 };

    private static readonly string[] AlwaysShown = ["Strength", "Dexterity", "Life", "Mana", "Cast Frames", "Hit Recovery Frames"];

    /// <summary>
    /// A wielded weapon is replaced by one held the same way, two hands for two hands and one for one, a barbarian's
    /// two-handed sword counting as both; with nothing
    /// wielded, a shield in the other hand rules out two-handed weapons.
    /// </summary>
    private static bool SameGrip(GearItem weapon, IReadOnlyDictionary<string, GearItem> worn)
    {
        if (worn.GetValueOrDefault(GearSlots.RightHand) is { Base: not null } wielded)
        {
            return GearEvaluation.TakesBothHands(wielded) ? weapon.Base?.TwoHandMinDamage > 0 : !GearEvaluation.TakesBothHands(weapon);
        }

        return !GearEvaluation.TakesBothHands(weapon)
            || worn.GetValueOrDefault(GearSlots.LeftHand) is not { } held
            || held.Source.ClassificationType is "Arrows" or "Bolts";
    }

    /// <summary>
    /// The left hand stays on its side: a shield is replaced by a shield, a weapon by a weapon, a quiver by
    /// a quiver; an empty left hand takes a quiver behind a two-handed weapon and a shield otherwise.
    /// </summary>
    private static Func<ClassificationType, bool> Side(string slot, IReadOnlyDictionary<string, GearItem> worn)
    {
        if (slot != GearSlots.LeftHand)
        {
            return null;
        }

        if (Enum.TryParse<ClassificationType>(worn.GetValueOrDefault(GearSlots.LeftHand)?.Source.ClassificationType, out var held))
        {
            return GearSlots.IsShield(held) ? GearSlots.IsShield
                : GearSlots.IsQuiver(held) ? GearSlots.IsQuiver
                : GearSlots.IsWeapon;
        }

        return worn.GetValueOrDefault(GearSlots.RightHand) is { } right && GearEvaluation.TakesBothHands(right) ? GearSlots.IsQuiver : GearSlots.IsShield;
    }

    public static UpgradeResult Rank(GearCharacterDb character, string slot, IEnumerable<MuleItemDb> stock, UpgradePreset preset, ResistMode mode, Difficulty difficulty, GearTables tables)
    {
        var worn = character.Equipped
            .Where(e => GearSlots.All.Contains(e.Key))
            .ToDictionary(e => e.Key, e => GearItem.From(e.Value, tables, character.Level));
        var charms = (character.Charms ?? []).Select(c => GearItem.From(c, tables, character.Level)).ToList();
        var before = GearEvaluation.Of(character, worn, charms, tables, difficulty);
        var beforeScore = Score(before, preset, mode, slot, worn.GetValueOrDefault(slot));
        var guarded = Guarded(preset, mode);
        var shown = AlwaysShown.Concat(preset.Weights.Where(w => w.Slot == null || w.Slot == slot).Select(w => w.Stat)).Distinct().ToList();

        var weapons = slot != GearSlots.RightHand ? []
            : preset.Weapons is { Count: > 0 } named ? named
            : character.Equipped.GetValueOrDefault(GearSlots.RightHand)?.ClassificationType is { } wielded ? [wielded]
            : new List<string>();
        var current = character.Equipped.GetValueOrDefault(slot);
        var side = Side(slot, worn);
        var candidates = stock
            .Where(item => Fits(item, slot, character) && (weapons.Count == 0 || weapons.Contains(item.ClassificationType)) && !SameAs(item, current)
                && (side == null || Enum.TryParse<ClassificationType>(item.ClassificationType, out var type) && side(type))
                && (slot != GearSlots.Charms || !charms.Any(c => SameAs(item, c.Source))))
            .ToList();
        var ranked = new List<UpgradeCandidate>(candidates.Count);
        foreach (var item in candidates)
        {
            var gearItem = GearItem.From(item, tables, character.Level);
            if (slot == GearSlots.RightHand && !SameGrip(gearItem, worn))
            {
                continue;
            }

            GearEvaluation after;
            MuleItemDb replaces = null;
            if (slot == GearSlots.Charms)
            {
                if (!CharmRows.TryGetValue(item.ClassificationType, out var room))
                {
                    continue;
                }

                var sameSize = charms.Where(c => c.Source.ClassificationType == item.ClassificationType).ToList();
                var swaps = sameSize
                    .Select(c => (Replaced: c, After: GearEvaluation.Of(character, worn, [.. charms.Where(o => o != c), gearItem], tables, difficulty)))
                    .ToList();
                if (sameSize.Count < room)
                {
                    swaps.Add((null, GearEvaluation.Of(character, worn, [.. charms, gearItem], tables, difficulty)));
                }

                var best = swaps.MaxBy(s => Score(s.After, preset, mode, slot, gearItem));
                (after, replaces) = (best.After, best.Replaced?.Source);
            }
            else
            {
                after = GearEvaluation.Of(character, new Dictionary<string, GearItem>(worn) { [slot] = gearItem }, charms, tables, difficulty);
            }

            var dropped = guarded.Where(element => after.Resists[element].Effective < before.Resists[element].Effective).ToList();
            var standing = after.RequirementFailure != null ? CandidateStanding.Blocked
                : dropped.Count > 0 ? CandidateStanding.DropsResist
                : CandidateStanding.Fits;
            ranked.Add(new UpgradeCandidate
            {
                Item = item,
                Score = Math.Round(Score(after, preset, mode, slot, gearItem) - beforeScore, 1),
                Standing = standing,
                Reason = standing switch
                {
                    CandidateStanding.Blocked => after.RequirementFailure,
                    CandidateStanding.DropsResist => "drops " + string.Join(" and ", dropped.Select(d => d.ToLowerInvariant())) + " below where it stands",
                    _ => null,
                },
                After = after,
                Changes = Changes(before, after, shown),
                Replaces = replaces,
            });
        }

        return new UpgradeResult
        {
            Slot = slot,
            Current = character.Equipped.GetValueOrDefault(slot),
            Before = before,
            Considered = candidates.Count,
            Candidates = [.. ranked.Where(c => c.Score > 0 || !SameNamed(c.Item, character.Equipped.GetValueOrDefault(slot), tables)).OrderBy(c => c.Standing == CandidateStanding.Blocked).ThenByDescending(c => c.Score)],
        };
    }

    /// <summary>A copy of the worn item, such as a second Stone of Jordan: same base, quality and rolls, so nothing to compare.</summary>
    /// <summary>The same unique or set item as the one worn, whatever it rolled.</summary>
    private static bool SameNamed(MuleItemDb item, MuleItemDb worn, GearTables tables)
        => worn != null && item.QualityType is "Unique" or "Set" && item.QualityType == worn.QualityType && item.BaseCode == worn.BaseCode
            && (tables == null ? item.UniqueCode == worn.UniqueCode && item.SetCode == worn.SetCode : tables.DisplayName(item) == tables.DisplayName(worn));

    /// <summary>What tells two items apart for a player: base, quality, unique or set, ethereal, sockets and every roll.</summary>
    public static string Rolls(MuleItemDb item)
        => $"{item.BaseCode}|{item.QualityType}|{item.UniqueCode}|{item.SetCode}|{item.Ethereal}|{item.Sockets}|"
            + string.Join(";", (item.Stats ?? []).Select(s => $"{s.Type}={s.Value}/{s.PerLevel}").Order());

    public static bool SameAs(MuleItemDb item, MuleItemDb worn)
    {
        if (worn == null || item.BaseCode != worn.BaseCode || item.QualityType != worn.QualityType || item.UniqueCode != worn.UniqueCode
            || item.SetCode != worn.SetCode || item.Ethereal != worn.Ethereal || item.Sockets != worn.Sockets)
        {
            return false;
        }

        return Rolls(item) == Rolls(worn);
    }

    /// <summary>Whether a mule item could go in the slot for this character: type, game mode and class.</summary>
    public static bool Fits(MuleItemDb item, string slot, GearCharacterDb character)
    {
        if (item.Expansion != character.Expansion || item.Stats == null || item.Stats.Count == 0
            || !Enum.TryParse<ClassificationType>(item.ClassificationType, out var type) || !GearSlots.Fits(slot, type))
        {
            return false;
        }

        return !ClassTables.ClassOnly.TryGetValue(item.ClassificationType, out var only) || only == character.Class;
    }

    /// <summary>The resists a swap may not lower: fire, cold and lightning in PvP, the ones the preset weighs in PvM.</summary>
    public static IReadOnlyList<string> Guarded(UpgradePreset preset, ResistMode mode)
        => mode == ResistMode.PvP
            ? ["Fire", "Cold", "Lightning"]
            : [.. GearEvaluation.Elements.Where(e => preset.ResistWeights.GetValueOrDefault(e) >= 1)];

    public static double Score(GearEvaluation evaluation, UpgradePreset preset, ResistMode mode, string slot, GearItem slotItem)
    {
        var score = 0.0;
        foreach (var weight in preset.Weights)
        {
            if (weight.Slot != null && weight.Slot != slot)
            {
                continue;
            }

            var value = weight.Slot == GearSlots.Charms ? evaluation.Total(GearEvaluation.CharmsPrefix + weight.Stat)
                : weight.Slot != null ? slotItem?.Stats.GetValueOrDefault(weight.Stat) ?? 0
                : evaluation.Total(weight.Stat);
            if (weight.Cap is double cap)
            {
                value = Math.Min(value, cap);
            }

            score += weight.Weight * value;
        }

        foreach (var (element, line) in evaluation.Resists)
        {
            score += ResistWeight(preset, mode, element) * line.Effective;
            if (mode == ResistMode.PvP && element == "Cold")
            {
                score += preset.ColdOverCap * line.OverCap;
            }
        }

        return score;
    }

    /// <summary>PvP counts fire, cold and lightning at least fully and ignores poison; PvM uses the preset's own weights.</summary>
    public static double ResistWeight(UpgradePreset preset, ResistMode mode, string element)
    {
        var weight = preset.ResistWeights.GetValueOrDefault(element);
        if (mode == ResistMode.PvM)
        {
            return weight;
        }

        return element == "Poison" ? 0 : Math.Max(weight, 1);
    }

    private static List<StatChange> Changes(GearEvaluation before, GearEvaluation after, IEnumerable<string> shown)
    {
        var changes = shown
            .Select(stat => new StatChange(stat, before.Total(stat), after.Total(stat)))
            .Where(c => c.Before != c.After)
            .ToList();
        foreach (var element in GearEvaluation.Elements)
        {
            var (old, now) = (before.Resists[element], after.Resists[element]);
            if (old.Raw != now.Raw)
            {
                changes.Add(new StatChange(element + " resist", old.Effective, now.Effective));
            }
        }

        return changes;
    }
}
