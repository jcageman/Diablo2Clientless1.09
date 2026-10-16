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

    public int RequiredStrength { get; set; }

    public int RequiredDexterity { get; set; }

    /// <summary>Armor defense range; an item rolls its own value inside it.</summary>
    public int MinDefense { get; set; }

    public int MaxDefense { get; set; }

    /// <summary>A shield's base chance to block, before class, dexterity and level.</summary>
    public int Block { get; set; }

    /// <summary>Weapon speed modifier: negative is faster.</summary>
    public int Speed { get; set; }

    /// <summary>One-handed damage. Barbarian swords carry both this and the two-handed range.</summary>
    public int MinDamage { get; set; }

    public int MaxDamage { get; set; }

    /// <summary>Two-handed damage, which is also where bows and crossbows keep theirs.</summary>
    public int TwoHandMinDamage { get; set; }

    public int TwoHandMaxDamage { get; set; }

    /// <summary>Thrown damage of javelins, knives and axes.</summary>
    public int MissileMinDamage { get; set; }

    public int MissileMaxDamage { get; set; }

    /// <summary>Damage percent per point of strength, in hundredths: 100 is 1% per point.</summary>
    public int StrengthBonus { get; set; }

    /// <summary>Damage percent per point of dexterity, in hundredths.</summary>
    public int DexterityBonus { get; set; }

    public bool IsExpansionOnly => Version >= 100;
}
