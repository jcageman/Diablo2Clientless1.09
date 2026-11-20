using D2NG.Leveling.Exploration;

namespace D2NG.Leveling.Tests;

public class ExplorationTourTests
{
    private static bool[][] Map(params string[] rows) => rows.Select(row => row.Select(cell => cell == '.').ToArray()).ToArray();

    [Fact]
    public void EveryCellWithReachableGroundGetsAStop()
    {
        var map = Map(
            "........",
            "........",
            "........",
            "........");

        var tour = ExplorationTour.Plan(map, (0, 0), 4);

        Assert.Equal(2, tour.Count);
    }

    [Fact]
    public void GroundBehindAWallIsLeftOut()
    {
        var map = Map(
            "....#...",
            "....#...",
            "....#...",
            "....#...");

        var tour = ExplorationTour.Plan(map, (0, 0), 4);

        Assert.All(tour, stop => Assert.True(stop.Column < 4));
    }

    [Fact]
    public void TheTourVisitsTheNearestStopNext()
    {
        var map = Map(
            "............",
            "............",
            "............");

        var tour = ExplorationTour.Plan(map, (11, 1), 3);

        Assert.Equal(tour.OrderByDescending(stop => stop.Column), tour);
    }

    [Fact]
    public void TheNextStopIsNearestOnFootNotThroughAWall()
    {
        // From the top-left the stop just below, behind the wall, is close in a straight line but a long
        // walk round; the stop to the right along the open row is the one to take first.
        var map = Map(
            "........",
            "######..",
            "........");

        var tour = ExplorationTour.Plan(map, (0, 0), 2);

        Assert.Equal(0, tour[0].Row);
        Assert.True(tour.FindIndex(stop => stop.Row == 2 && stop.Column < 2) > tour.FindIndex(stop => stop.Row == 0 && stop.Column >= 6));
    }

    [Fact]
    public void AStartOnUnwalkableGroundUsesTheNearestWalkableTile()
    {
        var map = Map(
            "#.......",
            "........");

        Assert.NotEmpty(ExplorationTour.Plan(map, (0, 0), 4));
    }
}
