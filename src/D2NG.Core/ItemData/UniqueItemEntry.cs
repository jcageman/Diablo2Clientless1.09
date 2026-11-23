namespace D2NG.Core.ItemData;

/// <summary>One row of uniqueitems.bin; its record index is the unique id an item packet carries.</summary>
public sealed class UniqueItemEntry
{
    public int Index { get; set; }

    public string Name { get; set; }

    /// <summary>The name the game shows, from the string tables; null when they have none for the key.</summary>
    public string DisplayName { get; set; }

    /// <summary>The base item's code.</summary>
    public string Code { get; set; }

    public int Level { get; set; }

    public int LevelRequirement { get; set; }

    public int CostMultiply { get; set; }

    public int CostAdd { get; set; }

    public System.Collections.Generic.List<PropertyRange> Properties { get; set; } = [];

    /// <summary>
    /// The realm's copy of a unique as it was before level requirements existed: "Pre" rows carry
    /// none, and the armory flags exactly these items as pre items.
    /// </summary>
    public bool Pre => Name?.StartsWith("Pre ", System.StringComparison.Ordinal) == true;
}

/// <summary>
/// One item of a set. 1.09's setitems.bin keeps a whole set per record, so an item packet's set id
/// names the set and the base code picks the item inside it.
/// </summary>
public sealed class SetItemEntry
{
    public int SetIndex { get; set; }

    public string SetName { get; set; }

    public string Name { get; set; }

    /// <summary>The name the game shows, from the string tables; null when they have none for the key.</summary>
    public string DisplayName { get; set; }

    public string Code { get; set; }

    public int LevelRequirement { get; set; }

    public int CostMultiply { get; set; }

    public int CostAdd { get; set; }

    /// <summary>What the item has on its own.</summary>
    public System.Collections.Generic.List<PropertyRange> Properties { get; set; } = [];

    /// <summary>What it gains in its set: the first entry with two of the set's items worn, the next with three, and so on.</summary>
    public System.Collections.Generic.List<PropertyRange> SetProperties { get; set; } = [];

    /// <summary>For each of <see cref="SetProperties"/>, how many of the set's items must be worn; empty in files written before it existed.</summary>
    public System.Collections.Generic.List<int> SetPropertiesWorn { get; set; } = [];
}

/// <summary>A whole set: its bonuses for two to five items worn and for the full set.</summary>
public sealed class SetEntry
{
    public int Index { get; set; }

    public string Name { get; set; }

    /// <summary>The name the game shows, from the string tables; null when they have none for the key.</summary>
    public string DisplayName { get; set; }

    public int ItemCount { get; set; }

    /// <summary>Bonuses keyed by the number of the set's items worn, two to five.</summary>
    public System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PropertyRange>> Partial { get; set; } = [];

    public System.Collections.Generic.List<PropertyRange> Full { get; set; } = [];
}

