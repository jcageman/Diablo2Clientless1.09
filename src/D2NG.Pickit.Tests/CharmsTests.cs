using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class CharmsTests
{
    [Fact]
    public void Keep_SmallCharm_Life()
    {
        var item = TestItemFactory.Create(ClassificationType.SmallCharm, ItemName.SmallCharm, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.Life, 20);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_SmallCharm_LowLife()
    {
        var item = TestItemFactory.Create(ClassificationType.SmallCharm, ItemName.SmallCharm, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.Life, 18);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Keep_LargeCharm_GoldFind()
    {
        var item = TestItemFactory.Create(ClassificationType.LargeCharm, ItemName.LargeCharm, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.ExtraGold, 25);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_LargeCharm_LowGoldFind()
    {
        var item = TestItemFactory.Create(ClassificationType.LargeCharm, ItemName.LargeCharm, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.ExtraGold, 15);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Keep_GrandCharm_Level95()
    {
        var item = TestItemFactory.Create(ClassificationType.GrandCharm, ItemName.GrandCharm, QualityType.Magical, level: 95);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_GrandCharm_LowLevel()
    {
        var item = TestItemFactory.Create(ClassificationType.GrandCharm, ItemName.GrandCharm, QualityType.Magical, level: 80);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
