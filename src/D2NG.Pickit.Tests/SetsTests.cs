using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class SetsTests
{
    [Fact]
    public void Pickup_SetLacqueredPlate()
    {
        var item = TestItemFactory.Create(ClassificationType.Armor, ItemName.LacqueredPlate, QualityType.Set, identified: false);

        Assert.True(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }

    [Fact]
    public void Reject_SetBelt()
    {
        var item = TestItemFactory.Create(ClassificationType.Belt, ItemName.HeavyBelt, QualityType.Set, identified: false);

        Assert.False(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }
}
