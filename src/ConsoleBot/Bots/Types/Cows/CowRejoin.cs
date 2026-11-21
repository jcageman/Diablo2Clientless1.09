using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Bots.Types.Cows;

public enum RejoinOutcome
{
    /// <summary>Back into the game, through town, into the cow level.</summary>
    Rejoin,

    /// <summary>Stay out; the game goes on, or ends, as it would have without a rejoin.</summary>
    StayOut,

    /// <summary>Too many of the party left: end the game for everyone and take the next one.</summary>
    EndGame,
}

/// <summary>What is known when a party member's loop finds it out of the game.</summary>
public sealed record RejoinSituation
{
    public bool InGame { get; init; }

    public bool NextGameSet { get; init; }

    /// <summary>The hunt is over: its budget spent, or the level finished with its closing loot sweep.</summary>
    public bool RunOver { get; init; }

    public bool StrangerFlee { get; init; }

    /// <summary>The character died for real (its corpse was assigned), rather than chickened.</summary>
    public bool Died { get; init; }

    public int RejoinsSoFar { get; init; }

    /// <summary>Party members, this one included, out of the game or gone from it within <see cref="CowRejoin.MassLeaveWindow"/>; see <see cref="CowRejoin.CountRecentLeaves"/>.</summary>
    public int RecentLeaves { get; init; }

    /// <summary>Killing sorceresses still in the game.</summary>
    public int KillersInGame { get; init; }

    /// <summary>How long it was back from its last rejoin before this leave; null when it has not rejoined this game.</summary>
    public TimeSpan? SinceLastReturn { get; init; }

    /// <summary>A killing sorceress or the lead: losing it ends the game, so it keeps the normal rejoin cap.</summary>
    public bool EndsGameWhenGone { get; init; }

    /// <summary>What is left of the hunt's budget; null when nothing bounds it but the level running out.</summary>
    public TimeSpan? HuntTimeLeft { get; init; }
}

public static class CowRejoin
{
    public const int MaxRejoinsPerGame = 2;

    /// <summary>This many of the party leaving within <see cref="MassLeaveWindow"/> is a dangerous game, not a chicken.</summary>
    public const int MassLeaveCount = 3;

    public static readonly TimeSpan MassLeaveWindow = TimeSpan.FromSeconds(20);

    /// <summary>Leaving, rejoining, a short town visit and the walk back to the level.</summary>
    public static readonly TimeSpan ExpectedRejoinTime = TimeSpan.FromSeconds(35);

    /// <summary>Less hunting than this left after coming back is not worth the trip.</summary>
    public static readonly TimeSpan MinUsefulHunt = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Out again this soon after coming back is not rejoined again this game, and counts twice towards a
    /// mass leave. Two members rejoined without battle orders, walked into the same fight and left again
    /// three seconds after arriving, one of them at 114 life.
    /// </summary>
    public static readonly TimeSpan QuickRelapseWindow = TimeSpan.FromSeconds(30);

    /// <summary>How long a client waits after finding itself out before deciding, so the party's other leaves are seen.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Party members out of the game now, and members that left within <see cref="MassLeaveWindow"/>
    /// even if they are already back. A member that never joined this game - a late joiner still on
    /// its way - is not a leave.
    /// </summary>
    public static int CountRecentLeaves(IEnumerable<(bool Joined, bool InGame, DateTime? LeftAt)> members, DateTime now)
        => CountRecentLeaves(members.Select(member => (member.Joined, member.InGame, member.LeftAt, (DateTime?)null)), now);

    /// <summary>
    /// As above; a member that left within <see cref="QuickRelapseWindow"/> of coming back from a rejoin
    /// counts twice while that leave is within <see cref="MassLeaveWindow"/>, and once after.
    /// </summary>
    public static int CountRecentLeaves(IEnumerable<(bool Joined, bool InGame, DateTime? LeftAt, DateTime? ReturnedAt)> members, DateTime now)
        => members.Sum(member =>
        {
            var recent = member.LeftAt is { } left && now - left <= MassLeaveWindow;
            if (!member.Joined || (member.InGame && !recent))
            {
                return 0;
            }

            return recent && QuickRelapse(member.LeftAt, member.ReturnedAt) ? 2 : 1;
        });

    /// <summary>Whether a leave came within <see cref="QuickRelapseWindow"/> of the member's return from a rejoin.</summary>
    public static bool QuickRelapse(DateTime? leftAt, DateTime? returnedAt)
        => leftAt is { } left && returnedAt is { } back && left >= back && left - back < QuickRelapseWindow;

    public static (RejoinOutcome Outcome, string Reason) Decide(RejoinSituation s)
    {
        if (s.InGame)
        {
            return (RejoinOutcome.StayOut, "still in the game");
        }

        if (s.StrangerFlee)
        {
            return (RejoinOutcome.StayOut, "fleeing a stranger");
        }

        if (s.NextGameSet)
        {
            return (RejoinOutcome.StayOut, "the game is ending");
        }

        // Before the death check: a death in a mass leave is one more reason to end the game, not a
        // reason to leave it running.
        if (s.KillersInGame == 0)
        {
            return (RejoinOutcome.EndGame, "both killing sorceresses are out");
        }

        if (s.RecentLeaves >= MassLeaveCount)
        {
            return (RejoinOutcome.EndGame, $"{s.RecentLeaves} of the party left within {MassLeaveWindow.TotalSeconds:F0}s");
        }

        if (s.RunOver)
        {
            return (RejoinOutcome.StayOut, "the level is finished");
        }

        if (s.Died)
        {
            return (RejoinOutcome.StayOut, "died");
        }

        if (!s.EndsGameWhenGone && s.SinceLastReturn is { } sinceReturn && sinceReturn < QuickRelapseWindow)
        {
            return (RejoinOutcome.StayOut, $"left again {sinceReturn.TotalSeconds:F0}s after coming back");
        }

        if (s.RejoinsSoFar >= MaxRejoinsPerGame)
        {
            return (RejoinOutcome.StayOut, $"already rejoined {s.RejoinsSoFar} times");
        }

        if (s.HuntTimeLeft is { } left && left < ExpectedRejoinTime + MinUsefulHunt)
        {
            return (RejoinOutcome.StayOut, $"only {left.TotalSeconds:F0}s of hunting left");
        }

        return (RejoinOutcome.Rejoin, "left the game");
    }
}
