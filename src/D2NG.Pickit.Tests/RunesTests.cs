using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class RunesTests
{
    [Fact]
    public void Pickup_JahRune()
    {
        var item = TestItemFactory.Create(ClassificationType.Rune, ItemName.JahRune, QualityType.Normal, identified: false);

        Assert.True(Pickit.ShouldPickupItem(shouldPickupGoldItems: true, item));
    }

    // Every rune but Io is kept: runeword runes (Tir, Ko, ...), craft runes (Tal, Ral, Ort, Thul),
    // cube feeders (Lum, Fal) and the high runes. Io only feeds Lum.
    [Theory]
    [InlineData(ItemName.ElRune)]
    [InlineData(ItemName.TirRune)]
    [InlineData(ItemName.TalRune)]
    [InlineData(ItemName.ThulRune)]
    [InlineData(ItemName.LumRune)]
    [InlineData(ItemName.KoRune)]
    [InlineData(ItemName.ZodRune)]
    public void Keep_Rune(ItemName rune)
    {
        var item = TestItemFactory.Create(ClassificationType.Rune, rune, QualityType.Normal);

        Assert.True(Pickit.ShouldPickupItem(shouldPickupGoldItems: true, item));
        Assert.True(Pickit.ShouldKeepItem(item));
    }

    [Fact]
    public void Reject_IoRune()
    {
        var item = TestItemFactory.Create(ClassificationType.Rune, ItemName.IoRune, QualityType.Normal);

        Assert.False(Pickit.ShouldPickupItem(shouldPickupGoldItems: true, item));
    }
}
