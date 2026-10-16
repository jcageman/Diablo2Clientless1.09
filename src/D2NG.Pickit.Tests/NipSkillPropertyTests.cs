using System.IO;
using System.Linq;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit.Tests;

/// <summary>
/// Skills that come with an item rather than as +levels: charges ("Level 3 Teleport (25/27
/// Charges)") and chance to cast ("5% Chance to Cast Level 1 Amplify Damage On Striking").
/// </summary>
public class NipSkillPropertyTests
{
    private static bool Keeps(string line, Item item)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, line);
            var rule = NipParser.ParseFile(path).Single();
            return rule.MatchesKeep(new NipEvaluationContext(item));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Item Amulet()
    {
        return TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Rare);
    }

    private static Item WithCharges(Item item, Skill skill, int level)
    {
        item.Properties[StatType.Charged] = new ItemProperty { Type = StatType.Charged, Skill = skill, SkillLevel = level, Charges = 20, MaximumCharges = 25 };
        return item;
    }

    private static Item WithChanceToCast(Item item, StatType trigger, Skill skill, int chance)
    {
        item.Properties[trigger] = new ItemProperty { Type = trigger, Skill = skill, SkillLevel = 1, SkillChance = chance };
        return item;
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(1, false)]
    public void ChargesReadTheChargedSkillLevel(int level, bool expected)
    {
        Assert.Equal(expected, Keeps("[type] == amulet # [chargesteleport] >= 2", WithCharges(Amulet(), Skill.Teleport, level)));
    }

    [Fact]
    public void ChargesOfAnotherSkillDoNotCount()
    {
        Assert.False(Keeps("[type] == amulet # [chargesteleport] >= 1", WithCharges(Amulet(), Skill.AmplifyDamage, 3)));
    }

    [Theory]
    [InlineData(StatType.SkillOnHit)]
    [InlineData(StatType.SkillOnStriking)]
    public void ChanceToCastReadsTheChance_OnAttackOrStriking(StatType trigger)
    {
        var knife = WithChanceToCast(Amulet(), trigger, Skill.AmplifyDamage, 5);

        Assert.True(Keeps("[type] == amulet # [ctcamplifydamage] >= 5", knife));
    }

    [Fact]
    public void ChanceToCast_WhenStruckIsSeparate()
    {
        var item = WithChanceToCast(Amulet(), StatType.SkillWhenStruck1, Skill.Teleport, 10);

        Assert.False(Keeps("[type] == amulet # [ctcteleport] >= 1", item));
        Assert.True(Keeps("[type] == amulet # [whenstruckteleport] >= 10", item));
    }

    [Fact]
    public void KolbotItemChargedSkill_ComparesTheSkillId()
    {
        Assert.True(Keeps("[type] == amulet # [itemchargedskill] == 54", WithCharges(Amulet(), Skill.Teleport, 3)));
    }

    [Fact]
    public void KolbotItemSkillOnHit_ComparesTheSkillId()
    {
        var item = WithChanceToCast(Amulet(), StatType.SkillOnHit, Skill.AmplifyDamage, 5);

        Assert.True(Keeps("[type] == amulet # [itemskillonhit] == 66", item));
    }
}
