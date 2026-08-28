using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class GemsTests
{
    [Fact]
    public void Pickup_PerfectAmethyst()
    {
        var item = TestItemFactory.Create(ClassificationType.Gem, ItemName.PerfectAmethyst, QualityType.Normal, identified: false);

        Assert.True(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }

    [Fact]
    public void Reject_FlawedAmethyst()
    {
        var item = TestItemFactory.Create(ClassificationType.Gem, ItemName.FlawedAmethyst, QualityType.Normal, identified: false);

        Assert.False(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }
}
