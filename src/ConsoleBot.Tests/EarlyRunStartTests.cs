using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class EarlyRunStartTests
{
    private static readonly EarlyRunStart.Member WithoutOrders = new(InGame: true, BattleOrders: false);
    private static readonly EarlyRunStart.Member WithOrders = new(InGame: true, BattleOrders: true);

    [Fact]
    public void Without_her_own_battle_orders_she_never_starts_early()
    {
        Assert.False(EarlyRunStart.MayStart(ownBattleOrders: false, [WithOrders, WithOrders]));
    }

    [Fact]
    public void A_member_still_waiting_for_its_battle_orders_holds_her_back()
    {
        Assert.False(EarlyRunStart.MayStart(ownBattleOrders: true, [WithOrders, WithOrders, WithOrders, WithoutOrders]));
    }

    [Fact]
    public void Once_everyone_has_battle_orders_she_goes()
    {
        Assert.True(EarlyRunStart.MayStart(ownBattleOrders: true, [WithOrders, WithOrders, WithOrders, WithOrders]));
    }

    [Fact]
    public void A_member_out_of_the_game_holds_her_back()
    {
        var notJoined = new EarlyRunStart.Member(InGame: false, BattleOrders: false);

        Assert.False(EarlyRunStart.MayStart(ownBattleOrders: true, [WithOrders, notJoined]));
    }
}
