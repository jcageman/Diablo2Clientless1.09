using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace ConsoleBot.Bots.Types;

/// <summary>
/// Credentials and per-character settings for one client. Single-client bots (mephisto, pindle,
/// travincal, cube) bind their whole configuration section to a subclass of this type, while
/// multi-client bots hold a list of these under <see cref="Bots.MultiClientConfiguration.Accounts"/>.
/// </summary>
public class AccountConfig
{
    /// <summary>
    /// Battle.net account name to log on with.
    /// </summary>
    [Required]
    public string Username { get; set; }

    /// <summary>
    /// Password for <see cref="Username"/>.
    /// </summary>
    [Required]
    public string Password { get; set; }

    /// <summary>
    /// Name of the character on the account to play. Must exist on the realm, matching is
    /// case-insensitive.
    /// </summary>
    [Required]
    public string Character { get; set; }

    /// <summary>
    /// Belt columns (0-3) reserved for healing potions. Together with <see cref="ManaSlots"/>
    /// these must cover all four columns without overlapping. Defaults to the two left columns.
    /// </summary>
    public List<int> HealthSlots = [0, 1];

    /// <summary>
    /// Belt columns (0-3) reserved for mana potions. Together with <see cref="HealthSlots"/>
    /// these must cover all four columns without overlapping. Defaults to the two right columns.
    /// </summary>
    public List<int> ManaSlots = [2, 3];

    /// <summary>
    /// When this character drinks and when it abandons a game. Overrides
    /// <see cref="Bots.BotConfiguration.Chicken"/> as a whole block when present, so a mixed-build
    /// run can give each character its own thresholds. Falls back to the bot-level block, then to
    /// the built-in defaults.
    /// </summary>
    public Chicken.ChickenConfiguration Chicken { get; set; }

    /// <summary>
    /// Whether the bot revives the mercenary in town when it has died. Defaults to
    /// <see langword="true"/>; set to <see langword="false"/> for characters that run without one.
    /// </summary>
    public bool ResurrectMerc { get; set; } = true;

    /// <summary>
    /// Validates that the credentials are filled in and that the belt slot assignment covers
    /// columns 0-3 exactly once.
    /// </summary>
    public virtual void Validate()
    {
        if (string.IsNullOrEmpty(Username))
        {
            throw new ValidationException($"{nameof(Username)} is required on account");
        }

        if (string.IsNullOrEmpty(Password))
        {
            throw new ValidationException($"{nameof(Password)} is required on account");
        }

        if (string.IsNullOrEmpty(Character))
        {
            throw new ValidationException($"{nameof(Character)} is required on account");
        }

        if (HealthSlots.Any(h => h < 0 || h > 3))
        {
            throw new ValidationException($"{nameof(HealthSlots)} should be between 0 and 3");
        }

        if (ManaSlots.Any(h => h < 0 || h > 3))
        {
            throw new ValidationException($"{nameof(ManaSlots)} should be between 0 and 3");
        }

        if (ManaSlots.Intersect(HealthSlots).Any())
        {
            throw new ValidationException($"{nameof(HealthSlots)} + {nameof(ManaSlots)} have overlapping slots");
        }

        var requiredSlots = new HashSet<int> { 0, 1, 2, 3 };
        requiredSlots.ExceptWith(ManaSlots);
        requiredSlots.ExceptWith(HealthSlots);
        if (requiredSlots.Count != 0)
        {
            throw new ValidationException($"{nameof(HealthSlots)} + {nameof(ManaSlots)} are missing some values {string.Join(",", requiredSlots)}");
        }
    }
}
