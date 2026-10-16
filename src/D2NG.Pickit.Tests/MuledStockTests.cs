using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Pickit.Tests;

/// <summary>
/// The muled part of a '[maxquantity]' count is worked out once per <see cref="MuledStock"/>. The
/// decisions must be the ones made when every held item, muled or not, is counted on each call.
/// </summary>
public class MuledStockTests
{
    private static Item CraftAmulet(uint level = 80)
    {
        return TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Magical, level: level);
    }

    private static Item WarcriesAmulet()
    {
        var amulet = CraftAmulet();
        TestItemFactory.AddSkillTab(amulet, SkillTab.BarbarianWarcries, 3);
        return amulet;
    }

    private static Item CraftRing()
    {
        return TestItemFactory.Create(ClassificationType.Ring, ItemName.Ring, QualityType.Magical, level: 80);
    }

    private static Item MagicJewel()
    {
        return TestItemFactory.Create(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical);
    }

    private static Item WhiteColossusBlade(bool ethereal = false, uint sockets = 0)
    {
        return TestItemFactory.Create(ClassificationType.Sword, ItemName.ColossusBlade, QualityType.Normal, ethereal: ethereal, sockets: sockets);
    }

    private static Item PerfectRuby()
    {
        return TestItemFactory.Create(ClassificationType.Gem, ItemName.PerfectRuby, QualityType.Normal);
    }

    private static readonly Func<Item>[] Candidates =
    [
        () => CraftAmulet(),
        () => CraftAmulet(40),
        WarcriesAmulet,
        CraftRing,
        MagicJewel,
        () => WhiteColossusBlade(),
        () => WhiteColossusBlade(ethereal: true, sockets: 3),
        PerfectRuby,
        () => TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Magical, identified: false, level: 80),
    ];

    private static List<Item> Held(int amulets, int rings, int jewels, int blades)
    {
        return Enumerable.Repeat(0, amulets).Select(_ => CraftAmulet())
            .Concat(Enumerable.Repeat(0, 25).Select(_ => WarcriesAmulet()))
            .Concat(Enumerable.Repeat(0, rings).Select(_ => CraftRing()))
            .Concat(Enumerable.Repeat(0, jewels).Select(_ => MagicJewel()))
            .Concat(Enumerable.Repeat(0, blades).Select(_ => WhiteColossusBlade()))
            .Concat(Enumerable.Repeat(0, 40).Select(_ => PerfectRuby()))
            .ToList();
    }

    [Fact]
    public void CachedMuledCountDecidesLikeCountingEveryHeldItem()
    {
        var results = new HashSet<(int Candidate, bool Result)>();
        foreach (var amulets in new[] { 19, 20 })
        foreach (var rings in new[] { 19, 20 })
        foreach (var jewels in new[] { 29, 30 })
        foreach (var blades in new[] { 2, 3 })
        foreach (var muledEvery in new[] { 1, 2, 3, int.MaxValue })
        {
            var held = Held(amulets, rings, jewels, blades);
            var muled = held.Where((_, i) => i % muledEvery == 0).ToList();
            var own = held.Where((_, i) => i % muledEvery != 0).ToList();
            var stock = new MuledStock(muled);

            for (var c = 0; c < Candidates.Length; c++)
            {
                var item = Candidates[c]();
                var keep = Pickit.ExplainKeep(item, own, stock);
                Assert.Equal(Pickit.ExplainKeep(item, held), keep);
                foreach (var goldItems in new[] { true, false })
                {
                    Assert.Equal(Pickit.ExplainPickup(goldItems, item, held), Pickit.ExplainPickup(goldItems, item, own, stock));
                }

                results.Add((c, keep.Result));
            }
        }

        // Each limited candidate lands on both sides of its limit across the scenarios.
        foreach (var c in new[] { 0, 3, 4, 5 })
        {
            Assert.Contains((c, true), results);
            Assert.Contains((c, false), results);
        }
    }

    [Fact]
    public void MuledStock_IgnoredWithoutHeldItems()
    {
        var stock = new MuledStock(Held(20, 0, 0, 0));

        Assert.True(Pickit.ShouldKeepItem(CraftAmulet(), muledStock: stock));
        Assert.False(Pickit.ShouldKeepItem(CraftAmulet(), [], stock));
    }
}
