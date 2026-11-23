using D2NG.Core.D2GS.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.ItemData;

public sealed class ItemRequirements
{
    /// <summary>The id a unique without a row of its own carries, such as the standard of heroes.</summary>
    private const uint NoUnique = 4095;

    private readonly Dictionary<string, BaseItemEntry> _bases;
    private readonly Dictionary<int, UniqueItemEntry> _uniques;
    private readonly Dictionary<(int Set, string Code), SetItemEntry> _setItems;
    private readonly Dictionary<int, SetEntry> _sets;

    public ItemRequirements(ItemAffixTable table)
    {
        _bases = table.BaseItems.GroupBy(item => item.Code, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _uniques = table.UniqueItems.ToDictionary(item => item.Index);
        _setItems = table.SetItems.GroupBy(item => (item.SetIndex, item.Code)).ToDictionary(group => group.Key, group => group.First());
        _sets = (table.Sets ?? []).GroupBy(set => set.Index).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>The set item a set quality item is, by its set id and base code.</summary>
    public SetItemEntry SetItem(Item item)
        => item.Quality == QualityType.Set && _setItems.TryGetValue(((int)item.SetCode, item.Type ?? ""), out var setItem) ? setItem : null;

    public SetEntry Set(int index) => _sets.GetValueOrDefault(index);

    /// <summary>The unique item a unique quality item is, by its unique id; null for one without a row of its own.</summary>
    public UniqueItemEntry Unique(Item item)
        => item.Quality == QualityType.Unique && item.UniqueCode != NoUnique && _uniques.TryGetValue((int)item.UniqueCode, out var unique) ? unique : null;

    /// <summary>
    /// The character level an item needs, or null when it depends on magic affixes. The realm rolls
    /// prefixes and suffixes from its own tables, whose ids do not line up with the client's
    /// magicprefix and magicsuffix bins, so those requirements cannot be read off the item yet.
    /// Uniques and sets are matched by id and agree with the realm's armory.
    /// </summary>
    public int? RequiredLevel(Item item)
    {
        var baseLevel = _bases.TryGetValue(item.Type ?? "", out var baseItem) ? baseItem.LevelRequirement : 0;
        switch (item.Quality)
        {
            case QualityType.Unique:
                return item.UniqueCode != NoUnique && _uniques.TryGetValue((int)item.UniqueCode, out var unique)
                    ? Math.Max(baseLevel, unique.LevelRequirement)
                    : null;
            case QualityType.Set:
                return _setItems.TryGetValue(((int)item.SetCode, item.Type), out var setItem)
                    ? Math.Max(baseLevel, setItem.LevelRequirement)
                    : null;
            case QualityType.Magical:
            case QualityType.Rare:
            case QualityType.Crafted:
                return null;
            default:
                return baseLevel;
        }
    }

    public BaseItemEntry BaseOf(Item item) => _bases.TryGetValue(item.Type ?? "", out var baseItem) ? baseItem : null;

    public int RequiredStrength(Item item) => _bases.TryGetValue(item.Type ?? "", out var baseItem) ? baseItem.RequiredStrength : 0;

    public int RequiredDexterity(Item item) => _bases.TryGetValue(item.Type ?? "", out var baseItem) ? baseItem.RequiredDexterity : 0;
}
