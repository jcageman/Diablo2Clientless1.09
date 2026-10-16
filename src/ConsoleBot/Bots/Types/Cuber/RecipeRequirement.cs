using D2NG.Core.D2GS.Items;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cuber;

/// <summary>
/// One ingredient of a cube recipe. The filters that are set must all match for an item to count
/// as this ingredient; at least one of them is required.
/// </summary>
public class RecipeRequirement
{
    /// <summary>
    /// Required item quality, for example <c>Magical</c> or <c>Rare</c>. Leave unset to accept
    /// any quality.
    /// </summary>
    public QualityType? Quality { get; set; }

    /// <summary>
    /// How many matching items one transmute consumes, for example 3 for a gem upgrade.
    /// </summary>
    public int Amount { get; set; }

    /// <summary>
    /// Accepted item names. Leave unset to accept any name that passes the other filters.
    /// </summary>
    public List<ItemName> ItemNames { get; set; }

    /// <summary>
    /// Required item classification, for example <c>Ring</c> or a gem tier. Leave unset to accept
    /// any classification.
    /// </summary>
    public ClassificationType? Classification { get; set; }

    /// <summary>
    /// How many matching items always stay on the mules, for example Ist kept for magic find gear or
    /// spare runes for runewords. Only what is above the reserve is cubed. Defaults to 0.
    /// </summary>
    public int Reserve { get; set; }

    /// <summary>
    /// Lowest item level accepted, for example 91 for grand charms that can roll the best affixes.
    /// Leave unset to accept any item level.
    /// </summary>
    public uint? MinLevel { get; set; }

    /// <summary>
    /// True when the item passes every filter that is set.
    /// </summary>
    public bool Matches(Item item)
    {
        return (ItemNames == null || ItemNames.Contains(item.Name))
            && (Classification == null || Classification == item.Classification)
            && (Quality == null || Quality == item.Quality)
            && (MinLevel == null || item.Level >= MinLevel);
    }

    /// <summary>
    /// Validates that at least one of <see cref="Classification"/>, <see cref="ItemNames"/> or
    /// <see cref="Quality"/> is set, so the requirement cannot match every item in the stash.
    /// </summary>
    public void Validate()
    {
        if (Classification == null && ItemNames == null && Quality == null)
        {
            throw new ValidationException($"at least one of {nameof(Classification)}, {nameof(ItemNames)} or {nameof(Quality)} is required on cube configuration recipe requirement");
        }
    }
}
