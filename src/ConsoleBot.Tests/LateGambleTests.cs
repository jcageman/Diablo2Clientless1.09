using ConsoleBot.Bots.Types.Cows;

namespace ConsoleBot.Tests;

public class LateGambleTests
{
    // Accounts in order: the host sorceress, a sorceress, the battle orders barbarian, the amazon, a second barbarian.
    private static readonly int?[] Party = [0, 0, 20, 0, 5];

    [Fact]
    public void A_member_without_battle_orders_gambles_after_the_party_has_started()
    {
        Assert.True(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 1, Party));
    }

    [Fact]
    public void A_killing_sorceress_gambles_before_the_run()
    {
        Assert.False(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 1, Party, killer: true));
    }

    [Fact]
    public void A_weak_walker_gambles_before_the_run()
    {
        Assert.False(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: true, 3, Party));
    }

    [Fact]
    public void A_barbarian_gambles_late_while_another_gives_the_party_more_battle_orders()
    {
        Assert.True(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 4, Party));
    }

    [Fact]
    public void The_barbarian_who_gives_the_battle_orders_gambles_before_the_run()
    {
        Assert.False(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 2, Party));
    }

    [Fact]
    public void Of_two_barbarians_with_as_many_battle_orders_the_earlier_account_gives_them()
    {
        int?[] party = [0, 0, 20, 0, 20];
        Assert.False(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 2, party));
        Assert.True(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 4, party));
    }

    [Fact]
    public void A_barbarian_gambles_before_the_run_while_the_other_barbarians_battle_orders_are_unknown()
    {
        int?[] party = [0, 0, null, 0, 5];
        Assert.False(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 4, party));
    }

    [Fact]
    public void A_member_whose_own_battle_orders_are_unknown_gambles_before_the_run()
    {
        int?[] party = [0, null, 20, 0, 5];
        Assert.False(LateGamble.Allowed(portalCharacter: false, createsGame: false, weakWalker: false, 1, party));
    }

    [Fact]
    public void The_portal_character_gambles_before_the_run()
    {
        Assert.False(LateGamble.Allowed(portalCharacter: true, createsGame: false, weakWalker: false, 1, Party));
    }

    [Fact]
    public void The_host_gambles_before_the_run()
    {
        Assert.False(LateGamble.Allowed(portalCharacter: false, createsGame: true, weakWalker: false, 0, Party));
    }
}
