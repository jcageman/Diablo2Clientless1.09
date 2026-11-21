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
    /// Whether every client clears the level rather than only following the lead. Nothing lists
    /// which monsters to kill: everything in the level is a target, and whether a given character
    /// can hurt a given pack comes from its resistances. With this off the bot behaves exactly as
    /// it did before active mode existed.
    /// </summary>
    public bool ActiveMode { get; set; }

    /// <summary>
    /// Seconds the hunt may run from the moment the party sets off; unset, the party clears the level. A party's last
    /// packs, spread out over the level, can earn less per minute than a fresh game does with its setup counted, but
    /// where that point lies depends on how fast the party kills and how long its setup takes: pick it from
    /// <c>Trace kpi xp &lt;date&gt; --leave-after</c> over the party's own games. The classic Hell cow party of
    /// 20 Nov 2026 did best leaving 110-120 s after setting off (+2.0-2.4% in a replay of 249 games).
    /// </summary>
    public int? HuntBudgetSeconds { get; set; }

    /// <summary>Validates that a portal character is configured.</summary>
    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrEmpty(PortalCharacterName))
        {
            throw new ValidationException($"{nameof(PortalCharacterName)} is required on cow configuration");
        }

    }
}
