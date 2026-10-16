using D2NG.Core.D2GS.Enums;

namespace D2NG.Core.SkillData;

public enum SkillElement
{
    None = 0,
    Fire = 1,
    Lightning = 2,
    Magic = 3,
    Cold = 4,
    Poison = 5
}

/// <summary>
/// One row of skills.bin. 1.09 has no damage formulas in its tables: the meaning of each parameter is
/// fixed per skill in the game code, so the parameters are kept raw and the caller decides.
/// </summary>
public sealed class SkillEntry
{
    /// <summary>The record index, which is the skill id the server sends.</summary>
    public int Id { get; set; }

    /// <summary>At most 16 characters: the table cuts longer names short ("Natural Resistan").</summary>
    public string Name { get; set; }

    /// <summary>Null for monster and item skills.</summary>
    public CharacterClass? Class { get; set; }

    /// <summary>The skill tree tab, 1 to 3, that a +tab item raises.</summary>
    public int Page { get; set; }

    public int Row { get; set; }

    public int Column { get; set; }

    public bool Passive { get; set; }

    public int RequiredLevel { get; set; }

    public int[] Params { get; set; } = new int[6];

    /// <summary>Attack rating percent at level 1, and per level after.</summary>
    public int ToHit { get; set; }

    public int LevelToHit { get; set; }

    /// <summary>Share of the weapon's damage the skill keeps, in eighths (8 = all of it).</summary>
    public int SourceDamage { get; set; }

    /// <summary>Damage values are in 1/256ths shifted left by this, so 8 means whole points.</summary>
    public int HitShift { get; set; }

    public int MinDamage { get; set; }

    public int MaxDamage { get; set; }

    /// <summary>Physical damage added per level for levels 2-8, 9-16 and 17 on.</summary>
    public int[] LevelDamage { get; set; } = new int[3];

    public SkillElement Element { get; set; }

    public int ElementMin { get; set; }

    public int ElementMax { get; set; }

    /// <summary>Elemental damage added per level for levels 2-8, 9-16 and 17 on.</summary>
    public int[] ElementLevel { get; set; } = new int[3];

    /// <summary>Frames the element lasts (cold length, poison duration).</summary>
    public int ElementLength { get; set; }
}
