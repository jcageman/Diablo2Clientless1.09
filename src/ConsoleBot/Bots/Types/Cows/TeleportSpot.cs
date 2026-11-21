using D2NG.Core.D2GS;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>A live cow near the sorceress: where the clients place it now, and the pack it belongs to.</summary>
public readonly record struct NearbyCow(Point Location, uint Pack);

/// <summary>Where a sorceress going for a pack teleports to, and what she sees from there.</summary>
public sealed record SpotSearch
{
    public Point From { get; init; }

    /// <summary>Every live cow nearby she can hurt, the targeted pack and its neighbours alike.</summary>
    public IReadOnlyList<NearbyCow> Cows { get; init; } = [];

    /// <summary>Every live monster nearby, harmable or not: none may be inside the clearance on landing.</summary>
    public IReadOnlyList<Point> Monsters { get; init; } = [];

    /// <summary>Lightning enchanted monsters and the Cow King: no landing within <see cref="TeleportSpot.DangerDistance"/> of one.</summary>
    public IReadOnlyList<Point> Dangers { get; init; } = [];

    /// <summary>The radius the landing is scored on: static range, or the nova radius when she goes in for nova.</summary>
    public double Radius { get; init; }

    /// <summary>The nearest cow must be at least this far from the landing spot, more for a big wave; see <see cref="TeleportSpot.WaveClearance"/>.</summary>
    public double Clearance { get; init; }

    /// <summary>Grow the clearance with the wave; an escape asks only for the base clearance.</summary>
    public bool WaveSized { get; init; } = true;

    public Func<Point, bool> Walkable { get; init; } = _ => true;

    public Func<Point, Point, bool> InSight { get; init; } = (_, _) => true;
}

public sealed record SpotChoice(Point Spot, int CowsInRange, int PacksInRange, double Nearest, double Hop);

public static class TeleportSpot
{
    /// <summary>One teleport reaches about this far; anything longer has to be pathed.</summary>
    public const double MaxHop = 30;

    /// <summary>A landing past one teleport has to cover this many more cows to beat one within it.</summary>
    public const int LongHopCostInCows = 2;

    /// <summary>Spacing of the rings the candidates are laid on.</summary>
    public const double RingStep = 3;

    public const int Directions = 16;

    /// <summary>
    /// How far a landing stays from anything lightning enchanted: the distance at which the sorceress
    /// already gives a pack up. Teleported onto a pack's drops while the others' novas set off its
    /// charged bolts, she went from 2513 life to none in under 0.4s.
    /// </summary>
    public const double DangerDistance = 30;

    /// <summary>
    /// A big pack arrives as a wave, and ten units in front of 46 cows was not room for one cast: the
    /// clearance grows by this much for every ten cows within <see cref="WaveRadius"/> of the landing.
    /// </summary>
    public const double WaveClearancePerTenCows = 1;

    public const double WaveRadius = 20;

    public const double MaxWaveClearance = 5;

    /// <summary>The clearance a landing needs with this many cows within <see cref="WaveRadius"/> of it.</summary>
    public static double WaveClearance(double clearance, int cowsNear)
        => clearance + Math.Min(cowsNear / 10.0 * WaveClearancePerTenCows, MaxWaveClearance);

    /// <summary>Packs further apart than this are not neighbours: no landing covers both.</summary>
    public const double NeighbourPackDistance = 60;

    /// <summary>
    /// Room for one static cast after landing, or for the Frost Nova timing: cows walk in for the
    /// teleport's cast and one more before they reach.
    /// </summary>
    public static double Clearance(TimeSpan castTime) => SorceressRotation.AttackReach(castTime) + (SorceressRotation.CowWalkSpeed * castTime.TotalSeconds);

    /// <summary>
    /// The walkable spot in sight of the nearest cow that has the most live cows inside the radius while
    /// the nearest is still out of reach; more packs covered, then the shorter hop, break ties. Teleporting
    /// ten short of the nearest cow on the line in put her at the edge of whatever stood nearest, with the
    /// rest of the pack beyond static range, or straight into it when that point was out of sight. Spots
    /// between neighbouring packs are candidates too: one that reaches both beats the far side of one.
    /// </summary>
    public static SpotChoice Choose(SpotSearch search)
    {
        if (search.From == null || search.Cows.Count == 0 || search.Radius <= search.Clearance)
        {
            return null;
        }

        // A spread pack of thirty or more leaves no ring spot with the full wave clearance; the base
        // clearance beats falling back to a teleport ten short of the nearest cow.
        return (search.WaveSized ? Best(search, waveSized: true) : null) ?? Best(search, waveSized: false);
    }

