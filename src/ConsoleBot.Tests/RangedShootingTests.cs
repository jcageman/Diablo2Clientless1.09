using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS;

namespace ConsoleBot.Tests;

public class RangedShootingTests
{
    private static readonly Point Leader = new(1000, 1000);

    private static readonly List<Point> Pack = [new Point(1005, 1000), new Point(1006, 1002), new Point(1006, 998)];

    [Fact]
    public void StaysWhereItStandsBehindTheLeaderOutOfReach()
    {
        Assert.False(Party.MustStepBehind(new Point(988, 1003), Leader, Pack, []));
    }

    [Fact]
    public void StaysOffToTheSideWhenNoFurtherForwardThanTheLeader()
    {
        Assert.False(Party.MustStepBehind(new Point(996, 1012), Leader, Pack, []));
    }

    [Fact]
    public void StepsBackWithAMonsterOnIt()
    {
        Assert.True(Party.MustStepBehind(new Point(988, 1000), Leader, [.. Pack, new Point(992, 1000)], []));
    }

    [Fact]
    public void StepsBackStandingNearerThePackThanTheLeader()
    {
        List<Point> farPack = [new Point(1020, 1000), new Point(1021, 1002), new Point(1021, 998)];

        Assert.True(Party.MustStepBehind(new Point(1012, 1010), Leader, farPack, []));
        Assert.False(Party.MustStepBehind(new Point(996, 1004), Leader, farPack, []));
    }

    [Fact]
    public void StepsBackFromABossNearerThanItKeeps()
    {
        var diablo = new Point(1003, 1000);

        Assert.True(Party.MustStepBehind(new Point(988, 1000), Leader, [diablo], [(diablo, 20)]));
        Assert.False(Party.MustStepBehind(new Point(982, 1000), Leader, [diablo], [(diablo, 20)]));
    }

    [Fact]
    public void NothingNearNeedsNoStep()
    {
        Assert.False(Party.MustStepBehind(new Point(1004, 1012), Leader, [], []));
    }

    [Fact]
    public void FourAroundTheTargetAreAPack()
    {
        var target = new Point(1010, 1000);

        Assert.True(LevelingCombat.IsPack(target, [target, new Point(1014, 1000), new Point(1010, 1006), new Point(1005, 1003)]));
    }

    [Fact]
    public void ThreeOrAStragglerAreNoPack()
    {
        var target = new Point(1010, 1000);

        Assert.False(LevelingCombat.IsPack(target, [target, new Point(1014, 1000), new Point(1010, 1006)]));
        Assert.False(LevelingCombat.IsPack(target, [target, new Point(1014, 1000), new Point(1010, 1006), new Point(1030, 1000)]));
    }
}
