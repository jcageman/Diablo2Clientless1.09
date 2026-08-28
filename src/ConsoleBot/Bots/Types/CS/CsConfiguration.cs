using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.CS;

/// <summary>
/// Configuration for the chaos sanctuary bot, bound from <c>bot:cs</c>. Adds the teleport
/// character on top of the shared multi-client settings.
/// </summary>
public class CsConfiguration : MultiClientConfiguration
{
    /// <summary>
    /// Name of the character that teleports through the sanctuary; the other clients follow it.
    /// Must be one of the configured <see cref="MultiClientConfiguration.Accounts"/>; matching is
    /// case-insensitive.
    /// </summary>
    public string TeleportCharacterName { get; set; }

    /// <summary>
    /// Validates the shared multi-client settings and that a teleport character is configured.
    /// </summary>
    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrEmpty(TeleportCharacterName))
        {
            throw new ValidationException($"{nameof(TeleportCharacterName)} is required on cs configuration");
        }
    }
}
