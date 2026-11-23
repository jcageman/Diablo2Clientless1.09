using D2NG.Mule.Upgrades;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace D2NG.MuleManager.Configuration;

/// <summary>
/// Configuration for the mule manager, bound from the <c>mulemanager</c> section. The mule manager
/// is a separate tool that logs on to the mule accounts and indexes what they are holding, reads
/// what the characters the bots play wear, and ranks mule items as upgrades for them. It does not
/// play the game.
/// </summary>
public class MuleManagerConfiguration
{
    /// <summary>
    /// Battle.net realm (gateway) host the accounts live on.
    /// </summary>
    [Required]
    public string Realm { get; set; }

    /// <summary>
    /// Path to the Diablo II installation folder, used to hash the game executables during logon
    /// and to read the MPQ data files.
    /// </summary>
    [Required]
    public string GameFolder { get; set; }

    /// <summary>
    /// Name registered as the owner of the CD keys, sent during the Battle.net logon handshake.
    /// </summary>
    [Required]
    public string KeyOwner { get; set; }

    /// <summary>
    /// The mule accounts to index. Every character on them is a mule.
    /// </summary>
    [Required]
    public List<MuleManagerAccount> Accounts { get; set; }

    /// <summary>
    /// Accounts whose characters the bots play. The mule manager logs on to them only to read what
    /// each character wears; the bots record the same thing whenever they leave a game.
    /// </summary>
    public List<MuleManagerAccount> GearAccounts { get; set; } = [];

    /// <summary>
    /// The bots' pickit config file. Its inventory layout decides which items are recorded as
    /// untouchable, the charms in the rows the bots never move.
    /// </summary>
    [Required]
    public string PickitConfigFile { get; set; }

    /// <summary>
    /// Folder holding the game data files, the bot folder's <c>data</c>. Unset, the data folder
    /// beside the assembly is used. Without <c>item-affixes.json</c> there the upgrade finder cannot
    /// check requirements or show roll ranges.
    /// </summary>
    public string DataDirectory { get; set; }

    /// <summary>
    /// What "better" means per build. Unset, <see cref="UpgradePresets.Defaults"/>.
    /// </summary>
    public List<UpgradePreset> Presets { get; set; } = [];

    /// <summary>
    /// How many accounts are visited at once. The realm limits game joins per address, so more
    /// mostly means more waiting at the gate.
    /// </summary>
    [Range(1, 8)]
    public int Parallelism { get; set; } = 3;
}
