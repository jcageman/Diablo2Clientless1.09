using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>Where a kiting walker steps to, and what it can see from there.</summary>
public sealed record StepSearch
{
    public Point From { get; init; }

    /// <summary>Every live hostile near the walker, at the positions its own client places them.</summary>
    public IReadOnlyList<Point> Cows { get; init; } = [];

    /// <summary>Lightning enchanted monsters and the Cow King near the walker: a step ends <see cref="Kiting.LightningEnchantedDistance"/> from them when it can.</summary>
    public IReadOnlyList<Point> LightningEnchanted { get; init; } = [];

    /// <summary>The lead, which a step leans towards; null when there is none.</summary>
    public Point Lead { get; init; }

    public Func<Point, bool> Walkable { get; init; } = _ => true;

    public Func<Point, Point, bool> InSight { get; init; } = (_, _) => true;
}

/// <param name="DeadEnd">No room is left after it (see <see cref="Kiting.Room"/>): the only step there was, and a portal in reach beats it.</param>
public sealed record KiteStep(Point Spot, bool Long, double Nearest, double Hop, bool DeadEnd = false);

/// <summary>A live cow near a weak walker, as the walker's own client sees it.</summary>
public readonly record struct CloseCow(double Distance, bool Enchanted = false, bool LightningEnchanted = false);

/// <summary>
/// Attack and run for the party's weak walkers. A Hell bovine burst took the amazon from 1236 life to
/// none in one update after she walked up to a pack to shoot from close: with 1310 life, the party's
/// lowest, no life rule saves her, only distance. So a weak walker keeps the cows at arm's length:
/// it shoots, and as soon as they close in it takes a short step back and shoots again.
/// </summary>
public static class Kiting
{
    /// <summary>A walker with less maximum life than this is weak whatever its class.</summary>
    public const int WeakMaxLife = 1600;

    /// <summary>No position a weak walker walks to on its own - a follow stop, an attack approach - is nearer a live cow than this.</summary>
    public const double KeepDistance = 8;

    /// <summary>Cows this close count as on the walker.</summary>
    public const double CrowdRadius = 5;

    /// <summary>
    /// This many plain cows on it and it steps back. One plain cow it attacks head on; when more arrive
    /// the real danger begins.
    /// </summary>
    public const int StepBackCows = 2;

    /// <summary>The danger level: a step that leaves this many cows on it is no step back at all.</summary>
    public const int DangerCows = 3;

    /// <summary>
    /// How far a weak walker stays from a lightning enchanted monster, which looses charged bolts every
    /// time it is hit, and from the Cow King. The dead amazon was shooting multiple shot from three units
    /// into the lightning enchanted King and his lightning enchanted guards. A charged bolt can fly 37
    /// units (velocity 12 for 98 frames in missiles.txt), but it wanders; this is the spread to keep out
    /// of, to tune from traces.
    /// </summary>
    public const double LightningEnchantedDistance = 15;

    /// <summary>A follow stop keeps this much more than <see cref="LightningEnchantedDistance"/>, so it does not land where the next tick steps back from.</summary>
    public const double LightningEnchantedMargin = 2;

    /// <summary>A lightning enchanted monster's neighbours this close to it are not shot at either: a stray arrow hits it.</summary>
    public const double LightningEnchantedNeighbour = 4;

    /// <summary>Short steps: a few units, so it is shooting again at once.</summary>
    public static readonly double[] ShortStepRadii = [3, 4.5, 6];

    /// <summary>With no free cell for a short step, a longer walk away from the pack.</summary>
    public static readonly double[] LongStepRadii = [8, 9.5, 11, 12];

    /// <summary>A step never passes nearer a cow than this, unless it walks straight away from it.</summary>
    public const double PassClearance = 3;

    /// <summary>
    /// What a way out passes a cow at, when the alternative is leaving the game. A cow already within
    /// <see cref="PassClearance"/> shuts every direction within a right angle of it, so two at 2.8 and 3 on
    /// either side shut the whole circle: the amazon left at full life with open ground east of her that
    /// led eleven units clear. The cows hit her from three anyway. Kite steps keep the wider berth: at 2.5
    /// they rated a spot two from a ledge, cows on three sides, as having room.
    /// </summary>
    public const double WayOutClearance = 2.5;

    /// <summary>Where a follow walk stops when the lead stands among cows: shooting range from the nearest.</summary>
    public const double ShootingRange = 12;

    public const int Directions = 16;

    /// <summary>How far the next step from a landing spot is tried, to judge the room it leaves.</summary>
    public const double RoomStep = 5;

    /// <summary>
    /// What each free direction out of a landing spot is worth, up to <see cref="RoomCap"/>. The amazon's
    /// steps each went away from the cows and towards a ledge until she stood two units from it, cows on the
    /// other three sides, and left the game at full life: three surrounded leaves in fifteen games, each
    /// costing her share of it. At two a spot by the ledge with one way on still outscored one with three.
    /// </summary>
    public const double RoomWeight = 4;

