using System;
using System.Collections.Generic;
using System.Linq;
using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

/// <summary>
/// A rule with a '[maxquantity]' section stops matching once that many items matching the rule
/// are already held, by the character or on the mules.
/// </summary>
public class StockLimitTests
{
    private static Item WhiteColossusBlade(bool ethereal = false, uint sockets = 0)
    {
        return TestItemFactory.Create(ClassificationType.Sword, ItemName.ColossusBlade, QualityType.Normal, ethereal: ethereal, sockets: sockets);
    }

    private static Item MagicAmulet(uint level)
    {
        return TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Magical, level: level);
    }

    private static List<Item> Held(int count, Func<Item> create)
    {
        return Enumerable.Range(0, count).Select(_ => create()).ToList();
    }

    [Fact]
    public void Pickup_WhiteRerollSword_BelowLimit()
    {
        Assert.True(Pickit.ShouldPickupItem(true, WhiteColossusBlade(), Held(2, () => WhiteColossusBlade())));
    }

    [Fact]
    public void Reject_WhiteRerollSword_AtLimit()
    {
        Assert.False(Pickit.ShouldPickupItem(true, WhiteColossusBlade(), Held(3, () => WhiteColossusBlade())));
    }

    [Fact]
    public void Limit_CountsPerBase()
    {
        var held = Held(3, () => TestItemFactory.Create(ClassificationType.Sword, ItemName.CrypticSword, QualityType.Normal));

        Assert.True(Pickit.ShouldPickupItem(true, WhiteColossusBlade(), held));
    }

    [Fact]
    public void Pickup_EtherealFuryBase_IgnoresLimit()
    {
        var held = Held(3, () => WhiteColossusBlade());

        Assert.True(Pickit.ShouldPickupItem(true, WhiteColossusBlade(ethereal: true, sockets: 3), held));
    }

    [Fact]
    public void Reject_WhitePhaseBlade_CannotRollCruel()
    {
        var item = TestItemFactory.Create(ClassificationType.Sword, ItemName.PhaseBlade, QualityType.Normal);

        Assert.False(Pickit.ShouldPickupItem(true, item, []));
    }

    [Fact]
    public void Keep_WithoutHeldItems_IgnoresLimit()
    {
        Assert.True(Pickit.ShouldKeepItem(WhiteColossusBlade()));
    }

    [Fact]
    public void Keep_CraftAmulet_HighItemLevel_BelowLimit()
    {
        Assert.True(Pickit.ShouldKeepItem(MagicAmulet(80), []));
    }

    [Fact]
    public void Reject_CraftAmulet_LowItemLevel()
    {
        Assert.False(Pickit.ShouldKeepItem(MagicAmulet(40), []));
    }

    [Fact]
    public void Reject_CraftAmulet_AtLimit()
    {
        Assert.False(Pickit.ShouldKeepItem(MagicAmulet(80), Held(20, () => MagicAmulet(80))));
    }

    // A limited rule only counts held items no unlimited rule wants: twenty +3 Warcries amulets are
    // kept for their stats, not as crafting stock, so the crafting amulets are not full.
    [Fact]
    public void Keep_CraftAmulet_HeldAmuletsWantedByOtherRulesDoNotCount()
    {
        var held = Held(20, () =>
        {
            var warcries = MagicAmulet(80);
            TestItemFactory.AddSkillTab(warcries, SkillTab.BarbarianWarcries, 3);
            return warcries;
        });

        Assert.True(Pickit.ShouldKeepItem(MagicAmulet(80), held));
    }

    [Fact]
    public void KeepRuleSources_NamesEveryFileThatKeepsTheItem()
    {
        var warcries = MagicAmulet(80);
        TestItemFactory.AddSkillTab(warcries, SkillTab.BarbarianWarcries, 3);

        Assert.Equal(["CraftBases.nip"], Pickit.KeepRuleSources(MagicAmulet(80)));
        Assert.Equal(["Amulets.nip", "CraftBases.nip"], Pickit.KeepRuleSources(warcries).Order());
        Assert.Empty(Pickit.KeepRuleSources(MagicAmulet(40)));
    }

    [Fact]
    public void Keep_BloodGlovesBase()
    {
        var item = TestItemFactory.Create(ClassificationType.Gloves, ItemName.VampireboneGloves, QualityType.Magical, level: 75);

        Assert.True(Pickit.ShouldKeepItem(item, []));
    }

    [Fact]
    public void Keep_MagicJewel_ForCrafting()
    {
        var item = TestItemFactory.Create(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical);

        Assert.True(Pickit.ShouldKeepItem(item, []));
    }
}
