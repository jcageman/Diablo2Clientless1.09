using D2NG.Core.D2GS;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Attack;

/// <summary>
/// The game-free rules of the wall-aware whirl: a whirl into a wall goes nowhere, hits nothing and
/// so leeches nothing, and the barbarian used to repeat it until his belt was dry.
/// </summary>
public static class WallAwareWhirl
{
    public const double NowhereDistance = 3;

    /// <summary>A whirl the server showed running that moved him less than this was into a wall.</summary>
    public const double BarelyMoved = 1;

    /// <summary>
    /// Positions sit a subtile or two off the map's walkable edge - his own and a monster's against a
    /// wall - so when an end stands on a blocked cell, blocked cells this close to it do not close a lane.
    /// </summary>
    public const double EdgeSlack = 2;

    /// <summary>More than this, and the lane is through a wall rather than off its edge.</summary>
    private const int MaxSkippedCells = 2;

    /// <summary>A path this much longer than the straight line still counts as his side of the wall.</summary>
    public const double MySidePathFactor = 1.5;

    public static bool IsLaneClear(AreaMap map, Point from, Point to, double endSlack = 0)
        => Walk(map, from, to, endSlack).Clear;

    /// <summary>The last walkable cell on the line before the first blocked one; <paramref name="to"/> when none is blocked.</summary>
    public static Point ClipLane(AreaMap map, Point from, Point to)
        => Walk(map, from, to, 0).LastOpen;

    private static (bool Clear, Point LastOpen) Walk(AreaMap map, Point from, Point to, double endSlack)
    {
        var points = AttackService.GetPointsOnLine(from.X, from.Y, to.X, to.Y).ToList();
        if (points.Count > 0 && (points[0].X != from.X || points[0].Y != from.Y))
        {
            points.Reverse();
        }

        var lastOpen = from;
        var leading = !IsWalkable(map, from);
        var trailing = endSlack > 0 && !IsWalkable(map, to);
        var skipped = 0;
        foreach (var point in points)
        {
            if (point.X == from.X && point.Y == from.Y)
            {
                continue;
            }

            if (IsWalkable(map, point))
            {
                leading = false;
                lastOpen = point;
                continue;
            }

            if (skipped < MaxSkippedCells
                && ((leading && point.Distance(from) <= EdgeSlack) || (trailing && point.Distance(to) <= endSlack)))
            {
                skipped++;
                continue;
            }

            return (false, lastOpen);
        }

        return (true, to);
    }

    /// <summary>
    /// A whirl from where he stands that sweeps any of <paramref name="reachable"/> comes first, whoever
    /// it hits; only when none does he walks beside the nearest one to whirl along the wall.
    /// </summary>
    /// <param name="refused">A direction from here a whirl just failed along (<see cref="FailedWhirls.Refuses"/>): dropped, the others still tried.</param>
    public static WhirlPlan PlanWhirl(AreaMap map, Point me, IReadOnlyList<Point> reachable, double minimumLength, Func<Point, bool> refused = null)
    {
        if (reachable.Count == 0)
        {
            return new WhirlPlan(null, 0, null);
        }

        var aim = WhirlwindPlanner.Aim(me, reachable, end => refused?.Invoke(end) == true ? me : ClipLane(map, me, end), minimumLength);
        if (aim.Target != null)
        {
            return new WhirlPlan(aim.Target, aim.Covered, null);
        }

        var nearest = reachable.MinBy(m => me.Distance(m));
        return FlankSpot(map, me, nearest, reachable) is { } flank
            ? new WhirlPlan(flank.End, flank.Covered, flank.Start)
            : new WhirlPlan(null, 0, null);
    }

    /// <summary>The monster <see cref="PlanWhirl"/> walks beside when nothing from here sweeps any.</summary>
    public static Point FlankTarget(Point me, IReadOnlyList<Point> reachable)
    {
        return reachable.MinBy(m => me.Distance(m));
    }

    private static readonly double[] FlankRadii = [3, 4, 5];

    /// <summary>Walks this close count as equal, and the spot sweeping more monsters wins.</summary>
    public const double FlankWalkTie = 2;

    /// <summary>
    /// Where to stand to whirl past a monster pressed against a wall when nothing from here sweeps it:
    /// a walkable spot a few units beside it, whose whirl through it runs on along the wall for at least
    /// <see cref="WhirlwindPlanner.MinimumSpin"/>. The shortest walk wins, so he gets there before the
    /// monster moves; more monsters swept only decides between walks within <see cref="FlankWalkTie"/>.
    /// </summary>
    public static FlankCandidate FlankSpot(AreaMap map, Point me, Point target, IReadOnlyList<Point> monsters)
        => BestFlank(FlankCandidates(map, target, monsters).Select(c => (c, me.Distance(c.Start))));

