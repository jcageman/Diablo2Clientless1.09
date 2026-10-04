namespace D2NG.Core.ItemData;

public enum BaseItemKind
{
    Weapon,
    Armor,
    Misc
}

/// <summary>One row of weapons, armor or misc: the base an affix lands on.</summary>
public sealed class BaseItemEntry
{
    public BaseItemKind Kind { get; set; }
    public string Code { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public string Type2 { get; set; }

    /// <summary>Below 100 the base drops in classic games too.</summary>
    public int Version { get; set; }

    /// <summary>Quality level: the lowest item level the base drops at.</summary>
    public int Level { get; set; }

    public int LevelRequirement { get; set; }

    public bool IsExpansionOnly => Version >= 100;
}
