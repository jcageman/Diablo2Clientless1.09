using ConsoleBot.Bots.Types.Cows;
using D2NG.Core.D2GS;
using D2NG.Navigation.Extensions;
using System.Linq;

namespace ConsoleBot.Tests;

public class StartLocationsTests
{
    private const int Wall = 1;
    private const int Floor = 0;

    /// <summary>Ten rows of thirty cells, walkable from x 10 to 24 in every row: wider than tall, as a mirrored read cannot fit.</summary>
    private static int[][] Band()
        => [.. Enumerable.Range(0, 10).Select(_ => Enumerable.Range(0, 30).Select(x => x is >= 10 and <= 24 ? Floor : Wall).ToArray())];

    private static readonly Point Origin = new(1000, 2000);
    private static readonly Point FarKing = new(5000, 5000);

    [Fact]
    public void Every_start_is_on_walkable_ground()
    {
        var map = Band();
        var starts = StartLocations.Candidates(map, Origin, FarKing);

        Assert.NotEmpty(starts);
        Assert.All(starts, start => Assert.True(AreaMapExtensions.IsMovable(map[start.Y - Origin.Y][start.X - Origin.X])));
    }

    [Fact]
    public void A_start_is_the_sixth_walkable_cell_in_from_either_edge_of_every_fifth_row()
    {
        var starts = StartLocations.Candidates(Band(), Origin, FarKing);

        int[] rows = [0, 2, 4, 6, 8];
        Assert.Equal(rows.SelectMany(y => new[] { new Point(1015, (ushort)(2000 + y)), new Point(1019, (ushort)(2000 + y)) }).OrderBy(p => p.X).ThenBy(p => p.Y), starts);
    }

    [Fact]
    public void Starts_near_the_cow_king_are_left_out()
    {
        var starts = StartLocations.Candidates(Band(), Origin, cowKing: new Point(1015, 2004), kingClearance: 3);

        Assert.DoesNotContain(new Point(1015, 2004), starts);
        Assert.Contains(new Point(1019, 2004), starts);
    }
}
