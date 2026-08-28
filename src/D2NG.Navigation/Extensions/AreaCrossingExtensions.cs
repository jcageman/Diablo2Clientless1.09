using D2NG.Core.D2GS;
using D2NG.Navigation.Services.MapApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Navigation.Extensions;

/// <summary>
/// Finding a way from one area into a neighbouring one when the map data offers no exit to aim at.
/// </summary>
/// <remarks>
/// Level exits exist only where the game has a discrete warp: a stairway, a cave mouth, a portal. Open
/// boundaries have none, so the map api reports no exits from the rogue encampment to the blood moor,
/// or from the river of flame to the chaos sanctuary, even though both are walked between constantly.
/// <para>
/// The way through is the one autotele uses: a level's collision grid is walkable along its outer edge
/// only where the level actually opens into its neighbour, so the gaps in that edge are the doorways.
/// Taking the middle of a gap gives a point that is both reachable from inside and clear to step
/// through, which a search based on nearness to the boundary does not - in a walled town the nearest
/// boundary cell is usually on the far side of the palisade.
/// </para>
/// </remarks>
public static class AreaCrossingExtensions
{
    /// <summary>
    /// A place where two areas can be stepped between: where to walk to on this side, and the point just
    /// beyond the edge to step onto.
    /// </summary>
    public sealed record AreaCrossing(Point Approach, Point Target);

    private sealed record Edge(int OutwardX, int OutwardY);

    /// <summary>
    /// Narrowest gap treated as a way through rather than as noise in the collision data.
    /// </summary>
    private const int MinimumGapWidth = 3;

    /// <summary>
    /// Whether a world point is inside this map and stands on ground that can be walked on.
    /// </summary>
    public static bool IsWalkableWorldPoint(this AreaMap areaMap, int x, int y)
    {
        var column = x - areaMap.LevelOrigin.X;
        var row = y - areaMap.LevelOrigin.Y;
        if (row < 0 || row >= areaMap.Map.Length || column < 0 || column >= areaMap.Map[row].Length)
        {
            return false;
        }

        return AreaMapExtensions.IsMovable(areaMap.Map[row][column]);
    }

    /// <summary>
    /// Crossings from this area into a neighbour, nearest to <paramref name="fromLocation"/> first. Each
    /// one is the middle of a gap in this area's edge whose outward side lands inside the neighbour.
    /// </summary>
    /// <param name="areaMap">Map of the area currently standing in.</param>
    /// <param name="adjacent">The neighbour's origin and size, used to tell which way it lies.</param>
    /// <param name="targetMap">
    /// The neighbour's map when available, used to confirm the far side is walkable. Optional because a
    /// crossing can be found without it.
    /// </param>
    /// <param name="fromLocation">Where the character is now, used to rank the candidates.</param>
    /// <param name="stepOver">
    /// How far past the edge to aim. Must clear the ten unit threshold at which a movement is treated as
    /// already arrived and skipped: a five unit step over the border was dropped silently, leaving the
    /// character standing one unit short of the boundary reporting success.
    /// </param>
    public static List<AreaCrossing> FindCrossings(
        this AreaMap areaMap,
        AdjacentLevel adjacent,
        AreaMap targetMap,
        Point fromLocation,
        int stepOver = 16)
    {
        ArgumentNullException.ThrowIfNull(areaMap);
        ArgumentNullException.ThrowIfNull(adjacent);

        var rows = areaMap.Map.Length;
        if (rows == 0)
        {
            return [];
        }

        var columns = areaMap.Map[0].Length;
        var neighbour = (Left: adjacent.LevelOrigin.X, Top: adjacent.LevelOrigin.Y,
            Right: adjacent.LevelOrigin.X + adjacent.Width, Bottom: adjacent.LevelOrigin.Y + adjacent.Height);

        var crossings = new List<AreaCrossing>();
        var narrow = new List<AreaCrossing>();
        foreach (var (edge, cells) in EdgeCells(areaMap, rows, columns))
        {
            foreach (var run in Runs(cells))
            {
                // Walk the whole opening rather than just its middle. A gap in this area's edge is only
                // a way through where the other side is walkable too, and the two do not have to line up:
                // Tamoe Highland's north edge opens for 268 cells while the monastery gate's road behind
                // it is far narrower, so the middle of the gap points straight into its scenery.
                var viable = new List<AreaCrossing>();
                foreach (var cell in run)
                {
                    var target = StepAcross(cell, edge, neighbour, targetMap, stepOver);
                    if (target != null)
                    {
                        viable.Add(new AreaCrossing(cell, target));
                    }
                }

                foreach (var stretch in Stretches(viable))
                {
                    var middle = stretch[stretch.Count / 2];
                    if (stretch.Count >= MinimumGapWidth)
                    {
                        crossings.Add(middle);
                    }
                    else
                    {
                        narrow.Add(middle);
                    }
                }
            }
        }

        var usable = crossings.Count > 0 ? crossings : narrow;
        return [.. usable.OrderBy(c => fromLocation.Distance(c.Approach))];
    }

