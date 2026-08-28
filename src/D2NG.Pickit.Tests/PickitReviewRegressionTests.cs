using System;
using System.IO;
using System.Linq;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit.Tests;

/// <summary>
/// Regression cover for the nip rules corrected during the review against the original
/// ConsoleBot/Pickit C# rules. Each test names the C# method it is protecting.
/// </summary>
public class PickitReviewRegressionTests
{
    private static readonly string ClassicNipDirectory = Path.Combine(AppContext.BaseDirectory, "Nips", "Classic");

    // Pickit.Configure points at the Expansion tree, so classic rules are evaluated directly.
    private static bool ClassicKeeps(Item item, CharacterClass characterClass)
    {
        var context = new NipEvaluationContext(item, characterClass);
        return NipParser.ParseDirectory(ClassicNipDirectory).Any(rule => rule.MatchesKeep(context));
    }

    // ---------- Expansion ----------

    [Fact]
    public void Keep_UniqueGoreRider() // Boots.ShouldKeepItemExpansion: case BattleBoots => true
    {
        var item = TestItemFactory.Create(ClassificationType.Boots, ItemName.BattleBoots, QualityType.Unique);

        Assert.True(Pickit.ShouldKeepItem(CharacterClass.Barbarian, item));
    }

    [Fact]
    public void Keep_EtherealUniqueWarBoots() // Boots.ShouldKeepItemExpansion: case WarBoots => item.Ethereal
    {
        var item = TestItemFactory.Create(ClassificationType.Boots, ItemName.WarBoots, QualityType.Unique, ethereal: true);

        Assert.True(Pickit.ShouldKeepItem(CharacterClass.Barbarian, item));
    }

    [Fact]
    public void Reject_NonEtherealUniqueWarBoots()
    {
        var item = TestItemFactory.Create(ClassificationType.Boots, ItemName.WarBoots, QualityType.Unique);

        Assert.False(Pickit.ShouldKeepItem(CharacterClass.Barbarian, item));
    }

    [Fact]
    public void Keep_SlayerGuard() // Helms.ShouldKeepItemExpansion - Slayer Guard is a BarbarianHelm, not a Helm
    {
        var item = TestItemFactory.Create(ClassificationType.BarbarianHelm, ItemName.SlayerGuard, QualityType.Unique);
        TestItemFactory.AddSkillTab(item, SkillTab.BarbarianCombatSkills, 1);

        Assert.True(Pickit.ShouldKeepItem(CharacterClass.Barbarian, item));
    }

    [Fact]
    public void Keep_BarbarianHelm_BattleOrders() // Helms.ShouldKeepItemExpansion barb skills + warcries + BO >= 5
    {
        var item = TestItemFactory.Create(ClassificationType.BarbarianHelm, ItemName.SlayerGuard, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.BarbarianSkills, 2);
        TestItemFactory.AddSkillTab(item, SkillTab.BarbarianWarcries, 2);
        TestItemFactory.AddSingleSkill(item, Skill.BattleOrders, 1);

        Assert.True(Pickit.ShouldKeepItem(CharacterClass.Barbarian, item));
    }

    [Fact]
    public void Keep_PerfectSkullGem() // Gems.ShouldKeepItemExpansion
    {
        var item = TestItemFactory.Create(ClassificationType.Gem, ItemName.PerfectSkull, QualityType.Normal);

        Assert.True(Pickit.ShouldKeepItem(CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Keep_Amulet_LeechDamageDexterity() // Amulets.ShouldKeepItemClassic, reached via ShouldKeepItemExpansion
    {
        var item = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.MinimumLifeStolenPerHit, 5);
        TestItemFactory.AddStat(item, StatType.MinimumDamage, 7);
        TestItemFactory.AddStat(item, StatType.Dexterity, 10);

        Assert.True(Pickit.ShouldKeepItem(CharacterClass.Barbarian, item));
    }

    // ---------- Classic ----------

    [Fact]
    public void Classic_Reject_DesirableHelmBaseWithOnlyLife()
    {
        // "desirableHelms.Contains(name) && Life >= 30" is the gate around seven sub-conditions
        // in Helms.ShouldKeepItemClassic, never a keep rule on its own.
        var item = TestItemFactory.Create(ClassificationType.Helm, ItemName.Cap, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.Life, 30);

        Assert.False(ClassicKeeps(item, CharacterClass.Barbarian));
    }

    [Fact]
    public void Classic_Keep_PaladinScepter_BlessedHammerConcentration()
    {
        // Pickit.cs routes Scepter to the Staves rules; [type] == staff alone never matched one.
        var item = TestItemFactory.Create(ClassificationType.Scepter, ItemName.Scepter, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.PaladinSkills, 2);
        TestItemFactory.AddSingleSkill(item, Skill.BlessedHammer, 2);
        TestItemFactory.AddSingleSkill(item, Skill.Concentration, 2, StatType.SingleSkill2);

        Assert.True(ClassicKeeps(item, CharacterClass.Paladin));
    }

    [Fact]
    public void Classic_Reject_UniqueRing_CannotBeFrozen()
    {
        // The "cannot be frozen" unique-ring rule is Rings.ShouldKeepItemExpansion only.
        var item = TestItemFactory.Create(ClassificationType.Ring, ItemName.Ring, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.CannotBeFrozen, 1);

        Assert.False(ClassicKeeps(item, CharacterClass.Barbarian));
    }

    [Fact]
    public void Classic_Reject_MagicFourSocketArmor()
    {
        // The magic 4os light armour rule is Armors.ShouldKeepItemExpansion only.
        var item = TestItemFactory.Create(ClassificationType.Armor, ItemName.MagePlate, QualityType.Magical, sockets: 4);
        TestItemFactory.AddStat(item, StatType.Life, 40);

        Assert.False(ClassicKeeps(item, CharacterClass.Sorceress));
    }

    [Fact]
    public void Classic_Keep_FlawlessSkull()
    {
        var item = TestItemFactory.Create(ClassificationType.Gem, ItemName.FlawlessSkull, QualityType.Normal);

        Assert.True(ClassicKeeps(item, CharacterClass.Barbarian));
    }

    [Fact]
    public void Classic_Reject_PerfectRuby()
    {
        // Gems.ShouldKeepItemClassic keeps skulls only; every other gem branch is commented out.
        var item = TestItemFactory.Create(ClassificationType.Gem, ItemName.PerfectRuby, QualityType.Normal);

        Assert.False(ClassicKeeps(item, CharacterClass.Barbarian));
    }
}
