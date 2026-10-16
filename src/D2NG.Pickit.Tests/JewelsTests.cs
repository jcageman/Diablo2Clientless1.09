using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class JewelsTests
{
    [Fact]
    public void Keep_Jewel_EnhancedDamage()
    {
        var item = TestItemFactory.Create(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.EnhancedDamage, 35);

        Assert.True(Pickit.ShouldKeepItem(item));
    }

    // Any magic jewel is kept as craft stock until 30 are held; past that only the stat rules count.
    [Fact]
    public void Reject_Jewel_LowEd_CraftStockFull()
    {
        var held = Enumerable.Range(0, 30).Select(_ => TestItemFactory.Create(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical)).ToList();
        var item = TestItemFactory.Create(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical);
        TestItemFactory.AddStat(item, StatType.EnhancedDamage, 20);

        Assert.False(Pickit.ShouldKeepItem(item, held));
    }
}
