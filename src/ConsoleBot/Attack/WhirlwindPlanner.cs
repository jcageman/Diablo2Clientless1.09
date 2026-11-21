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

    /// <summary>A clipped whirl still covers a monster standing this far past its end, half in the wall.</summary>
    public const double ClippedReach = 2.0;

    /// <param name="clip">When given, cuts each whirl's end back to where the ground stops, and sixteen compass directions are tried besides those toward the monsters.</param>
    /// <param name="minimumLength">Clipped whirls shorter than this are dropped: a whirl that starts within arrival distance of its end is never cast.</param>
    public static (Point Target, int Covered) Aim(Point from, IReadOnlyList<Point> monsters, Func<Point, Point> clip = null, double minimumLength = 0)
    {
        if (monsters == null || monsters.Count == 0)
        {
            return (null, 0);
        }

        Point best = null;
        var bestScore = 0;
        var bestLength = double.MaxValue;

        var directions = clip == null ? Directions(from, monsters) : Directions(from, monsters).Concat(Compass);
        foreach (var direction in directions)
        {
            var swept = monsters.Where(m => DistanceToRay(from, direction, m) <= SweepRadius).ToList();
            if (swept.Count == 0)
            {
                continue;
            }

            var length = Math.Max(MinimumSpin, swept.Min(m => Along(from, direction, m)) + Overshoot);
            var end = Travel(from, direction, length);
            var reach = length;
            if (clip != null)
            {
                end = clip(end);
                length = from.Distance(end);
                if (length < minimumLength)
                {
                    continue;
                }

                reach = length + ClippedReach;
            }

            var covered = swept.Count(m => Along(from, direction, m) <= reach);
            if (covered == 0)
            {
                continue;
            }

            if (covered > bestScore || (covered == bestScore && length < bestLength))
            {
                best = end;
                bestScore = covered;
                bestLength = length;
            }
        }

        return (best, bestScore);
    }

    /// <summary>
    /// A monster beside him with a wall at its back clips every whirl aimed at it; a whirl hits what it
    /// passes, so one out along open ground sweeps it all the same.
    /// </summary>
    private static readonly (double X, double Y)[] Compass = Enumerable.Range(0, 16)
        .Select(i => (Math.Cos(i * Math.PI / 8), Math.Sin(i * Math.PI / 8)))
        .ToArray();

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
