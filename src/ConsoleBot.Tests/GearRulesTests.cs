using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.ItemData;

namespace ConsoleBot.Tests;

/// <summary>The rules the leveling gear score rests on; the set is Death's Disguise as 1.09's setitems.bin has it.</summary>
public class GearRulesTests
{
    private static PropertyRange Property(string code, int value) => new() { Property = code, Min = value, Max = value };

    private static readonly SetItemEntry Hand = new() { Name = "Death's Hand", Code = "lgl", SetProperties = [Property("swing3", 30)] };

    private static readonly SetItemEntry Guard = new() { Name = "Death's Guard", Code = "lbl", SetProperties = [Property("res-all", 15)] };

    private static readonly SetItemEntry Touch = new() { Name = "Death's Touch", Code = "wsd", SetProperties = [Property("dmg-cold", 75)] };

    private static readonly SetEntry Disguise = new()
    {
        Name = "Death's Disguise",
        ItemCount = 3,
        Partial = new() { [2] = [Property("lifesteal", 8)] },
        Full = [Property("att%", 40), Property("dmg-min", 10), Property("res-all", 25)]
    };

    private static List<string> Codes(IEnumerable<PropertyRange> properties) => [.. properties.Select(property => $"{property.Property} {property.Max}").Order()];

    [Fact]
    public void TheGlovesJoiningTheSashSwitchOnBothPiecesPropertiesAndTheTwoPieceBonus()
    {
        var added = GearRules.SetPropertiesAdded(Hand, [Guard], Disguise);

        Assert.Equal(["lifesteal 8", "res-all 15", "swing3 30"], Codes(added));
    }

    [Fact]
    public void ALonePieceSwitchesOnNothing()
    {
        Assert.Empty(GearRules.SetPropertiesAdded(Hand, [], Disguise));
    }

    [Fact]
    public void ALonePieceKeptForLaterCountsItsOwnPropertyAndTheTwoPieceBonus()
    {
        var added = GearRules.SetPropertiesAdded(Hand, [], Disguise, later: true);

        Assert.Equal(["lifesteal 8", "swing3 30"], Codes(added));
    }

    [Fact]
    public void TheThirdPieceAddsItsOwnPropertyAndTheFullBonusOnly()
    {
        var added = GearRules.SetPropertiesAdded(Touch, [Hand, Guard], Disguise);

        Assert.Equal(["att% 40", "dmg-cold 75", "dmg-min 10", "res-all 25"], Codes(added));
    }

    [Fact]
    public void WhatAPieceAddsIsTheDifferenceBetweenTheSetWithAndWithoutIt()
    {
        SetItemEntry[] pieces = [Hand, Guard, Touch];
        foreach (var piece in pieces)
        {
            var others = pieces.Where(other => other != piece).ToList();
            var with = Codes(GearRules.ActiveSetProperties(pieces, Disguise));
            var without = Codes(GearRules.ActiveSetProperties(others, Disguise).Concat(GearRules.SetPropertiesAdded(piece, others, Disguise)));

            Assert.Equal(with, without);
        }
    }

