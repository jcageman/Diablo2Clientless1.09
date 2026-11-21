using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class PortalRetreatTests
{
    [Fact]
    public void Arriving_into_a_pack_with_the_portal_under_her_takes_it()
    {
        // Eight cows at three to six units, the red portal she came through right under her.
        var cows = Enumerable.Range(0, 8).Select(i => new CloseCow(3 + (i % 4))).ToList();

        Assert.True(Kiting.InDanger(cows));
        Assert.True(PortalRetreat.ShouldRetreat(Kiting.InDanger(cows), belowRetreatLine: false, hostileNear: true, portalDistance: 1));
    }

    [Fact]
    public void Without_a_portal_in_reach_nothing_changes()
    {
        Assert.False(PortalRetreat.ShouldRetreat(weakWalkerInDanger: true, belowRetreatLine: true, hostileNear: true, portalDistance: 25));
        Assert.False(PortalRetreat.ShouldRetreat(weakWalkerInDanger: true, belowRetreatLine: true, hostileNear: true, portalDistance: null));
    }

    [Fact]
    public void A_hurt_member_with_monsters_on_it_beside_a_portal_takes_it()
    {
        Assert.True(PortalRetreat.ShouldRetreat(weakWalkerInDanger: false, belowRetreatLine: true, hostileNear: true, portalDistance: 6));
        Assert.False(PortalRetreat.ShouldRetreat(weakWalkerInDanger: false, belowRetreatLine: true, hostileNear: false, portalDistance: 6));
        Assert.False(PortalRetreat.ShouldRetreat(weakWalkerInDanger: false, belowRetreatLine: false, hostileNear: true, portalDistance: 6));
    }

    [Fact]
    public void One_plain_cow_on_a_weak_walker_is_no_danger()
    {
        Assert.False(Kiting.InDanger([new CloseCow(2)]));
        Assert.True(Kiting.InDanger([new CloseCow(2, Enchanted: true), new CloseCow(4)]));
    }

    [Fact]
    public void It_goes_back_once_the_landing_is_reported_clear()
    {
        Assert.False(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(2), hostilesAtLanding: 0));
        Assert.True(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(4), hostilesAtLanding: 1));
        Assert.False(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(4), hostilesAtLanding: 6));
    }

    [Fact]
    public void Unseen_it_goes_back_after_ten_seconds()
    {
        Assert.False(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(8), hostilesAtLanding: null));
        Assert.True(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(10), hostilesAtLanding: null));
    }

    [Fact]
    public void While_the_landing_is_reported_busy_it_keeps_waiting()
    {
        Assert.False(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(10), hostilesAtLanding: 6));
        Assert.False(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(45), hostilesAtLanding: 6));
        Assert.True(PortalRetreat.ShouldReturn(TimeSpan.FromSeconds(60), hostilesAtLanding: 6));
    }

    [Fact]
    public void A_portal_under_her_feet_is_taken_whatever_cows_stand_by_the_way()
    {
        Assert.True(PortalRetreat.WayToPortalClear(portalDistance: 0, walkPassesNoCow: false));
        Assert.True(PortalRetreat.WayToPortalClear(portalDistance: 2.8, walkPassesNoCow: false));
        Assert.False(PortalRetreat.WayToPortalClear(portalDistance: 6, walkPassesNoCow: false));
        Assert.True(PortalRetreat.WayToPortalClear(portalDistance: 6, walkPassesNoCow: true));
    }

    [Fact]
    public void A_weak_walker_enters_at_once_onto_a_clear_landing()
    {
        Assert.True(PortalRetreat.ShouldEnter(TimeSpan.Zero, hostilesAtLanding: 0));
        Assert.True(PortalRetreat.ShouldEnter(TimeSpan.Zero, hostilesAtLanding: PortalRetreat.ClearHostiles));
    }

    [Fact]
    public void A_weak_walker_waits_while_the_landing_is_a_pack_but_not_for_ever()
    {
        // The sorceress who opened the portal stood among ten cows when the amazon came through.
        Assert.False(PortalRetreat.ShouldEnter(TimeSpan.FromSeconds(1), hostilesAtLanding: 10));
        Assert.False(PortalRetreat.ShouldEnter(TimeSpan.FromSeconds(19), hostilesAtLanding: 10));
        Assert.True(PortalRetreat.ShouldEnter(PortalRetreat.MaxEntryWait, hostilesAtLanding: 10));
    }

    [Fact]
    public void With_nobody_to_see_the_landing_a_weak_walker_enters_as_before()
    {
        Assert.True(PortalRetreat.ShouldEnter(TimeSpan.Zero, hostilesAtLanding: null));
    }

    [Fact]
    public void Three_retreats_a_game_are_the_most()
    {
        Assert.True(PortalRetreat.RetreatsLeft(2));
        Assert.False(PortalRetreat.RetreatsLeft(3));
    }

    [Fact]
    public void A_retreat_right_after_coming_back_takes_the_other_way_in()
    {
        Assert.True(PortalRetreat.MetDangerAgain(TimeSpan.FromSeconds(5)));
        Assert.False(PortalRetreat.MetDangerAgain(TimeSpan.FromSeconds(40)));
        Assert.False(PortalRetreat.MetDangerAgain(null));
    }
}