    private static SpotChoice Best(SpotSearch search, bool waveSized)
    {
        SpotChoice best = null;
        var bestScore = int.MinValue;
        foreach (var candidate in Candidates(search))
        {
            var nearestCow = search.Cows.MinBy(cow => cow.Location.Distance(candidate)).Location;
            var nearest = Math.Min(nearestCow.Distance(candidate), search.Monsters.Count == 0 ? double.MaxValue : search.Monsters.Min(monster => monster.Distance(candidate)));
            var wave = Math.Max(
                search.Cows.Count(cow => cow.Location.Distance(candidate) <= WaveRadius),
                search.Monsters.Count(monster => monster.Distance(candidate) <= WaveRadius));
            if (nearest < (waveSized ? WaveClearance(search.Clearance, wave) : search.Clearance)
                || search.Dangers.Any(danger => danger.Distance(candidate) < DangerDistance)
                || !search.Walkable(candidate) || !search.InSight(candidate, nearestCow))
            {
                continue;
            }

            var covered = search.Cows.Where(cow => cow.Location.Distance(candidate) <= search.Radius).ToList();
            if (covered.Count == 0)
            {
                continue;
            }

            var packs = covered.Select(cow => cow.Pack).Distinct().Count();
            var hop = search.From.Distance(candidate);
            var score = covered.Count - (hop > MaxHop ? LongHopCostInCows : 0);
            if (best == null || score > bestScore
                || (score == bestScore && (packs > best.PacksInRange || (packs == best.PacksInRange && hop < best.Hop))))
            {
                best = new SpotChoice(candidate, covered.Count, packs, nearest, hop);
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Shorter hops are not worth a cast.</summary>
    public const double MinHop = 3;

    /// <summary>On an escape, every this many units further from the cows that were on her count as one more cow in range.</summary>
    public const double AwayUnitsPerCow = 5;

    /// <summary>A long hop lands where no monster is within this.</summary>
    public const double LongHopClearance = 15;

    public const double LongHopMin = 15;

    /// <summary>
    /// Where to escape to: within one teleport, the nearest monster at least the clearance away, nothing
    /// dangerous near; the most cows still in static range so she casts on, weighed against getting away
    /// from <paramref name="awayFrom"/>, where the cows that were on her stand.
    /// </summary>
    public static SpotChoice ChooseEscape(SpotSearch search, Point awayFrom)
    {
        if (search.From == null)
        {
            return null;
        }

        SpotChoice best = null;
        var bestScore = double.MinValue;
        foreach (var candidate in Candidates(search).Concat(Ring(search.From, 10, MaxHop, 4)))
        {
            var hop = search.From.Distance(candidate);
            if (hop < MinHop || hop > MaxHop || !Clear(search, candidate, search.Clearance, out var nearest))
            {
                continue;
            }

            var covered = search.Cows.Where(cow => cow.Location.Distance(candidate) <= search.Radius).ToList();
            var score = covered.Count + ((awayFrom?.Distance(candidate) ?? 0) / AwayUnitsPerCow);
            if (best == null || score > bestScore || (score == bestScore && hop < best.Hop))
            {
                best = new SpotChoice(candidate, covered.Count, covered.Select(cow => cow.Pack).Distinct().Count(), nearest, hop);
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// After escapes in quick succession: one long hop, fifteen to thirty units, to where no monster is
    /// near, as far from <paramref name="awayFrom"/> as that allows.
    /// </summary>
    public static SpotChoice ChooseLongHop(SpotSearch search, Point awayFrom)
    {
        if (search.From == null)
        {
            return null;
        }

        SpotChoice best = null;
        var bestAway = double.MinValue;
        foreach (var candidate in Ring(search.From, LongHopMin, MaxHop, RingStep))
        {
            if (!Clear(search, candidate, LongHopClearance, out var nearest))
            {
                continue;
            }

            var hop = search.From.Distance(candidate);
            if (hop > MaxHop)
            {
                continue;
            }

            var away = awayFrom?.Distance(candidate) ?? hop;
            if (best == null || away > bestAway || (away == bestAway && hop < best.Hop))
            {
                best = new SpotChoice(candidate, search.Cows.Count(cow => cow.Location.Distance(candidate) <= search.Radius), 0, nearest, hop);
                bestAway = away;
            }
        }

        return best;
    }

    /// <summary>A danger within <see cref="DangerDistance"/> of a least-bad pick costs it this many units of distance from the nearest monster.</summary>
    public const double DangerPenalty = 20;

    /// <summary>
    /// When neither an escape spot nor a long hop exists - a wave with packs on both sides - the walkable
    /// spot within one teleport that is furthest from every monster, dangers counted against it rather
    /// than ruled out. Standing still is the one thing worse.
    /// </summary>
    public static SpotChoice ChooseLeastBad(SpotSearch search)
    {
        if (search.From == null)
        {
            return null;
        }

        SpotChoice best = null;
        var bestScore = double.MinValue;
        foreach (var candidate in Ring(search.From, MinHop, MaxHop - 1, RingStep))
        {
            if (!search.Walkable(candidate))
            {
                continue;
            }

            var nearest = Math.Min(
                search.Cows.Count == 0 ? double.MaxValue : search.Cows.Min(cow => cow.Location.Distance(candidate)),
                search.Monsters.Count == 0 ? double.MaxValue : search.Monsters.Min(monster => monster.Distance(candidate)));
            var score = Math.Min(nearest, 1000) - (DangerPenalty * search.Dangers.Count(danger => danger.Distance(candidate) < DangerDistance));
            if (best == null || score > bestScore)
            {
                best = new SpotChoice(candidate, search.Cows.Count(cow => cow.Location.Distance(candidate) <= search.Radius), 0, nearest, search.From.Distance(candidate));
                bestScore = score;
            }
        }

        return best;
    }

    private static bool Clear(SpotSearch search, Point candidate, double clearance, out double nearest)
    {
        nearest = Math.Min(
            search.Cows.Count == 0 ? double.MaxValue : search.Cows.Min(cow => cow.Location.Distance(candidate)),
            search.Monsters.Count == 0 ? double.MaxValue : search.Monsters.Min(monster => monster.Distance(candidate)));
        return nearest >= clearance
            && !search.Dangers.Any(danger => danger.Distance(candidate) < DangerDistance)
            && search.Walkable(candidate);
    }

    private static IEnumerable<Point> Ring(Point centre, double from, double to, double step)
    {
        for (var radius = from; radius <= to; radius += step)
        {
            for (var direction = 0; direction < Directions; direction++)
            {
                var angle = 2 * Math.PI * direction / Directions;
                if (At(centre.X + (radius * Math.Cos(angle)), centre.Y + (radius * Math.Sin(angle))) is { } point)
                {
                    yield return point;
                }
            }
        }
    }

    /// <summary>Rings around every pack's centre, and around and along the line between neighbouring packs.</summary>
    private static IEnumerable<Point> Candidates(SpotSearch search)
    {
        var centres = search.Cows
            .GroupBy(cow => cow.Pack)
            .Select(pack => MonsterTracking.Centroid(pack.Select(cow => cow.Location).ToList(), pack.First().Location))
            .ToList();

        var anchors = new List<Point>(centres);
        for (var i = 0; i < centres.Count; i++)
        {
            for (var j = i + 1; j < centres.Count; j++)
            {
                if (centres[i].Distance(centres[j]) > NeighbourPackDistance)
                {
                    continue;
                }

                for (var step = 1; step <= 3; step++)
                {
                    anchors.Add(Between(centres[i], centres[j], step / 4.0));
                }
            }
        }

        foreach (var anchor in anchors)
        {
            yield return anchor;
            for (var radius = search.Clearance; radius <= search.Radius + MaxWaveClearance; radius += RingStep)
            {
                for (var direction = 0; direction < Directions; direction++)
                {
                    var angle = 2 * Math.PI * direction / Directions;
                    if (At(anchor.X + (radius * Math.Cos(angle)), anchor.Y + (radius * Math.Sin(angle))) is { } point)
                    {
                        yield return point;
                    }
                }
            }
        }
    }

    private static Point Between(Point a, Point b, double share) => At(a.X + ((b.X - a.X) * share), a.Y + ((b.Y - a.Y) * share));

    private static Point At(double x, double y)
        => x >= 0 && y >= 0 && x <= ushort.MaxValue && y <= ushort.MaxValue ? new Point((ushort)Math.Round(x), (ushort)Math.Round(y)) : null;
}
