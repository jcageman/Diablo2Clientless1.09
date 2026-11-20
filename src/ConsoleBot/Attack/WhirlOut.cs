using D2NG.Core.D2GS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Attack;

/// <summary>
/// A whirlwind barbarian on his way out of a pack whirls instead of walking. The hunt lead, a dozen cows
/// within three of him, walked towards the party twice: the server held him in place both times while
/// his life went from 75% to 16% and he left the game. A whirl carries him through monsters; a walk does not.
/// </summary>
public static class WhirlOut
{
    /// <summary>A monster this close to the line is in the way.</summary>
    public const double PathClearance = 3;

    /// <summary>How far along the way one whirl goes; the rest is the next round's.</summary>
    public const double WhirlLength = 12;

    /// <summary>At or below this mana a whirl stops half way.</summary>
    public const int ManaFloor = 30;

    /// <summary>A walk the server placed this far from where the client believes it ended was held.</summary>
    public const double HeldDistance = 5;

    /// <summary>How long a held walk counts for the next move.</summary>
    public static readonly TimeSpan HeldMemory = TimeSpan.FromSeconds(3);

    /// <summary>Whirls in one round, before the walk: the party's retreat runs fifty units. <see cref="MaxWhirlsPerWay"/> bounds the rounds.</summary>
    public const int MaxWhirls = 3;

    /// <summary>
    /// How many monsters in the way make a pack that closes it. One or two do not block a walk: of 116
    /// whirl-outs in nine games, 85 were on one or two in the way with the last walk not held.
    /// </summary>
    public const int PackInTheWay = 3;

    /// <summary>The level from which a barbarian whirls rather than swings, as his assist has it: above 33 in Nightmare and Hell, above 40 in Normal.</summary>
    public static bool Whirls(bool hasWhirlwind, int level, bool normal)
        => hasWhirlwind && (normal ? level > 40 : level > 33);

    /// <summary>
    /// Whether to whirl the way rather than walk it: a barbarian who whirls, the mana for it, and a pack in the way, his last
    /// walk held or a monster attacking him. Walking with cows on him is what drops his life; a whirl leeches it back and
    /// carries him out, so a barbarian backs out of a fight by whirling and walks once nothing is on him.
    /// </summary>
    public static bool ShouldWhirl(bool whirls, int mana, int inTheWay, bool lastWalkHeld, bool underAttack = false)
        => whirls && mana > ManaFloor && (inTheWay >= PackInTheWay || lastWalkHeld || (underAttack && mana > AttackedManaReserve));

    /// <summary>
    /// Mana a whirl out for an attack alone leaves him: the cows on him stand beside or behind, so the whirl often hits
    /// nothing and leeches nothing back, at about 23 mana, and the fight he is backing out of needs the rest.
    /// </summary>
    public const int AttackedManaReserve = 100;

    /// <summary>Whirls on one way, all rounds together, before a walk under attack is no longer broken off for another.</summary>
    public const int MaxWhirlsPerWay = 10;

    /// <summary>The monsters ahead of him within <see cref="PathClearance"/> of the whirl from <paramref name="from"/> to <paramref name="end"/>; one behind him is not in the way.</summary>
    public static int InTheWay(Point from, Point end, IEnumerable<Point> monsters)
    {
        double dx = end.X - from.X;
        double dy = end.Y - from.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared == 0)
        {
            return 0;
        }

        return monsters.Count(monster =>
        {
            var t = (((monster.X - from.X) * dx) + ((monster.Y - from.Y) * dy)) / lengthSquared;
            if (t <= 0)
            {
                return false;
            }

            t = Math.Min(t, 1);
            var x = from.X + (t * dx) - monster.X;
            var y = from.Y + (t * dy) - monster.Y;
            return Math.Sqrt((x * x) + (y * y)) < PathClearance;
        });
    }

    /// <summary>
    /// Where a whirl along the walking way can end, furthest first: the point <see cref="WhirlLength"/> along
    /// the path, then the path's turns before it. A whirl is a straight line, and the straight line to a
    /// destination round a fence ends in the pocket against it; the caller takes the first with a clear lane.
    /// </summary>
    public static IReadOnlyList<Point> Ends(Point from, IReadOnlyList<Point> path)
    {
        var ends = new List<Point>();
        var at = from;
        var walked = 0.0;
        foreach (var point in path)
        {
            var leg = at.Distance(point);
            if (walked + leg >= WhirlLength)
            {
                var share = leg == 0 ? 0 : (WhirlLength - walked) / leg;
                ends.Add(new Point(
                    (ushort)Math.Round(at.X + ((point.X - at.X) * share)),
                    (ushort)Math.Round(at.Y + ((point.Y - at.Y) * share))));
                break;
            }

            walked += leg;
            ends.Add(point);
            at = point;
        }

        ends.Reverse();
        return ends;
    }

    /// <summary>
    /// Whether a walk was held: the client believes it went from <paramref name="from"/> to <paramref name="believedEnd"/>,
    /// and the server, after it, placed the walker <see cref="HeldDistance"/> or more from there and nearer where it
    /// started. A move of his own after the walk - a whirl, a pickup - also puts him far from the walk's end, but not back at its start.
    /// </summary>
    public static bool WalkHeld(Point from, Point believedEnd, Point confirmed)
        => from != null && believedEnd != null && confirmed != null
            && from.Distance(believedEnd) > HeldDistance && confirmed.Distance(believedEnd) > HeldDistance
            && confirmed.Distance(from) < confirmed.Distance(believedEnd);

    /// <summary>
    /// Up to <see cref="MaxWhirls"/> whirls, each only after <paramref name="stop"/> and <paramref name="arrived"/>
    /// said no, until one does not happen; how many did. It yields first: a whirl blocks, and the caller's own
    /// checks - the party retreating, the lead moving on - run between them.
    /// </summary>
    public static async Task<int> WhirlAlong(Func<bool> stop, Func<bool> arrived, Func<Task<bool>> whirl)
    {
        var whirls = 0;
        while (whirls < MaxWhirls)
        {
            await Task.Yield();
            if (stop() || arrived() || !await whirl())
            {
                break;
            }

            whirls++;
        }

        return whirls;
    }
}
