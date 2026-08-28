using System.ComponentModel.DataAnnotations;

namespace D2NG.MuleManager.Configuration;

/// <summary>
/// Credentials of one account the mule manager logs on to. Every character on the account is
/// indexed.
/// </summary>
public class MuleManagerAccount
{
    /// <summary>
    /// Battle.net account name.
    /// </summary>
    [Required]
    public string Name { get; set; }

    /// <summary>
    /// Password for <see cref="Name"/>.
    /// </summary>
    [Required]
    public string Password { get; set; }
}
