using ConsoleBot.TownManagement;

namespace ConsoleBot.Tests;

public class VendorVisitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_follower_one_or_two_healing_potions_short_with_nothing_else_to_do_skips_the_vendor(long potionsShort)
    {
        Assert.False(VendorVisit.Needed(otherNeed: false, healthPotionsToBuy: potionsShort, healthPotionsNotWorthAVisit: 2));
    }

    [Fact]
    public void Three_short_is_worth_the_walk()
    {
        Assert.True(VendorVisit.Needed(otherNeed: false, healthPotionsToBuy: 3, healthPotionsNotWorthAVisit: 2));
    }

    [Fact]
    public void Anything_else_to_do_there_sends_it_whatever_the_shortfall()
    {
        Assert.True(VendorVisit.Needed(otherNeed: true, healthPotionsToBuy: 1, healthPotionsNotWorthAVisit: 2));
        Assert.True(VendorVisit.Needed(otherNeed: true, healthPotionsToBuy: 0, healthPotionsNotWorthAVisit: 2));
        Assert.True(VendorVisit.Needed(otherNeed: true, healthPotionsToBuy: null, healthPotionsNotWorthAVisit: 0));
    }

    [Fact]
    public void Every_bot_skips_a_walk_for_one_or_two_spares_by_default()
    {
        var defaults = new TownManagementOptions(new Bots.Types.AccountConfig(), D2NG.Core.D2GS.Act.Act.Act1);

        Assert.Equal(2, defaults.HealthPotionsNotWorthAVisit);
        Assert.False(VendorVisit.Needed(otherNeed: false, healthPotionsToBuy: 2, defaults.HealthPotionsNotWorthAVisit));
        Assert.True(VendorVisit.Needed(otherNeed: false, healthPotionsToBuy: 3, defaults.HealthPotionsNotWorthAVisit));
    }

    [Fact]
    public void A_bot_can_still_ask_for_a_visit_for_a_single_potion()
    {
        Assert.True(VendorVisit.Needed(otherNeed: false, healthPotionsToBuy: 1, healthPotionsNotWorthAVisit: 0));
    }

    [Fact]
    public void Nothing_short_and_nothing_to_do_is_no_visit()
    {
        Assert.False(VendorVisit.Needed(otherNeed: false, healthPotionsToBuy: 0, healthPotionsNotWorthAVisit: 0));
        Assert.False(VendorVisit.Needed(otherNeed: false, healthPotionsToBuy: null, healthPotionsNotWorthAVisit: 0));
    }
}
