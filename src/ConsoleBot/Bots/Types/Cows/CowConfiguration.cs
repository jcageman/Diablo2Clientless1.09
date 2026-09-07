using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
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
    /// Whether the non-sorceress clients hunt monsters of their own instead of only following the
    /// lead. With this off the bot behaves exactly as it did before active mode existed.
    /// </summary>
    public bool ActiveMode { get; set; }

    /// <summary>
    /// The monsters the hunting party goes after in <see cref="ActiveMode"/>. Their packs are
    /// clustered like cow packs are, so the party can tell when it has been over all of them.
    /// Required with <see cref="ActiveMode"/>: the configuration binder appends to a list that
    /// already holds values, so a default here would be added to whatever is configured rather
    /// than replaced by it.
    /// </summary>
    public List<NPCCode> HuntedMonsters { get; set; } = [];

    /// <summary>
    /// Validates that a portal character is configured, and that active mode has something to hunt.
    /// </summary>
    public override void Validate()
    {
        if (string.IsNullOrEmpty(PortalCharacterName))
        {
            throw new ValidationException($"{nameof(PortalCharacterName)} is required on cow configuration");
        }

        if (ActiveMode && (HuntedMonsters == null || HuntedMonsters.Count == 0))
        {
            throw new ValidationException($"{nameof(HuntedMonsters)} must list at least one monster when {nameof(ActiveMode)} is enabled");
        }
    }
}