    /// <summary>
    /// Two free directions are room enough. Counting up to five outweighed the pull to the lead, and
    /// open ground away from the party beat a step towards it that still had a way on.
    /// </summary>
    public const int RoomCap = 2;

    /// <summary>
    /// How much further from the cows a direction out of a landing spot has to lead to count as room. A
    /// sideways step gains less the further the cows are: five units sideways from cows thirteen away on
    /// two sides gains 0.9, and a full unit made open ground a dead end.
    /// </summary>
    public const double RoomGain = 0.5;

    /// <summary>
    /// The party members that kite. Ranged and caster walkers do - the amazon and the necromancer - and
    /// any other walker too thin to stand in a pack. Not the sorceresses, which teleport out, and not
    /// the whirlwind barbarians, whose whirl is how they get out.
    /// </summary>
    public static bool IsWeakWalker(CharacterClass characterClass, int maxLife, bool whirls)
    {
        if (characterClass == CharacterClass.Sorceress || (characterClass == CharacterClass.Barbarian && whirls))
        {
            return false;
        }

        return characterClass is CharacterClass.Amazon or CharacterClass.Necromancer
            || (maxLife > 0 && maxLife < WeakMaxLife);
    }

    /// <summary>Whether a weak walker of this class attacks from range, so it can go on attacking while it keeps away.</summary>
    public static bool AttacksFromRange(CharacterClass characterClass)
        => characterClass is CharacterClass.Amazon or CharacterClass.Necromancer;

    /// <summary>
    /// Whether the cows have closed in enough to step back from them: <see cref="StepBackCows"/> of them on
    /// it, or one with enchantments - a champion, a unique, the Cow King - even alone, or anything
    /// lightning enchanted within <see cref="LightningEnchantedDistance"/>.
    /// </summary>
    public static bool ShouldStepBack(IReadOnlyCollection<CloseCow> cows)
        => cows.Count(cow => cow.Distance <= CrowdRadius) >= StepBackCows
            || cows.Any(cow => cow.Distance <= CrowdRadius && cow.Enchanted)
            || TooCloseToLightning(cows);

    /// <summary>Whether a lightning enchanted monster is within <see cref="LightningEnchantedDistance"/>.</summary>
    public static bool TooCloseToLightning(IEnumerable<CloseCow> cows)
        => cows.Any(cow => cow.LightningEnchanted && cow.Distance < LightningEnchantedDistance);

    /// <summary>
    /// Whether a weak walker is surrounded: the danger level reached - <see cref="DangerCows"/> cows on it,
    /// or an enchanted one and another - with no way out. Then it leaves the game before the damage arrives,
    /// the way a chicken does; never with a way out, never for one plain cow. Only cows it can see count.
    /// </summary>
    public static bool Surrounded(IReadOnlyCollection<CloseCow> cows, bool wayOut) => InDanger(cows) && !wayOut;

    /// <summary>The danger level: <see cref="DangerCows"/> cows on it, or an enchanted one and another.</summary>
    public static bool InDanger(IReadOnlyCollection<CloseCow> cows)
    {
        var close = cows.Where(cow => cow.Distance <= CrowdRadius).ToList();
        return close.Count >= DangerCows || (close.Count >= 2 && close.Any(cow => cow.Enchanted || cow.LightningEnchanted));
    }

    /// <summary>
    /// The walk out of a pack when <see cref="ChooseStep"/> has none: a walkable cell three to twelve units
    /// away, reached in a straight line over walkable ground without passing a cow, that gains a unit on the
    /// nearest cow, has none within <see cref="CrowdRadius"/> and is no nearer anything lightning enchanted.
    /// Unlike <see cref="ChooseStep"/> it does not ask for sight of a cow: this decides whether it leaves the
    /// game. Nor does the lead pull it: towards a lead standing in a pack, the pull outweighed the distance
    /// from the cows. Walked straight, never pathed: the line is what was checked.
    /// </summary>
    public static KiteStep WayOut(StepSearch search)
    {
        if (search.From == null)
        {
            return null;
        }

        var (open, deadEnd) = Best(search, [.. ShortStepRadii, .. LongStepRadii], NearestDistance(search.From, search.Cows),
            NearestDistance(search.From, search.LightningEnchanted), longStep: false, gainOnly: false, wayOut: true);
        return open ?? deadEnd;
    }

    public static bool HasWayOut(StepSearch search) => WayOut(search) != null;

    /// <summary>Whether a spot keeps the cows at <see cref="KeepDistance"/>.</summary>
    public static bool KeepsDistance(Point spot, IEnumerable<Point> cows) => NearestDistance(spot, cows) >= KeepDistance;

