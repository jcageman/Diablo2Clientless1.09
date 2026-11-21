using ConsoleBot.Bots.Types.Travincal;
using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Attack;

/// <summary>What the wall-aware whirl does from a moment, set against a whirl the bot cast.</summary>
public enum WhirlChoice
{
    /// <summary>A direct whirl ending within <see cref="WallAwareWhirlDecision.SameTolerance"/> of the point the bot asked for.</summary>
    Same,

    /// <summary>A direct whirl, but at another point.</summary>
    DifferentWhirl,

    /// <summary>A walk beside a monster to whirl along the wall.</summary>
    WalkAlongWall,

    /// <summary>No whirl: the target is across a wall (or every direction to it just failed), so walk around.</summary>
    Blocked,

    /// <summary>No whirl: nothing with an open lane within whirl reach; walk toward the target.</summary>
    Approach,

    /// <summary>No enemy in range at all.</summary>
    NoTarget,
}

/// <summary>A monster as the decision sees it: what the bot's own filters read.</summary>
public sealed record Foe(uint Id, NPCCode Code, Point At, EntityState State, double Life);

/// <param name="AimAt">The monsters the whirl was aimed through.</param>
public sealed record Decision(WhirlChoice Choice, Point Target, Point WalkTo, uint? Pick, int SkippedAcrossWall, IReadOnlyList<uint> AimAt);

/// <summary>
/// The Travincal bot's wall-aware whirl decision as one pure function of a moment: TravincalBot.KillCouncilMembers' member
/// pick (<see cref="WallAwareWhirl.ChooseMySideFirst"/> over the live council members not given up), then
/// AttackService.BarbarianAssist and WhirlWindThroughPack with wallAwareWhirl on - the ten nearest enemies within twenty,
/// the pack of those with an open lane, the four within whirl reach, <see cref="WallAwareWhirl.PlanWhirl"/> over their
/// lanes - with the bot's own constants and filters. It is the first step of a pure planner: the live bot does not call it
/// yet. What it leaves to the caller, since the bot gets it from the live client: the map (the bot asks the map service),
/// the walking path length (the bot asks the pathing service; offline <see cref="GridPath"/>), the directions a whirl just
/// failed along (<see cref="FailedWhirls"/>) and the members given up. Left out: the approach walk (the pack is within whirl
/// reach anyway), the path-based flank (PlanWhirl's straight-line flank stands in), Iron Maiden, the rejuvenation hold and
/// the mana floor.
/// </summary>
public static class WallAwareWhirlDecision
{
    /// <summary>A planned whirl ending this close to the point the bot asked for is the same whirl.</summary>
    public const double SameTolerance = 3;

    /// <param name="refused">Whether a whirl from <paramref name="me"/> toward a point runs along one that just failed (<see cref="FailedWhirls.Refuses"/>).</param>
    public static Decision Decide(AreaMap map, Point me, IReadOnlyList<Foe> foes, Point asked, Func<Point, Point, double?> pathLength,
        Func<Point, bool> refused = null, IReadOnlySet<uint> givenUp = null)
    {
        refused ??= _ => false;
        givenUp ??= new HashSet<uint>();
        double Distance(Foe f) => me.Distance(f.At);
        bool LaneOpen(Foe f) => WallAwareWhirl.IsLaneClear(map, me, f.At, WallAwareWhirl.EdgeSlack);
        bool OnMySide(Foe f) => !refused(f.At)
            && (LaneOpen(f) || (pathLength(me, f.At) is { } path && WallAwareWhirl.IsShortDetour(me.Distance(f.At), path)));

        uint? targetId = null;
        var skipped = 0;
        var council = foes
            .Where(f => TravincalBot.CouncilCodes.Contains(f.Code) && f.State is not (EntityState.Dead or EntityState.Dieing) && f.Life > 0 && !givenUp.Contains(f.Id))
            .OrderBy(Distance)
            .ToList();
        if (council.Count > 0)
        {
            var (pick, passed) = WallAwareWhirl.ChooseMySideFirst(council, Distance, f => Task.FromResult(OnMySide(f))).GetAwaiter().GetResult();
            targetId = pick.Id;
            skipped = passed.Count;
        }

        var enemies = foes
            .Where(f => f.State is not (EntityState.Dead or EntityState.Dieing) && !NPCHelpers.IsFriendly(f.Code) && Distance(f) < AttackService.AssistNearbyRange)
            .OrderBy(Distance)
            .Take(AttackService.AssistNearbyCount)
            .ToList();
        var nearest = enemies.FirstOrDefault(e => e.Id == targetId)
            ?? WallAwareWhirl.ChooseMySideFirst(enemies, Distance, f => Task.FromResult(LaneOpen(f))).GetAwaiter().GetResult().Pick;
        if (nearest == null)
        {
            return new Decision(WhirlChoice.NoTarget, null, null, targetId, skipped, []);
        }

        var pack = enemies.Where(e => e == nearest || LaneOpen(e)).ToList();
        var aimPack = pack.Where(e => Distance(e) <= AttackService.MaxWhirlwindAimDistance).OrderBy(Distance).Take(AttackService.MaxWhirlwindTargets).ToList();
        var aimAt = aimPack.Where(LaneOpen).ToList();
        if (aimAt.Count == 0)
        {
            var blocked = aimPack.Count > 0 || (Distance(nearest) < AttackService.BossChaseRange && (refused(nearest.At) || !LaneOpen(nearest)));
            return new Decision(blocked ? WhirlChoice.Blocked : WhirlChoice.Approach, null, null, nearest.Id, skipped, []);
        }

        var ids = aimAt.Select(e => e.Id).ToList();
        var plan = WallAwareWhirl.PlanWhirl(map, me, [.. aimAt.Select(e => e.At)], AttackService.WhirlwindArrivalDistance + 1, refused);
        if (plan.WalkTo != null)
        {
            return new Decision(WhirlChoice.WalkAlongWall, plan.Target, plan.WalkTo, nearest.Id, skipped, ids);
        }

        if (plan.Target == null)
        {
            return new Decision(WhirlChoice.Blocked, null, null, nearest.Id, skipped, ids);
        }

        var same = asked != null && plan.Target.Distance(asked) <= SameTolerance;
        return new Decision(same ? WhirlChoice.Same : WhirlChoice.DifferentWhirl, plan.Target, null, nearest.Id, skipped, ids);
    }
}

