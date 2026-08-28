using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class GlovesTests
{
    [Fact]
    public void Keep_RareGloves_JavTab_Res()
    {
        var item = TestItemFactory.Create(ClassificationType.Gloves, ItemName.LightGauntlets, QualityType.Rare);
        TestItemFactory.AddSkillTab(item, SkillTab.AmazonJavelinAndSpearSkills, 2);
        TestItemFactory.AddStat(item, StatType.FireResistance, 20);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 10);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Amazon, item));
    }

    [Fact]
    public void Reject_RareGloves_LowRes()
    {
        var item = TestItemFactory.Create(ClassificationType.Gloves, ItemName.LightGauntlets, QualityType.Rare);
        TestItemFactory.AddSkillTab(item, SkillTab.AmazonJavelinAndSpearSkills, 2);
        TestItemFactory.AddStat(item, StatType.FireResistance, 10);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 5);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Amazon, item));
    }
}
