using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class SocketBasesTests
{
    private static Item Base(ClassificationType classification, ItemName name, string code, uint sockets, QualityType quality = QualityType.Normal)
    {
        var item = TestItemFactory.Create(classification, name, quality, sockets: sockets);
        item.Type = code;
        return item;
    }

    [Fact]
    public void Keep_RhymeBase_TwoSocketMonarch()
    {
        Assert.True(Pickit.ShouldKeepItem(Base(ClassificationType.Shield, ItemName.Monarch, "uit", 2)));
    }

    [Fact]
    public void Reject_TwoSocketNormalTierShield()
    {
        Assert.False(Pickit.ShouldKeepItem(Base(ClassificationType.Shield, ItemName.KiteShield, "kit", 2)));
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(6u)]
    public void Keep_HonorAndSilenceBase_EliteWeapon(uint sockets)
    {
        Assert.True(Pickit.ShouldKeepItem(Base(ClassificationType.Polearm, ItemName.GiantThresher, "7gt", sockets, QualityType.Superior)));
    }

    [Fact]
    public void Reject_FourSocketEliteWeapon()
    {
        Assert.False(Pickit.ShouldKeepItem(Base(ClassificationType.Polearm, ItemName.GiantThresher, "7gt", 4)));
    }

    [Fact]
    public void Keep_WealthBase_ThreeSocketDuskShroud()
    {
        Assert.True(Pickit.ShouldKeepItem(Base(ClassificationType.Armor, ItemName.DuskShroud, "uui", 3)));
    }
}
