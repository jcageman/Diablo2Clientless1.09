using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule;

/// <summary>
/// One account used to store loot on, plus the rules deciding which items land here.
/// </summary>
public class MuleAccount
{
    /// <summary>
    /// Battle.net account name of the mule.
    /// </summary>
    [Required]
    public string Username { get; set; }

    /// <summary>
    /// Password for <see cref="Username"/>.
    /// </summary>
    [Required]
    public string Password { get; set; }

    /// <summary>
    /// Rules deciding which items this account accepts; an item is accepted when it matches at
    /// least one rule. An empty list, the default, accepts every muleable item.
    /// </summary>
    public List<MuleRule> MatchesAny { get; set; } = [];

    /// <summary>
    /// Characters on this account that may be filled, by name. Leave empty to log on and use
    /// every character found on the account. Matching is case-insensitive.
    /// </summary>
    public List<string> IncludedCharacters { get; set; } = [];

    /// <summary>
    /// Characters on this account to leave alone, by name. Applied after
    /// <see cref="IncludedCharacters"/>, so it also excludes characters that were discovered
    /// automatically. Matching is case-insensitive.
    /// </summary>
    public List<string> ExcludedCharacters { get; set; } = [];
}
