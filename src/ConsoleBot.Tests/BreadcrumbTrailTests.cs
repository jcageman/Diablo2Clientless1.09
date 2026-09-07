using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class BreadcrumbTrailTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    [Fact]
    public void An_empty_trail_offers_nowhere_to_retreat_to()
    {
        var trail = new BreadcrumbTrail();

        Assert.Null(trail.FindRetreatPoint(At(100, 100), 50));
    }

    [Fact]
    public void Retreating_picks_the_most_recent_point_far_enough_back()
    {
        var trail = new BreadcrumbTrail();
        trail.Record(At(100, 100));
        trail.Record(At(200, 100));
        trail.Record(At(280, 100));

        Assert.Equal(At(200, 100), trail.FindRetreatPoint(At(300, 100), 50));
    }

    [Fact]
    public void A_party_that_has_not_travelled_far_enough_has_nowhere_to_go()
    {
        var trail = new BreadcrumbTrail();
        trail.Record(At(100, 100));
        trail.Record(At(110, 100));

        Assert.Null(trail.FindRetreatPoint(At(120, 100), 50));
    }

    [Fact]
    public void Recording_the_same_spot_twice_does_not_grow_the_trail()
    {
        var trail = new BreadcrumbTrail();
        trail.Record(At(100, 100));
        trail.Record(At(100, 100));

        Assert.Equal(1, trail.Count);
    }

    [Fact]
    public void The_trail_forgets_the_oldest_points_past_its_capacity()
    {
        var trail = new BreadcrumbTrail();
        for (var i = 0; i < BreadcrumbTrail.Capacity + 10; i++)
        {
            trail.Record(At(100 + i, 100));
        }

        Assert.Equal(BreadcrumbTrail.Capacity, trail.Count);
        // The first points walked are gone, so the furthest retreat is bounded by the trail length.
        Assert.Null(trail.FindRetreatPoint(At(100 + BreadcrumbTrail.Capacity + 9, 100), BreadcrumbTrail.Capacity + 5));
    }

    [Fact]
    public void Clearing_drops_the_whole_trail_between_games()
    {
        var trail = new BreadcrumbTrail();
        trail.Record(At(100, 100));
        trail.Record(At(200, 100));

        trail.Clear();

        Assert.Equal(0, trail.Count);
        Assert.Null(trail.FindRetreatPoint(At(300, 100), 50));
    }
}
