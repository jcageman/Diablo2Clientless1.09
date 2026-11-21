using ConsoleBot.Attack;
using D2NG.Core.D2GS;
using D2NG.Navigation.Services.MapApi;

namespace ConsoleBot.Tests;

public class BlessedHammerSpiralTests
{
    private static readonly Point Origin = new(1000, 1000);

    private static bool Everywhere(Point _) => true;

    /// <summary>
    /// The Chaos Sanctuary entrance corridor where the paladin hammered for nothing on 16 Nov 2026, from the map server:
    /// x 7780 on, y 5520 on, '#' a wall.
    /// </summary>
    private static readonly string[] CorridorRows =
    [
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######.................#############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######..................############",
        "######.................#############",
    ];

    private static AreaMap Corridor() => new()
    {
        LevelOrigin = new Point(7780, 5520),
        Map = CorridorRows.Select(row => row.Select(c => c == '#' ? 1 : 0).ToArray()).ToArray(),
    };

    [Fact]
    public void TheSpiralHas77CellsEndingFourteenOut()
    {
        var path = BlessedHammerSpiral.Path(Origin);

        Assert.Equal(77, path.Count);
        Assert.InRange(path[^1].Distance(Origin), 12, 15);
    }

    [Fact]
    public void ItTurnsClockwiseFromEastToSouth()
    {
        // Y grows downwards on the map: a quarter turn on from east is the cells below the origin.
        var quarter = BlessedHammerSpiral.Path(Origin).First(cell => cell.Distance(Origin) >= 1.5 && cell.X <= Origin.X);
        Assert.True(quarter.Y > Origin.Y);
    }

    [Fact]
    public void AMonsterOnTheSpiralInTheOpenIsHit()
    {
        var onPath = BlessedHammerSpiral.Path(Origin)[40];

        Assert.Equal(1, BlessedHammerSpiral.Hits(Origin, [onPath], Everywhere));
    }

    [Fact]
    public void AWallSixEastSwallowsTheHammersBeforeTheKnight()
    {
        var open = BlessedHammerSpiral.OpenOn(Corridor());

        Assert.Equal(0, BlessedHammerSpiral.Hits(new Point(7798, 5539), [new Point(7802, 5533)], open));
    }

    [Fact]
    public void TheBestSpotInTheCorridorReachesTheKnight()
    {
        var open = BlessedHammerSpiral.OpenOn(Corridor());
        var knight = new Point(7802, 5533);
        var paladin = new Point(7798, 5539);

        var spot = BlessedHammerSpiral.BestSpot(paladin, [knight], open);

        Assert.NotEqual(paladin, spot);
        Assert.True(spot.Distance(paladin) <= 6.5);
        Assert.Equal(1, BlessedHammerSpiral.Hits(spot, [knight], open));
    }

    [Fact]
    public void StandingStillWinsWhenNothingDoesBetter()
    {
        var onPath = BlessedHammerSpiral.Path(Origin)[30];

        Assert.Equal(Origin, BlessedHammerSpiral.BestSpot(Origin, [onPath], Everywhere));
    }
}
