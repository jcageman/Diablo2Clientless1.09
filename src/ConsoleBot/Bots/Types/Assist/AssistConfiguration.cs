using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Assist;

/// <summary>
/// Configuration for the assist bot, bound from <c>bot:assist</c>. The assist bot follows a lead
/// character around and fights alongside it instead of running a fixed route.
/// </summary>
public class AssistConfiguration
{
    /// <summary>
    /// The accounts and characters that do the assisting, one entry per client.
    /// </summary>
    public List<AccountConfig> Accounts { get; set; }

    /// <summary>
    /// Name of the character from <see cref="Accounts"/> that hosts (creates) the game. Optional;
    /// matching is case-insensitive.
    /// </summary>
    public string HostCharacterName { get; set; }

    /// <summary>
    /// Name of the character to follow. It does not have to be one of <see cref="Accounts"/> - it
    /// is normally played by a human. The bot restarts the game when this player cannot be found.
    /// </summary>
    public string LeadCharacterName { get; set; }

    /// <summary>
    /// Validates that the accounts are present and valid and that a lead character is configured.
    /// </summary>
    public void Validate()
    {
        if (Accounts == null)
        {
            throw new ValidationException($"{nameof(Accounts)} is required on cow configuration");
        }

        Accounts.ForEach(a => a.Validate());

        if (string.IsNullOrEmpty(LeadCharacterName))
        {
            throw new ValidationException($"{nameof(LeadCharacterName)} is required on assist configuration");
        }
    }
}
