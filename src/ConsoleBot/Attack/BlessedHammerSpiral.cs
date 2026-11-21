using D2NG.Core.D2GS;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Attack;

/// <summary>
/// Where a Blessed Hammer flies. The server builds its path from the paladin's position alone (D2Common
/// PATH_ComputePathBlessedHammer, decompiled in D2MOO): 77 cells, each a 32nd of a turn further clockwise and
/// 9600/65536 of a subtile further out, always starting due east whatever was targeted - so the spiral grows about
/// 4.7 subtiles a turn and ends near 14 out. missiles.txt gives the hammer CollideType 3 and CollideKill 0: it passes
/// through monsters, hitting each once, and dies on the first wall it meets. A paladin six subtiles west of the Chaos
/// Sanctuary corridor's wall threw 97 hammers into it in 28 seconds and hit nothing; from the middle they would have
/// reached the knight he aimed at.
/// </summary>
public static class BlessedHammerSpiral
{
    public const int PathPoints = 77;

    private const double TurnPerPoint = 2 * Math.PI / 32;

    private const double DistancePerPoint = 9600 / 65536.0;

    /// <summary>A monster this close to a cell of the path is hit: the hammer's size of one plus the monster's body.</summary>
    public const double HitReach = 1.5;

    /// <summary>The cells a hammer cast from <paramref name="origin"/> passes, in flight order, walls not counted.</summary>
    public static IReadOnlyList<Point> Path(Point origin)
    {
        // Positions are fixed point from the subtile's centre; the cells are what the fraction falls in.
        double x = origin.X + 0.5, y = origin.Y + 0.5;
        var points = new List<Point>(PathPoints);
        var previous = origin;
        for (var step = 1; points.Count < PathPoints; step++)
        {
            var distance = step * DistancePerPoint;
            var cell = new Point((ushort)Math.Floor(x + Math.Cos(step * TurnPerPoint) * distance), (ushort)Math.Floor(y + Math.Sin(step * TurnPerPoint) * distance));
            if (cell.X != previous.X || cell.Y != previous.Y)
            {
                points.Add(cell);
                previous = cell;
            }
        }

        return points;
    }

    /// <summary>The part of the path a hammer flies before the first cell it cannot cross.</summary>
    public static IReadOnlyList<Point> Reach(Point origin, Func<Point, bool> open)
        => Path(origin).TakeWhile(open).ToList();

    /// <summary>How many of <paramref name="monsters"/> a hammer cast from <paramref name="origin"/> hits.</summary>
    public static int Hits(Point origin, IReadOnlyCollection<Point> monsters, Func<Point, bool> open)
    {
        var reach = Reach(origin, open);
        return monsters.Count(monster => reach.Any(cell => cell.Distance(monster) <= HitReach));
    }

    private static readonly int[] Rings = [1, 2, 3, 4, 5, 6];

    private const int Directions = 16;

    /// <summary>
    /// The open spot within <paramref name="maxStep"/> of <paramref name="me"/> whose hammers hit the most of
    /// <paramref name="monsters"/> - the longer spiral and the shorter step breaking ties - or <paramref name="me"/> when none
    /// beats standing still.
    /// </summary>
    public static Point BestSpot(Point me, IReadOnlyCollection<Point> monsters, Func<Point, bool> open, int maxStep = 6)
    {
        var best = me;
        var bestHits = Hits(me, monsters, open);
        var bestReach = Reach(me, open).Count;
        foreach (var ring in Rings.Where(ring => ring <= maxStep))
        {
            for (var direction = 0; direction < Directions; direction++)
            {
                var angle = direction * 2 * Math.PI / Directions;
                var spot = new Point((ushort)Math.Round(me.X + Math.Cos(angle) * ring), (ushort)Math.Round(me.Y + Math.Sin(angle) * ring));
                if (!open(spot))
                {
                    continue;
                }

                var hits = Hits(spot, monsters, open);
                var reach = Reach(spot, open).Count;
                if (hits > bestHits || (hits == bestHits && hits > 0 && reach > bestReach + 10))
                {
                    best = spot;
                    bestHits = hits;
                    bestReach = reach;
                }
            }
        }

        return best;
    }

    /// <summary>Open ground on the area map; a cell off the map counts as a wall.</summary>
    public static Func<Point, bool> OpenOn(AreaMap map)
        => point =>
        {
            var row = point.Y - map.LevelOrigin.Y;
            var column = point.X - map.LevelOrigin.X;
            return row >= 0 && row < map.Map.Length && column >= 0 && column < map.Map[row].Length && AreaMapExtensions.IsMovable(map.Map[row][column]);
        };
}
