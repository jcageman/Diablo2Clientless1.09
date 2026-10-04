using D2NG.Core.D2GS;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Attack;

public static class WhirlwindPlanner
{
    public const double SweepRadius = 3.0;

    public const double Overshoot = 6.0;

    public const double MinimumSpin = 8.0;

    public static (Point Target, int Covered) Aim(Point from, IReadOnlyList<Point> monsters)
    {
        if (monsters == null || monsters.Count == 0)
        {
            return (null, 0);
        }

        Point best = null;
        var bestScore = 0;
        var bestLength = double.MaxValue;

        foreach (var direction in Directions(from, monsters))
        {
            var swept = monsters.Where(m => DistanceToRay(from, direction, m) <= SweepRadius).ToList();
            if (swept.Count == 0)
            {
                continue;
            }

            var length = Math.Max(MinimumSpin, swept.Min(m => Along(from, direction, m)) + Overshoot);
            var covered = swept.Count(m => Along(from, direction, m) <= length);

            if (covered > bestScore || (covered == bestScore && length < bestLength))
            {
                best = Travel(from, direction, length);
                bestScore = covered;
                bestLength = length;
            }
        }

        return (best, bestScore);
    }

    private static IEnumerable<(double X, double Y)> Directions(Point from, IReadOnlyList<Point> monsters)
    {
        foreach (var monster in monsters)
        {
            var dx = (double)monster.X - from.X;
            var dy = (double)monster.Y - from.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0.001)
            {
                yield return (dx / length, dy / length);
            }
        }
    }

    private static double Along(Point from, (double X, double Y) direction, Point monster)
    {
        return ((double)monster.X - from.X) * direction.X + ((double)monster.Y - from.Y) * direction.Y;
    }

    private static double DistanceToRay(Point from, (double X, double Y) direction, Point monster)
    {
        var along = Along(from, direction, monster);
        if (along < 0)
        {
            return double.MaxValue;
        }

        var dx = (double)monster.X - from.X;
        var dy = (double)monster.Y - from.Y;
        return Math.Sqrt(Math.Max(0, dx * dx + dy * dy - along * along));
    }

    private static Point Travel(Point from, (double X, double Y) direction, double distance)
    {
        var x = Math.Clamp(from.X + direction.X * distance, 0, ushort.MaxValue);
        var y = Math.Clamp(from.Y + direction.Y * distance, 0, ushort.MaxValue);
        return new Point((ushort)Math.Round(x), (ushort)Math.Round(y));
    }
}
