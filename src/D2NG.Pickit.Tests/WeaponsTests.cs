using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class WeaponsTests
{
    [Fact]
    public void Keep_RareEtherealPhaseBlade_WithEdAndRepair()
    {
        var item = TestItemFactory.Create(ClassificationType.Sword, ItemName.PhaseBlade, QualityType.Rare, ethereal: true);
        TestItemFactory.AddStat(item, StatType.EnhancedDamage, 170);
        TestItemFactory.AddStat(item, StatType.RepairsDurability, 1);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Barbarian, item));
    }

    [Fact]
    public void Reject_RareEtherealPhaseBlade_LowEd()
    {
        var item = TestItemFactory.Create(ClassificationType.Sword, ItemName.PhaseBlade, QualityType.Rare, ethereal: true);
        TestItemFactory.AddStat(item, StatType.EnhancedDamage, 120);
        TestItemFactory.AddStat(item, StatType.RepairsDurability, 1);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Barbarian, item));
    }
}
