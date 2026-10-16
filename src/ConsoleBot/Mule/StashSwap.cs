using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Mule;

/// <summary>
/// Gets stash items into a full inventory by swapping places with inventory items of the same size:
/// the stash item is lifted (0x19), put down on top of the inventory item (0x1F) - which the server
/// then hands to the cursor - and that item goes into the stash cell just freed (0x18). The same
/// three packets a 1.09 client sends for it (capture of 2026-10-10). A full mule can hand over what
/// it holds this way without anything leaving its containers.
/// </summary>
/// <remarks>
/// Never drops. When a step fails, whatever is on the cursor goes back to where it came from, or to
/// any free stash or inventory cell; the swap stops there and is reported.
/// </remarks>
public static class StashSwap
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2);

    /// <param name="fromStash">Stash items wanted in the inventory, in order of preference.</param>
    /// <param name="mayGoToStash">Inventory items allowed to move to the stash in exchange.</param>
    /// <returns>How many stash items reached the inventory.</returns>
    public static int SwapIntoInventory(Game game, IEnumerable<Item> fromStash, Func<Item, bool> mayGoToStash, ILogger logger)
    {
        // Rows 8 and up of the 10x10 stash are left out: the client's own put-down there goes out as a
        // plain stash insert, but Game.InsertItemIntoContainer sends an extra second-page packet for
        // them that no capture shows. Once the first swaps have traded away, the rest come out of the
        // stash the normal way.
        var wanted = fromStash.Where(i => i.Container == ContainerType.Stash && i.Location.Y <= 7).ToList();
        if (wanted.Count == 0 || !InventoryHelpers.OpenStash(game))
        {
            return 0;
        }

        var swapped = 0;
        try
        {
            foreach (var stashItem in wanted)
            {
                var partner = game.Inventory.Items.FirstOrDefault(i =>
                    i.Width == stashItem.Width && i.Height == stashItem.Height && mayGoToStash(i));
                if (partner == null)
                {
                    break;
                }

                if (!SwapOne(game, stashItem, partner, logger))
                {
                    break;
                }

                swapped++;
            }
        }
        finally
        {
            InventoryHelpers.CloseStash(game);
        }

        logger.LogInformation("{Character}: swapped {Swapped} of {Wanted} stash items into the inventory", game.Me.Name, swapped, wanted.Count);
        return swapped;
    }

    private static bool SwapOne(Game game, Item stashItem, Item partner, ILogger logger)
    {
        var stashCell = stashItem.Location;

        game.RemoveItemFromContainer(stashItem);
        if (!GeneralHelpers.TryWithTimeout(_ => game.CursorItem?.Id == stashItem.Id, StepTimeout))
        {
            // Nothing was lifted, or the lift is only late: make sure it ends up back in the stash.
            PutAway(game, stashCell, ItemContainer.Stash, logger);
            return false;
        }

        game.SwapCursorItemWith(stashItem, partner);
        if (!GeneralHelpers.TryWithTimeout(_ => game.Inventory.FindItemById(stashItem.Id) != null && game.CursorItem?.Id == partner.Id, StepTimeout))
        {
            logger.LogWarning("{Character}: swapping {StashItem} with {Partner} was not accepted", game.Me.Name, stashItem.Name, partner.Name);
            PutAway(game, stashCell, ItemContainer.Stash, logger);
            return false;
        }

        game.InsertItemIntoContainer(partner, stashCell, ItemContainer.Stash);
        if (!GeneralHelpers.TryWithTimeout(_ => game.Stash.FindItemById(partner.Id) != null && game.CursorItem == null, StepTimeout))
        {
            PutAway(game, stashCell, ItemContainer.Stash, logger);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts whatever is on the cursor down: in the given cell, else any free stash cell, else any free
    /// inventory cell. Never drops; an item that fits nowhere stays on the cursor and is logged.
    /// </summary>
    private static void PutAway(Game game, Point cell, ItemContainer container, ILogger logger)
    {
        var held = game.CursorItem;
        if (held == null)
        {
            return;
        }

        var candidates = new List<(Point Cell, ItemContainer Container)> { (cell, container) };
        if (game.Stash.FindFreeSpace(held) is { } stashCell)
        {
            candidates.Add((stashCell, ItemContainer.Stash));
        }

        if (game.Inventory.FindFreeSpace(held) is { } inventoryCell)
        {
            candidates.Add((inventoryCell, ItemContainer.Inventory));
        }

        foreach (var (target, targetContainer) in candidates)
        {
            game.InsertItemIntoContainer(held, target, targetContainer);
            if (GeneralHelpers.TryWithTimeout(_ => game.CursorItem == null, StepTimeout))
            {
                return;
            }
        }

        logger.LogError("{Character}: {Item} is still on the cursor after a failed swap; not dropping it", game.Me.Name, held.Name);
    }
}
