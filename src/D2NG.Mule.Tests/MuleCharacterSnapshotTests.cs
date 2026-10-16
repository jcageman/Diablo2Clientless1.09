using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Mule;

namespace D2NG.Mule.Tests;

public class MuleCharacterSnapshotTests
{
    private static Item Charm(uint id) => new()
    {
        Id = id,
        Name = ItemName.GrandCharm,
        Classification = ClassificationType.GrandCharm,
        Quality = QualityType.Magical,
        Properties = []
    };

    [Fact]
    public void MapItems_MarksTheItemsTheBotsNeverMove()
    {
        var snapshot = new MuleCharacterSnapshot
        {
            Items = [Charm(1), Charm(2)],
            FitProfile = [],
            UntouchableIds = [2]
        };

        var mapped = snapshot.MapItems("account", "character");

        Assert.False(mapped.Single(i => i.Id.EndsWith("-1", StringComparison.Ordinal)).Untouchable);
        Assert.True(mapped.Single(i => i.Id.EndsWith("-2", StringComparison.Ordinal)).Untouchable);
    }
}
