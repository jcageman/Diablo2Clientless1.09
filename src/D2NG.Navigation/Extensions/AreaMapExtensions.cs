using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Navigation.Services.MapApi;
using Roy_T.AStar.Graphs;
using Roy_T.AStar.Grids;
using Roy_T.AStar.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Navigation.Extensions
{
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
                Objects = areaMapDto.Objects.ToDictionary(k => int.Parse(k.Key), k => k.Value.Select(p => p.MapFromDto()).ToList())
            };
        }

        public static Point MapFromDto(this PointDto pointDto)
        {
            return new Point((ushort)pointDto.X, (ushort)pointDto.Y);
        }

        private static void DisconnectFromNode(this INode fromNode, INode toNode)
        {
            var existingEdgeFrom = fromNode.Outgoing.SingleOrDefault(o => o.End.Position == toNode.Position);
            if (existingEdgeFrom != null)
            {
                fromNode.Outgoing.Remove(existingEdgeFrom);
                var existingEdgeTo = toNode.Outgoing.Single(o => o.End.Position == toNode.Position);
                toNode.Outgoing.Remove(existingEdgeTo);
            }
        }

        public static bool IsMovable(int value)
        {
            return value == 0 || value == 16;
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
}
