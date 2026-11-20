using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS;

namespace ConsoleBot.Tests;

public class FallBackSpotTests
{
    private static readonly Point Leader = new(1000, 1000);

    /// <summary>The pack fifteen east of the leader.</summary>
    private static readonly Point Pack = new(1015, 1000);

    [Fact]
    public void TakesTheLatestSpotBehindTheLeader()
    {
        var spot = Party.AwayFromPack([new Point(960, 1000), new Point(975, 1000)], Pack, Leader);

        Assert.Equal(new Point(975, 1000), spot);
    }

    [Fact]
    public void PassesOverASpotOnThePacksFarSide()
    {
        // Cleared before a retreat: 25 from the pack, but the way there runs through it.
        var spot = Party.AwayFromPack([new Point(970, 1000), new Point(1040, 1000)], Pack, Leader);

        Assert.Equal(new Point(970, 1000), spot);
    }

    [Fact]
    public void PassesOverASpotPastThePacksFlank()
    {
        // Far enough from the pack, but the way there leads the party along its edge.
        var spot = Party.AwayFromPack([new Point(970, 1000), new Point(1025, 1026)], Pack, Leader);

        Assert.Equal(new Point(970, 1000), spot);
    }

    [Fact]
    public void TakesASpotOffToTheSide()
    {
        var spot = Party.AwayFromPack([new Point(1000, 1030)], Pack, Leader);

        Assert.Equal(new Point(1000, 1030), spot);
    }

    [Fact]
    public void NoneWhenEverySpotLeadsTowardsThePack()
    {
        Assert.Null(Party.AwayFromPack([new Point(1040, 1000), new Point(1030, 1020)], Pack, Leader));
    }

    [Fact]
    public void NoneWhenTheLeaderStandsOnIt()
    {
        Assert.Null(Party.AwayFromPack([new Point(997, 1000)], new Point(1027, 1000), Leader));
    }
}
