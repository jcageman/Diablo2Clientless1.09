using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit;

public static class Pickit
{
    private static readonly Lazy<IReadOnlyList<NipRule>> NipRules = new(LoadNipRules);
    private static PickitConfiguration? Configuration { get; set; }

    public static void Configure(PickitConfiguration configuration)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }


    public static bool ShouldPickupItem(Game game, Item item, bool shouldPickupGoldItems)
    {
        return ShouldPickupItem(game.Me.Class, shouldPickupGoldItems, item);
    }

    public static bool ShouldPickupItem(CharacterClass characterClass, bool shouldPickupGoldItems, Item item)
    {
        return ExplainPickup(characterClass, shouldPickupGoldItems, item).Result;
    }
    public static bool ShouldKeepItem(Game game, Item item)
    {
        return ShouldKeepItem(game.Me.Class, item);
    }

    public static bool ShouldKeepItem(CharacterClass characterClass, Item item)
    {
        return ExplainKeep(characterClass, item).Result;
    }

    /// <summary>
    /// Same decision as <see cref="ShouldPickupItem(CharacterClass, bool, Item)"/>, but also
    /// reports which nip rule decided it. Used for auditing what the bot leaves on the ground.
    /// </summary>
    public static PickitVerdict ExplainPickup(CharacterClass characterClass, bool shouldPickupGoldItems, Item item)
    {
        // Gold piles arrive with the identified flag set, like everything the game never asks Cain
        // about, but gold never lands in the inventory so no keep rule can ever speak for it. Judging
        // it on the pickup side is what makes '[type] == gold && [amount] > x' pick anything up at all.
        var useKeepRules = item.IsIdentified && !item.IsGold;

        var rule = EvaluateNipRules(
            item,
            characterClass,
            useKeepRules: useKeepRules,
            includeGoldItems: shouldPickupGoldItems && !useKeepRules);

        return rule == null ? PickitVerdict.NoRule() : PickitVerdict.Matched(rule);
    }

    /// <summary>
    /// Same decision as <see cref="ShouldKeepItem(CharacterClass, Item)"/>, but also reports why.
    /// </summary>
    public static PickitVerdict ExplainKeep(CharacterClass characterClass, Item item)
    {
        if (!item.IsIdentified)
        {
            return PickitVerdict.Unidentified();
        }

        var rule = EvaluateNipRules(item, characterClass, useKeepRules: true, includeGoldItems: false);
        return rule == null ? PickitVerdict.NoRule() : PickitVerdict.Matched(rule);
    }

    private static NipRule? EvaluateNipRules(Item item, CharacterClass characterClass, bool useKeepRules, bool includeGoldItems)
    {
        var rules = NipRules.Value;
        if (rules.Count == 0)
        {
            return null;
        }

        var context = new NipEvaluationContext(item, characterClass);
        if (useKeepRules)
        {
            foreach (var rule in rules)
            {
                if (!rule.PickupOnly && rule.MatchesKeep(context))
                {
                    return rule;
                }
            }

            return null;
        }

        foreach (var rule in rules)
        {
            if ((!rule.PickupOnly || includeGoldItems) && rule.MatchesPickup(context))
            {
                return rule;
            }
        }

        return null;
    }

    private static IReadOnlyList<NipRule> LoadNipRules()
    {
        return NipParser.ParseDirectory(GetConfiguration().NipDirectory);
    }

    public static bool ShouldGamble(Self self, Item item)
    {
        return ShouldGamble(self, item, GetConfiguration());
    }

    public static bool ShouldGamble(Self self, Item item, PickitConfiguration configuration)
    {
        return ShouldGamble((uint)self.Attributes[Core.D2GS.Players.Attribute.Level], item, configuration);
    }

    public static bool ShouldGamble(uint characterLevel, Item item, PickitConfiguration configuration)
    {
        if (item.IsIdentified)
        {
            return false;
        }

        foreach (var rule in configuration.Gamble.Rules)
        {
            if (characterLevel >= rule.MinimumCharacterLevel)
            {
                return rule.ItemNames.Contains(item.Name);
            }
        }

        return false;
    }

    /// <summary>
    /// Items the bot always needs to hold on to. The tome of identify is deliberately absent: bots
    /// identify at Deckard Cain, nothing reads a tome of identify any more, and holding one only costs
    /// inventory space that could carry loot.
    /// </summary>
    private static readonly ItemName[] RequiredInventoryItems =
    [
        ItemName.TomeOfTownPortal,
        ItemName.HoradricCube
    ];

    private static readonly ClassificationType[] RequiredInventoryClassifications =
    [
        ClassificationType.RejuvenationPotion
    ];

    private static readonly ClassificationType[] CharmClassifications =
    [
        ClassificationType.SmallCharm,
        ClassificationType.LargeCharm,
        ClassificationType.GrandCharm
    ];

    private static readonly HashSet<ItemName> AdditionalReservedItems = [];
    private static readonly HashSet<ClassificationType> AdditionalReservedClassifications = [];

    /// <summary>
    /// Reserves an item for the lifetime of this bot, on top of the ones the bot always needs. Used
    /// for items only one bot role cares about - the cow portal character keeping Wirt's leg - which
    /// every other bot should be free to sell.
    /// </summary>
    public static void ReserveInventoryItem(ItemName itemName)
    {
        lock (AdditionalReservedItems)
        {
            AdditionalReservedItems.Add(itemName);
        }
    }

    /// <summary>
    /// Reserves a whole classification the way <see cref="ReserveInventoryItem"/> reserves one name.
    /// A rusher carries spare healing and mana in its inventory and drinks from there first, so the
    /// vendor step selling that reserve left it fighting on the belt alone.
    /// </summary>
    public static void ReserveInventoryClassification(ClassificationType classification)
    {
        lock (AdditionalReservedItems)
        {
            AdditionalReservedClassifications.Add(classification);
        }
    }

    /// <summary>
    /// Whether an item is one the bot needs to keep, wherever it happens to be sitting. Unlike
    /// <see cref="CanTouchInventoryItem(Game, Item)"/> this ignores which container it is in.
    /// </summary>
    /// <remarks>
    /// A cursor item with no room in the inventory gets parked in the horadric cube, and cube contents
    /// used to be sold without any reservation check at all - which is how characters lost their tome of
    /// town portal and then failed every attempt to make one.
    /// </remarks>
    public static bool IsReservedItem(Item item)
    {
        if (RequiredInventoryItems.Contains(item.Name) || RequiredInventoryClassifications.Contains(item.Classification))
        {
            return true;
        }

        lock (AdditionalReservedItems)
        {
            return AdditionalReservedItems.Contains(item.Name) || AdditionalReservedClassifications.Contains(item.Classification);
        }
    }

    public static bool CanTouchInventoryItem(Game game, Item item)
    {
        return CanTouchInventoryItem(game.Me.Class, item, (int)game.Inventory.Height);
    }

    public static bool CanTouchInventoryItem(CharacterClass characterClass, Item item, int inventoryHeight)
    {
        if (item.Container != ContainerType.Inventory)
        {
            return true;
        }

        if (RequiredInventoryItems.Contains(item.Name) || RequiredInventoryClassifications.Contains(item.Classification))
        {
            return false;
        }

        lock (AdditionalReservedItems)
        {
            if (AdditionalReservedItems.Contains(item.Name) || AdditionalReservedClassifications.Contains(item.Classification))
            {
                return false;
            }
        }

        if (characterClass == CharacterClass.Amazon && (item.Name == ItemName.Arrows || item.Name == ItemName.Javelin))
        {
            return false;
        }

        var reservedRows = GetConfiguration().Inventory.BottomCharmRowsToNotTouch;
        var firstReservedRow = Math.Max(0, inventoryHeight - reservedRows);
        if (reservedRows > 0
            && item.Location.Y >= firstReservedRow
            && CharmClassifications.Contains(item.Classification))
        {
            return false;
        }

        return true;
    }

    public static bool SendItemToKeepToExternalClient(Item item)
    {
        return SendItemToKeepToExternalClient(item, GetConfiguration());
    }

    public static bool SendItemToKeepToExternalClient(Item item, PickitConfiguration configuration)
    {
        return !configuration.ExternalItemBlacklist.Any(rule => rule.Matches(item));
    }

    private static PickitConfiguration GetConfiguration()
    {
        return Configuration ?? throw new InvalidOperationException("Pickit must be configured before use.");
    }
}
