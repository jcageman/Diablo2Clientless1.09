using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;

namespace D2NG.Core.D2GS.Items;

public class ItemProperty
{
    public StatType Type { get; set; }

    /// <summary>The row in itemstatcost the packet named; paired stats carry the second row's value in MaximumValue.</summary>
    public int StatId { get; set; }

    public int Value { get; set; }

    public int MonsterReanimate { get; set; }

    public CharacterClass CharacterClass { get; set; }

    public Skill Skill { get; set; }

    public int SkillLevel { get; set; }

    public int SkillChance { get; set; }

    public SkillTab SkillTab { get; set; }

    public int Charges { get; set; }

    public int MaximumCharges { get; set; }

    public int PerLevel { get; set; }

    public int MinimumValue { get; set; }

    public int MaximumValue { get; set; }

    /// <summary>Frames a cold or poison hit lasts: the third stat of those packed damage stats.</summary>
    public int Length { get; set; }
}