    /// <summary>Shortest walk first; walks within <see cref="FlankWalkTie"/> of each other go to the one sweeping more.</summary>
    public static FlankCandidate BestFlank(IEnumerable<(FlankCandidate Candidate, double Walk)> candidates)
    {
        FlankCandidate best = null;
        var bestWalk = double.MaxValue;
        foreach (var (candidate, walk) in candidates)
        {
            var aboutEqualWalk = Math.Abs(walk - bestWalk) <= FlankWalkTie;
            if (best == null
                || (!aboutEqualWalk && walk < bestWalk)
                || (aboutEqualWalk && (candidate.Covered > best.Covered || (candidate.Covered == best.Covered && walk < bestWalk))))
            {
                best = candidate;
                bestWalk = walk;
            }
        }

        return best;
    }

    /// <summary>
    /// The walkable cell the lanes run through: the monster's own, or for one standing half in the wall
    /// the nearest walkable cell within <see cref="EdgeSlack"/> of it.
    /// </summary>
    private static Point Foothold(AreaMap map, Point target)
    {
        if (IsWalkable(map, target))
        {
            return target;
        }

        Point best = null;
        var reach = (int)EdgeSlack;
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var x = target.X + dx;
                var y = target.Y + dy;
                if (x < 0 || y < 0 || x > ushort.MaxValue || y > ushort.MaxValue)
                {
                    continue;
                }

                var cell = new Point((ushort)x, (ushort)y);
                if (target.Distance(cell) <= EdgeSlack && IsWalkable(map, cell) && (best == null || target.Distance(cell) < target.Distance(best)))
                {
                    best = cell;
                }
            }
        }

        return best;
    }

    public static IReadOnlyList<FlankCandidate> FlankCandidates(AreaMap map, Point monster, IReadOnlyList<Point> monsters)
    {
        var found = new List<FlankCandidate>();
        var target = Foothold(map, monster);
        if (target == null)
        {
            return found;
        }

        foreach (var radius in FlankRadii)
        {
            for (var i = 0; i < 16; i++)
            {
                var angle = i * Math.PI / 8;
                var x = Math.Round(target.X + Math.Cos(angle) * radius);
                var y = Math.Round(target.Y + Math.Sin(angle) * radius);
                if (x < 0 || y < 0 || x > ushort.MaxValue || y > ushort.MaxValue)
                {
                    continue;
                }

                var start = new Point((ushort)x, (ushort)y);
                var toTarget = start.Distance(target);
                if (toTarget < 0.5 || !IsWalkable(map, start))
                {
                    continue;
                }

                var direction = (X: ((double)target.X - start.X) / toTarget, Y: ((double)target.Y - start.Y) / toTarget);
                var length = Math.Max(WhirlwindPlanner.MinimumSpin, toTarget + WhirlwindPlanner.Overshoot);
                var end = new Point(
                    (ushort)Math.Clamp(Math.Round(start.X + direction.X * length), 0, ushort.MaxValue),
                    (ushort)Math.Clamp(Math.Round(start.Y + direction.Y * length), 0, ushort.MaxValue));
                var clipped = ClipLane(map, start, end);
                var clippedLength = start.Distance(clipped);
                if (clippedLength < WhirlwindPlanner.MinimumSpin)
                {
                    continue;
                }

                var reach = clippedLength + WhirlwindPlanner.ClippedReach;
                if (!Sweeps(start, direction, monster, reach))
                {
                    continue;
                }

                found.Add(new FlankCandidate(start, clipped, monsters.Count(m => Sweeps(start, direction, m, reach))));
            }
        }

        return found;
    }

    private static bool Sweeps(Point from, (double X, double Y) direction, Point monster, double reach)
    {
        var dx = (double)monster.X - from.X;
        var dy = (double)monster.Y - from.Y;
        var along = dx * direction.X + dy * direction.Y;
        return along >= 0 && along <= reach
            && Math.Sqrt(Math.Max(0, dx * dx + dy * dy - along * along)) <= WhirlwindPlanner.SweepRadius;
    }

    private static bool IsWalkable(AreaMap map, Point point)
    {
        var row = point.Y - map.LevelOrigin.Y;
        var column = point.X - map.LevelOrigin.X;
        return row >= 0 && row < map.Map.Length && column >= 0 && column < map.Map[row].Length
            && AreaMapExtensions.IsMovable(map.Map[row][column]);
    }

    public static double PathLength(Point from, IReadOnlyList<Point> path)
    {
        var length = 0.0;
        var at = from;
        foreach (var point in path)
        {
            length += at.Distance(point);
            at = point;
        }

        return length;
    }

    public static bool IsShortDetour(double straight, double pathLength)
        => pathLength > 0 && pathLength < straight * MySidePathFactor;

    /// <summary>
    /// Nothing got hurt and he stayed put: under <see cref="BarelyMoved"/> if the server showed the
    /// whirl running, under <see cref="NowhereDistance"/> if it never did. Life only updates on hits,
    /// so a short real whirl is not judged on life alone.
    /// </summary>
    public static bool WentNowhere(Point before, Point after, double packLifeBefore, double packLifeAfter, bool started)
        => packLifeAfter >= packLifeBefore && before.Distance(after) < (started ? BarelyMoved : NowhereDistance);

    /// <summary>
    /// The nearest candidate on his side of the wall, and the nearer ones passed over because they are
    /// not; the nearest of all when none is on his side.
    /// </summary>
    public static async Task<(T Pick, IReadOnlyList<T> Skipped)> ChooseMySideFirst<T>(IEnumerable<T> candidates, Func<T, double> distance, Func<T, Task<bool>> onMySide)
        where T : class
    {
        var ordered = candidates.OrderBy(distance).ToList();
        var skipped = new List<T>();
        foreach (var candidate in ordered)
        {
            if (await onMySide(candidate))
            {
                return (candidate, skipped);
            }

            skipped.Add(candidate);
        }

        return (ordered.FirstOrDefault(), []);
    }
}

