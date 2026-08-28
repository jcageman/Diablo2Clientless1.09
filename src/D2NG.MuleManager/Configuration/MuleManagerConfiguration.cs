using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace D2NG.MuleManager.Configuration;

/// <summary>
/// Configuration for the mule manager, bound from the <c>mulemanager</c> section. The mule manager
/// is a separate tool that logs on to the mule accounts and indexes what they are holding; it does
/// not play the game.
/// </summary>
public class MuleManagerConfiguration
{
    /// <summary>
    /// Battle.net realm (gateway) host the mule accounts live on.
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
    /// The mule accounts to index. They are processed in parallel, so keep the list to what the
    /// realm tolerates in logons.
    /// </summary>
    [Required]
    public List<MuleManagerAccount> Accounts { get; set; }
}
