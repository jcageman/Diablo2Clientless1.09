using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class GemsTests
{
    [Fact]
    public void Pickup_PerfectAmethyst()
    {
        var item = TestItemFactory.Create(ClassificationType.Gem, ItemName.PerfectAmethyst, QualityType.Normal, identified: false);

        Assert.True(Pickit.ShouldPickupItem(shouldPickupGoldItems: true, item));
    }

    // Flawed Amethyst and Flawed Ruby are the catalysts for 3 Lum -> Ko and 3 Fal -> Lem.
    [Theory]
    [InlineData(ItemName.FlawedAmethyst)]
    [InlineData(ItemName.FlawedRuby)]
    public void Keep_RuneUpgradeCatalyst(ItemName gem)
    {
        var item = TestItemFactory.Create(ClassificationType.Gem, gem, QualityType.Normal);

        Assert.True(Pickit.ShouldKeepItem(item));
    }

    [Fact]
    public void Reject_FlawedTopaz()
    {
        var item = TestItemFactory.Create(ClassificationType.Gem, ItemName.FlawedTopaz, QualityType.Normal, identified: false);

        Assert.False(Pickit.ShouldPickupItem(shouldPickupGoldItems: true, item));
    }
}
