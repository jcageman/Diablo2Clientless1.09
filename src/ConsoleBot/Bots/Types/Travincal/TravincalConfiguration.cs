namespace ConsoleBot.Bots.Types.Travincal;

/// <summary>
/// Configuration for the travincal bot, bound from <c>bot:travincal</c>. The run needs no settings
/// of its own, so this is just the single account and character to play - see
/// <see cref="AccountConfig"/>.
/// </summary>
public class TravincalConfiguration : AccountConfig
{
    /// <summary>
    /// Identify from a carried tome instead of walking to Deckard Cain. The walk to Cain and on to the
    /// merchant is two thirds of the town phase, so skipping one leg of it is worth the scrolls.
    /// </summary>
    public bool IdentifyWithTome { get; set; }

    /// <summary>
    /// Multiplies the speed the movement sleeps assume. 1.0 keeps the behaviour the bot has always
    /// had; above that it waits less between hops than it believes the character needs.
    /// </summary>
    public double MovementSpeedFactor { get; set; } = 1.0;
}
