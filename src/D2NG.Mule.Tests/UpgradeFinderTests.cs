using D2NG.Core.ItemData;
using D2NG.Mule;
using D2NG.Mule.Models;
using D2NG.Mule.Upgrades;

namespace D2NG.Mule.Tests;

public class UpgradeFinderTests
{
    private static readonly GearTables Tables = new(new ItemAffixTable
    {
        BaseItems =
        [
            new BaseItemEntry { Kind = BaseItemKind.Armor, Code = "rin", Name = "Ring", Type = "ring" },
            new BaseItemEntry { Kind = BaseItemKind.Armor, Code = "lbt", Name = "Boots", Type = "boot" },
            new BaseItemEntry { Kind = BaseItemKind.Armor, Code = "tow", Name = "Tower Shield", Type = "shie", RequiredStrength = 75, Block = 24 },
            new BaseItemEntry { Kind = BaseItemKind.Weapon, Code = "gsd", Name = "Great Sword", Type = "swor", RequiredStrength = 100, RequiredDexterity = 60, MinDamage = 25, MaxDamage = 42, TwoHandMinDamage = 50, TwoHandMaxDamage = 84 },
            new BaseItemEntry { Kind = BaseItemKind.Weapon, Code = "mau", Name = "Maul", Type = "hamm", RequiredStrength = 69, TwoHandMinDamage = 30, TwoHandMaxDamage = 43 },
            new BaseItemEntry { Kind = BaseItemKind.Weapon, Code = "gma", Name = "Great Maul", Type = "hamm", RequiredStrength = 99, TwoHandMinDamage = 38, TwoHandMaxDamage = 58 },
            new BaseItemEntry { Kind = BaseItemKind.Weapon, Code = "whm", Name = "War Hammer", Type = "hamm", RequiredStrength = 53, MinDamage = 19, MaxDamage = 29 },
            new BaseItemEntry { Kind = BaseItemKind.Weapon, Code = "lbw", Name = "Long Battle Bow", Type = "bow", RequiredStrength = 40, RequiredDexterity = 50, TwoHandMinDamage = 3, TwoHandMaxDamage = 18 },
        ],
    });

    private static MuleItemDb Item(string name, string type, string code, params (string Stat, int Value)[] stats) => new()
    {
        Id = name,
        AccountName = "mule-account",
        CharacterName = "mule",
        ItemName = name,
        BaseCode = code,
        QualityType = "Rare",
        ClassificationType = type,
        Expansion = false,
        Stats = [.. stats.Select(s => new StatDb { Type = s.Stat, Value = s.Value })],
    };

    private static GearCharacterDb Character(string characterClass, int strength = 30, params (string Slot, MuleItemDb Item)[] worn) => new()
    {
        AccountName = "bot-account",
        CharacterName = "bot",
        Class = characterClass,
        Level = 90,
        Strength = strength,
        Dexterity = 50,
        Vitality = 300,
        Energy = 30,
        BaseLife = 900,
        BaseMana = 200,
        Equipped = worn.ToDictionary(w => w.Slot, w => w.Item),
    };

    private static UpgradePreset Preset(string id) => UpgradePresets.Defaults.Single(p => p.Id == id);

    [Theory]
    [InlineData(0, 13)]
    [InlineData(19, 12)]
    [InlineData(20, 11)]
    [InlineData(60, 10)]
    [InlineData(105, 8)]
    public void SorceressCastFramesDropOnlyAtBreakpoints(int fasterCastRate, int frames)
        => Assert.Equal(frames, GearEvaluation.Frames(fasterCastRate, ClassTables.CastBreakpoints["Sorceress"]));

