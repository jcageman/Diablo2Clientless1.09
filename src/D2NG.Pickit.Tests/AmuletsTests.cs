using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class AmuletsTests
{
    [Fact]
    public void Keep_RareFcrSkillAmulet()
    {
        var item = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.SorceressSkills, 2);
        TestItemFactory.AddStat(item, StatType.FasterCastRate, 10);
        TestItemFactory.AddStat(item, StatType.FireResistance, 30);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 25);
        TestItemFactory.AddStat(item, StatType.ColdResistance, 25);
        TestItemFactory.AddStat(item, StatType.Mana, 80);
        TestItemFactory.AddStat(item, StatType.Life, 50);
        TestItemFactory.AddStat(item, StatType.Strength, 10);
        TestItemFactory.AddStat(item, StatType.Dexterity, 25);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    [Fact]
    public void Reject_RareFcrSkillAmulet_TooLowFcr()
    {
        var item = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.SorceressSkills, 1);
        TestItemFactory.AddStat(item, StatType.FasterCastRate, 8);
        TestItemFactory.AddStat(item, StatType.FireResistance, 10);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 10);
        TestItemFactory.AddStat(item, StatType.ColdResistance, 10);
        TestItemFactory.AddStat(item, StatType.Mana, 80);
        TestItemFactory.AddStat(item, StatType.Life, 0);
        TestItemFactory.AddStat(item, StatType.Strength, 10);
        TestItemFactory.AddStat(item, StatType.Dexterity, 25);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    // Mara's Kaleidoscope carries +2 to ALL skills, not amazon skills - see unique_items.txt. The
    // rule used to test amazonskills, which no unique amulet in 1.09 has, so it never fired.
    [Fact]
    public void Keep_UniqueMaraKaleidoscope()
    {
        var item = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.AllSkills, 2);
        TestItemFactory.AddStat(item, StatType.FireResistance, 20);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 20);
        TestItemFactory.AddStat(item, StatType.ColdResistance, 20);
        TestItemFactory.AddStat(item, StatType.Life, 20);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    // Highlord's Wrath: +1 all skills and 20 IAS. Same wrong-stat problem as Mara's above.
    [Fact]
    public void Keep_UniqueHighlordsWrath()
    {
        var item = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.AllSkills, 1);
        TestItemFactory.AddStat(item, StatType.IncreasedAttackSpeed, 20);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 35);

        Assert.True(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }

    // No unique amulet in 1.09 has amazon skills, so that must not be what these rules key on.
    [Fact]
    public void Reject_UniqueAmuletWithAmazonSkillsOnly()
    {
        var item = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Unique);
        TestItemFactory.AddStat(item, StatType.AmazonSkills, 2);
        TestItemFactory.AddStat(item, StatType.FireResistance, 20);
        TestItemFactory.AddStat(item, StatType.LightningResistance, 20);
        TestItemFactory.AddStat(item, StatType.ColdResistance, 20);
        TestItemFactory.AddStat(item, StatType.Life, 20);

        Assert.False(Pickit.ShouldKeepItem(characterClass: CharacterClass.Sorceress, item));
    }
}
