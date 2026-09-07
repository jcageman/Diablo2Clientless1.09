using System;
using System.Collections.Generic;

namespace ConsoleBot.Helpers;

/// <summary>
/// A serpentine route over the walkable cells of an area: sweep one band of rows left to right,
/// drop to the next band and sweep back. Clients that take their work in route order advance as one
/// line across the level instead of each chasing whatever is nearest to itself, which is what lets
/// a walking group trail them without being dragged back and forth.
/// </summary>
public static class SweepRoute
{
    /// <summary>
    /// Builds the route over <paramref name="walkable"/>, indexed as <c>walkable[y][x]</c> to match
    /// the area map. One waypoint per <paramref name="step"/> columns per <paramref name="band"/>
    /// of rows, skipping stretches with nothing walkable in them.
    /// </summary>
    public static List<(int X, int Y)> Build(bool[][] walkable, int band, int step)
    {
        var route = new List<(int X, int Y)>();
        if (walkable == null || walkable.Length == 0 || band <= 0 || step <= 0)
        {
            return route;
        }

        var leftToRight = true;
        for (var bandStart = 0; bandStart < walkable.Length; bandStart += band)
        {
            var bandEnd = Math.Min(bandStart + band, walkable.Length);
            var width = 0;
            for (var y = bandStart; y < bandEnd; y++)
            {
                width = Math.Max(width, walkable[y]?.Length ?? 0);
            }

            var stripe = new List<(int X, int Y)>();
            for (var x = 0; x < width; x += step)
            {
                if (TryFindWalkable(walkable, bandStart, bandEnd, x, Math.Min(x + step, width), out var found))
                {
                    stripe.Add(found);
                }
            }

            if (stripe.Count == 0)
            {
                continue;
            }

            if (!leftToRight)
            {
                stripe.Reverse();
            }

            route.AddRange(stripe);
            leftToRight = !leftToRight;
        }

        return route;
    }

    /// <summary>
    /// The nearest walkable cell to the centre of the given block, so a waypoint sits in open
    /// ground rather than against whatever wall happens to come first.
    /// </summary>
    private static bool TryFindWalkable(bool[][] walkable, int yStart, int yEnd, int xStart, int xEnd, out (int X, int Y) found)
    {
        var centreY = (yStart + yEnd) / 2;
        var centreX = (xStart + xEnd) / 2;
        var best = (X: 0, Y: 0);
        var bestDistance = int.MaxValue;
        for (var y = yStart; y < yEnd; y++)
        {
            var row = walkable[y];
            if (row == null)
            {
                continue;
            }

            for (var x = xStart; x < xEnd && x < row.Length; x++)
            {
                if (!row[x])
                {
                    continue;
                }

                var distance = ((x - centreX) * (x - centreX)) + ((y - centreY) * (y - centreY));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (x, y);
                }
            }
        }

        found = best;
        return bestDistance != int.MaxValue;
    }
}
