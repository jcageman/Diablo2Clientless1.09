using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Players;
using D2NG.Core.SkillData;
using System;
using Xunit;

namespace ConsoleBot.Tests;

public class ShoutDurationsTests
{
    // Parameters as the 1.09 patch's skills.bin carries them.
    private static readonly SkillEntry Shout = new() { Id = 138, Params = [100, 10, 4000, 250, 0, 0] };
    private static readonly SkillEntry BattleOrders = new() { Id = 149, Params = [4000, 250, 35, 3, 0, 0] };

    [Theory]
    [InlineData(1, 160)]
    [InlineData(20, 350)]
    public void ShoutLastsFromItsThirdAndFourthParameter(int level, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ShoutDurations.For(Shout, Skill.Shout, level));
    }

    [Theory]
    [InlineData(1, 160)]
    [InlineData(25, 400)]
    public void BattleOrdersLastsFromItsFirstAndSecondParameter(int level, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ShoutDurations.For(BattleOrders, Skill.BattleOrders, level));
    }
}
