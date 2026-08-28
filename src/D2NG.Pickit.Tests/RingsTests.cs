using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class RingsTests
{
    [Fact]
    public void Keep_UniqueStoneOfJordan_UsesAllSkillsAlias()
    {
        var item = TestItemFactory.Create(ClassificationType.Ring, ItemName.Ring, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.AllSkills, 1);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}