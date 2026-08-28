using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class JewelsTests
{
    [Fact]
    public void Keep_Jewel_EnhancedDamage()
    {
        var item = TestItemFactory.Create(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.EnhancedDamage, 35);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_Jewel_LowEd()
    {
        var item = TestItemFactory.Create(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.EnhancedDamage, 20);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