    /// <summary>
    /// A short step back: the free cell three to six units away that puts the most room between it and
    /// the nearest cow, never walking past one, still in sight of a cow to shoot, leaning towards the
    /// lead. When no short step gains room, or a lightning enchanted monster is too close for one, the
    /// same search eight to twelve units out, clear of the lightning enchanted. When fifteen from them is
    /// out of reach in one step, the step that gains the most distance from them: a walker that only
    /// steps when it can reach safety stood beside one, neither moving nor attacking.
    /// </summary>
    public static KiteStep ChooseStep(StepSearch search)
    {
        if (search.From == null)
        {
            return null;
        }

        var nearestNow = NearestDistance(search.From, search.Cows);
        var lightningNow = NearestDistance(search.From, search.LightningEnchanted);
        KiteStep deadEnd = null;
        foreach (var (open, dead) in Searches(search, nearestNow, lightningNow))
        {
            if (open != null)
            {
                return open;
            }

            deadEnd ??= dead;
        }

        // Room only chooses between steps. Without one she stood while the cows closed in, never
        // surrounded enough to leave: a step into a dead end at least gains the distance.
        return deadEnd;
    }

    private static IEnumerable<(KiteStep Open, KiteStep DeadEnd)> Searches(StepSearch search, double nearestNow, double lightningNow)
    {
        var lightningClose = lightningNow < LightningEnchantedDistance;
        if (!lightningClose)
        {
            yield return Best(search, ShortStepRadii, nearestNow, lightningNow, longStep: false, gainOnly: false);
        }

        yield return Best(search, LongStepRadii, nearestNow, lightningNow, longStep: true, gainOnly: false);
        if (lightningClose)
        {
            yield return Best(search, LongStepRadii.Concat(ShortStepRadii).ToArray(), nearestNow, lightningNow, longStep: true, gainOnly: true);
        }
    }

    private static (KiteStep Open, KiteStep DeadEnd) Best(StepSearch search, double[] radii, double nearestNow, double lightningNow, bool longStep, bool gainOnly, bool wayOut = false)
    {
        KiteStep open = null;
        KiteStep deadEnd = null;
        var openScore = double.MinValue;
        var deadEndScore = double.MinValue;
        foreach (var (spot, radius) in Candidates(search.From, radii))
        {
            var nearest = NearestDistance(spot, search.Cows);
            var lightning = NearestDistance(spot, search.LightningEnchanted);
            var crowd = search.Cows.Count(cow => cow.Distance(spot) <= CrowdRadius);
            bool enough;
            if (wayOut)
            {
                // Out of the pack, never nearer anything lightning enchanted, whatever the pull to the lead.
                enough = nearest > nearestNow + 1 && nearest > CrowdRadius
                    && lightning >= Math.Min(lightningNow, LightningEnchantedDistance);
            }
            else if (gainOnly)
            {
                // Out of reach of safety: any step that takes it further from the lightning enchanted.
                enough = lightning > lightningNow + 1 && crowd < DangerCows;
            }
            else
            {
                // A short step has to gain room and leave no crowd; a long one has to reach keeping
                // distance; either keeps clear of anything lightning enchanted.
                enough = (longStep ? nearest >= KeepDistance : nearest > nearestNow + 1 && crowd < DangerCows)
                    && lightning >= LightningEnchantedDistance;
            }

            if (!enough || !WalkClear(search, search.From, spot, wayOut ? WayOutClearance : PassClearance))
            {
                continue;
            }

            var target = search.Cows.Count == 0 ? null : search.Cows.MinBy(cow => cow.Distance(spot));
            if (!gainOnly && !wayOut && target != null && !search.InSight(spot, target))
            {
                continue;
            }

            // A spot with no step left out of it is a dead end, however far from the cows it is.
            var room = Room(search, spot);
            var leadPull = search.Lead == null || wayOut ? 0 : (search.From.Distance(search.Lead) - spot.Distance(search.Lead)) / 2;
            var score = (gainOnly
                ? Math.Min(lightning, LightningEnchantedDistance) + Math.Min(nearest, KeepDistance) + leadPull
                : Math.Min(nearest, KeepDistance + 4) + leadPull - (radius / 4))
                + (RoomWeight * Math.Min(room, RoomCap));
            if (room > 0 && score > openScore)
            {
                openScore = score;
                open = new KiteStep(spot, longStep, nearest, search.From.Distance(spot));
            }
            else if (room == 0 && score > deadEndScore)
            {
                deadEndScore = score;
                deadEnd = new KiteStep(spot, longStep, nearest, search.From.Distance(spot), DeadEnd: true);
            }
        }

        return (open, deadEnd);
    }

