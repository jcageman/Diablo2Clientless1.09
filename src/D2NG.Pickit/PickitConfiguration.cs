using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace D2NG.Pickit;

/// <summary>
/// Pickit settings, bound from the root <c>pickit</c> section of the separate file passed via the
/// <c>pickitconfig</c> command line parameter. What to pick up and what to keep is decided by
/// the nip rules in <see cref="NipDirectory"/>; the properties here cover the two decisions that
/// nip rules do not express - gambling and what is forwarded to the external client.
/// </summary>
public sealed class PickitConfiguration
{
    /// <summary>
    /// Directory holding the <c>.nip</c> files for this run, searched recursively. Point it at the
    /// tree matching the game mode, so a Classic tree for classic characters and an Expansion tree
    /// for expansion ones. Rules are loaded once, on first use.
    /// </summary>
    [Required]
    public required string NipDirectory { get; set; }

    /// <summary>
    /// Which items the bot gambles for at the vendor.
    /// </summary>
    [Required]
    public required GambleConfiguration Gamble { get; set; }

    /// <summary>
    /// Items that are kept but not reported to the external messaging client, so routine loot does
    /// not flood the chat.
    /// </summary>
    [Required]
    public required List<ExternalItemBlacklistRule> ExternalItemBlacklist { get; set; }

    /// <summary>
    /// How the bot treats the inventory it is carrying. Optional; the defaults match the layout the
    /// bot has always assumed.
    /// </summary>
    public InventoryConfiguration Inventory { get; set; } = new();
}

/// <summary>
/// Inventory layout the bot must respect. Anything the bot itself depends on - the town portal and
/// identify tomes, the cube, rejuvenation potions and a bow class's ammunition - is handled in code
/// rather than here, because selling those breaks the run rather than changing a preference.
/// </summary>
public sealed class InventoryConfiguration
{
    /// <summary>
    /// Number of rows at the bottom of the inventory kept for charms. Charms sitting in those rows
    /// are never sold, stashed or muled. Counted from the bottom, so it stays correct whatever the
    /// inventory height is. Defaults to 4; set 0 to stop reserving rows at all.
    /// </summary>
    public int BottomCharmRowsToNotTouch { get; set; } = 4;
}

/// <summary>
/// Gambling settings for the pickit.
/// </summary>
public sealed class GambleConfiguration
{
    /// <summary>
    /// Gamble rules, evaluated in order. The first rule whose
    /// <see cref="GambleRule.MinimumCharacterLevel"/> the character meets decides the answer, so
    /// list the rules with the highest level requirement first.
    /// </summary>
    [Required]
    public required List<GambleRule> Rules { get; set; }

}

/// <summary>
/// One gamble rule: the item slots to gamble for once the character is high enough level.
/// </summary>
public sealed class GambleRule
{
    /// <summary>
    /// Item names to gamble for, for example <c>Amulet</c> or <c>Ring</c>.
    /// </summary>
    [Required]
    public required List<ItemName> ItemNames { get; set; }

    /// <summary>
    /// Character level from which this rule applies. Defaults to 0, which makes the rule apply at
    /// any level.
    /// </summary>
    public uint MinimumCharacterLevel { get; set; }
}

/// <summary>
/// One blacklist rule for external reporting. Every property that is set must match; the ones left
/// unset are ignored, so a rule with nothing set blacklists every item.
/// </summary>
public sealed class ExternalItemBlacklistRule
{
    /// <summary>
    /// Item names this rule covers. An empty list, the default, matches any name.
    /// </summary>
    public List<ItemName> ItemNames { get; set; } = [];

    /// <summary>
    /// Classification this rule covers, for example <c>Gem</c>. Leave unset to match any
    /// classification.
    /// </summary>
    public ClassificationType? Classification { get; set; }

    /// <summary>
    /// Quality this rule covers, for example <c>Unique</c>. Leave unset to match any quality.
    /// </summary>
    public QualityType? Quality { get; set; }

    /// <summary>
    /// Returns whether <paramref name="item"/> is covered by this rule, meaning it should not be
    /// reported to the external client.
    /// </summary>
    public bool Matches(Item item)
    {
        var nameMatches = ItemNames.Count == 0 || ItemNames.Contains(item.Name);
        var classificationMatches = !Classification.HasValue || Classification == item.Classification;
        var qualityMatches = !Quality.HasValue || Quality == item.Quality;

        return nameMatches && classificationMatches && qualityMatches;
    }
}
