using D2NG.Core.D2GS.Items;
using D2NG.Core.ItemData;
using D2NG.Mule.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace D2NG.Mule.Upgrades;

/// <summary>
/// The game tables the upgrade finder reads: bases for requirements, block and kind, and the
/// affixes for the range a roll could have landed in. Built from <c>item-affixes.json</c>.
/// </summary>
public sealed partial class GearTables
{
    private readonly ItemAffixTable _table;
    private readonly Dictionary<string, BaseItemEntry> _byCode;
    private readonly Dictionary<string, BaseItemEntry> _byName;
    private readonly ItemRequirements _requirements;
    private readonly Dictionary<int, UniqueItemEntry> _uniques;
    private readonly Dictionary<(string Code, bool Expansion, AffixQuality Quality), Dictionary<string, (int Min, int Max)>> _ranges = [];

    public GearTables(ItemAffixTable table)
    {
        _table = table;
        _byCode = table.BaseItems.GroupBy(b => b.Code.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        _byName = table.BaseItems.GroupBy(b => Normalized(b.Name)).ToDictionary(g => g.Key, g => g.First());
        _requirements = new ItemRequirements(table);
        _uniques = table.UniqueItems.GroupBy(u => u.Index).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>The item's base, by the code the packet carried or, on older records, by its name.</summary>
    public BaseItemEntry Base(MuleItemDb item)
    {
        if (item.BaseCode != null && _byCode.TryGetValue(item.BaseCode.ToLowerInvariant(), out var byCode))
        {
            return byCode;
        }

        return _byName.GetValueOrDefault(Normalized(item.ItemName ?? ""));
    }

    /// <summary>The level an item needs, or null when its affixes decide and the realm's ids cannot be read.</summary>
    public int? RequiredLevel(MuleItemDb item)
    {
        var baseItem = Base(item);
        if (baseItem == null || !Enum.TryParse<QualityType>(item.QualityType, out var quality))
        {
            return null;
        }

        return _requirements.RequiredLevel(new Item { Type = baseItem.Code, Quality = quality, UniqueCode = item.UniqueCode, SetCode = item.SetCode });
    }

    /// <summary>
    /// What the item is called: a unique's or set item's own name, otherwise its base. Rare and magic
    /// names are not kept: the realm's name ids do not line up with the client's tables.
    /// </summary>
    public string DisplayName(MuleItemDb item)
    {
        var baseItem = Base(item);
        if (baseItem != null && Enum.TryParse<QualityType>(item.QualityType, out var quality))
        {
            var probe = new Item { Type = baseItem.Code, Quality = quality, UniqueCode = item.UniqueCode, SetCode = item.SetCode };
            var unique = quality == QualityType.Unique ? _uniques.GetValueOrDefault((int)item.UniqueCode) : null;
            var set = _requirements.SetItem(probe);
            var named = unique != null ? unique.DisplayName ?? unique.Name : set?.DisplayName ?? set?.Name;
            if (!string.IsNullOrEmpty(named))
            {
                return named.StartsWith("Pre ", StringComparison.Ordinal) ? named[4..] : named;
            }
        }

        return BaseName(item);
    }

    /// <summary>The base's name, such as Ring or Rune Bow.</summary>
    public string BaseName(MuleItemDb item) => Base(item) is { } baseItem ? baseItem.DisplayName ?? baseItem.Name.Trim() : StatText.Humanize(item.ItemName ?? "");

    /// <summary>
    /// The lowest and highest one affix can roll for the stat on this base, or for a set item its
    /// own property's range. Null for uniques, whose rolls the client tables do not carry, and for
    /// stats no affix gives.
    /// </summary>
    public (int Min, int Max)? RangeOf(MuleItemDb item, string stat)
    {
        var baseItem = Base(item);
        if (baseItem == null || !Enum.TryParse<QualityType>(item.QualityType, out var quality))
        {
            return null;
        }

        Dictionary<string, (int Min, int Max)> ranges;
        if (quality == QualityType.Set)
        {
            var setItem = _requirements.SetItem(new Item { Type = baseItem.Code, Quality = quality, SetCode = item.SetCode });
            ranges = setItem == null ? [] : Collect(setItem.Properties);
        }
        else if (quality == QualityType.Unique)
        {
            ranges = _uniques.TryGetValue((int)item.UniqueCode, out var unique) ? Collect(unique.Properties.Where(p => p.Min != p.Max)) : [];
        }
        else if (quality is QualityType.Magical or QualityType.Rare or QualityType.Crafted)
        {
            var affixQuality = quality == QualityType.Magical ? AffixQuality.Magic : AffixQuality.Rare;
            var key = (baseItem.Code, item.Expansion ?? false, affixQuality);
            if (!_ranges.TryGetValue(key, out ranges))
            {
                ranges = Collect(_table.EligibleAffixes(baseItem, key.Item2, affixQuality).SelectMany(a => a.Mods));
                _ranges[key] = ranges;
            }
        }
        else
        {
            return null;
        }

        return ranges.TryGetValue(stat, out var range) ? range : null;
    }

    private static Dictionary<string, (int Min, int Max)> Collect(IEnumerable<PropertyRange> mods)
    {
        var ranges = new Dictionary<string, (int Min, int Max)>();
        foreach (var mod in mods)
        {
            foreach (var stat in StatsOf(mod.Property))
            {
                var low = Math.Min(mod.Min, mod.Max);
                var high = Math.Max(mod.Min, mod.Max);
                ranges[stat] = ranges.TryGetValue(stat, out var known) ? (Math.Min(known.Min, low), Math.Max(known.Max, high)) : (low, high);
            }
        }

        return ranges;
    }

    /// <summary>The set an item belongs to, as the game names it; null for anything not a set item.</summary>
    public string SetNameOf(MuleItemDb item)
    {
        if (item.QualityType != nameof(QualityType.Set) || Base(item) is not { } baseItem)
        {
            return null;
        }

        var set = _requirements.Set((int)item.SetCode);
        return set?.DisplayName ?? set?.Name ?? _requirements.SetItem(new Item { Type = baseItem.Code, Quality = QualityType.Set, SetCode = item.SetCode })?.SetName;
    }

    /// <summary>
    /// What the worn set items add on top of their own stats: each item's bonuses for the number of its set worn,
    /// then the set's partial bonuses, and the full set bonus once every item is on.
    /// </summary>
    public List<SetBonus> SetBonuses(IEnumerable<GearItem> worn)
    {
        var bonuses = new List<SetBonus>();
        foreach (var group in worn.Where(i => i.Base != null && i.Source.QualityType == nameof(QualityType.Set)).GroupBy(i => i.Source.SetCode))
        {
            var count = group.Count();
            var stats = new List<(string Stat, double Value)>();
            foreach (var item in group)
            {
                var entry = _requirements.SetItem(new Item { Type = item.Base.Code, Quality = QualityType.Set, SetCode = item.Source.SetCode });
                for (var i = 0; entry != null && i < entry.SetProperties.Count; i++)
                {
                    var needed = i < entry.SetPropertiesWorn.Count ? entry.SetPropertiesWorn[i] : i + 2;
                    if (needed <= count)
                    {
                        Add(stats, entry.SetProperties[i]);
                    }
                }
            }

            var set = _requirements.Set((int)group.Key);
            foreach (var (needed, mods) in set?.Partial ?? [])
            {
                if (needed <= count)
                {
                    mods.ForEach(mod => Add(stats, mod));
                }
            }

            if (set != null && set.ItemCount > 0 && count >= set.ItemCount)
            {
                set.Full.ForEach(mod => Add(stats, mod));
            }

            bonuses.Add(new SetBonus(set?.DisplayName ?? set?.Name ?? "", count, set?.ItemCount ?? 0, stats));
        }

        return bonuses;

        static void Add(List<(string Stat, double Value)> stats, PropertyRange mod)
        {
            foreach (var stat in StatsOf(mod.Property))
            {
                stats.Add((stat, Math.Max(mod.Min, mod.Max)));
            }
        }
    }

    /// <summary>The properties.bin codes whose rolls the finder shows, and the stats they raise.</summary>
    private static readonly Dictionary<string, string[]> PropertyStats = new(StringComparer.Ordinal)
    {
        ["str"] = ["Strength"], ["dex"] = ["Dexterity"], ["vit"] = ["Vitality"], ["enr"] = ["Energy"],
        ["hp"] = ["Life"], ["mana"] = ["Mana"], ["hp%"] = ["EnhancedLife"], ["mana%"] = ["EnhancedMana"],
        ["lifesteal"] = ["MinimumLifeStolenPerHit"], ["manasteal"] = ["MinimumManaStolenPerHit"],
        ["res-fire"] = ["FireResistance"], ["res-cold"] = ["ColdResistance"], ["res-ltng"] = ["LightningResistance"], ["res-pois"] = ["PoisonResistance"],
        ["res-all"] = ["FireResistance", "ColdResistance", "LightningResistance", "PoisonResistance"],
        ["res-fire-max"] = ["MaximumFireResistance"], ["res-cold-max"] = ["MaximumColdResistance"],
        ["res-ltng-max"] = ["MaximumLightningResistance"], ["res-pois-max"] = ["MaximumPoisonResistance"],
        ["swing1"] = ["IncreasedAttackSpeed"], ["swing2"] = ["IncreasedAttackSpeed"], ["swing3"] = ["IncreasedAttackSpeed"],
        ["cast1"] = ["FasterCastRate"], ["cast2"] = ["FasterCastRate"], ["cast3"] = ["FasterCastRate"],
        ["balance1"] = ["FasterHitRecovery"], ["balance2"] = ["FasterHitRecovery"], ["balance3"] = ["FasterHitRecovery"],
        ["block1"] = ["FasterBlockRate"], ["block2"] = ["FasterBlockRate"], ["block3"] = ["FasterBlockRate"],
        ["move1"] = ["FasterRunWalk"], ["move2"] = ["FasterRunWalk"], ["move3"] = ["FasterRunWalk"],
        ["block"] = ["IncreasedBlocking"], ["att"] = ["AttackRating"], ["att%"] = ["ToHitPercent"],
        ["ac"] = ["Defense"], ["ac%"] = ["EnhancedDefense"], ["dmg%"] = ["EnhancedMaximumDamage"],
        ["dmg-min"] = ["MinimumDamage"], ["dmg-max"] = ["MaximumDamage"],
        ["gold%"] = ["ExtraGold"], ["mag%"] = ["BetterChanceOfGettingMagicItem"],
        ["red-dmg"] = ["DamageReduction"], ["red-mag"] = ["MagicalDamageReduction"], ["regen"] = ["ReplenishLife"],
        ["thorns"] = ["AttackerTakesDamage"], ["res-pois-len"] = ["PoisonLengthReduction"], ["regen-mana"] = ["ManaRecoveryBonus"],
        ["light"] = ["LightRadius"], ["allskills"] = ["AllSkills"], ["ease"] = ["ReducedRequirements"],
        ["ama"] = ["AmazonSkills"], ["sor"] = ["SorceressSkills"], ["nec"] = ["NecromancerSkills"], ["pal"] = ["PaladinSkills"],
        ["bar"] = ["BarbarianSkills"], ["dru"] = ["DruidSkills"], ["ass"] = ["AssassinSkills"],
    };

    private static string[] StatsOf(string property) => PropertyStats.GetValueOrDefault(property ?? "") ?? [];

    public static string Normalized(string name) => NonAlphanumeric().Replace(name.ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
