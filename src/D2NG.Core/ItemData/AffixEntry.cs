using D2NG.Core.D2GS.Enums;
using System.Collections.Generic;

namespace D2NG.Core.ItemData;

public enum AffixKind
{
    Prefix,
    Suffix,
    AutoMagic
}

/// <summary>One row of magicprefix, magicsuffix or automagic as the game loads it.</summary>
public sealed class AffixEntry
{
    public AffixKind Kind { get; set; }

    /// <summary>Row in its own table, which is the prefix or suffix id an item packet carries.</summary>
    public int Index { get; set; }

    public string Name { get; set; }

    /// <summary>Below 100 the affix rolls in classic games too; 100 and up is expansion only.</summary>
    public int Version { get; set; }

    public bool Spawnable { get; set; }

    /// <summary>Whether the affix can roll on rare and crafted items, not only on magic ones.</summary>
    public bool Rare { get; set; }

    public int Level { get; set; }

    /// <summary>0 means no upper bound.</summary>
    public int MaxLevel { get; set; }

    public int LevelRequirement { get; set; }

    public CharacterClass? ClassSpecific { get; set; }

    /// <summary>Two affixes of one group never roll on the same item.</summary>
    public int Group { get; set; }

    /// <summary>Relative weight among the affixes eligible for an item. 0 never rolls.</summary>
    public int Frequency { get; set; }

    /// <summary>Item types the affix can roll on, including every type that descends from one.</summary>
    public List<string> ItemTypes { get; set; } = [];

    /// <summary>Item types excluded even when an entry of <see cref="ItemTypes"/> covers them.</summary>
    public List<string> ExcludedItemTypes { get; set; } = [];

    public List<PropertyRange> Mods { get; set; } = [];

    public bool IsExpansionOnly => Version >= 100;
}
