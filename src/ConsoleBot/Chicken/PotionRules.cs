using System;

namespace ConsoleBot.Chicken;

/// <summary>What the chicken service knows about a character when it decides what to drink.</summary>
public sealed record PotionSituation
{
    /// <summary>Life as a fraction of maximum; meaningless while <see cref="LifeKnown"/> is false.</summary>
    public double LifeFraction { get; init; }

    public bool LifeKnown { get; init; }

    public int Mana { get; init; }

    public int MaxMana { get; init; }

    /// <summary>Life was lost within the unharmed grace period.</summary>
    public bool RecentlyDamaged { get; init; }

    /// <summary>A maximum rose without damage (Battle Orders) and no hit has come since.</summary>
    public bool AwaitingDamage { get; init; }

    public bool Whirling { get; init; }

    /// <summary>The server's last word on the position is outside the towns; see <c>Game.IsOutOfTownConfirmed</c>.</summary>
    public bool OutOfTownConfirmed { get; init; }

    public TimeSpan SinceHealingPotion { get; init; } = TimeSpan.MaxValue;

    public TimeSpan SinceManaPotion { get; init; } = TimeSpan.MaxValue;

    public TimeSpan SinceRejuvenation { get; init; } = TimeSpan.MaxValue;

    /// <summary>Rejuvenations in the inventory, the only place one is drunk from.</summary>
    public int Rejuvenations { get; init; }

    public int HealingPotions { get; init; }

    public int ManaPotions { get; init; }
}

public readonly record struct PotionPlan(
    bool DrinkMana,
    bool DrinkRejuvenation,
    bool DrinkHealing,
    bool WarnNoHealing,
    bool LeaveOutOfHealing,
    bool LeaveOutOfMana);

public static class PotionRules
{
    /// <summary>
    /// On 1.09 a healing potion heals over a few seconds and a second one drunk meanwhile heals nothing
    /// more: at a configured 700ms the cow party wasted 51 potions in 11 games, drunk about 0.8s apart.
    /// The configuration may ask for a longer gap, never a shorter one.
    /// </summary>
    public static readonly TimeSpan HealingPotionFloor = TimeSpan.FromMilliseconds(1500);

    /// <summary>Mana potions restore over time the same way, so a second one inside this gap is wasted too.</summary>
    public static readonly TimeSpan ManaPotionFloor = TimeSpan.FromMilliseconds(1500);

    public static readonly TimeSpan RejuvenationGap = TimeSpan.FromMilliseconds(500);

    public static TimeSpan HealingInterval(ChickenConfiguration config)
        => Max(TimeSpan.FromMilliseconds(config.MinPotionIntervalMs), HealingPotionFloor);

    public static TimeSpan ManaInterval(ChickenConfiguration config)
        => Max(TimeSpan.FromMilliseconds(config.MinPotionIntervalMs), ManaPotionFloor);

    public static PotionPlan Plan(PotionSituation s, ChickenConfiguration config)
    {
        var manaLow = s.MaxMana > 0 && (double)s.Mana / s.MaxMana < config.UseManaPotionPercent;
        var manaDue = s.SinceManaPotion > ManaInterval(config);

        // Mana is judged apart from life. Behind the damage window a nova sorceress killing from
        // safety sat at 11 of 1043 mana for twenty seconds with three mana potions in her belt, and
        // behind the healing branch a wounded one never got to drink mana at all. Unhurt, the town
        // guard the damage window gave is the server-confirmed position instead; hurt, it is the hit.
        var drinkMana = manaLow && manaDue && s.ManaPotions > 0 && !s.Whirling && (s.RecentlyDamaged || s.OutOfTownConfirmed);

        if (!s.RecentlyDamaged || s.AwaitingDamage || !s.LifeKnown || s.Whirling)
        {
            return new PotionPlan(drinkMana, false, false, false, false, false);
        }

        var healingDue = s.SinceHealingPotion > HealingInterval(config);
        if (s.LifeFraction < config.UseRejuvenationPercent)
        {
            if (s.Rejuvenations > 0)
            {
                // A rejuvenation fills mana as well.
                return new PotionPlan(false, s.SinceRejuvenation > RejuvenationGap, false, false, false, false);
            }

            // With nothing left to drink there is nothing to wait for.
            return new PotionPlan(drinkMana, false,
                healingDue && s.HealingPotions > 0, false,
                s.HealingPotions == 0 && config.LeaveWhenOutOfHealthPotions, false);
        }

        if (s.LifeFraction < config.UseHealthPotionPercent && healingDue)
        {
            return new PotionPlan(drinkMana, false, s.HealingPotions > 0, s.HealingPotions == 0, false, false);
        }

        // Leaving for want of mana is reached exactly where it always was: hurt, at or above the
        // healing line, and with no drink of either kind inside the configured interval. So a
        // sorceress idling at low mana with an empty belt does not now walk out of games.
        var configured = TimeSpan.FromMilliseconds(config.MinPotionIntervalMs);
        var leaveOutOfMana = manaLow && s.ManaPotions == 0 && config.LeaveWhenOutOfManaPotions
            && s.LifeFraction >= config.UseHealthPotionPercent
            && s.SinceHealingPotion > configured && s.SinceManaPotion > configured;
        return new PotionPlan(drinkMana, false, false, false, false, leaveOutOfMana);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
