using D2NG.Core.D2GS;
using D2NG.Navigation.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>Where the portal character may open the party's portal in the cow level: at its edge, away from the Cow King.</summary>
public static class StartLocations
{
    /// <summary>
    /// The walkable edge of every fifth row of the level, from the west and from the east, further than
    /// <paramref name="kingClearance"/> from the Cow King, west first. The map is rows of cells: <c>map[y][x]</c>. Read as
    /// <c>map[x][y]</c> the spots came out mirrored across the diagonal, and one start in four was not walkable at all:
    /// the last hop to it failed for four seconds and the portal went up wherever she stood.
    /// </summary>
    public static List<Point> Candidates(int[][] map, Point origin, Point cowKing, double kingClearance = 150)
    {
        var result = new List<Point>();
        var step = Math.Max(1, map.Length / 5);
        for (var y = 0; y < map.Length; y += step)
        {
            foreach (var fromWest in new[] { true, false })
            {
                if (EdgeOf(map[y], fromWest) is int x)
                {
                    var spot = new Point((ushort)(origin.X + x), (ushort)(origin.Y + y));
                    if (spot.Distance(cowKing) > kingClearance)
                    {
                        result.Add(spot);
                    }
                }
            }
        }

        return [.. result.OrderBy(p => p.X).ThenBy(p => p.Y)];
    }

    /// <summary>The sixth walkable cell in a row of them, counted in from the row's west or east end.</summary>
    private static int? EdgeOf(int[] row, bool fromWest)
    {
        var run = 0;
        for (var i = 0; i < row.Length; i++)
        {
            var x = fromWest ? i : row.Length - 1 - i;
            run = AreaMapExtensions.IsMovable(row[x]) ? run + 1 : 0;
            if (run > 5)
            {
                return x;
            }
        }

        return null;
    }
}
