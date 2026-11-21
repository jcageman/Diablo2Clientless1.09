using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class BattleOrdersWaitTests
{
    private static Point At(int x, int y) => new((ushort)x, (ushort)y);

    private static BattleOrdersSituation Back(double inReachSeconds = 0, double sinceArrivalSeconds = 3) => new(
        Rejoined: true,
        HasBattleOrders: false,
        LeadPlaced: true,
        InPartyWithLead: true,
        TimeSpan.FromSeconds(inReachSeconds),
        TimeSpan.FromSeconds(sinceArrivalSeconds));

    [Fact]
    public void A_member_that_never_left_fights()
    {
        Assert.Equal(BattleOrdersStance.Fight, BattleOrdersWait.Decide(Back() with { Rejoined = false }));
    }

    [Fact]
    public void Back_without_battle_orders_it_goes_to_the_barbarian_first()
    {
        Assert.Equal(BattleOrdersStance.GoToLead, BattleOrdersWait.Decide(Back()));
    }

    [Fact]
    public void With_battle_orders_it_fights()
    {
        Assert.Equal(BattleOrdersStance.Fight, BattleOrdersWait.Decide(Back() with { HasBattleOrders = true }));
    }

    [Fact]
    public void With_the_barbarian_away_it_holds()
    {
        Assert.Equal(BattleOrdersStance.Hold, BattleOrdersWait.Decide(Back() with { LeadPlaced = false }));
    }

    [Fact]
    public void Only_time_within_his_shout_reach_counts()
    {
        // Thirty seconds back, but only five of them beside him: it keeps going to him.
        Assert.Equal(BattleOrdersStance.GoToLead, BattleOrdersWait.Decide(Back(inReachSeconds: 5, sinceArrivalSeconds: 30)));
        Assert.Equal(BattleOrdersStance.Fight, BattleOrdersWait.Decide(Back(inReachSeconds: 21, sinceArrivalSeconds: 30)));
    }

    [Fact]
    public void Past_the_overall_cap_it_fights_whatever_kept_the_shouts_away()
    {
        Assert.Equal(BattleOrdersStance.Fight, BattleOrdersWait.Decide(Back(inReachSeconds: 0, sinceArrivalSeconds: 46)));
        Assert.Equal(BattleOrdersStance.Fight, BattleOrdersWait.Decide(Back(sinceArrivalSeconds: 46) with { LeadPlaced = false }));
    }

    [Fact]
    public void Outside_his_party_it_does_not_wait_for_shouts_that_cannot_land()
    {
        Assert.Equal(BattleOrdersStance.Fight, BattleOrdersWait.Decide(Back() with { InPartyWithLead = false }));
    }

    [Fact]
    public void A_stop_beside_a_barbarian_in_a_pack_is_out_of_his_shout_reach()
    {
        // Why he steps up to it: with cows three units round him the walker's stop is ten or more away.
        var lead = At(100, 100);
        var cows = new[] { At(103, 100), At(97, 100), At(100, 103), At(100, 97) };

        var stop = Kiting.FollowDestination(lead, At(70, 100), cows);

        Assert.True(stop.Distance(lead) >= 10);
    }
}
