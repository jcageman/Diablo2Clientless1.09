using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class CowRejoinTests
{
    /// <summary>One party member chickened, everyone else fighting, plenty of hunt left.</summary>
    private static RejoinSituation SingleChicken() => new()
    {
        InGame = false,
        RecentLeaves = 1,
        KillersInGame = 2,
        HuntTimeLeft = TimeSpan.FromMinutes(4),
    };

    [Fact]
    public void Out_again_soon_after_coming_back_is_not_rejoined_again()
    {
        // Back without battle orders, both left again three seconds after arriving.
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { RejoinsSoFar = 1, SinceLastReturn = TimeSpan.FromSeconds(3) }).Outcome);
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(SingleChicken() with { RejoinsSoFar = 1, SinceLastReturn = TimeSpan.FromSeconds(45) }).Outcome);
    }

    [Fact]
    public void A_quick_second_leave_counts_twice_towards_a_mass_leave()
    {
        var now = new DateTime(2026, 11, 17, 19, 12, 8);
        var quick = (true, false, (DateTime?)now.AddSeconds(-1), (DateTime?)now.AddSeconds(-4));
        var other = (true, false, (DateTime?)now.AddSeconds(-2), (DateTime?)null);
        var inGame = (true, true, (DateTime?)null, (DateTime?)null);

        var count = CowRejoin.CountRecentLeaves([quick, other, inGame], now);

        // Two members out, one of them three seconds after coming back: three, and the game ends.
        Assert.Equal(3, count);
        Assert.Equal(RejoinOutcome.EndGame, CowRejoin.Decide(SingleChicken() with { RecentLeaves = count }).Outcome);

        // Without the quick relapse the same two leaves are two, and the chicken rejoins.
        var plain = (true, false, (DateTime?)now.AddSeconds(-1), (DateTime?)null);
        var plainCount = CowRejoin.CountRecentLeaves([plain, other, inGame], now);
        Assert.Equal(2, plainCount);
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(SingleChicken() with { RecentLeaves = plainCount }).Outcome);
    }

    [Fact]
    public void Out_two_minutes_after_a_quick_relapse_counts_once()
    {
        var now = new DateTime(2026, 11, 17, 19, 14, 8);
        var members = new (bool Joined, bool InGame, DateTime? LeftAt, DateTime? ReturnedAt)[]
        {
            (true, false, now.AddMinutes(-2), now.AddMinutes(-2).AddSeconds(-3)),
        };

        Assert.Equal(1, CowRejoin.CountRecentLeaves(members, now));
    }

    [Fact]
    public void A_killer_or_the_lead_keeps_the_normal_cap_after_a_quick_relapse()
    {
        var quickly = SingleChicken() with { RejoinsSoFar = 1, SinceLastReturn = TimeSpan.FromSeconds(3) };

        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(quickly).Outcome);
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(quickly with { EndsGameWhenGone = true }).Outcome);
    }

    [Fact]
    public void A_single_chicken_rejoins()
    {
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(SingleChicken()).Outcome);
    }

    [Fact]
    public void A_hunt_with_no_budget_running_yet_rejoins()
    {
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(SingleChicken() with { HuntTimeLeft = null }).Outcome);
    }

    [Fact]
    public void A_mass_leave_ends_the_game_instead()
    {
        Assert.Equal(RejoinOutcome.EndGame, CowRejoin.Decide(SingleChicken() with { RecentLeaves = 3 }).Outcome);
    }

    [Fact]
    public void Two_leaving_together_still_rejoin()
    {
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(SingleChicken() with { RecentLeaves = 2 }).Outcome);
    }

    [Fact]
    public void Both_killing_sorceresses_out_ends_the_game()
    {
        // Live, both out also makes the level read finished; ending the game still wins.
        var bothOut = SingleChicken() with { KillersInGame = 0, RunOver = true, RecentLeaves = 2 };

        Assert.Equal(RejoinOutcome.EndGame, CowRejoin.Decide(bothOut).Outcome);
    }

    [Fact]
    public void A_death_in_a_mass_leave_still_ends_the_game()
    {
        Assert.Equal(RejoinOutcome.EndGame, CowRejoin.Decide(SingleChicken() with { RecentLeaves = 3, Died = true }).Outcome);
    }

    [Fact]
    public void A_finished_level_is_not_rejoined()
    {
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { RunOver = true }).Outcome);
    }

    [Fact]
    public void Members_out_now_count_however_long_ago_they_left()
    {
        var now = new DateTime(2026, 11, 16, 13, 0, 0);
        var members = new (bool Joined, bool InGame, DateTime? LeftAt)[]
        {
            (true, false, now.AddSeconds(-1)),
            (true, false, now.AddMinutes(-2)),
            (true, true, null),
        };

        Assert.Equal(2, CowRejoin.CountRecentLeaves(members, now));
    }

    [Fact]
    public void A_member_back_in_the_game_counts_only_within_the_window()
    {
        var now = new DateTime(2026, 11, 16, 13, 0, 0);
        var members = new (bool Joined, bool InGame, DateTime? LeftAt)[]
        {
            (true, true, now.AddSeconds(-10)),
            (true, true, now.AddSeconds(-40)),
        };

        Assert.Equal(1, CowRejoin.CountRecentLeaves(members, now));
    }

    [Fact]
    public void A_late_joiner_that_never_joined_is_not_a_leave()
    {
        var now = new DateTime(2026, 11, 16, 13, 0, 0);
        var members = new (bool Joined, bool InGame, DateTime? LeftAt)[]
        {
            (false, false, null),
            (true, false, null),
        };

        Assert.Equal(1, CowRejoin.CountRecentLeaves(members, now));
    }

    [Fact]
    public void A_real_death_does_not_rejoin()
    {
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { Died = true }).Outcome);
    }

    [Fact]
    public void A_stranger_flee_does_not_rejoin()
    {
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { StrangerFlee = true }).Outcome);
    }

    [Fact]
    public void A_game_that_is_ending_is_not_rejoined()
    {
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { NextGameSet = true }).Outcome);
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { NextGameSet = true, RecentLeaves = 4 }).Outcome);
    }

    [Fact]
    public void Two_rejoins_a_game_are_the_most()
    {
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(SingleChicken() with { RejoinsSoFar = 1 }).Outcome);
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { RejoinsSoFar = 2 }).Outcome);
    }

    [Fact]
    public void Too_little_hunt_left_after_the_trip_back_is_not_worth_it()
    {
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { HuntTimeLeft = TimeSpan.FromSeconds(50) }).Outcome);
        Assert.Equal(RejoinOutcome.Rejoin, CowRejoin.Decide(SingleChicken() with { HuntTimeLeft = TimeSpan.FromSeconds(70) }).Outcome);
    }

    [Fact]
    public void A_client_still_in_the_game_does_nothing()
    {
        Assert.Equal(RejoinOutcome.StayOut, CowRejoin.Decide(SingleChicken() with { InGame = true }).Outcome);
    }
}
