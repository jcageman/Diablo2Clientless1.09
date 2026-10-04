namespace ConsoleBot.Bots.Types.Mephisto;

/// <summary>
/// Configuration for the mephisto bot, bound from <c>bot:mephisto</c>. The run needs no settings
/// of its own, so this is just the single account and character to play - see
/// <see cref="AccountConfig"/>.
/// </summary>
public class MephistoConfiguration : AccountConfig
{
    /// <summary>
    /// Whether to keep casting Static Field while waiting for Frozen Orb to finish Mephisto off.
    /// Static Field has no cast delay, so the sub-second waits between orb casts are otherwise idle -
    /// but each one costs a skill swap back and forth on the right hand, which interrupts the repeat
    /// cast. Which way that trades is measured, not assumed, so this exists to A/B it.
    /// </summary>
    public bool StaticDuringOrbPhase { get; set; }
}
