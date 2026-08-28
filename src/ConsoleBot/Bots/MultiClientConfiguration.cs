using ConsoleBot.Bots.Types;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots;

/// <summary>
/// Base configuration for bots that drive several clients at once (cows, chaos sanctuary, baal,
/// ...). Every client logs on with its own <see cref="AccountConfig"/> and they all meet in the
/// same game, named from <see cref="BotConfiguration.GameNamePrefix"/>.
/// </summary>
public class MultiClientConfiguration
{
    /// <summary>
    /// Whether this bot creates the games itself. Set to <see langword="false"/> to only join
    /// games created elsewhere, for example when a second bot instance is hosting the run.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ShouldCreateGames { get; set; } = true;

    /// <summary>
    /// The accounts and characters taking part in the run, one entry per client. The first entry
    /// is the client that creates the game when <see cref="ShouldCreateGames"/> is enabled.
    /// </summary>
    public List<AccountConfig> Accounts { get; set; }

    /// <summary>
    /// Validates that <see cref="Accounts"/> is present and that every account is itself valid.
    /// Derived configurations override this to add their own checks.
    /// </summary>
    public virtual void Validate()
    {
        if (Accounts == null)
        {
            throw new ValidationException($"{nameof(Accounts)} is required on multi-client configuration");
        }

        Accounts.ForEach(a => a.Validate());
    }
}
