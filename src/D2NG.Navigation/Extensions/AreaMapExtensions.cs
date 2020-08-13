using D2NG.Core.D2GS;
using D2NG.Navigation.Services.MapApi;
using System.Linq;
using D2NG.Core.D2GS.Act;
using Roy_T.AStar.Graphs;
using Roy_T.AStar.Grids;
using Roy_T.AStar.Primitives;
using System;
using System.Collections.Generic;
using D2NG.Navigation.Services.Pathing;

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

        private static void DisconnectNodeFromNodeAtLocation(this Grid grid, INode fromNode, int i, int j)
        {
            var toGridPosition = new GridPosition(i, j);
            var toNode = grid.GetNode(toGridPosition);
            fromNode.DisconnectFromNode(toNode);
        }

        public static bool IsMovable(int value)
        {
            return value == 0 || value == 7 || value == 16;
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
            if (i < 0 || j < 0)
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

        public static Grid MapToGrid(this AreaMap areaMap, MovementMode movementMode)
        {
            var rows = areaMap.Map.GetLength(0);
            var columns = areaMap.Map[0].GetLength(0);
            var nodes = new Node[columns, rows];

            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < rows; j++)
                {
                    nodes[i, j] = new Node(new Position((float)i * DistanceBetweenCells, (float)j * DistanceBetweenCells));
                }
            }

            var connectedNodes = new Dictionary<GridPosition, HashSet<GridPosition>>();

            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < rows; j++)
                {
                    if (!IsMovable(areaMap.Map[j][i]))
                    {
                        continue;
                    }

                    var fromNode = nodes[i, j];
                    var gridPosition = new GridPosition(i, j);
                    var connectedSet = connectedNodes.GetOrAdd(gridPosition, () => new HashSet<GridPosition>());

                    var speed = GetVelocityWithAdjacency(areaMap, j, i, rows, columns);
                    if (movementMode == MovementMode.Walking)
                    {
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
                    else
                    {
                        var TeleportRange = 35;
                        for (var x = 0; x <= TeleportRange; x++)
                        {
                            for (var y = 0; y <= TeleportRange; y++)
                            {
                                var teleDistance = Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2));
                                if (teleDistance > TeleportRange || teleDistance < TeleportRange * 0.8)
                                {
                                    continue;
                                }

                                var teleportGridPosition = new GridPosition(i + y, j + x);
                                if (i + y < columns && i + y >= 0 && j + x < rows && j + x >= 0 && IsMovable(areaMap.Map[j + x][i + y]) && !connectedSet.Contains(teleportGridPosition))
                                {
                                    var teleSet = connectedNodes.GetOrAdd(teleportGridPosition, () => new HashSet<GridPosition>());
                                    teleSet.Add(gridPosition);
                                    connectedSet.Add(teleportGridPosition);
                                    var toNode = nodes[i + y, j + x];

                                    var velocity = Velocity.FromMetersPerSecond((float)teleDistance);
                                    fromNode.Connect(toNode, velocity);
                                    toNode.Connect(fromNode, velocity);
                                }
                            }
                        }
                    }
                }
            }

            var grid = Grid.CreateGridFrom2DArrayOfNodes(nodes);
            return grid;
        }

        public static GridPosition MapToGridPosition(this AreaMap areaMap, Point point)
        {
            var relativePosition = point - areaMap.LevelOrigin;
            return new GridPosition(relativePosition.X, relativePosition.Y);
        }

        public static Point MapToPoint(this AreaMap areaMap, Position position)
        {
            var point = areaMap.LevelOrigin;
            return new Point((ushort)(point.X + position.X / DistanceBetweenCells), (ushort)(point.Y + position.Y / DistanceBetweenCells));
        }
    }
}
