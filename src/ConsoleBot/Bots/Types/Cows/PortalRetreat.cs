using System;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// A portal to town within reach is a better way out of danger than leaving the game. The amazon came
/// through the red portal into eight cows at three to six units and left the game 0.65 s later as
/// surrounded with no way out, with the portal she had just used right under her. Better still is not
/// coming through into a pack at all: a weak walker waits in town until the landing is clear.
/// </summary>
public static class PortalRetreat
{
    /// <summary>A portal this close is taken instead of leaving the game.</summary>
    public const double PortalReach = 10;

    /// <summary>
    /// A portal this close is under her feet: taken whatever cows stand by the line to it. Both of the
    /// amazon's surrounded leaves in fifteen games came 0.3 s after arriving with ten and thirteen cows
    /// on the landing and the portal she came through two units away; the walk to it passed a cow, so
    /// she left the game instead.
    /// </summary>
    public const double UnderFeet = 3;

    /// <summary>
    /// The longest a weak walker waits in town for the landing to be reported clear before it enters
    /// the level anyway.
    /// </summary>
    public static readonly TimeSpan MaxEntryWait = TimeSpan.FromSeconds(20);

    /// <summary>The least time in town before going back, even when the landing looks clear.</summary>
    public static readonly TimeSpan MinWait = TimeSpan.FromSeconds(3);

    /// <summary>With nobody able to see the landing, it goes back after this.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    /// <summary>While the landing is reported busy it waits on, but never longer than this.</summary>
    public static readonly TimeSpan MaxBusyWait = TimeSpan.FromSeconds(60);

    /// <summary>Portal retreats a member makes in one game; past this the old way out applies.</summary>
    public const int MaxRetreatsPerGame = 3;

    /// <summary>A retreat this soon after coming back met danger again: the next return takes the other way in.</summary>
    public static readonly TimeSpan RelapseWindow = TimeSpan.FromSeconds(15);

    /// <summary>How near the landing a hostile counts as on it, by the other party members' view.</summary>
    public const double LandingRadius = 10;

    /// <summary>The landing is clear with at most this many hostiles on it.</summary>
    public const int ClearHostiles = 1;

    /// <summary>
    /// Whether to take the portal: one within <see cref="PortalReach"/>, and a weak walker at its danger
    /// level, or any member under the retreat line with something on it.
    /// </summary>
    public static bool ShouldRetreat(bool weakWalkerInDanger, bool belowRetreatLine, bool hostileNear, double? portalDistance)
        => portalDistance is double distance && distance <= PortalReach
            && (weakWalkerInDanger || (belowRetreatLine && hostileNear));

    /// <summary>
    /// Whether to go back: the landing reported clear after <see cref="MinWait"/>; with nobody able to see
    /// it (<paramref name="hostilesAtLanding"/> null), after <see cref="MaxWait"/>; while it is reported
    /// busy, not before <see cref="MaxBusyWait"/>.
    /// </summary>
    public static bool ShouldReturn(TimeSpan waited, int? hostilesAtLanding)
        => hostilesAtLanding switch
        {
            int hostiles when hostiles <= ClearHostiles => waited >= MinWait,
            int => waited >= MaxBusyWait,
            null => waited >= MaxWait,
        };

    /// <summary>Whether the way to the portal allows taking it: under her feet, or a walk that passes no cow.</summary>
    public static bool WayToPortalClear(double portalDistance, bool walkPassesNoCow)
        => portalDistance <= UnderFeet || walkPassesNoCow;

    /// <summary>
    /// Whether a weak walker enters the level through the portal now: the members in the level report at
    /// most <see cref="ClearHostiles"/> on its landing, as <see cref="ShouldReturn"/> asks; or none of them
    /// can see it (<paramref name="hostilesAtLanding"/> null), when entering as before is all there is; or
    /// it has waited <see cref="MaxEntryWait"/>.
    /// </summary>
    public static bool ShouldEnter(TimeSpan waited, int? hostilesAtLanding)
        => hostilesAtLanding is not int hostiles || hostiles <= ClearHostiles || waited >= MaxEntryWait;

    /// <summary>Whether a member may still retreat by portal this game.</summary>
    public static bool RetreatsLeft(int retreatsSoFar) => retreatsSoFar < MaxRetreatsPerGame;

    /// <summary>Whether this retreat came soon after the last return, so its return takes the other way in.</summary>
    public static bool MetDangerAgain(TimeSpan? sinceLastReturn) => sinceLastReturn is { } since && since < RelapseWindow;
}
