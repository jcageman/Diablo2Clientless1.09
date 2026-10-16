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

    private static readonly Lazy<IReadOnlyList<NipRule>> UnlimitedKeepRules =
        new(() => NipRules.Value.Where(r => !r.PickupOnly && r.MaxQuantity == null).ToList());

    /// <summary>
    /// Items held outside the current game - on the mules - counted against a rule's
    /// '[maxquantity]'. Unset, only what the character itself carries and stashes is counted.
    /// </summary>
    public static Func<MuledStock>? MuledItems { get; set; }

    public static void Configure(PickitConfiguration configuration)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        QuietRules = LoadQuietRules(configuration);
    }


    public static bool ShouldPickupItem(Game game, Item item, bool shouldPickupGoldItems)
    {
        return ExplainPickup(game, shouldPickupGoldItems, item).Result;
    }

    /// <param name="heldItems">Items already held, counted against '[maxquantity]'. Null ignores the limits.</param>
    /// <param name="muledStock">Counted on top of <paramref name="heldItems"/>, and only when that is given.</param>
    public static bool ShouldPickupItem(bool shouldPickupGoldItems, Item item, IReadOnlyCollection<Item>? heldItems = null, MuledStock? muledStock = null)
    {
        return ExplainPickup(shouldPickupGoldItems, item, heldItems, muledStock).Result;
    }

    /// <summary>
    /// Stashed items are always kept: '[maxquantity]' only decides about new finds, so a limit
    /// that is already reached never sells or drops the stock that reached it.
    /// </summary>
    public static bool ShouldKeepItem(Game game, Item item)
    {
        return ExplainKeep(game, item).Result;
    }

    public static PickitVerdict ExplainKeep(Game game, Item item)
    {
        var stashed = item.Container == ContainerType.Stash || item.Container == ContainerType.Stash2;
        return stashed ? ExplainKeep(item) : ExplainKeep(item, OwnItems(game, item), MuledItems?.Invoke());
    }

    /// <param name="heldItems">Items already held, counted against '[maxquantity]'. Null ignores the limits.</param>
    /// <param name="muledStock">Counted on top of <paramref name="heldItems"/>, and only when that is given.</param>
    public static bool ShouldKeepItem(Item item, IReadOnlyCollection<Item>? heldItems = null, MuledStock? muledStock = null)
    {
        return ExplainKeep(item, heldItems, muledStock).Result;
    }

    public static PickitVerdict ExplainPickup(Game game, bool shouldPickupGoldItems, Item item)
    {
        return ExplainPickup(shouldPickupGoldItems, item, OwnItems(game, item), MuledItems?.Invoke());
    }

    /// <summary>
    /// What the character carries and stashes, minus <paramref name="item"/> itself.
    /// </summary>
    private static List<Item> OwnItems(Game game, Item item)
    {
        var held = game.Stash.Items;
        held.AddRange(game.Inventory.Items);
        held.AddRange(game.Cube.Items);
        held.RemoveAll(i => i.Id == item.Id);
        return held;
    }

    /// <summary>
    /// Same decision as <see cref="ShouldPickupItem(bool, Item, IReadOnlyCollection{Item}, MuledStock)"/>, but also
    /// reports which nip rule decided it. Used for auditing what the bot leaves on the ground.
    /// </summary>
    public static PickitVerdict ExplainPickup(bool shouldPickupGoldItems, Item item, IReadOnlyCollection<Item>? heldItems = null, MuledStock? muledStock = null)
    {
        // Gold piles arrive with the identified flag set, like everything the game never asks Cain
        // about, but gold never lands in the inventory so no keep rule can ever speak for it. Judging
        // it on the pickup side is what makes '[type] == gold && [amount] > x' pick anything up at all.
        var useKeepRules = item.IsIdentified && !item.IsGold;

        var rule = EvaluateNipRules(
            item,
            useKeepRules: useKeepRules,
            includeGoldItems: shouldPickupGoldItems && !useKeepRules,
            heldItems,
            muledStock);

        // An identified item never reaches the property-only pickup pass, so a line with nothing right
        // of '#' - pick up to sell, never keep - would otherwise do nothing for runes, essences or bases.
        if (rule == null && useKeepRules)
        {
            var context = new NipEvaluationContext(item);
            rule = NipRules.Value.FirstOrDefault(r => !r.PickupOnly && r.StatCondition == null && r.MatchesPickup(context) && HasRoom(r, heldItems, muledStock));
        }

        return rule == null ? PickitVerdict.NoRule() : PickitVerdict.Matched(rule);
    }

    /// <summary>
    /// Same decision as <see cref="ShouldKeepItem(Item, IReadOnlyCollection{Item}, MuledStock)"/>, but also reports why.
    /// </summary>
    public static PickitVerdict ExplainKeep(Item item, IReadOnlyCollection<Item>? heldItems = null, MuledStock? muledStock = null)
    {
        if (!item.IsIdentified)
        {
            return PickitVerdict.Unidentified();
        }

        var rule = EvaluateNipRules(item, useKeepRules: true, includeGoldItems: false, heldItems, muledStock);
        return rule == null ? PickitVerdict.NoRule() : PickitVerdict.Matched(rule);
    }

    private static NipRule? EvaluateNipRules(Item item, bool useKeepRules, bool includeGoldItems, IReadOnlyCollection<Item>? heldItems, MuledStock? muledStock)
    {
        var rules = NipRules.Value;
        if (rules.Count == 0)
        {
            return null;
        }

        var context = new NipEvaluationContext(item);
        if (useKeepRules)
        {
            foreach (var rule in rules)
            {
                if (!rule.PickupOnly && rule.MatchesKeep(context) && HasRoom(rule, heldItems, muledStock))
                {
                    return rule;
                }
            }

            return null;
        }

        foreach (var rule in rules)
        {
            if ((!rule.PickupOnly || includeGoldItems) && rule.MatchesPickup(context) && HasRoom(rule, heldItems, muledStock))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// False when the rule has a '[maxquantity]' and at least that many held and muled items count
    /// towards it, so the next rule gets its turn. The muled count is worked out once per stock.
    /// </summary>
    private static bool HasRoom(NipRule rule, IReadOnlyCollection<Item>? heldItems, MuledStock? muledStock)
    {
        if (rule.MaxQuantity is not { } maxQuantity || heldItems == null)
        {
            return true;
        }

        var held = CountTowards(rule, heldItems) + (muledStock?.CountTowards(rule, CountTowards) ?? 0);
        return held < maxQuantity;
    }

    /// <summary>
    /// How many of the items count towards the rule's limit: those this rule would keep and no rule
    /// without a limit would. An amulet kept for its skills is not crafting stock.
    /// </summary>
    private static int CountTowards(NipRule rule, IReadOnlyCollection<Item> items)
    {
        var unlimited = UnlimitedKeepRules.Value;
        return items
            .Select(i => new NipEvaluationContext(i))
            .Count(context => rule.MatchesKeep(context) && !unlimited.Any(r => r.MatchesKeep(context)));
    }

    /// <summary>
    /// The nip files with a keep rule for the item, limits ignored. The mule run uses it to send
    /// items kept only by one file - crafting stock - to their own mule.
    /// </summary>
    public static IReadOnlyList<string> KeepRuleSources(Item item)
    {
        var context = new NipEvaluationContext(item);
        return NipRules.Value
            .Where(r => !r.PickupOnly && r.MatchesKeep(context))
            .Select(r => Path.GetFileName(r.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static IReadOnlyList<NipRule> QuietRules = [];

    public static bool SendItemToKeepToExternalClient(Item item)
    {
        return SendItemToKeepToExternalClient(item, QuietRules);
    }

    /// <summary>
    /// False for a kept item a line of the quiet nip matches: routine loot such as gems, and
    /// crafting stock no other keep file wants.
    /// </summary>
    public static bool SendItemToKeepToExternalClient(Item item, IReadOnlyList<NipRule> quietRules)
    {
        var context = new NipEvaluationContext(item)
        {
            KeepRuleSources = () => KeepRuleSources(item)
        };
        return !quietRules.Any(rule => rule.MatchesExpression(context));
    }

    /// <summary>
    /// Read when the pickit is configured, so a mistyped path stops the bot at startup rather than
    /// at the first stash.
    /// </summary>
    public static IReadOnlyList<NipRule> LoadQuietRules(PickitConfiguration configuration)
    {
        if (string.IsNullOrEmpty(configuration.QuietNip))
        {
            return [];
        }

        if (!File.Exists(configuration.QuietNip))
        {
            throw new FileNotFoundException($"Quiet nip file {configuration.QuietNip} does not exist", configuration.QuietNip);
        }

        return NipParser.ParseFile(configuration.QuietNip);
    }

    private static PickitConfiguration GetConfiguration()
    {
        return Configuration ?? throw new InvalidOperationException("Pickit must be configured before use.");
    }
}
