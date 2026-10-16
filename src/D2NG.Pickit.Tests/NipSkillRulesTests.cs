using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;

namespace D2NG.Pickit.Tests;

/// <summary>
/// The charge and chance-to-cast rules in Amulets.nip and Weapons.nip.
/// </summary>
public class NipSkillRulesTests
{
    private static Item TeleportAmulet(QualityType quality)
    {
        var item = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, quality);
        item.Properties[StatType.Charged] = new ItemProperty { Type = StatType.Charged, Skill = Skill.Teleport, SkillLevel = 3, Charges = 25, MaximumCharges = 27 };
        return item;
    }

    [Fact]
    public void Keep_TeleportChargesAmulet_WithClassSkills_EvenWhenPlainOnesAreFull()
    {
        var item = TeleportAmulet(QualityType.Rare);
        TestItemFactory.AddStat(item, StatType.BarbarianSkills, 2);
        var held = Enumerable.Range(0, 10).Select(_ => TeleportAmulet(QualityType.Magical)).ToList();

        Assert.True(Pickit.ShouldKeepItem(item, held));
    }

    [Fact]
    public void Keep_PlainTeleportChargesAmulet_UntilTenAreHeld()
    {
        var held = Enumerable.Range(0, 9).Select(_ => TeleportAmulet(QualityType.Magical)).ToList();

        Assert.True(Pickit.ShouldKeepItem(TeleportAmulet(QualityType.Magical), held));
        held.Add(TeleportAmulet(QualityType.Magical));
        Assert.False(Pickit.ShouldKeepItem(TeleportAmulet(QualityType.Magical), held));
    }

    [Theory]
    [InlineData(185, true)]
    [InlineData(100, false)]
    public void MagicWeapon_AmplifyDamageOnStriking_NeedsHighEd(int ed, bool expected)
    {
        var knife = TestItemFactory.Create(ClassificationType.Dagger, ItemName.FangedKnife, QualityType.Magical);
        TestItemFactory.AddStat(knife, StatType.EnhancedDamage, ed);
        knife.Properties[StatType.SkillOnHit] = new ItemProperty { Type = StatType.SkillOnHit, Skill = Skill.AmplifyDamage, SkillLevel = 1, SkillChance = 5 };

        Assert.Equal(expected, Pickit.ShouldKeepItem(knife, []));
    }
}
