using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule;

/// <summary>
/// Muling settings, bound from the <c>mule</c> section. Describes the accounts the bot may stash
/// loot on once a playing character's inventory fills up.
/// </summary>
public class MuleConfiguration
{
    /// <summary>
    /// The mule accounts to use, tried in order. Each account defines which items it accepts and
    /// which of its characters may be filled.
    /// </summary>
    [Required]
    public List<MuleAccount> Accounts { get; set; }

    /// <summary>
    /// Items that are never worth a mule slot and are sold instead. An item is excluded when it
    /// matches any one of these filters. Omit the key to use the built-in list of flawless gems;
    /// give an empty list to mule everything.
    /// </summary>
    public List<MuleFilter> NeverMule { get; set; }
}
