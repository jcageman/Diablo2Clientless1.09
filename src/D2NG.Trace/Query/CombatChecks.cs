using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// Attacks that cannot have done anything, from <see cref="CombatObserver"/>. Repeats of one kind less than
/// <see cref="Gap"/> apart are one incident with their count, time span, mana spent and target.
/// <list type="bullet">
/// <item><c>skill move went nowhere</c>: a skill move (Whirlwind, Charge, Leap, Teleport...) the server played out
/// moving the client under <see cref="Nowhere"/> with no monster hit along it; at least <see cref="MinimumMoves"/>, one incident
/// until a move hits something or <see cref="MoveGap"/> passes without one.</item>
/// <item><c>attack into a wall</c>: an attack at a unit, or at a point (judged by the nearest monster in its direction),
/// whose straight line from the caster crosses a subtile that blocks line of sight (<see cref="MapCollision.BlocksSight"/>:
/// a wall, not water or a ledge missiles fly over), and after which nothing near the target was hit within the skill's
/// hit window; at least <see cref="MinimumCasts"/>, one incident until a cast lands or <see cref="Gap"/> passes. Skill
/// moves are left to <c>skill move went nowhere</c>. Needs the map service.</item>
/// <item><c>attacks hitting nothing</c>: <see cref="MinimumRun"/> or more attacks in a row over <see cref="MinimumRunSpan"/> or more, none further than
/// <see cref="RunGap"/> from the one before, none followed by a hit near its target in its window - what the wall check
/// cannot prove (no map, a target out of sight, a target beyond the reach of the skill). Attacks already in an
/// <c>attack into a wall</c> incident are left out of it.</item>
/// </list>
/// </summary>
public sealed class CombatCheck : CompletedCheck
{
    public const string NowhereKind = "skill move went nowhere";
    public const string WallKind = "attack into a wall";
    public const string NothingKind = "attacks hitting nothing";

    public const double Nowhere = 3;

    /// <summary>A move asked for a point closer than this is not one that went nowhere when it moved under <see cref="Nowhere"/>.</summary>
    public const double NowhereAsked = 5;

    /// <summary>
    /// A skill move that went nowhere: it moved the client under <see cref="Nowhere"/> and hit nothing, though it asked for a
    /// point <see cref="NowhereAsked"/> or more away. A path's last hop to an NPC or a waypoint asks a few units off and
    /// lands short of <see cref="Nowhere"/> by design; counted, it made two of a batch's teleports going nowhere.
    /// </summary>
    public static bool WentNowhere(SkillMoveView view)
        => view.Moved < Nowhere && view.Hits.Count == 0
            && (view.Move.Asked == null || view.From == null || view.From.Distance(view.Move.Asked) >= NowhereAsked);

    public const int MinimumMoves = 3;
    public const int MinimumCasts = 3;
    public const int MinimumRun = 8;

    /// <summary>A run of attacks hitting nothing must last this long.</summary>
    public static readonly TimeSpan MinimumRunSpan = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RunGap = TimeSpan.FromSeconds(3);

    /// <summary>Skill moves that went nowhere are one incident while no move between them hit anything and none is this long after the one before.</summary>
    public static readonly TimeSpan MoveGap = TimeSpan.FromSeconds(15);

    public override string Family => "bot";

    public override IEnumerable<Incident> Run(CheckContext context)
    {
        var combat = context.Combat;
        if (combat == null)
        {
            return [];
        }

        var found = new List<Incident>();
        found.AddRange(WentNowhere(context, combat));
        var walled = IntoWalls(context, combat);
        found.AddRange(WallGroups(combat, walled).Where(g => g.Count >= MinimumCasts).Select(casts => WallIncident(context.Client, combat, casts, walled)));
        var inWall = walled.Select(w => w.Cast).ToHashSet();
        found.AddRange(HittingNothing(context.Client, combat, inWall));
        return found;
    }

