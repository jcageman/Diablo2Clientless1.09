using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class ArmorsTests
{
    [Fact]
    public void Keep_UniqueSerpentskin_WithColdResAndSkill()
    {
        var item = TestItemFactory.Create(ClassificationType.Armor, ItemName.SerpentskinArmor, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.ColdResistance, 34);
        TestItemFactory.AddStat(item, StatType.AllSkills, 1);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_UniqueSerpentskin_LowColdRes()
    {
        var item = TestItemFactory.Create(ClassificationType.Armor, ItemName.SerpentskinArmor, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.ColdResistance, 20);
        TestItemFactory.AddStat(item, StatType.AllSkills, 1);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
