namespace ConsoleBot.Chicken;

/// <summary>
/// When the bot drinks and when it abandons a game to keep the character alive. Configured per
/// character under <c>bot:accounts:chicken</c>, falling back to <c>bot:chicken</c> and finally to
/// these defaults, which reproduce the behaviour the bot had when these numbers were hardcoded.
/// </summary>
/// <remarks>
/// Thresholds are fractions of maximum, except <see cref="LifeChickenAbsolute"/>. On patch 1.09
/// Battle Orders raises maximum life and mana without raising the current values, so a fraction can
/// collapse without the character taking a single point of damage. The service therefore only acts
/// on fractions once it has seen real damage - see <see cref="ChickenService"/>.
/// </remarks>
public sealed class ChickenConfiguration
{
    /// <summary>
    /// Leave the game when life falls to or below this fraction of maximum life.
    /// </summary>
    public double LifeChickenPercent { get; set; } = 0.2;

    /// <summary>
    /// Leave the game when life falls to or below this many hit points, whatever the maximum is.
    /// Unlike a fraction this cannot be moved by Battle Orders, so it is the more reliable guard for
    /// a character with a large life pool. Defaults to 0, which disables it.
    /// </summary>
    public int LifeChickenAbsolute { get; set; }

    /// <summary>
    /// Drink a healing potion when life is under this fraction of maximum life.
    /// </summary>
    public double UseHealthPotionPercent { get; set; } = 0.9;

    /// <summary>
    /// Drink a rejuvenation potion when life is under this fraction of maximum life. Set below
    /// <see cref="UseHealthPotionPercent"/> so healing potions are spent first.
    /// </summary>
    public double UseRejuvenationPercent { get; set; } = 0.3;

    /// <summary>
    /// Leave the game when life is under <see cref="UseRejuvenationPercent"/> and no healing or
    /// rejuvenation potion is left to drink.
    /// </summary>
    public bool LeaveWhenOutOfHealthPotions { get; set; } = true;

    /// <summary>
    /// Drink a mana potion when mana is under this fraction of maximum mana.
    /// </summary>
    public double UseManaPotionPercent { get; set; } = 0.3;

    /// <summary>
    /// Leave the game when mana is under <see cref="UseManaPotionPercent"/> and no mana potion is
    /// left. Matters for a character that needs mana to teleport away.
    /// </summary>
    public bool LeaveWhenOutOfManaPotions { get; set; } = true;

    /// <summary>
    /// Shortest gap between two drinks. Needed because the bot reacts to a life value arriving over
    /// the network: without a gap a single damage spike would empty the belt before the updated life
    /// value ever arrived.
    /// </summary>
    public int MinPotionIntervalMs { get; set; } = 700;

    /// <summary>
    /// How often the backstop thread re-evaluates, in case life and mana packets stop arriving.
    /// The primary path is packet-driven and does not wait for this.
    /// </summary>
    public int BackstopIntervalMs { get; set; } = 250;
}
