using ConsoleBot.Helpers;

namespace ConsoleBot.Tests;

public class SweepRouteTests
{
    /// <summary>A fully walkable grid of the given size, indexed as <c>[y][x]</c>.</summary>
    private static bool[][] OpenField(int width, int height)
    {
        var grid = new bool[height][];
        for (var y = 0; y < height; y++)
        {
            grid[y] = new bool[width];
            for (var x = 0; x < width; x++)
            {
                grid[y][x] = true;
            }
        }

        return grid;
    }

    [Fact]
    public void Consecutive_bands_run_in_opposite_directions()
    {
        var route = SweepRoute.Build(OpenField(100, 100), band: 25, step: 25);

        var firstBand = route.Where(p => p.Y < 25).ToList();
        var secondBand = route.Where(p => p.Y >= 25 && p.Y < 50).ToList();

        Assert.Equal(firstBand.OrderBy(p => p.X), firstBand);
        Assert.Equal(secondBand.OrderByDescending(p => p.X), secondBand);
    }

    [Fact]
    public void The_route_covers_every_band_of_the_level()
    {
        var route = SweepRoute.Build(OpenField(100, 100), band: 25, step: 25);

        Assert.Equal(4, route.Select(p => p.Y / 25).Distinct().Count());
    }

    [Fact]
    public void Consecutive_waypoints_stay_within_a_step_of_each_other()
    {
        var route = SweepRoute.Build(OpenField(200, 200), band: 40, step: 40);

        // A route that jumped across the level between waypoints would defeat the point of
        // sweeping: the party has to be able to walk from one to the next.
        for (var i = 1; i < route.Count; i++)
        {
            var dx = Math.Abs(route[i].X - route[i - 1].X);
            var dy = Math.Abs(route[i].Y - route[i - 1].Y);
            Assert.True(dx <= 40 && dy <= 40, $"waypoint {i} jumped {dx},{dy}");
        }
    }

    [Fact]
    public void Blocked_stretches_produce_no_waypoints()
    {
        var grid = OpenField(100, 100);
        for (var y = 0; y < 100; y++)
        {
            for (var x = 25; x < 75; x++)
            {
                grid[y][x] = false;
            }
        }

        var route = SweepRoute.Build(grid, band: 25, step: 25);

        Assert.DoesNotContain(route, p => p.X >= 25 && p.X < 75);
        Assert.NotEmpty(route);
    }

    [Fact]
    public void A_level_with_nowhere_to_walk_has_no_route()
    {
        var grid = OpenField(50, 50);
        for (var y = 0; y < 50; y++)
        {
            for (var x = 0; x < 50; x++)
            {
                grid[y][x] = false;
            }
        }

        Assert.Empty(SweepRoute.Build(grid, band: 25, step: 25));
    }

    [Fact]
    public void Waypoints_land_on_walkable_ground()
    {
        var grid = OpenField(100, 100);
        // Only a narrow diagonal corridor is open.
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                grid[y][x] = Math.Abs(x - y) < 5;
            }
        }

        var route = SweepRoute.Build(grid, band: 20, step: 20);

        Assert.NotEmpty(route);
        Assert.All(route, p => Assert.True(grid[p.Y][p.X]));
    }

    [Fact]
    public void Nonsense_input_yields_an_empty_route()
    {
        Assert.Empty(SweepRoute.Build(null!, 10, 10));
        Assert.Empty(SweepRoute.Build([], 10, 10));
        Assert.Empty(SweepRoute.Build(OpenField(10, 10), 0, 10));
        Assert.Empty(SweepRoute.Build(OpenField(10, 10), 10, 0));
    }
}
