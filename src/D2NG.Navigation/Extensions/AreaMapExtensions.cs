using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Navigation.Services.MapApi;
using Roy_T.AStar.Graphs;
using Roy_T.AStar.Grids;
using Roy_T.AStar.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Navigation.Extensions;

public static class AreaMapExtensions
{
    private static readonly float DistanceBetweenCells = 5.0f;
    public static AreaMap MapFromDto(this AreaMapDto areaMapDto)
    {
        return new AreaMap
        {
            AdjacentLevels = areaMapDto.AdjacentLevels.ToDictionary(k => (Area)int.Parse(k.Key), v => v.Value),
            LevelOrigin = areaMapDto.LevelOrigin.MapFromDto(),
            Map = areaMapDto.Map.Select(a => a.ToArray()).ToArray(),
            Npcs = areaMapDto.Npcs.ToDictionary(k => int.Parse(k.Key), k => k.Value.Select(p => p.MapFromDto()).ToList()),
            Objects = areaMapDto.Objects.ToDictionary(k => int.Parse(k.Key), k => k.Value.Select(p => p.MapFromDto()).ToList()),
            TombArea = Enum.TryParse<Area>(areaMapDto.TombArea, true, out var tombArea) ? tombArea : null
        };
    }

    public static Point MapFromDto(this PointDto pointDto)
    {
        return new Point((ushort)pointDto.X, (ushort)pointDto.Y);
    }

    public static bool IsMovable(int value)
    {
        return value % 2 == 0;
    }

    public static float GetVelocityWithAdjacency(this AreaMap areaMap, int i, int j, int columns, int rows)
    {
        var velocityCurrent = GetVelocityToPoint(areaMap, i, j, columns, rows);
        var minAdjacents = GetVelocityToPoint(areaMap, i - 3, j - 3, columns, rows);
        minAdjacents = Math.Min(minAdjacents, GetVelocityToPoint(areaMap, i - 3, j + 3, columns, rows));
        minAdjacents = Math.Min(minAdjacents, GetVelocityToPoint(areaMap, i + 3, j - 3, columns, rows));
        minAdjacents = Math.Min(minAdjacents, GetVelocityToPoint(areaMap, i + 3, j + 3, columns, rows));
        return (float)(velocityCurrent * 0.3 + minAdjacents * 0.7);
    }

