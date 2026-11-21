using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class FollowRulesTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    [Fact]
    public void A_follower_that_never_reached_its_last_target_walks_again()
    {
        // The lead still stands on the last target; the follower's walk was cancelled half way.
        Assert.True(FollowRules.ShouldWalkToLead(At(100, 100), At(130, 100), At(130, 100), walkInProgress: false));
    }

    [Fact]
    public void A_lead_that_moved_off_the_last_target_is_followed()
    {
        Assert.True(FollowRules.ShouldWalkToLead(At(130, 100), At(145, 100), At(130, 100), walkInProgress: false));
    }

    [Fact]
    public void A_follower_on_its_target_is_not_pulled_onto_the_lead()
    {
        // Lead and follower both within the leash of the last target, though eleven apart.
        Assert.False(FollowRules.ShouldWalkToLead(At(125, 100), At(136, 100), At(130, 100), walkInProgress: false));
    }

    [Fact]
    public void Without_a_target_yet_the_lead_itself_is_the_leash()
    {
        Assert.True(FollowRules.ShouldWalkToLead(At(100, 100), At(130, 100), null!, walkInProgress: false));
        Assert.False(FollowRules.ShouldWalkToLead(At(100, 100), At(108, 100), null!, walkInProgress: false));
    }

    [Fact]
    public void No_second_walk_while_one_is_under_way()
    {
        Assert.False(FollowRules.ShouldWalkToLead(At(100, 100), At(130, 100), At(130, 100), walkInProgress: true));
    }

    [Fact]
    public void A_lead_in_another_area_is_not_walked_to()
    {
        Assert.False(FollowRules.ShouldWalkToLead(At(100, 100), At(5000, 100), At(130, 100), walkInProgress: false));
        Assert.False(FollowRules.ShouldWalkToLead(At(100, 100), null!, At(130, 100), walkInProgress: false));
    }

    private static bool Hold(bool threatened = true, bool nearLead = true, bool retreating = false, bool hurt = false,
        bool outOfHealing = false, double heldSeconds = 1, bool lostLife = false)
        => FollowRules.ShouldHold(threatened, nearLead, retreating, hurt, outOfHealing, TimeSpan.FromSeconds(heldSeconds), lostLife);

    [Fact]
    public void A_walker_with_monsters_on_it_beside_the_lead_holds_to_fight()
    {
        Assert.True(Hold());
        Assert.False(Hold(threatened: false));
    }

    [Fact]
    public void A_walker_follows_a_retreat_whatever_is_on_it()
    {
        Assert.False(Hold(retreating: true));
        Assert.False(Hold(hurt: true));
        Assert.False(Hold(outOfHealing: true));
    }

    [Fact]
    public void A_straggler_walks_on()
    {
        Assert.False(Hold(nearLead: false));
    }

    [Fact]
    public void A_hold_that_costs_nothing_ends_after_three_seconds()
    {
        Assert.False(Hold(heldSeconds: 4));
        Assert.True(Hold(heldSeconds: 4, lostLife: true));
    }

    private static bool Tick(HoldTimer timer, DateTime start, double seconds, bool threatened = true, int life = 1000)
        => timer.Update(start.AddSeconds(seconds), threatened, nearLead: true, retreating: false, hurt: false, outOfHealing: false, life);

    [Fact]
    public void A_spent_hold_stays_spent_while_the_same_harmless_threat_lasts()
    {
        var timer = new HoldTimer();
        var start = new DateTime(2026, 11, 16, 14, 0, 0);

        Assert.True(Tick(timer, start, 0));
        Assert.True(Tick(timer, start, 2.9));
        Assert.False(Tick(timer, start, 3.1));
        // The walk starts; 200ms later the same monster is still there and must not stop it again.
        Assert.False(Tick(timer, start, 3.3));
        Assert.False(Tick(timer, start, 6));
    }

    [Fact]
    public void A_hit_or_the_threat_going_away_starts_the_hold_over()
    {
        var timer = new HoldTimer();
        var start = new DateTime(2026, 11, 16, 14, 0, 0);
        Tick(timer, start, 0);
        Assert.False(Tick(timer, start, 3.1));

        Assert.True(Tick(timer, start, 3.5, life: 900));

        Assert.False(Tick(timer, start, 4, threatened: false));
        Assert.True(Tick(timer, start, 4.2));
    }

    [Fact]
    public void A_walk_is_cut_short_only_when_the_lead_moved_away_from_its_target()
    {
        Assert.True(FollowRules.ShouldRetarget(At(100, 100), At(115, 100)));
        Assert.False(FollowRules.ShouldRetarget(At(100, 100), At(105, 100)));
        Assert.False(FollowRules.ShouldRetarget(null!, At(115, 100)));
    }

    [Fact]
    public void A_server_that_keeps_disagreeing_is_asked_where_she_is()
    {
        Assert.True(FollowRules.ShouldRequestUpdate(12, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
        Assert.True(FollowRules.ShouldRequestUpdate(null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_moment_of_disagreement_or_agreement_asks_nothing()
    {
        Assert.False(FollowRules.ShouldRequestUpdate(12, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5)));
        Assert.False(FollowRules.ShouldRequestUpdate(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Requests_are_at_least_three_seconds_apart()
    {
        Assert.False(FollowRules.ShouldRequestUpdate(12, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
    }
}
