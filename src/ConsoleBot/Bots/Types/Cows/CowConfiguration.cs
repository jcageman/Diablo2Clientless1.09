using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// Configuration for the cow level bot, bound from <c>bot:cows</c>. Adds the portal character on
/// top of the shared multi-client settings.
/// </summary>
public class CowConfiguration : MultiClientConfiguration
{
    /// <summary>
    /// Name of the character that cubes Wirt's leg with a town portal book and opens the cow
    /// portal for the other clients. Must be one of the configured
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