    private static IEnumerable<Incident> WentNowhere(CheckContext context, CombatObserver combat)
    {
        var groups = new List<(List<SkillMoveView> Nowhere, int Others)>();
        List<SkillMoveView> current = null;
        var others = 0;
        var pendingOthers = 0;
        void Close()
        {
            if (current != null)
            {
                groups.Add((current, others));
            }

            current = null;
            others = 0;
            pendingOthers = 0;
        }

        foreach (var view in combat.Moves.Select(combat.View).Where(v => v.From is not null && v.To is not null))
        {
            if (view.Hits.Count > 0 || (current != null && current[^1].Move.Skill != view.Move.Skill))
            {
                Close();
                if (view.Hits.Count > 0)
                {
                    continue;
                }
            }

            if (!WentNowhere(view))
            {
                pendingOthers++;
                continue;
            }

            if (current != null && view.Move.Start - current[^1].Move.End > MoveGap)
            {
                Close();
            }

            if (current == null)
            {
                current = [];
                pendingOthers = 0;
            }

            others += pendingOthers;
            pendingOthers = 0;
            current.Add(view);
        }

        Close();
        foreach (var (group, between) in groups.Where(g => g.Nowhere.Count >= MinimumMoves))
        {
            var first = group[0];
            var last = group[^1];
            var spent = group.Sum(v => Math.Max(0, v.ManaSpent ?? 0));
            var asks = group.Sum(v => v.Move.Asks);
            var asked = Common(group.Select(v => v.Move.Asked));
            var blocked = BlockedPath(context, first.Move.Start, Common(group.Select(v => v.From)), asked);
            yield return new Incident(first.Move.Start, context.Client, NowhereKind,
                $"{group.Count} {first.Move.Skill} moves in {(last.Move.End - first.Move.Start).TotalSeconds:0.0}s moved the client {group.Average(v => v.Moved):0.0} on average (most {group.Max(v => v.Moved):0.0}) from about {Names.Point(Common(group.Select(v => v.From)))} and hit nothing"
                + $"{(between > 0 ? $" ({between} more in between moved further and hit nothing either)" : "")}; aimed mostly at {Names.Point(asked)}{(blocked is { } cell ? $", a path crossing unwalkable ground (e.g. ({cell.X}, {cell.Y}))" : "")}, asked {asks} times for them; {spent} mana spent on them, {combat.ManaSpent(first.Move.Start - TimeSpan.FromMilliseconds(100), last.Move.End + TimeSpan.FromMilliseconds(600))} in the span")
            { End = last.Move.End };
        }
    }

    /// <summary>The first subtile on the straight path from <paramref name="from"/> to <paramref name="to"/> that blocks walking - a skill move is stopped by any; null when none does or no map is to be had.</summary>
    private static (int X, int Y)? BlockedPath(CheckContext context, TimeSpan at, Point from, Point to)
    {
        if (context.Maps == null || from is null || to is null || context.Activity.AreaAt(at) is not { } area)
        {
            return null;
        }

        try
        {
            var grid = context.Maps.Grid(context.MapId, context.Difficulty, area);
            foreach (var (x, y) in Geometry.Between(from, to))
            {
                if (grid.Ground(x, y) is '#' or '~')
                {
                    return (x, y);
                }
            }
        }
        catch (MapUnavailableException)
        {
        }

        return null;
    }

    private static List<(AttackCast Cast, (int X, int Y) Wall)> IntoWalls(CheckContext context, CombatObserver combat)
    {
        var found = new List<(AttackCast, (int, int))>();
        if (context.Maps == null || combat.Casts.Count == 0)
        {
            return found;
        }

        var missing = new HashSet<Area>();
        var known = new Dictionary<(int, int, int, int, Area), (int X, int Y)?>();
        foreach (var cast in combat.Casts)
        {
            var target = cast.Target;
            if (CombatObserver.IsSkillMove(cast.Skill) || cast.From is null || target is null || cast.From.Distance(target) < 2 || context.Activity.AreaAt(cast.At) is not { } area || missing.Contains(area))
            {
                continue;
            }

            var key = (cast.From.X, cast.From.Y, target.X, target.Y, area);
            if (!known.TryGetValue(key, out var wall))
            {
                try
                {
                    var grid = context.Maps.Grid(context.MapId, context.Difficulty, area);
                    wall = null;
                    foreach (var (x, y) in Geometry.Between(cast.From, target))
                    {
                        if (grid.BlocksSight(x, y))
                        {
                            wall = (x, y);
                            break;
                        }
                    }
                }
                catch (MapUnavailableException e)
                {
                    missing.Add(area);
                    context.Notes.Add(e.WholeService ? $"attack-into-a-wall check skipped in areas with no cached map: {e.Message}" : $"attack-into-a-wall check skipped in {area}: {e.Message}");
                    continue;
                }

                known[key] = wall;
            }

            if (wall is { } cell && !combat.Landed(cast))
            {
                found.Add((cast, cell));
            }
        }

        return found;
    }

