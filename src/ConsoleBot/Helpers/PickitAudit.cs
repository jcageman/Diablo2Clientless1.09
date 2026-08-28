using D2NG.Core;
using D2NG.Core.D2GS.Items;
using D2NG.Pickit;
using Serilog;
using System;
using System.Linq;

namespace ConsoleBot.Helpers;

/// <summary>
/// Records what the bot found and what the pickit decided about each item, so nip rules can be
/// audited against real runs - both the items left on the ground and, after identification, the
/// items sold instead of kept.
/// </summary>
public static class PickitAudit
{
    private static ILogger _auditLogger;

    /// <summary>
    /// Sends the per-item audit to its own logger, keeping the main log readable. When this is not
    /// called, kept items are logged at Information and the rest at Debug instead.
    /// </summary>
    public static void Configure(ILogger auditLogger)
    {
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Logs the pickit verdict for a single item as it drops. Used by the multi-client bots, which
    /// react to drops as they happen rather than sweeping the ground after a kill.
    /// </summary>
    public static void LogItemDrop(Game game, Item item, bool shouldPickupGoldItems)
    {
        var verdict = Pickit.ExplainPickup(game.Me.Class, shouldPickupGoldItems, item);
        Write(game, "drop", item, verdict.Result ? "PICKUP" : "leave", verdict.Reason, verdict.Result);
    }

    /// <summary>
    /// Logs every item currently on the ground with the pickit verdict and the nip rule that
    /// produced it. Call this before picking items up, while everything the run dropped is visible.
    /// </summary>
    public static void LogGroundItems(Game game, string context, bool shouldPickupGoldItems)
    {
        // Gold is in here too: it is decided by an amount rule like everything else, and a run that
        // walks past its piles is exactly the kind of thing this audit is meant to show.
        var groundItems = game.Items.Values
            .Where(i => i.Ground)
            .OrderBy(i => i.Location.Distance(game.Me.Location))
            .ToList();

        if (groundItems.Count == 0)
        {
            return;
        }

        var matched = 0;
        foreach (var item in groundItems)
        {
            var verdict = Pickit.ExplainPickup(game.Me.Class, shouldPickupGoldItems, item);
            if (verdict.Result)
            {
                matched++;
            }

            Write(game, context, item, verdict.Result ? "PICKUP" : "leave", verdict.Reason, verdict.Result);
        }

        Log.Information(
            "{Context}: {Total} items on the ground, {Matched} matched the pickit, {Left} left behind",
            context,
            groundItems.Count,
            matched,
            groundItems.Count - matched);
    }

    /// <summary>
    /// Logs the keep-or-sell verdict for everything identified in the inventory and cube. Call this
    /// after identifying and before selling, which is the point the rules actually decide what the
    /// run walks away with.
    /// </summary>
    public static void LogKeepDecisions(Game game, string context)
    {
        var items = game.Inventory.Items
            .Concat(game.Cube.Items)
            .Where(i => i.IsIdentified)
            .ToList();

        if (items.Count == 0)
        {
            return;
        }

        // Items the bot may not touch are not up for a keep-or-sell decision at all, so they are left out
        // of the audit entirely rather than reported as a third category.
        var decidable = items.Where(i => Pickit.CanTouchInventoryItem(game, i)).ToList();
        if (decidable.Count == 0)
        {
            return;
        }

        var kept = 0;
        foreach (var item in decidable)
        {
            var verdict = Pickit.ExplainKeep(game.Me.Class, item);
            if (verdict.Result)
            {
                kept++;
            }

            Write(game, context, item, verdict.Result ? "KEEP" : "sell", verdict.Reason, verdict.Result);
        }

        Log.Information(
            "{Context}: {Total} identified items, {Kept} kept, {Sold} sold",
            context,
            decidable.Count,
            kept,
            decidable.Count - kept);
    }

    private static void Write(Game game, string context, Item item, string decision, string reason, bool isPositive)
    {
        if (_auditLogger != null)
        {
            _auditLogger.Information("{Character} {Context}: {Decision} {Item} [{Reason}]", game.Me.Name, context, decision, Describe(item), reason);
        }
        else if (isPositive)
        {
            Log.Information("{Context}: {Decision} {Item} [{Reason}]", context, decision, Describe(item), reason);
        }
        else
        {
            Log.Debug("{Context}: {Decision} {Item} [{Reason}]", context, decision, Describe(item), reason);
        }
    }

    // GetFullDescription is a multi-line stat block; the audit wants one grep-able line per item.
    // A null separator splits on every kind of whitespace, collapsing the newlines and tabs.
    private static string Describe(Item item)
    {
        return string.Join(" ", item.GetFullDescription().Split(null as char[], StringSplitOptions.RemoveEmptyEntries));
    }
}
