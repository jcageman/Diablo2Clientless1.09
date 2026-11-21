using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Tests;

public class SocketedStatTests
{
    private static Item With(ClassificationType kind, params ItemName[] gems)
    {
        var item = new Item { Name = ItemName.LargeShield, Classification = kind, Properties = [] };
        for (var i = 0; i < gems.Length; i++)
        {
            item.SocketedItems[(uint)(100 + i)] = new Item { Name = gems[i], Classification = ClassificationType.Gem, Properties = [] };
        }

        return item;
    }

    [Fact]
    public void ThreePerfectDiamondsInAShieldGiveFiftySevenToEveryResistance()
    {
        var shield = With(ClassificationType.Shield, ItemName.PerfectDiamond, ItemName.PerfectDiamond, ItemName.PerfectDiamond);

        Assert.Equal(57, LevelingGear.SocketedStat(shield, StatType.FireResistance));
        Assert.Equal(57, LevelingGear.SocketedStat(shield, StatType.PoisonResistance));
    }

    [Fact]
    public void LesserDiamondsGiveLess()
    {
        var shield = With(ClassificationType.Shield, ItemName.FlawlessDiamond, ItemName.ChippedDiamond);

        Assert.Equal(20, LevelingGear.SocketedStat(shield, StatType.ColdResistance));
    }

    [Fact]
    public void OnlyResistancesCount()
    {
        Assert.Equal(0, LevelingGear.SocketedStat(With(ClassificationType.Shield, ItemName.PerfectDiamond), StatType.Life));
    }

    [Fact]
    public void DiamondsOutsideAShieldAreNotCountedHere()
    {
        Assert.Equal(0, LevelingGear.SocketedStat(With(ClassificationType.Armor, ItemName.PerfectDiamond), StatType.FireResistance));
    }
}