    /// <summary>Walled casts less than <see cref="Gap"/> apart, a group ending at any cast of this client that landed.</summary>
    private static List<List<AttackCast>> WallGroups(CombatObserver combat, List<(AttackCast Cast, (int X, int Y) Wall)> walled)
    {
        var flagged = walled.Select(w => w.Cast).ToHashSet();
        var groups = new List<List<AttackCast>>();
        List<AttackCast> current = null;
        foreach (var cast in combat.Casts)
        {
            if (flagged.Contains(cast))
            {
                if (current != null && cast.At - current[^1].At > Gap)
                {
                    current = null;
                }

                if (current == null)
                {
                    current = [];
                    groups.Add(current);
                }

                current.Add(cast);
            }
            else if (current != null && !CombatObserver.IsSkillMove(cast.Skill) && combat.Landed(cast))
            {
                current = null;
            }
        }

        return groups;
    }

    private static Incident WallIncident(string client, CombatObserver combat, List<AttackCast> casts, List<(AttackCast Cast, (int X, int Y) Wall)> walled)
    {
        var first = casts[0];
        var last = casts[^1];
        var walls = walled.Where(w => w.Cast.At >= first.At && w.Cast.At <= last.At).Select(w => w.Wall).ToList();
        var wall = walls.GroupBy(w => w).OrderByDescending(g => g.Count()).First().Key;
        var targets = casts.GroupBy(c => c.MeantName ?? $"the point {Names.Point(c.Aim)}").OrderByDescending(g => g.Count()).ToList();
        var spent = combat.ManaSpent(first.At, last.At + CombatObserver.HitWindow(last.Skill));
        return new Incident(first.At, client, WallKind,
            $"{casts.Count} {Skills(casts)} cast{(casts.Count == 1 ? "" : "s")} in {(last.At - first.At).TotalSeconds:0.0}s from {Names.Point(Common(casts.Select(c => c.From)))} at "
            + $"{string.Join(", ", targets.Take(3).Select(g => $"{g.Key} (x{g.Count()}{(g.First().Target is { } at ? $" at {Names.Point(at)}" : "")})"))}{(targets.Count > 3 ? $" and {targets.Count - 3} more" : "")}"
            + $" across unwalkable ground (e.g. ({wall.X}, {wall.Y})), none followed by a hit near the target within its window; {spent} mana spent in the span")
        { End = last.At };
    }

    private static IEnumerable<Incident> HittingNothing(string client, CombatObserver combat, HashSet<AttackCast> inWall)
    {
        var run = new List<AttackCast>();
        var runs = new List<List<AttackCast>>();
        foreach (var cast in combat.Casts)
        {
            if (inWall.Contains(cast))
            {
                continue;
            }

            if (combat.Landed(cast) || (run.Count > 0 && cast.At - run[^1].At > RunGap))
            {
                if (run.Count >= MinimumRun)
                {
                    runs.Add(run);
                }

                run = [];
                if (combat.Landed(cast))
                {
                    continue;
                }
            }

            run.Add(cast);
        }

        if (run.Count >= MinimumRun)
        {
            runs.Add(run);
        }

        foreach (var casts in runs.Where(r => r[^1].At - r[0].At >= MinimumRunSpan))
        {
            var first = casts[0];
            var last = casts[^1];
            var targets = casts.GroupBy(c => c.MeantName ?? (c.AtPoint ? $"the point {Names.Point(c.Aim)}" : "a unit out of sight")).OrderByDescending(g => g.Count()).ToList();
            var spent = combat.ManaSpent(first.At, last.At + CombatObserver.HitWindow(last.Skill));
            var nearest = casts.Where(c => c.From is not null && c.Target is not null).Select(c => c.From.Distance(c.Target)).DefaultIfEmpty(-1).Min();
            yield return new Incident(first.At, client, NothingKind,
                $"{casts.Count} {Skills(casts)} casts in {(last.At - first.At).TotalSeconds:0.0}s from about {Names.Point(Common(casts.Select(c => c.From)))} at "
                + $"{string.Join(", ", targets.Take(3).Select(g => $"{g.Key} (x{g.Count()})"))}{(targets.Count > 3 ? $" and {targets.Count - 3} more" : "")}"
                + $" and no monster near a target was hit or died within the window{(nearest >= 0 ? $"; the target was {nearest:0.0} away at the closest" : "")}; {spent} mana spent in the span")
            { End = last.At };
        }
    }

    private static string Skills(IEnumerable<AttackCast> casts)
        => string.Join("/", casts.GroupBy(c => c.Skill).OrderByDescending(g => g.Count()).Select(g => g.Key));

    private static Point Common(IEnumerable<Point> points)
        => points.Where(p => p is not null).GroupBy(p => (p.X, p.Y)).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault();
}
