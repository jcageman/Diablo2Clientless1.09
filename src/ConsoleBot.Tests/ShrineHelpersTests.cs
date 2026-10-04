using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.ObjectData;
using System.Collections.Generic;

namespace ConsoleBot.Tests;

/// <summary>
/// The experience shrine goes to whoever gains the most from it, which is the highest level
/// character in the game at the time. The choice is made once, when the seals are done, from the
/// levels the clients report for themselves.
/// </summary>
public class ShrineHelpersTests
{
    [Fact]
    public void TheHighestLevelCharacterIsPicked()
    {
        var taker = ShrineHelpers.PickHighestLevel([("Sorc", 84), ("Barb", 91), ("Pally", 88)]);

        Assert.Equal("Barb", taker);
    }

    [Fact]
    public void TiesGoToTheFirstListed()
    {
        Assert.Equal("Sorc", ShrineHelpers.PickHighestLevel([("Sorc", 90), ("Barb", 90)]));
    }

    [Fact]
    public void NobodyInTheGameMeansNoTaker()
    {
        Assert.Null(ShrineHelpers.PickHighestLevel(new List<(string, int)>()));
        Assert.Null(ShrineHelpers.PickHighestLevel([((string)null, 99)]));
    }

    [Fact]
    public void TheShrineCodeOnTheWireIsTheShrinesTxtCode()
    {
        // Captured from the realm with a real client: the last byte of the assign object packet for
        // shrine objects, against what the character then got.
        Assert.Equal(ShrineType.HealthBoost, (ShrineType)2);
        Assert.Equal(ShrineType.ManaBoost, (ShrineType)3);
        Assert.Equal(ShrineType.SkillBoost, (ShrineType)12);
        Assert.Equal(ShrineType.RechargeBoost, (ShrineType)13);
        Assert.Equal(ShrineType.StaminaBoost, (ShrineType)14);
        Assert.Equal(ShrineType.ExperienceBoost, (ShrineType)15);
        Assert.Equal(ShrineType.Exploding, (ShrineType)21);
    }

    [Fact]
    public void TheShrineBuffsMatchTheStateIdsTheServerSends()
    {
        // Same capture: taking a skill shrine put state 134 on the character, recharge 135, stamina
        // 136, experience 137. The enum used to be two short before this block (golem_mastery and
        // skilldelay were missing), which made ShrineExperience the stamina state.
        Assert.Equal(EntityEffect.ShrineSkill, (EntityEffect)134);
        Assert.Equal(EntityEffect.ShrineManaregen, (EntityEffect)135);
        Assert.Equal(EntityEffect.ShrineStamina, (EntityEffect)136);
        Assert.Equal(EntityEffect.ShrineExperience, (EntityEffect)137);
    }
}