    [Theory]
    [InlineData(Difficulty.Normal, false, 0)]
    [InlineData(Difficulty.Nightmare, false, 20)]
    [InlineData(Difficulty.Hell, false, 50)]
    [InlineData(Difficulty.Nightmare, true, 40)]
    [InlineData(Difficulty.Hell, true, 100)]
    public void ClassicAndExpansionHaveTheirOwnResistPenalty(Difficulty difficulty, bool expansion, int penalty)
    {
        Assert.Equal(penalty, GearRules.ResistPenalty(difficulty, expansion));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 12)]
    [InlineData(2, 21)]
    [InlineData(5, 40)]
    [InlineData(10, 54)]
    [InlineData(20, 67)]
    public void NaturalResistanceFollowsItsDiminishingCurve(int level, int resistance)
    {
        Assert.Equal(resistance, GearRules.NaturalResistance(level));
    }

    [Fact]
    public void PerLevelSetPropertiesGrowWithTheCharacter()
    {
        var visor = new PropertyRange { Property = "att/lvl", Param = 16 };
        var wrap = new PropertyRange { Property = "ac/lvl", Param = 16 };

        Assert.Equal(656, GearRules.PropertyAmount(visor, 82));
        Assert.Equal("att", GearRules.BaseProperty(visor.Property));
        Assert.Equal(164, GearRules.PropertyAmount(wrap, 82));
        Assert.Equal("ac", GearRules.BaseProperty(wrap.Property));
        Assert.Equal(30, GearRules.PropertyAmount(Property("swing3", 30), 82));
    }

    [Fact]
    public void AttackSpeedGivesLessOnTopOfWhatIsWorn()
    {
        Assert.Equal(24, GearRules.AttackRateGain(0, 30), 3);
        Assert.Equal(9.108, GearRules.AttackRateGain(30, 20), 3);
    }

    [Theory]
    [InlineData(ClassificationType.Armor, SocketKind.Armor)]
    [InlineData(ClassificationType.Helm, SocketKind.Armor)]
    [InlineData(ClassificationType.Shield, SocketKind.Shield)]
    [InlineData(ClassificationType.Bow, SocketKind.Weapon)]
    [InlineData(ClassificationType.Mace, SocketKind.Weapon)]
    [InlineData(ClassificationType.Belt, null)]
    [InlineData(ClassificationType.Gloves, null)]
    [InlineData(ClassificationType.Boots, null)]
    [InlineData(ClassificationType.Ring, null)]
    [InlineData(ClassificationType.Amulet, null)]
    [InlineData(ClassificationType.Javelin, null)]
    [InlineData(ClassificationType.ThrowingKnife, null)]
    public void OnlyKindsWithSocketsInItemTypesTakeOne(ClassificationType classification, SocketKind? kind)
    {
        Assert.Equal(kind, GearRules.SocketKindOf(classification));
    }

    [Fact]
    public void APerfectRubyIsLifeInArmorFireResistanceInAShieldAndFireDamageInAWeapon()
    {
        Assert.Equal(["hp 38"], Codes(GearRules.GemProperties(ItemName.PerfectRuby, SocketKind.Armor)));
        Assert.Equal(["res-fire 40"], Codes(GearRules.GemProperties(ItemName.PerfectRuby, SocketKind.Shield)));
        Assert.Equal(["fire-max 20", "fire-min 15"], Codes(GearRules.GemProperties(ItemName.PerfectRuby, SocketKind.Weapon)));
        Assert.Empty(GearRules.GemProperties(ItemName.StandardOfHeroes, SocketKind.Armor));
    }

    [Theory]
    [InlineData(ClassificationType.Armor, ItemName.PerfectRuby, StatType.Life, 38)]
    [InlineData(ClassificationType.Armor, ItemName.PerfectRuby, StatType.FireResistance, 0)]
    [InlineData(ClassificationType.Shield, ItemName.PerfectRuby, StatType.FireResistance, 40)]
    [InlineData(ClassificationType.Shield, ItemName.FlawlessDiamond, StatType.ColdResistance, 14)]
    [InlineData(ClassificationType.Shield, ItemName.PerfectDiamond, StatType.PoisonResistance, 19)]
    [InlineData(ClassificationType.Helm, ItemName.PerfectDiamond, StatType.AttackRating, 100)]
    [InlineData(ClassificationType.Sword, ItemName.PerfectSkull, StatType.MinimumLifeStolenPerHit, 4)]
    [InlineData(ClassificationType.Gloves, ItemName.PerfectRuby, StatType.Life, 0)]
    public void ASocketedGemCountsInTheItemItSitsIn(ClassificationType classification, ItemName gem, StatType stat, int value)
    {
        var item = new Item { Classification = classification };
        item.SocketedItems[1] = new Item { Id = 1, Name = gem, Classification = ClassificationType.Gem };

        Assert.Equal(value, LevelingGear.SocketedStat(item, stat));
    }
}