/// <summary>
/// Walking distance over a map's collision grid: an A* over walkable subtiles (8 neighbours, diagonal √2) in a box around
/// the two points. An offline stand-in for the pathing service the bot asks; null when no path is found within the box.
/// </summary>
public static class GridPath
{
    public const int Margin = 30;

    public static double? Length(AreaMap map, Point from, Point to)
    {
        var minX = Math.Min(from.X, to.X) - Margin;
        var minY = Math.Min(from.Y, to.Y) - Margin;
        var width = Math.Abs(from.X - to.X) + (2 * Margin) + 1;
        var height = Math.Abs(from.Y - to.Y) + (2 * Margin) + 1;
        bool Walkable(int x, int y)
        {
            var row = y - map.LevelOrigin.Y;
            var column = x - map.LevelOrigin.X;
            return row >= 0 && row < map.Map.Length && column >= 0 && column < map.Map[row].Length && AreaMapExtensions.IsMovable(map.Map[row][column]);
        }

        var start = Snap(from, Walkable);
        var goal = Snap(to, Walkable);
        if (start == null || goal == null)
        {
            return null;
        }

        var best = new Dictionary<(int X, int Y), double> { [start.Value] = 0 };
        var open = new PriorityQueue<(int X, int Y), double>();
        open.Enqueue(start.Value, Heuristic(start.Value, goal.Value));
        while (open.TryDequeue(out var cell, out _))
        {
            if (cell == goal.Value)
            {
                return best[cell] + Gap(from, start.Value) + Gap(to, goal.Value);
            }

            var g = best[cell];
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var next = (X: cell.X + dx, Y: cell.Y + dy);
                    if ((dx == 0 && dy == 0) || next.X < minX || next.Y < minY || next.X >= minX + width || next.Y >= minY + height || !Walkable(next.X, next.Y))
                    {
                        continue;
                    }

                    var cost = g + (dx != 0 && dy != 0 ? Math.Sqrt(2) : 1);
                    if (!best.TryGetValue(next, out var known) || cost < known)
                    {
                        best[next] = cost;
                        open.Enqueue(next, cost + Heuristic(next, goal.Value));
                    }
                }
            }
        }

        return null;
    }

    private static double Gap(Point point, (int X, int Y) cell) => Math.Sqrt(Math.Pow(point.X - cell.X, 2) + Math.Pow(point.Y - cell.Y, 2));

    private static double Heuristic((int X, int Y) a, (int X, int Y) b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return Math.Max(dx, dy) + ((Math.Sqrt(2) - 1) * Math.Min(dx, dy));
    }

    /// <summary>The point itself when walkable, else the nearest walkable cell within 2 - a monster against a wall stands half in it.</summary>
    private static (int X, int Y)? Snap(Point point, Func<int, int, bool> walkable)
    {
        (int X, int Y)? best = null;
        var bestDistance = double.MaxValue;
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var d = Math.Sqrt((dx * dx) + (dy * dy));
                if (d < bestDistance && walkable(point.X + dx, point.Y + dy))
                {
                    best = (point.X + dx, point.Y + dy);
                    bestDistance = d;
                }
            }
        }

        return best;
    }
}
