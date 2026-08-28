using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class ShieldsTests
{
    [Fact]
    public void Keep_MagicMonarch_Block()
    {
        var item = TestItemFactory.Create(ClassificationType.Shield, ItemName.Monarch, QualityType.Magical, sockets: 4);
        TestItemFactory.AddStat(item, StatType.IncreasedBlocking, 10);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_MagicMonarch_NoSockets()
    {
        var item = TestItemFactory.Create(ClassificationType.Shield, ItemName.Monarch, QualityType.Magical, sockets: 2);
        TestItemFactory.AddStat(item, StatType.FasterBlockRate, 10);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
