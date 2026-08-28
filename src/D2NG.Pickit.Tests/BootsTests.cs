using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class BootsTests
{
    [Fact]
    public void Keep_RareBoots_FrW_Res_Life()
    {
        var item = TestItemFactory.Create(ClassificationType.Boots, ItemName.MeshBoots, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.FasterRunWalk, 30);
        TestItemFactory.AddStat(item, StatType.FireResistance, 50);
        TestItemFactory.AddStat(item, StatType.ColdResistance, 30);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 40);
        TestItemFactory.AddStat(item, StatType.Life, 60);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_RareBoots_InsufficientRes()
    {
        var item = TestItemFactory.Create(ClassificationType.Boots, ItemName.MeshBoots, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.FasterRunWalk, 30);
        TestItemFactory.AddStat(item, StatType.FireResistance, 20);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 20);
        TestItemFactory.AddStat(item, StatType.Life, 40);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
