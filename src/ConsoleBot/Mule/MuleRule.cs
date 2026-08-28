using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule;

/// <summary>
/// A single mule rule: one AND-group of filters inside the OR-list of
/// <see cref="MuleAccount.MatchesAny"/>.
/// </summary>
public class MuleRule
{
    /// <summary>
    /// Filters that must all match for this rule to accept an item. An empty list matches
    /// everything.
    /// </summary>
    [Required]
    public List<MuleFilter> MatchesAll { get; set; } = [];
}
