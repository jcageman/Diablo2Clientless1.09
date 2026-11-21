using ConsoleBot.Chicken;

namespace ConsoleBot.Tests;

public class PotionRulesTests
{
    private static readonly ChickenConfiguration Config = new()
    {
        UseHealthPotionPercent = 0.7,
        UseRejuvenationPercent = 0.35,
        UseManaPotionPercent = 0.3,
        MinPotionIntervalMs = 700,
    };

    private static PotionSituation Field() => new()
    {
        LifeFraction = 1,
        LifeKnown = true,
        Mana = 1000,
        MaxMana = 1000,
        OutOfTownConfirmed = true,
        Rejuvenations = 3,
        HealingPotions = 6,
        ManaPotions = 3,
    };

    [Fact]
    public void A_sorceress_casting_from_safety_drinks_mana_without_having_been_hit()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, RecentlyDamaged = false }, Config);

        Assert.True(plan.DrinkMana);
    }

    [Fact]
    public void Mana_is_drunk_while_the_healing_branch_drinks_too()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, RecentlyDamaged = true, LifeFraction = 0.5 }, Config);

        Assert.True(plan.DrinkHealing);
        Assert.True(plan.DrinkMana);
    }

    [Fact]
    public void A_healing_potion_just_drunk_does_not_hold_back_mana()
    {
        var plan = PotionRules.Plan(Field() with
        {
            Mana = 15,
            RecentlyDamaged = true,
            LifeFraction = 0.5,
            SinceHealingPotion = TimeSpan.FromMilliseconds(200),
        }, Config);

        Assert.False(plan.DrinkHealing);
        Assert.True(plan.DrinkMana);
    }

    [Fact]
    public void A_second_mana_potion_waits_for_the_first_to_finish()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, SinceManaPotion = TimeSpan.FromMilliseconds(1000) }, Config);

        Assert.False(plan.DrinkMana);
    }

    [Fact]
    public void Unhurt_no_mana_is_drunk_unless_the_server_places_her_outside_the_towns()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, OutOfTownConfirmed = false }, Config);

        Assert.False(plan.DrinkMana);
    }

    [Fact]
    public void Hurt_mana_is_drunk_before_the_server_has_stated_a_position()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, OutOfTownConfirmed = false, RecentlyDamaged = true }, Config);

        Assert.True(plan.DrinkMana);
    }

    [Fact]
    public void Nothing_is_drunk_mid_whirl()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, RecentlyDamaged = true, LifeFraction = 0.5, Whirling = true }, Config);

        Assert.Equal(default, plan);
    }

    [Fact]
    public void Healing_potions_keep_1500ms_apart_whatever_the_config_says()
    {
        var hurt = Field() with { RecentlyDamaged = true, LifeFraction = 0.5 };

        Assert.False(PotionRules.Plan(hurt with { SinceHealingPotion = TimeSpan.FromMilliseconds(800) }, Config).DrinkHealing);
        Assert.False(PotionRules.Plan(hurt with { SinceHealingPotion = TimeSpan.FromMilliseconds(1400) }, Config).DrinkHealing);
        Assert.True(PotionRules.Plan(hurt with { SinceHealingPotion = TimeSpan.FromMilliseconds(1600) }, Config).DrinkHealing);
    }

    [Fact]
    public void A_longer_configured_interval_is_kept()
    {
        var slow = new ChickenConfiguration { MinPotionIntervalMs = 2500 };

        Assert.Equal(TimeSpan.FromMilliseconds(2500), PotionRules.HealingInterval(slow));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), PotionRules.HealingInterval(Config));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), PotionRules.ManaInterval(Config));
    }

    [Fact]
    public void A_rejuvenation_does_not_wait_for_the_healing_interval()
    {
        var plan = PotionRules.Plan(Field() with
        {
            RecentlyDamaged = true,
            LifeFraction = 0.2,
            SinceHealingPotion = TimeSpan.FromMilliseconds(100),
            SinceRejuvenation = TimeSpan.FromMilliseconds(600),
        }, Config);

        Assert.True(plan.DrinkRejuvenation);
        Assert.False(plan.DrinkHealing);
    }

    [Fact]
    public void Rejuvenations_keep_their_short_gap()
    {
        var plan = PotionRules.Plan(Field() with { RecentlyDamaged = true, LifeFraction = 0.2, SinceRejuvenation = TimeSpan.FromMilliseconds(300) }, Config);

        Assert.False(plan.DrinkRejuvenation);
    }

    [Fact]
    public void Unhurt_nothing_heals_and_nothing_leaves()
    {
        var plan = PotionRules.Plan(Field() with { LifeFraction = 0.2, Mana = 15, ManaPotions = 0, RecentlyDamaged = false }, Config);

        Assert.False(plan.DrinkHealing);
        Assert.False(plan.DrinkRejuvenation);
        Assert.False(plan.LeaveOutOfHealing);
        Assert.False(plan.LeaveOutOfMana);
    }

    [Fact]
    public void Idling_at_low_mana_with_none_left_does_not_leave_the_game()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, ManaPotions = 0, RecentlyDamaged = false }, Config);

        Assert.False(plan.DrinkMana);
        Assert.False(plan.LeaveOutOfMana);
    }

    [Fact]
    public void Hit_at_low_mana_with_none_left_leaves_as_before()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, ManaPotions = 0, RecentlyDamaged = true }, Config);

        Assert.True(plan.LeaveOutOfMana);
    }

    [Fact]
    public void The_last_mana_potion_still_working_is_not_a_reason_to_leave()
    {
        var plan = PotionRules.Plan(Field() with { Mana = 15, ManaPotions = 0, RecentlyDamaged = true, SinceManaPotion = TimeSpan.FromMilliseconds(500) }, Config);

        Assert.False(plan.LeaveOutOfMana);
    }

    [Fact]
    public void Out_of_healing_and_rejuvenations_below_the_line_leaves()
    {
        var plan = PotionRules.Plan(Field() with { RecentlyDamaged = true, LifeFraction = 0.2, Rejuvenations = 0, HealingPotions = 0 }, Config);

        Assert.True(plan.LeaveOutOfHealing);
    }

    [Fact]
    public void Out_of_everything_just_after_the_last_rejuvenation_leaves_at_once()
    {
        // Drinking a rejuvenation sets the healing time too; with nothing left there is nothing to wait for.
        var plan = PotionRules.Plan(Field() with
        {
            RecentlyDamaged = true,
            LifeFraction = 0.2,
            Rejuvenations = 0,
            HealingPotions = 0,
            SinceHealingPotion = TimeSpan.FromMilliseconds(800),
            SinceRejuvenation = TimeSpan.FromMilliseconds(800),
        }, Config);

        Assert.True(plan.LeaveOutOfHealing);
    }

    [Fact]
    public void Hurt_below_the_healing_line_and_out_of_mana_does_not_leave_while_healing_waits()
    {
        var plan = PotionRules.Plan(Field() with
        {
            RecentlyDamaged = true,
            LifeFraction = 0.5,
            SinceHealingPotion = TimeSpan.FromMilliseconds(1000),
            Mana = 15,
            ManaPotions = 0,
        }, Config);

        Assert.False(plan.LeaveOutOfMana);
    }

    [Fact]
    public void An_empty_belt_above_the_rejuvenation_line_is_reported_not_left()
    {
        var plan = PotionRules.Plan(Field() with { RecentlyDamaged = true, LifeFraction = 0.5, HealingPotions = 0 }, Config);

        Assert.True(plan.WarnNoHealing);
        Assert.False(plan.LeaveOutOfHealing);
    }

    [Fact]
    public void After_battle_orders_life_waits_for_damage_but_mana_does_not()
    {
        var plan = PotionRules.Plan(Field() with { RecentlyDamaged = true, AwaitingDamage = true, LifeFraction = 0.5, Mana = 15 }, Config);

        Assert.False(plan.DrinkHealing);
        Assert.True(plan.DrinkMana);
    }
}
