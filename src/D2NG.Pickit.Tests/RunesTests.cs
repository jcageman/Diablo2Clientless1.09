using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class RunesTests
{
    [Fact]
    public void Pickup_JahRune()
    {
        var item = TestItemFactory.Create(ClassificationType.Rune, ItemName.JahRune, QualityType.Normal, identified: false);

        Assert.True(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }

    [Fact]
    public void Reject_TirRune()
    {
        var item = TestItemFactory.Create(ClassificationType.Rune, ItemName.TirRune, QualityType.Normal, identified: false);

        Assert.False(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }
}
