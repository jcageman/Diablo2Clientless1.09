using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS;

namespace ConsoleBot.Tests;

public class BehindSpotTests
{
    private static readonly Point Leader = new(1000, 1000);

    [Fact]
    public void SevenBehindTheLeaderAwayFromThePack()
    {
        var spot = Party.BehindSpot(Leader, [new Point(1010, 1000), new Point(1010, 1002), new Point(1010, 998)], []);

        Assert.Equal(new Point(993, 1000), spot);
    }

    [Fact]
    public void FurtherBackWhileABossIsNearerThanTheMemberKeeps()
    {
        // The paladin hammering Diablo three away: seven behind him is ten from Diablo.
        var diablo = new Point(1003, 1000);

        var spot = Party.BehindSpot(Leader, [diablo], [(diablo, 12)]);

        Assert.Equal(new Point(989, 1000), spot);
        Assert.True(spot.Distance(diablo) >= 14);
    }

    [Fact]
    public void TheAmazonStandsFurtherBackThanTheSorceress()
    {
        var diablo = new Point(1003, 1000);

        var sorceress = Party.BehindSpot(Leader, [diablo], [(diablo, 12)]);
        var amazon = Party.BehindSpot(Leader, [diablo], [(diablo, 20)]);

        Assert.True(amazon.Distance(diablo) >= 22);
        Assert.True(amazon.Distance(diablo) > sorceress.Distance(diablo));
    }

    [Fact]
    public void NoneWhenNothingIsNear()
    {
        Assert.Null(Party.BehindSpot(Leader, [], []));
    }

    [Fact]
    public void NoneWhenNoSpotBehindIsClearOfTheBoss()
    {
        var boss = new Point(990, 1000);

        Assert.Null(Party.BehindSpot(Leader, [new Point(1005, 1000)], [(boss, 60)]));
    }
}