    [Fact]
    public void ClassicHellTakesFiftyAndCapsAtSeventyFive()
    {
        var ring = Item("Ring", "Ring", "rin", ("FireResistance", 140), ("ColdResistance", 60));
        var evaluation = GearEvaluation.Of(Character("Sorceress", worn: (GearSlots.LeftRing, ring)),
            new Dictionary<string, GearItem> { [GearSlots.LeftRing] = GearItem.From(ring, Tables, 90) }, [], Tables, Difficulty.Hell);

        Assert.Equal(new ResistLine("Fire", 90, 75, 75), evaluation.Resists["Fire"]);
        Assert.Equal(10, evaluation.Resists["Cold"].Effective);
        Assert.Equal(15, evaluation.Resists["Fire"].OverCap);
    }

    [Fact]
    public void VitalityAndLifePercentRaiseLife()
    {
        var ring = Item("Ring", "Ring", "rin", ("Vitality", 10), ("Life", 20), ("EnhancedLife", 10));
        var evaluation = GearEvaluation.Of(Character("Barbarian", worn: (GearSlots.LeftRing, ring)),
            new Dictionary<string, GearItem> { [GearSlots.LeftRing] = GearItem.From(ring, Tables, 90) }, [], Tables, Difficulty.Hell);

        Assert.Equal(Math.Floor((900 + 20 + 10 * 4) * 1.1), evaluation.Total("Life"));
    }