    /// <summary>Runs of adjacent walkable cells along an edge. One run is one opening.</summary>
    private static IEnumerable<List<Point>> Runs(List<Point> edgeCells)
    {
        if (edgeCells.Count == 0)
        {
            yield break;
        }

        var run = new List<Point> { edgeCells[0] };
        for (var i = 1; i < edgeCells.Count; i++)
        {
            if (edgeCells[i].Distance(edgeCells[i - 1]) <= 1.5)
            {
                run.Add(edgeCells[i]);
                continue;
            }

            yield return run;
            run = [edgeCells[i]];
        }

        yield return run;
    }

    /// <summary>
    /// Consecutive stretches of crossings, so the middle of each usable part of an opening is offered
    /// rather than the middle of the opening as a whole.
    /// </summary>
    private static IEnumerable<List<AreaCrossing>> Stretches(List<AreaCrossing> viable)
    {
        if (viable.Count == 0)
        {
            yield break;
        }

        var stretch = new List<AreaCrossing> { viable[0] };
        for (var i = 1; i < viable.Count; i++)
        {
            if (viable[i].Approach.Distance(viable[i - 1].Approach) <= 1.5)
            {
                stretch.Add(viable[i]);
                continue;
            }

            yield return stretch;
            stretch = [viable[i]];
        }

        yield return stretch;
    }

    /// <summary>
    /// A point beyond the edge that lies inside the neighbour and can be stood on. Several distances are
    /// tried because the ground immediately over a boundary is not always clear, and anything closer than
    /// the movement threshold would be skipped rather than walked.
    /// </summary>
    private static Point StepAcross(
        Point gapCentre,
        Edge edge,
        (int Left, int Top, int Right, int Bottom) neighbour,
        AreaMap targetMap,
        int preferredStep)
    {
        foreach (var step in new[] { preferredStep, preferredStep + 6, preferredStep + 14, 12 })
        {
            var x = gapCentre.X + (edge.OutwardX * step);
            var y = gapCentre.Y + (edge.OutwardY * step);
            if (x < 0 || y < 0 || x < neighbour.Left || x > neighbour.Right || y < neighbour.Top || y > neighbour.Bottom)
            {
                continue;
            }

            if (targetMap != null && !targetMap.IsWalkableWorldPoint(x, y))
            {
                continue;
            }

            return new Point((ushort)x, (ushort)y);
        }

        return null;
    }

    /// <summary>
    /// The cells along each of the four outer edges of the grid, with the direction that leads out of the
    /// area through that edge.
    /// </summary>
    private static IEnumerable<(Edge Edge, List<Point> Cells)> EdgeCells(AreaMap areaMap, int rows, int columns)
    {
        var origin = areaMap.LevelOrigin;

        var west = new List<Point>();
        var east = new List<Point>();
        for (var row = 0; row < rows; row++)
        {
            if (AreaMapExtensions.IsMovable(areaMap.Map[row][0]))
            {
                west.Add(new Point((ushort)origin.X, (ushort)(origin.Y + row)));
            }

            var lastColumn = areaMap.Map[row].Length - 1;
            if (lastColumn >= 0 && AreaMapExtensions.IsMovable(areaMap.Map[row][lastColumn]))
            {
                east.Add(new Point((ushort)(origin.X + lastColumn), (ushort)(origin.Y + row)));
            }
        }

        var north = new List<Point>();
        var south = new List<Point>();
        for (var column = 0; column < columns; column++)
        {
            if (AreaMapExtensions.IsMovable(areaMap.Map[0][column]))
            {
                north.Add(new Point((ushort)(origin.X + column), (ushort)origin.Y));
            }

            if (column < areaMap.Map[rows - 1].Length && AreaMapExtensions.IsMovable(areaMap.Map[rows - 1][column]))
            {
                south.Add(new Point((ushort)(origin.X + column), (ushort)(origin.Y + rows - 1)));
            }
        }

        yield return (new Edge(-1, 0), west);
        yield return (new Edge(1, 0), east);
        yield return (new Edge(0, -1), north);
        yield return (new Edge(0, 1), south);
    }

}
