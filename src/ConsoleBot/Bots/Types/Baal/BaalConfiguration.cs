using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Baal;

/// <summary>
/// Configuration for the baal bot, bound from <c>bot:baal</c>. Adds the portal character on top of
/// the shared multi-client settings.
/// </summary>
public class BaalConfiguration : MultiClientConfiguration
{
    /// <summary>
    /// Name of the character that teleports to the throne room and opens the portal the other
    /// clients take. Must be one of the configured
    /// <see cref="MultiClientConfiguration.Accounts"/>; matching is case-insensitive.
    /// </summary>
    public string PortalCharacterName { get; set; }

    /// <summary>
    /// Validates that a portal character is configured.
    /// </summary>
    public override void Validate()
    {
        if (string.IsNullOrEmpty(PortalCharacterName))
        {
            throw new ValidationException($"{nameof(PortalCharacterName)} is required on cow configuration");
        }
    }
}
