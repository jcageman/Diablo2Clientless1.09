using D2NG.Core.D2GS.Items;
using D2NG.Pickit;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;

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
    /// The one nip file allowed to keep a matching item, for example <c>CraftBases</c>: a craft then
    /// takes crafting stock only, never a magic find ring or a life jewel another file keeps for its
    /// own stats. Leave unset to accept an item whatever keeps it.
    /// </summary>
    public string KeptOnlyBy { get; set; }

    /// <summary>
    /// The nip files keeping an item, for <see cref="KeptOnlyBy"/>. Unset, the pickit's own rules decide.
    /// </summary>
    public Func<Item, IReadOnlyList<string>> KeepRuleSources { get; set; }

    /// <summary>
    /// The mule accounts a matching item must be held on, for example the crafting-stock mules: a reroll
    /// then never takes a charm stored on the general mules, whatever the keep rules say of it now.
    /// Leave unset to take it from any mule.
    /// </summary>
    public List<string> Accounts { get; set; }

    /// <summary>
    /// True when the held item passes every filter, <see cref="Accounts"/> included.
    /// </summary>
    public bool Takes(HeldItem held)
    {
        return Matches(held.Item) && (Accounts == null || Accounts.Contains(held.Account, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the item passes every filter that is set.
    /// </summary>
    public bool Matches(Item item)
    {
        return (ItemNames == null || ItemNames.Contains(item.Name))
            && (Classification == null || Classification == item.Classification)
            && (Quality == null || Quality == item.Quality)
            && (MinLevel == null || item.Level >= MinLevel)
            && (KeptOnlyBy == null || IsKeptOnlyBy(item));
    }

    private bool IsKeptOnlyBy(Item item)
    {
        var sources = (KeepRuleSources ?? Pickit.KeepRuleSources)(item);
        return sources.Count == 1
            && string.Equals(Path.GetFileNameWithoutExtension(sources[0]), Path.GetFileNameWithoutExtension(KeptOnlyBy), StringComparison.OrdinalIgnoreCase);
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