/// <summary>Whirl to <paramref name="Target"/>, first walking to <paramref name="WalkTo"/> when it is set; no target is no whirl.</summary>
public sealed record WhirlPlan(Point Target, int Covered, Point WalkTo);

public sealed record FlankCandidate(Point Start, Point End, int Covered);

/// <summary>
/// Whirls the server stopped although the map showed the lane open - an object the collision map does not have. The
/// direction of a failed whirl is refused for a few seconds while he stays where it failed; the monsters it aimed at stay
/// candidates, so one standing on top of him is still whirled through along another direction. Remembering the monsters
/// instead refused every whirl at a member at his feet and left him standing beside the pack.
/// </summary>
public sealed class FailedWhirls
{
    public static readonly TimeSpan Memory = TimeSpan.FromSeconds(3);

    public const double MovedOn = 2;

    /// <summary>A whirl this close in angle to a failed one runs into the same obstacle.</summary>
    public const double RefusedDegrees = 30;

    private readonly List<(Point From, Point Toward, DateTime At)> _failed = [];

    public void Record(Point from, Point toward, DateTime now)
    {
        lock (_failed)
        {
            _failed.RemoveAll(f => now - f.At >= Memory);
            _failed.Add((from, toward, now));
        }
    }

    /// <summary>Whether a whirl from <paramref name="me"/> toward <paramref name="end"/> runs within <see cref="RefusedDegrees"/> of one that just failed from about here.</summary>
    public bool Refuses(Point me, Point end, DateTime now)
    {
        lock (_failed)
        {
            return _failed.Any(f => now - f.At < Memory && me.Distance(f.From) <= MovedOn && SameDirection(f.From, f.Toward, me, end));
        }
    }

    public static bool SameDirection(Point failedFrom, Point failedToward, Point from, Point toward)
    {
        double ax = failedToward.X - failedFrom.X, ay = failedToward.Y - failedFrom.Y;
        double bx = toward.X - from.X, by = toward.Y - from.Y;
        var lengths = Math.Sqrt((ax * ax) + (ay * ay)) * Math.Sqrt((bx * bx) + (by * by));
        if (lengths < 1e-9)
        {
            return false;
        }

        return ((ax * bx) + (ay * by)) / lengths >= Math.Cos(RefusedDegrees * Math.PI / 180);
    }
}

/// <summary>
/// Whether a kill is getting anywhere: progress is getting closer to a target than ever before or
/// its life dropping, remembered per target, so walking past members across a wall and switching
/// between them does not count.
/// </summary>
public sealed class KillProgress(TimeSpan limit)
{
    private readonly Dictionary<uint, (double Closest, double Life)> _best = [];

    private DateTime? _lastProgress;

    /// <summary>True once nothing has progressed for longer than the limit.</summary>
    public bool Stalled(uint target, double distance, double life, DateTime now)
    {
        _lastProgress ??= now;
        if (!_best.TryGetValue(target, out var best))
        {
            _best[target] = (distance, life);
            _lastProgress = now;
            return false;
        }

        if (distance < best.Closest - 1 || life < best.Life)
        {
            _best[target] = (Math.Min(distance, best.Closest), Math.Min(life, best.Life));
            _lastProgress = now;
            return false;
        }

        return now - _lastProgress > limit;
    }

    /// <summary>The next target gets a full limit of its own.</summary>
    public void GaveUp(DateTime now) => _lastProgress = now;
}

/// <summary>
/// A mana potion refills over time, so a second one drunk while the first still runs is wasted.
/// After a drink the next waits until mana has climbed past the threshold or the wait is over.
/// </summary>
public sealed class ManaPotionPacer(int threshold, TimeSpan wait)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(1.5);

    private DateTime? _drankAt;

    public bool Waiting(int mana, DateTime now)
    {
        if (_drankAt is { } drankAt && (mana > threshold || now - drankAt >= wait))
        {
            _drankAt = null;
        }

        return _drankAt != null;
    }

    public void Drank(DateTime now) => _drankAt = now;
}
