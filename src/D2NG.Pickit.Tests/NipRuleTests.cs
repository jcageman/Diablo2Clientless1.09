using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit.Tests;

public class NipRuleTests
{
    private static readonly string NipDirectory = Path.Combine(AppContext.BaseDirectory, "Nips", "Expansion");

    [Fact]
    public void ParseDirectory_LoadsRules()
    {
        var rules = NipParser.ParseDirectory(NipDirectory);

        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.PropertyCondition is not null);
    }

    [Fact]
    public void LowSmallCharm_DoesNotMatchKeepRule()
    {
        var item = CreateItem(ClassificationType.SmallCharm, ItemName.SmallCharm, QualityType.Magical, identified: true);
        AddStat(item, StatType.Life, 18);
        var context = new NipEvaluationContext(item, CharacterClass.Sorceress);
        var matchingRules = NipParser.ParseDirectory(NipDirectory)
            .Where(rule => rule.MatchesKeep(context))
            .Select(rule => rule.Raw)
            .ToArray();

        Assert.True(matchingRules.Length == 0, string.Join(Environment.NewLine, matchingRules));
    }

    [Fact]
    public void ShouldPickup_UnidentifiedRareRing_UsesPropertyRules()
    {
        var item = CreateTestRing(identified: false);

        var result = Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPickup_UnidentifiedRareRing_UsesPropertySideBeforeStats()
    {
        var item = CreateItem(ClassificationType.Ring, ItemName.Ring, QualityType.Rare, identified: false);

        var result = Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: false, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldNotKeep_IdentifiedOptionalGoldPickupItem()
    {
        var item = CreateItem(ClassificationType.Armor, ItemName.ArchonPlate, QualityType.Normal, identified: true);
        item.Type = "uap";

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void ShouldNotPickup_LowValueGoldWhenGoldPickupIsDisabled()
    {
        var item = CreateItem(ClassificationType.Gold, ItemName.Gold, QualityType.Normal, identified: false);
        item.IsGold = true;
        item.Amount = 5000;

        Assert.False(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: false, item));
    }

    [Fact]
    public void ShouldNotPickup_LowValueGoldWhenGoldPickupIsEnabled()
    {
        var item = CreateItem(ClassificationType.Gold, ItemName.Gold, QualityType.Normal, identified: false);
        item.IsGold = true;
        item.Amount = 5000;

        Assert.False(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }

    [Fact]
    public void ShouldPickup_HighValueGoldEvenWhenGoldPickupIsDisabled()
    {
        var item = CreateItem(ClassificationType.Gold, ItemName.Gold, QualityType.Normal, identified: false);
        item.IsGold = true;
        item.Amount = 5001;

        Assert.True(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: false, item));
    }

    // The game sends gold with the identified flag set, so these three are the cases that actually
    // happen on a run - the identified: false ones above only cover the flag being absent.
    [Fact]
    public void ShouldPickup_HighValueIdentifiedGold()
    {
        var item = CreateItem(ClassificationType.Gold, ItemName.Gold, QualityType.Normal, identified: true);
        item.IsGold = true;
        item.Amount = 5001;

        Assert.True(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: false, item));
        Assert.True(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }

    [Fact]
    public void ShouldNotPickup_LowValueIdentifiedGold()
    {
        var item = CreateItem(ClassificationType.Gold, ItemName.Gold, QualityType.Normal, identified: true);
        item.IsGold = true;
        item.Amount = 5000;

        Assert.False(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: false, item));
        Assert.False(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item));
    }

    [Fact]
    public void ShouldPickup_BigIdentifiedGoldPile()
    {
        var item = CreateItem(ClassificationType.Gold, ItemName.Gold, QualityType.Normal, identified: true);
        item.IsGold = true;
        item.Amount = 250000;

        Assert.True(Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: false, item));
    }

    [Fact]
    public void ShouldNotKeep_IdentifiedRareRingWithoutMatchingStats()
    {
        var item = CreateItem(ClassificationType.Ring, ItemName.Ring, QualityType.Rare, identified: true);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void ShouldKeep_IdentifiedRareRing_UsesStatRules()
    {
        var item = CreateTestRing(identified: true);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_RareBoots_FrWResists()
    {
        var item = CreateItem(ClassificationType.Boots, ItemName.MeshBoots, QualityType.Rare, identified: true);
        AddStat(item, StatType.FasterRunWalk, 30);
        AddStat(item, StatType.FireResistance, 50);
        AddStat(item, StatType.LightningResistance, 40);
        AddStat(item, StatType.Life, 60);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_UniqueHeavyBelt_GoldFind()
    {
        var item = CreateItem(ClassificationType.Belt, ItemName.HeavyBelt, QualityType.Unique, identified: true);
        AddStat(item, StatType.ExtraGold, 120);
        AddStat(item, StatType.FireResistance, 20);
        AddStat(item, StatType.LightningResistance, 20);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Barbarian, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_UniqueShako()
    {
        var item = CreateItem(ClassificationType.Helm, ItemName.Shako, QualityType.Unique, identified: true);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_MagicMonarch_Block()
    {
        var item = CreateItem(ClassificationType.Shield, ItemName.Monarch, QualityType.Magical, identified: true);
        item.Sockets = 4;
        AddStat(item, StatType.IncreasedBlocking, 10);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_RareEtherealPhaseBlade_WithEdAndRepair()
    {
        var item = CreateItem(ClassificationType.Sword, ItemName.PhaseBlade, QualityType.Rare, identified: true);
        item.Ethereal = true;
        AddStat(item, StatType.EnhancedDamage, 160);
        AddStat(item, StatType.RepairsDurability, 1);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Barbarian, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_SmallCharm_Life()
    {
        var item = CreateItem(ClassificationType.SmallCharm, ItemName.SmallCharm, QualityType.Magical, identified: true);
        AddStat(item, StatType.Life, 20);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_GrandCharm_HighLevel()
    {
        var item = CreateItem(ClassificationType.GrandCharm, ItemName.GrandCharm, QualityType.Magical, identified: true);
        item.Level = 95;

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldKeep_Jewel_EnhancedDamage()
    {
        var item = CreateItem(ClassificationType.Jewel, ItemName.Jewel, QualityType.Magical, identified: true);
        AddStat(item, StatType.EnhancedDamage, 35);

        var result = Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPickup_PerfectAmethyst()
    {
        var item = CreateItem(ClassificationType.Gem, ItemName.PerfectAmethyst, QualityType.Normal, identified: false);

        var result = Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPickup_JahRune()
    {
        var item = CreateItem(ClassificationType.Rune, ItemName.JahRune, QualityType.Normal, identified: false);

        var result = Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPickup_SetLacqueredPlate()
    {
        var item = CreateItem(ClassificationType.Armor, ItemName.LacqueredPlate, QualityType.Set, identified: false);

        var result = Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item);

        Assert.True(result);
    }

    [Fact]
    public void ShouldPickup_RareOrb()
    {
        var item = CreateItem(ClassificationType.SorceressOrb, ItemName.SwirlingCrystal, QualityType.Rare, identified: false);

        var result = Pickit.ShouldPickupItem(characterClass: CharacterClass.Sorceress, shouldPickupGoldItems: true, item);

        Assert.True(result);
    }

    private static Item CreateTestRing(bool identified)
    {
        var item = new Item
        {
            Classification = ClassificationType.Ring,
            Quality = QualityType.Rare,
            Name = ItemName.Ring,
            IsIdentified = identified,
            Properties = []
        };

        item.Properties[StatType.FasterCastRate] = new ItemProperty { Type = StatType.FasterCastRate, Value = 10 };
        item.Properties[StatType.ToHitPercent] = new ItemProperty { Type = StatType.ToHitPercent, Value = 120 };
        item.Properties[StatType.MinimumLifeStolenPerHit] = new ItemProperty { Type = StatType.MinimumLifeStolenPerHit, Value = 6 };
        item.Properties[StatType.MinimumManaStolenPerHit] = new ItemProperty { Type = StatType.MinimumManaStolenPerHit, Value = 5 };
        item.Properties[StatType.FireResistance] = new ItemProperty { Type = StatType.FireResistance, Value = 20 };
        item.Properties[StatType.LightningResistance] = new ItemProperty { Type = StatType.LightningResistance, Value = 25 };
        item.Properties[StatType.ColdResistance] = new ItemProperty { Type = StatType.ColdResistance, Value = 20 };
        item.Properties[StatType.Life] = new ItemProperty { Type = StatType.Life, Value = 30 };

        return item;
    }

    private static Item CreateItem(ClassificationType classification, ItemName name, QualityType quality, bool identified)
    {
        return new Item
        {
            Classification = classification,
            Name = name,
            Quality = quality,
            IsIdentified = identified,
            Properties = []
        };
    }

    private static void AddStat(Item item, StatType stat, int value)
    {
        item.Properties[stat] = new ItemProperty { Type = stat, Value = value };
    }
}