    [Fact]
    public void PvpCountsColdOverTheCapAndPvmDoesNot()
    {
        var worn = Item("Worn", "Ring", "rin", ("FireResistance", 125), ("ColdResistance", 125), ("LightningResistance", 125));
        var colder = Item("Colder", "Ring", "rin", ("FireResistance", 125), ("ColdResistance", 165), ("LightningResistance", 125));
        var character = Character("Sorceress", worn: (GearSlots.LeftRing, worn));

        var pvp = UpgradeFinder.Rank(character, GearSlots.LeftRing, [colder], Preset("sorceress-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);
        var pvm = UpgradeFinder.Rank(character, GearSlots.LeftRing, [colder], Preset("sorceress-pvp"), ResistMode.PvM, Difficulty.Hell, Tables);

        Assert.Equal(12, pvp.Candidates[0].Score);
        Assert.Equal(0, pvm.Candidates[0].Score);
    }

    [Fact]
    public void PvpIgnoresPoison()
    {
        var worn = Item("Worn", "Ring", "rin", ("Life", 10));
        var poison = Item("Poison", "Ring", "rin", ("PoisonResistance", 40));
        var result = UpgradeFinder.Rank(Character("Sorceress", worn: (GearSlots.LeftRing, worn)), GearSlots.LeftRing, [poison],
            Preset("sorceress-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);

        Assert.Equal(-10, result.Candidates[0].Score);
    }

    [Fact]
    public void GoldBarbarianRanksExtraGoldAndLeechOverLife()
    {
        var worn = Item("Worn", "Ring", "rin", ("Life", 10));
        var gold = Item("Gold", "Ring", "rin", ("ExtraGold", 40), ("MinimumLifeStolenPerHit", 5));
        var life = Item("Life", "Ring", "rin", ("Life", 40), ("Strength", 10));
        var result = UpgradeFinder.Rank(Character("Barbarian", worn: (GearSlots.LeftRing, worn)), GearSlots.LeftRing, [life, gold],
            Preset("gold-barbarian"), ResistMode.PvM, Difficulty.Hell, Tables);

        Assert.Equal(["Gold", "Life"], result.Candidates.Select(c => c.Item.Id));
        Assert.Equal(100, result.Candidates[0].Score);
    }

    [Fact]
    public void RunWalkOnlyCountsOnBoots()
    {
        var preset = Preset("gold-barbarian");
        var boots = Item("Boots", "Boots", "lbt", ("FasterRunWalk", 30));
        var ring = Item("Ring", "Ring", "rin", ("FasterRunWalk", 30));

        var onBoots = UpgradeFinder.Rank(Character("Barbarian"), GearSlots.Boots, [boots], preset, ResistMode.PvM, Difficulty.Hell, Tables);
        var onRing = UpgradeFinder.Rank(Character("Barbarian"), GearSlots.LeftRing, [ring], preset, ResistMode.PvM, Difficulty.Hell, Tables);

        Assert.Equal(30, onBoots.Candidates[0].Score);
        Assert.Equal(0, onRing.Candidates[0].Score);
    }

    [Fact]
    public void AnItemsOwnStrengthDoesNotLiftItsRequirement()
    {
        var shield = Item("Shield", "Shield", "tow", ("Strength", 20), ("FireResistance", 30));
        var result = UpgradeFinder.Rank(Character("Paladin", strength: 60), GearSlots.LeftHand, [shield], Preset("caster-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);

        Assert.Equal(CandidateStanding.Blocked, result.Candidates[0].Standing);
        Assert.Equal("needs 75 strength, has 60", result.Candidates[0].Reason);
    }

    [Fact]
    public void ReducedRequirementsAndEtherealLowerTheStrengthNeeded()
    {
        var shield = Item("Shield", "Shield", "tow", ("ReducedRequirements", -40), ("FireResistance", 30));
        shield.Ethereal = true;
        var result = UpgradeFinder.Rank(Character("Paladin", strength: 35), GearSlots.LeftHand, [shield], Preset("caster-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);

        Assert.Equal(CandidateStanding.Fits, result.Candidates[0].Standing);
    }

    [Fact]
    public void AShieldWearerIsOfferedNoTwoHandedWeapon()
    {
        var shield = Item("Shield", "Shield", "tow", ("FireResistance", 30));
        var sword = Item("Sword", "Sword", "gsd", ("EnhancedMaximumDamage", 150));
        var bow = Item("Bow", "Bow", "lbw", ("EnhancedMaximumDamage", 150));
        var character = Character("Barbarian", strength: 200, worn: (GearSlots.LeftHand, shield));
        character.Dexterity = 100;

        var result = UpgradeFinder.Rank(character, GearSlots.RightHand, [sword, bow], Preset("barbarian-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);

        Assert.Equal(["Sword"], result.Candidates.Select(c => c.Item.Id));
        Assert.Equal(CandidateStanding.Fits, result.Candidates[0].Standing);
    }

    [Fact]
    public void AWieldedWeaponIsReplacedByOneHeldTheSameWay()
    {
        var maul = Item("Maul", "Hammer", "mau", ("EnhancedMaximumDamage", 100));
        var greatMaul = Item("GreatMaul", "Hammer", "gma", ("EnhancedMaximumDamage", 150));
        var warHammer = Item("WarHammer", "Hammer", "whm", ("EnhancedMaximumDamage", 150));
        var twoHanded = Character("Barbarian", strength: 200, worn: (GearSlots.RightHand, maul));
        twoHanded.Dexterity = 100;
        var oneHanded = Character("Barbarian", strength: 200, worn: (GearSlots.RightHand, warHammer));
        oneHanded.Dexterity = 100;

        var forTwo = UpgradeFinder.Rank(twoHanded, GearSlots.RightHand, [warHammer, greatMaul], Preset("barbarian-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);
        var forOne = UpgradeFinder.Rank(oneHanded, GearSlots.RightHand, [maul, greatMaul], Preset("barbarian-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);

        Assert.Equal(["GreatMaul"], forTwo.Candidates.Select(c => c.Item.Id));
        Assert.Empty(forOne.Candidates);
    }

    [Fact]
    public void AShieldIsReplacedByAShieldOnly()
    {
        var worn = Item("Worn", "Shield", "tow", ("FireResistance", 10));
        var shield = Item("Shield", "Shield", "tow", ("FireResistance", 30));
        var bow = Item("Bow", "Bow", "lbw", ("Dexterity", 30));
        var character = Character("Amazon", strength: 200, worn: (GearSlots.LeftHand, worn));

        var result = UpgradeFinder.Rank(character, GearSlots.LeftHand, [shield, bow], Preset("bow-amazon"), ResistMode.PvP, Difficulty.Hell, Tables);

        Assert.Equal(["Shield"], result.Candidates.Select(c => c.Item.Id));
    }

    [Fact]
    public void CharmsFollowTheLayoutOfSmallsOnTopAndGrandsBelow()
    {
        var carried = Item("Carried", "GrandCharm", "cm3", ("Life", 20));
        var grand = Item("Grand", "GrandCharm", "cm3", ("Life", 40));
        var large = Item("Large", "LargeCharm", "cm2", ("Life", 80));
        var character = Character("Barbarian");
        character.Expansion = true;
        character.Charms = [carried];
        foreach (var item in new[] { carried, grand, large })
        {
            item.Expansion = true;
        }

        var result = UpgradeFinder.Rank(character, GearSlots.Charms, [grand, large], Preset("barbarian-pvp"), ResistMode.PvP, Difficulty.Hell, Tables);

        Assert.Equal(["Grand"], result.Candidates.Select(c => c.Item.Id));
        Assert.Null(result.Candidates[0].Replaces);
    }

    [Theory]
    [InlineData(10, 50, 11)]
    [InlineData(10, 60, 10)]
    [InlineData(0, 60, 10)]
    [InlineData(0, 70, 9)]
    [InlineData(0, 0, 13)]
    public void BowAttackFramesFollowIncreasedAttackSpeedLessTheBaseSpeed(int weaponSpeed, int ias, int frames)
        => Assert.Equal(frames, GearEvaluation.AttackFrames(14, weaponSpeed, ias));

    [Fact]
    public void ASwapThatDropsAGuardedResistRanksOnItsScoreAndCarriesTheWarning()
    {
        var worn = Item("Worn", "Ring", "rin", ("LightningResistance", 30));
        var lifeOnly = Item("LifeOnly", "Ring", "rin", ("Life", 200));
        var keeps = Item("Keeps", "Ring", "rin", ("LightningResistance", 30), ("Life", 5));
        var result = UpgradeFinder.Rank(Character("Sorceress", worn: (GearSlots.LeftRing, worn)), GearSlots.LeftRing, [lifeOnly, keeps],
            Preset("sorceress-pvp"), ResistMode.PvP, Difficulty.Normal, Tables);

        Assert.Equal(["LifeOnly", "Keeps"], result.Candidates.Select(c => c.Item.Id));
        Assert.Equal(CandidateStanding.DropsResist, result.Candidates[0].Standing);
        Assert.Equal("drops lightning below where it stands", result.Candidates[0].Reason);
    }

    [Fact]
    public void ACopyOfTheWornItemIsLeftOut()
    {
        var worn = Item("Worn", "Ring", "rin", ("AllSkills", 1), ("Mana", 20));
        var copy = Item("Copy", "Ring", "rin", ("AllSkills", 1), ("Mana", 20));
        var better = Item("Better", "Ring", "rin", ("AllSkills", 1), ("Mana", 30));
        var result = UpgradeFinder.Rank(Character("Sorceress", worn: (GearSlots.LeftRing, worn)), GearSlots.LeftRing, [copy, better],
            Preset("sorceress-pvp"), ResistMode.PvP, Difficulty.Normal, Tables);

        Assert.Equal(["Better"], result.Candidates.Select(c => c.Item.Id));
    }

    [Fact]
    public void OtherGameModesAndOtherClassesItemsAreLeftOut()
    {
        var expansionRing = Item("Expansion", "Ring", "rin", ("Life", 10));
        expansionRing.Expansion = true;
        var orb = Item("Orb", "SorceressOrb", "rin", ("Life", 10));

        Assert.False(UpgradeFinder.Fits(expansionRing, GearSlots.LeftRing, Character("Sorceress")));
        Assert.False(UpgradeFinder.Fits(orb, GearSlots.RightHand, Character("Barbarian")));
        Assert.True(UpgradeFinder.Fits(orb, GearSlots.RightHand, Character("Sorceress")));
    }
}
