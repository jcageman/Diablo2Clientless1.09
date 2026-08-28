using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class HelmsTests
{
    [Fact]
    public void Keep_UniqueShako()
    {
        var item = TestItemFactory.Create(ClassificationType.Helm, ItemName.Shako, QualityType.Unique);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_EtherealShako()
    {
        var item = TestItemFactory.Create(ClassificationType.Helm, ItemName.Shako, QualityType.Unique, ethereal: true);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
