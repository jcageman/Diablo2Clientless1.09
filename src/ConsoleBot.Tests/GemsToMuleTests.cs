using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Tests;

public class GemsToMuleTests
{
    private static Item Gem(ItemName name, uint id) => new() { Name = name, Id = id, Classification = ClassificationType.Gem, Properties = [] };

    private static readonly Item Ring = new() { Name = ItemName.Ring, Id = 99, Classification = ClassificationType.Ring, Properties = [] };

    [Fact]
    public void EveryGemGoesWhenNoShieldIsOwed()
    {
        List<Item> held = [Gem(ItemName.PerfectDiamond, 1), Gem(ItemName.FlawlessRuby, 2)];
        var mules = LevelingGems.GemsToMule(() => held, 0);

        Assert.All(held, gem => Assert.True(mules(gem)));
    }

    [Fact]
    public void OnlyGemsGo()
    {
        Assert.False(LevelingGems.GemsToMule(() => [], 0)(Ring));
    }

    [Fact]
    public void TheLowestPerfectDiamondsTheShieldsNeedStay()
    {
        List<Item> held = [.. Enumerable.Range(1, 8).Select(id => Gem(ItemName.PerfectDiamond, (uint)(10 - id)))];
        var mules = LevelingGems.GemsToMule(() => held, 6);

        Assert.Equal([2u, 3u, 4u, 5u, 6u, 7u], held.Where(gem => !mules(gem)).Select(gem => gem.Id).Order());
        Assert.Equal([8u, 9u], held.Where(mules).Select(gem => gem.Id).Order());
    }

    [Fact]
    public void LesserDiamondsStayWhilePerfectOnesFallShort()
    {
        List<Item> held = [Gem(ItemName.PerfectDiamond, 1), Gem(ItemName.FlawlessDiamond, 2), Gem(ItemName.ChippedDiamond, 3)];
        var mules = LevelingGems.GemsToMule(() => held, 3);

        Assert.False(mules(held[1]));
        Assert.False(mules(held[2]));
    }

    [Fact]
    public void LesserDiamondsGoOnceThePerfectOnesAreHeld()
    {
        List<Item> held = [Gem(ItemName.PerfectDiamond, 1), Gem(ItemName.PerfectDiamond, 2), Gem(ItemName.PerfectDiamond, 3), Gem(ItemName.FlawlessDiamond, 4)];
        var mules = LevelingGems.GemsToMule(() => held, 3);

        Assert.True(mules(held[3]));
        Assert.False(mules(held[0]));
    }

    [Fact]
    public void OtherGemsGoWhileDiamondsAreKept()
    {
        List<Item> held = [Gem(ItemName.FlawlessRuby, 1), Gem(ItemName.PerfectSkull, 2)];
        var mules = LevelingGems.GemsToMule(() => held, 6);

        Assert.All(held, gem => Assert.True(mules(gem)));
    }

    [Fact]
    public void TheKeptOnesAreDecidedAgainAfterARenumbering()
    {
        List<Item> held = [Gem(ItemName.PerfectDiamond, 5), Gem(ItemName.PerfectDiamond, 6), Gem(ItemName.PerfectDiamond, 7), Gem(ItemName.PerfectDiamond, 8)];
        var mules = LevelingGems.GemsToMule(() => held, 3);
        Assert.True(mules(held[3]));

        // A refused trade gives every item a new id: the rule reads the held gems again, still keeping three.
        held = [Gem(ItemName.PerfectDiamond, 40), Gem(ItemName.PerfectDiamond, 30), Gem(ItemName.PerfectDiamond, 20), Gem(ItemName.PerfectDiamond, 10)];
        Assert.Equal(3, held.Count(gem => !mules(gem)));
        Assert.True(mules(held[0]));
    }
}