    public static float GetVelocityToPoint(this AreaMap areaMap, int i, int j, int columns, int rows)
    {
        if (i < 0 || j < 0 || i >= columns || j >= rows)
        {
            return DistanceBetweenCells;
        }

        if ((j == 0 || IsMovable(areaMap.Map[i][j - 1])) && (j + 1 >= rows || IsMovable(areaMap.Map[i][j + 1])) && (i == 0 || IsMovable(areaMap.Map[i - 1][j])) && (i + 1 >= columns || IsMovable(areaMap.Map[i + 1][j])))
        {
            return DistanceBetweenCells;
        }

        return DistanceBetweenCells / 10;
    }

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary,
        TKey key, Func<TValue> valueCreator)
    {
        TValue value;
        if (!dictionary.TryGetValue(key, out value))
        {
            value = valueCreator();
            dictionary.Add(key, value);
        }
        return value;
    }

    public static Grid MapToGrid(this AreaMap areaMap)
    {
        var rows = areaMap.Map.GetLength(0);
        var columns = areaMap.Map[0].GetLength(0);
        var nodes = new Node[columns, rows];

        for (var i = 0; i < columns; i++)
        {
            for (var j = 0; j < rows; j++)
            {
                nodes[i, j] = new Node(new Position(i * DistanceBetweenCells, j * DistanceBetweenCells));
            }
        }

        for (var i = 0; i < columns; i++)
        {
            for (var j = 0; j < rows; j++)
            {
                if (!IsMovable(areaMap.Map[j][i]))
                {
                    continue;
                }

                var fromNode = nodes[i, j];

                var speed = GetVelocityWithAdjacency(areaMap, j, i, rows, columns);
                if (i + 1 < columns && IsMovable(areaMap.Map[j][i + 1]))
                {
                    var toNode = nodes[i + 1, j];
                    var velocity = Velocity.FromMetersPerSecond(speed);
                    fromNode.Connect(toNode, velocity);
                    toNode.Connect(fromNode, velocity);
                }

                if (j + 1 < rows && IsMovable(areaMap.Map[j + 1][i]))
                {
                    var toNode = nodes[i, j + 1];
                    var velocity = Velocity.FromMetersPerSecond(speed);
                    fromNode.Connect(toNode, velocity);
                    toNode.Connect(fromNode, velocity);
                }
            }
        }

        var grid = Grid.CreateGridFrom2DArrayOfNodes(nodes);
        return grid;
    }

    public static bool TryMapToPointInMap(this AreaMap areaMap, Point point, out Point relativePoint)
    {
        try
        {
            var relativePosition = point - areaMap.LevelOrigin;
            var rows = areaMap.Map.GetLength(0);
            if(rows == 0)
            {
                relativePoint = null;
                return false;

            }
            var columns = areaMap.Map[0].GetLength(0);
            if (relativePosition.X < columns && relativePosition.Y < rows)
            {
                relativePoint = relativePosition;
                return true;
            }
        }
        catch (ArithmeticException)
        {

        }

        relativePoint = null;
        return false;
    }

    public static bool TryMapToGridPosition(this AreaMap areaMap, Point point, out GridPosition? gridPosition)
    {
        try
        {
            var relativePosition = point - areaMap.LevelOrigin;
            var rows = areaMap.Map.GetLength(0);
            var columns = areaMap.Map[0].GetLength(0);
            if (relativePosition.X < columns && relativePosition.Y < rows)
            {
                gridPosition = new GridPosition(relativePosition.X, relativePosition.Y);
                return true;
            }
        }
        catch(ArithmeticException)
        {

        }

        gridPosition = null;
        return false;
    }

    /// <summary>
    /// The nearest cell that can be stood on, for a point the level grid cannot path from or to.
    /// </summary>
    /// <remarks>
    /// A teleporting character regularly ends up off the grid of the level it is in, and a point that
    /// is off the grid or inside scenery has no A* edges, so the pathfinder returns nothing and the
    /// caller reads that as "unreachable" and gives up. Snapping to the closest walkable cell gives it
    /// somewhere real to path from instead. The search starts from the point clamped into the grid but
    /// ranks candidates by their distance to the original, unclamped position, so a character far
    /// outside the level still gets the cell nearest to where it actually is.
    /// </remarks>
    public static bool TryFindNearestNavigablePoint(this AreaMap areaMap, Point point, int maximumRadius, out Point navigablePoint)
    {
        navigablePoint = null;
        var rows = areaMap.Map.GetLength(0);
        if (rows == 0)
        {
            return false;
        }

        var columns = areaMap.Map[0].GetLength(0);
        var relativeX = point.X - areaMap.LevelOrigin.X;
        var relativeY = point.Y - areaMap.LevelOrigin.Y;
        var fromX = Math.Clamp(relativeX, 0, columns - 1);
        var fromY = Math.Clamp(relativeY, 0, rows - 1);

        var best = double.MaxValue;
        int? firstHitRadius = null;
        for (var radius = 0; radius <= maximumRadius; radius++)
        {
            ScanRing(areaMap, fromX, fromY, radius, rows, relativeX, relativeY, ref best, ref navigablePoint);
            if (navigablePoint == null)
            {
                continue;
            }

            // A ring is a square, so its corners are further away than the next ring's edges. One extra
            // ring is enough for the closest cell to win.
            firstHitRadius ??= radius;
            if (radius > firstHitRadius)
            {
                break;
            }
        }

        return navigablePoint != null;
    }

    private static void ScanRing(AreaMap areaMap, int fromX, int fromY, int radius, int rows,
        int towardsX, int towardsY, ref double best, ref Point navigablePoint)
    {
        for (var y = fromY - radius; y <= fromY + radius; y++)
        {
            if (y < 0 || y >= rows)
            {
                continue;
            }

            var onHorizontalEdge = y == fromY - radius || y == fromY + radius;
            var row = areaMap.Map[y];
            for (var x = fromX - radius; x <= fromX + radius; x++)
            {
                if (x < 0 || x >= row.Length || (!onHorizontalEdge && x != fromX - radius && x != fromX + radius))
                {
                    continue;
                }

                if (!IsMovable(row[x]))
                {
                    continue;
                }

                var distance = Math.Pow(x - towardsX, 2.0) + Math.Pow(y - towardsY, 2.0);
                if (distance < best)
                {
                    best = distance;
                    navigablePoint = areaMap.MapToPoint(x, y);
                }
            }
        }
    }

    /// <summary>
    /// Whether the level grid has a cell for this point and that cell can be stood on.
    /// </summary>
    public static bool IsNavigable(this AreaMap areaMap, Point point)
    {
        return areaMap.TryMapToPointInMap(point, out var pointInMap)
            && pointInMap.Y < areaMap.Map.GetLength(0)
            && pointInMap.X < areaMap.Map[pointInMap.Y].Length
            && IsMovable(areaMap.Map[pointInMap.Y][pointInMap.X]);
    }

    public static Point MapToPoint(this AreaMap areaMap, Position position)
    {
        var point = areaMap.LevelOrigin;
        return new Point((ushort)(point.X + position.X / DistanceBetweenCells), (ushort)(point.Y + position.Y / DistanceBetweenCells));
    }

    public static Point MapToPoint(this AreaMap areaMap, int x, int y)
    {
        var point = areaMap.LevelOrigin;
        return new Point((ushort)(point.X + x), (ushort)(point.Y + y));
    }
}
