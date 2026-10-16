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
    /// Nip file of items that are never worth a mule slot and are sold instead. Omit it to mule
    /// every item an account's nip accepts.
    /// </summary>
    public string NeverMuleNip { get; set; }

    /// <summary>
    /// Folder holding the mule nip files named by <see cref="MuleAccount.Nip"/> and
    /// <see cref="NeverMuleNip"/>. Keep it apart from the pickit's nip folder, which loads every
    /// nip file below it as a keep rule.
    /// </summary>
    [Required]
    public string NipDirectory { get; set; }

    /// <summary>
    /// Postgres connection string of the mule database shared with the mule manager. When set, the
    /// bot records what each mule character holds after visiting it and skips characters that were
    /// seen full within the last week. Omit it to visit every character on every mule run.
    /// </summary>
    public string ConnectionString { get; set; }
}
