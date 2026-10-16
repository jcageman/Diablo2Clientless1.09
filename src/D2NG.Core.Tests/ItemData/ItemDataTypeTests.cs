using D2NG.Core.D2GS.Items;
using Xunit;

namespace D2NG.Core.Tests.ItemData;

public class ItemDataTypeTests
{
    // The realm's cube reagents are 1x1 misc items in its misc.bin. An item code missing from
    // item_data.txt makes the item packet parser throw, so one lying on the ground could never be
    // picked up.
    [Theory]
    [InlineData("rcs", ItemName.RareSkull)]
    [InlineData("rcr", ItemName.RareRuby)]
    [InlineData("rca", ItemName.RareAmethyst)]
    [InlineData("rcd", ItemName.RareDiamond)]
    [InlineData("rce", ItemName.RareEmerald)]
    [InlineData("rct", ItemName.RareTopaz)]
    [InlineData("rcp", ItemName.RareSapphire)]
    public void RealmReagentsResolve(string code, ItemName name)
    {
        var entry = D2NG.Core.DataManager.DataManager.Instance.ItemData.Get(code);

        Assert.NotNull(entry);
        Assert.Equal(name, entry.Name);
        Assert.Equal(ClassificationType.Reagent, entry.Classification);
        Assert.Equal(1, entry.Width);
        Assert.Equal(1, entry.Height);
    }
}
