using ConsoleBot.Helpers;

namespace ConsoleBot.Tests;

public class SpatialGridTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    [Fact]
    public void Within_returns_only_entries_inside_the_radius()
    {
        var grid = new SpatialGrid<string>();
        grid.TryAdd(1, At(100, 100), "inside");
        grid.TryAdd(2, At(100, 115), "edge");
        grid.TryAdd(3, At(100, 200), "far");

        var result = grid.Within(At(100, 100), 20);

        Assert.Equal(["inside", "edge"], result);
    }

    [Fact]
    public void Within_orders_nearest_first_and_caps_the_result()
    {
        var grid = new SpatialGrid<string>();
        grid.TryAdd(1, At(130, 100), "far");
        grid.TryAdd(2, At(105, 100), "near");
        grid.TryAdd(3, At(115, 100), "middle");

        var result = grid.Within(At(100, 100), 40, 2);

        Assert.Equal(["near", "middle"], result);
    }

    [Fact]
    public void Within_finds_entries_across_cell_boundaries()
    {
        // Deliberately straddles the 32 unit cell edge in both axes.
        var grid = new SpatialGrid<string>();
        grid.TryAdd(1, At(31, 31), "before");
        grid.TryAdd(2, At(33, 33), "after");

        var result = grid.Within(At(32, 32), 10);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TryUpdateLocation_moves_an_entry_between_cells()
    {
        var grid = new SpatialGrid<string>();
        grid.TryAdd(1, At(100, 100), "wanderer");

        Assert.True(grid.TryUpdateLocation(1, At(500, 500)));

        Assert.Empty(grid.Within(At(100, 100), 20));
        Assert.Single(grid.Within(At(500, 500), 20));
    }

    [Fact]
    public void TryRemove_takes_the_entry_out_of_its_cell()
    {
        var grid = new SpatialGrid<string>();
        grid.TryAdd(1, At(100, 100), "doomed");

        Assert.True(grid.TryRemove(1, out var removed));

        Assert.Equal("doomed", removed);
        Assert.Empty(grid.Within(At(100, 100), 20));
        Assert.Equal(0, grid.Count);
        Assert.False(grid.TryRemove(1, out _));
    }

    [Fact]
    public void TryAdd_keeps_the_first_value_for_an_id()
    {
        var grid = new SpatialGrid<string>();

        Assert.True(grid.TryAdd(1, At(100, 100), "first"));
        Assert.False(grid.TryAdd(1, At(200, 200), "second"));

        Assert.True(grid.TryGetValue(1, out var value));
        Assert.Equal("first", value);
    }

    [Fact]
    public void Any_matches_only_inside_the_radius()
    {
        var grid = new SpatialGrid<string>();
        grid.TryAdd(1, At(100, 100), "cow");
        grid.TryAdd(2, At(100, 200), "soul");

        Assert.True(grid.Any(At(100, 100), 20, v => v == "cow"));
        Assert.False(grid.Any(At(100, 100), 20, v => v == "soul"));
        Assert.True(grid.Any(At(100, 190), 20, v => v == "soul"));
    }
}
