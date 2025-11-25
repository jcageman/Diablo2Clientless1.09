using D2NG.Core.D2GS;
using D2NG.Navigation.Services.MapApi;
using System;
using System.Collections.Generic;

namespace D2NG.Navigation.Services.Pathing;

enum PathingResult
{
    Failed = 0,     // Failed, error occurred or no available path
    DestinationNotReachedYet,      // Path OK, destination not reached yet
    Reached // Path OK, destination reached(Path finding completed successfully)
};

public class TeleportPather
{
    private static readonly short RangeInvalid = 10000;
    private static readonly short TpRange = 30;
    private static readonly short BlockRange = 2;
    private readonly short[,] m_distanceMatrix;
    private readonly int m_rows;
    private readonly int m_columns;

    public TeleportPather(AreaMap areaMap)
    {
        m_rows = areaMap.Map.GetLength(0);
        m_columns = areaMap.Map[0].GetLength(0);
        m_distanceMatrix = new short[m_columns, m_rows];
        for (int i = 0; i < m_columns; i++)
        {
            for (int k = 0; k < m_rows; k++)
            {
                m_distanceMatrix[i, k] = (short)areaMap.Map[k][i];
            }
        }

    }

    public void MakeDistanceTable(Point toLocation)
    {
        for (int x = 0; x < m_columns; x++)
        {
            for (int y = 0; y < m_rows; y++)
            {
                if ((m_distanceMatrix[x, y] % 2) == 0)
                    m_distanceMatrix[x, y] = (short)CalculateDistance(x, y, toLocation.X, toLocation.Y);
                else
                    m_distanceMatrix[x, y] = RangeInvalid;
            }
        }

        m_distanceMatrix[toLocation.X, toLocation.Y] = 1;
    }


    private static void AddToListAtIndex(List<Point> list, Point point, int index)
    {
        if (index < list.Count)
        {
            list[index] = point;
            return;
        }
        else if (index == list.Count)
        {
            list.Add(point);
            return;
        }

        throw new InvalidOperationException();
    }

    public Path GetTeleportPath(Point fromLocation, Point toLocation)
    {
        MakeDistanceTable(toLocation);
        var path = new List<Point>
        {
            fromLocation
        };
        int idxPath = 1;

        var bestMove = new BestMove
        {
            Move = fromLocation,
            Result = PathingResult.DestinationNotReachedYet
        };

        var move = GetBestMove(bestMove.Move, toLocation, BlockRange);
        while (move.Result != PathingResult.Failed && idxPath < 100)
        {
            // Reached?
            if (move.Result == PathingResult.Reached)
            {
                AddToListAtIndex(path, toLocation, idxPath);
                idxPath++;
                return new Path()
                {
                    Found = true,
                    Points = path.GetRange(0, idxPath)
                };
            }

            // Perform a redundancy check
            int nRedundancy = GetRedundancy(path, idxPath, move.Move);
            if (nRedundancy == -1)
            {
                // no redundancy
                AddToListAtIndex(path, move.Move, idxPath);
                idxPath++;
            }
            else
            {
                // redundancy found, discard all redundant steps
                idxPath = nRedundancy + 1;
                AddToListAtIndex(path, move.Move, idxPath);
            }

            move = GetBestMove(move.Move, toLocation, BlockRange);
        }

        return new Path()
        {
            Found = false,
            Points = null
        };
    }

    private BestMove GetBestMove(Point position, Point toLocation, int blockRange)
    {
        if (CalculateDistance(toLocation, position) <= TpRange)
        {
            return new BestMove
            {
                Result = PathingResult.Reached,
                Move = toLocation
            };
        }

        if (!IsValidIndex(position.X, position.Y))
        {
            return new BestMove
            {
                Result = PathingResult.Failed,
                Move = null
            };
        }

        Block(position, blockRange);

        Point best = null;
        int value = RangeInvalid;

        for (var x = position.X - TpRange; x <= position.X + TpRange; x++)
        {
            for (var y = position.Y - TpRange; y <= position.Y + TpRange; y++)
            {
                if (!IsValidIndex(x, y))
                    continue;

                Point p = new((ushort)x, (ushort)y);

                if (m_distanceMatrix[p.X, p.Y] < value && CalculateDistance(p, position) <= TpRange)
                {
                    value = m_distanceMatrix[p.X, p.Y];
                    best = p;
                }
            }
        }

        if (value >= RangeInvalid || best == null)
        {
            return new BestMove
            {
                Result = PathingResult.Failed,
                Move = null
            };
        }

        Block(best, blockRange);
        return new BestMove
        {
            Result = PathingResult.DestinationNotReachedYet,
            Move = best
        };
    }


    private void Block(Point position, int nRange)
    {
        nRange = Math.Max(nRange, 1);

        for (int i = position.X - nRange; i < position.X + nRange; i++)
        {
            for (int j = position.Y - nRange; j < position.Y + nRange; j++)
            {
                if (IsValidIndex(i, j))
                    m_distanceMatrix[i, j] = RangeInvalid;
            }
        }
    }

    static int GetRedundancy(List<Point> currentPath, int idxPath, Point position)
    {
        // step redundancy check
        for (int i = 1; i < idxPath; i++)
        {
            if (CalculateDistance(currentPath[i].X, currentPath[i].Y, position.X, position.Y) <= TpRange / 2.0)
                return i;
        }

        return -1;
    }

    private bool IsValidIndex(int x, int y)
    {
        return x >= 0 && x < m_columns && y >= 0 && y < m_rows;
    }

    private static double CalculateDistance(long x1, long y1, long x2, long y2)
    {
        return Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    }

    private static double CalculateDistance(Point point1, Point point2)
    {
        return CalculateDistance(point1.X, point1.Y, point2.X, point2.Y);
    }

    private struct BestMove
    {
        public PathingResult Result { get; set; }

        public Point Move { get; set; }
    }
}