    private static IEnumerable<(Point Point, double Radius)> Candidates(Point from, IEnumerable<double> radii)
    {
        foreach (var radius in radii)
        {
            for (var direction = 0; direction < Directions; direction++)
            {
                var angle = 2 * Math.PI * direction / Directions;
                var x = from.X + (radius * Math.Cos(angle));
                var y = from.Y + (radius * Math.Sin(angle));
                if (x >= 0 && y >= 0 && x <= ushort.MaxValue && y <= ushort.MaxValue)
                {
                    yield return (new Point((ushort)Math.Round(x), (ushort)Math.Round(y)), radius);
                }
            }
        }
    }

    /// <summary>Walkable at the end and along the straight line there, and not walking past a cow.</summary>
    public static bool Reachable(StepSearch search, Point spot) => WalkClear(search, search.From, spot);

    /// <summary>
    /// The room a landing spot leaves: of eight directions, how many a further <see cref="RoomStep"/> from it
    /// can still take on the way out - walkable, past no cow, out of their crowd and <see cref="RoomGain"/>
    /// further from them. A spot with its back to a ledge has room along the ledge only while the cows are
    /// not there yet, and none once they are.
    /// </summary>
    public static int Room(StepSearch search, Point spot)
    {
        var here = NearestDistance(spot, search.Cows);
        var room = 0;
        for (var direction = 0; direction < 8; direction++)
        {
            var angle = 2 * Math.PI * direction / 8;
            var x = spot.X + (RoomStep * Math.Cos(angle));
            var y = spot.Y + (RoomStep * Math.Sin(angle));
            if (x < 0 || y < 0 || x > ushort.MaxValue || y > ushort.MaxValue)
            {
                continue;
            }

            var next = new Point((ushort)Math.Round(x), (ushort)Math.Round(y));
            var nearest = NearestDistance(next, search.Cows);
            if (nearest >= CrowdRadius && (search.Cows.Count == 0 || nearest > here + RoomGain) && WalkClear(search, spot, next))
            {
                room++;
            }
        }

        return room;
    }

    private static bool WalkClear(StepSearch search, Point from, Point to, double clearance = PassClearance)
    {
        if (!search.Walkable(to) || PassesCow(from, to, search.Cows, clearance))
        {
            return false;
        }

        var length = from.Distance(to);
        for (var along = 1.0; along < length; along += 1)
        {
            var share = along / length;
            var point = new Point(
                (ushort)Math.Round(from.X + ((to.X - from.X) * share)),
                (ushort)Math.Round(from.Y + ((to.Y - from.Y) * share)));
            if (!search.Walkable(point))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Where a weak walker's follow walk ends. Beside the lead, unless the lead stands among cows or near
    /// anything lightning enchanted: then on the line back from the lead to the walker, the first point at
    /// <see cref="ShootingRange"/> from every cow and <see cref="LightningEnchantedDistance"/> plus a margin
    /// from the lightning enchanted, and where it stands now when there is none.
    /// </summary>
    public static Point FollowDestination(Point lead, Point me, IReadOnlyCollection<Point> cows, IReadOnlyCollection<Point> lightningEnchanted = null)
    {
        lightningEnchanted ??= [];
        bool Clear(Point point) => NearestDistance(point, cows) >= ShootingRange
            && NearestDistance(point, lightningEnchanted) >= LightningEnchantedDistance + LightningEnchantedMargin;

        if (lead == null || me == null || Clear(lead))
        {
            return lead;
        }

        var length = lead.Distance(me);
        for (var along = 1.0; along <= length; along += 1)
        {
            var share = along / length;
            var point = new Point(
                (ushort)Math.Round(lead.X + ((me.X - lead.X) * share)),
                (ushort)Math.Round(lead.Y + ((me.Y - lead.Y) * share)));
            if (Clear(point))
            {
                return point;
            }
        }

        return me;
    }

    public static double NearestDistance(Point spot, IEnumerable<Point> cows)
    {
        var nearest = double.MaxValue;
        foreach (var cow in cows)
        {
            nearest = Math.Min(nearest, cow.Distance(spot));
        }

        return nearest;
    }

    /// <summary>
    /// Whether the straight walk from one spot to the other passes within <paramref name="clearance"/> of a
    /// cow. A cow it walks straight away from - nearest at the start of the walk - does not count.
    /// </summary>
    private static bool PassesCow(Point from, Point to, IEnumerable<Point> cows, double clearance)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared == 0)
        {
            return false;
        }

        foreach (var cow in cows)
        {
            var t = Math.Clamp((((cow.X - from.X) * dx) + ((cow.Y - from.Y) * dy)) / (double)lengthSquared, 0, 1);
            if (t <= 0)
            {
                continue;
            }

            var px = from.X + (t * dx) - cow.X;
            var py = from.Y + (t * dy) - cow.Y;
            if (Math.Sqrt((px * px) + (py * py)) < clearance)
            {
                return true;
            }
        }

        return false;
    }
}
