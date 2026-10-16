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
    /// Nip file deciding which items this account accepts: an item is accepted when any line of it
    /// matches. A file name is looked up in <see cref="MuleConfiguration.NipDirectory"/>. Required,
    /// so an account can never end up accepting everything by accident.
    /// </summary>
    [Required]
    public string Nip { get; set; }

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

    /// <summary>
    /// Send each item to the character on this account that already holds the most items of its
    /// classification, rather than filling the characters in order. Meant for the crafting
    /// material mules, so the gloves, the amulets and the rings each gather on their own character
    /// and a craft needs as few logins as possible. Reads the mule database; off by default.
    /// </summary>
    public bool PlaceWithSameClassification { get; set; }
}
