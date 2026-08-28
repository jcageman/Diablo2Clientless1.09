using D2NG.Core.D2GS.Enums;
using D2NG.Pickit.Tests;

namespace D2NG.Pickit.Tests;

public class BeltsTests
{
    [Fact]
    public void Keep_UniqueHeavyBelt_GoldFind()
    {
        var item = TestItemFactory.Create(ClassificationType.Belt, ItemName.HeavyBelt, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.ExtraGold, 120);
        TestItemFactory.AddStat(item, StatType.FireResistance, 20);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 20);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Barbarian, item));
    }

    [Fact]
    public void Reject_LowGoldRareBelt()
    {
        var item = TestItemFactory.Create(ClassificationType.Belt, ItemName.HeavyBelt, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.ExtraGold, 60);
        TestItemFactory.AddStat(item, StatType.FireResistance, 20);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 20);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Barbarian, item));
    }
}
