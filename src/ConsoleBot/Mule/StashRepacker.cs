using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Mule.Packing;
using Destination = D2NG.Mule.Packing.Destination;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace ConsoleBot.Mule;

/// <summary>
/// Executes a <see cref="RepackPlan"/> against a live stash, one pick-up and put-down per move.
/// </summary>
public class StashRepacker
{
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;

    public StashRepacker(ILogger logger)
    {
        _logger = logger;
    }

    public MoveItemResult Execute(Game game, RepackPlan plan)
    {
        if (!InventoryHelpers.OpenStash(game))
        {
            return MoveItemResult.Failed;
        }

        Thread.Sleep(100);
        var result = MoveItemResult.Succes;
        foreach (var move in plan.Moves)
        {
            if (!game.Items.TryGetValue(move.Id, out var item))
            {
                _logger.LogError("{Name}: repack refers to item {ItemId} which the game no longer knows", game.Me.Name, move.Id);
                result = MoveItemResult.Failed;
                break;
            }

            game.RemoveItemFromContainer(item);
            if (!GeneralHelpers.TryWithTimeout((_) => game.CursorItem?.Id == item.Id, MoveTimeout))
            {
                _logger.LogError("{Name}: lifting {ItemId} {ItemName} for the repack failed", game.Me.Name, item.Id, item.Name);
                result = MoveItemResult.Failed;
                break;
            }

            Thread.Sleep(100);
            var container = move.To == Destination.MuleStash ? ItemContainer.Stash : ItemContainer.Inventory;
            game.InsertItemIntoContainer(item, new Point((ushort)move.At.X, (ushort)move.At.Y), container);
            var landed = GeneralHelpers.TryWithTimeout(
                (_) => game.CursorItem == null && (move.To == Destination.MuleStash ? game.Stash : game.Inventory).FindItemById(item.Id) != null,
                MoveTimeout);
            if (!landed)
            {
                _logger.LogError("{Name}: placing {ItemId} {ItemName} at {Where} {Cell} during the repack failed", game.Me.Name, item.Id, item.Name, move.To, move.At);
                InventoryHelpers.CleanupCursorItem(game);
                result = MoveItemResult.Failed;
                break;
            }
            Thread.Sleep(100);
        }

        InventoryHelpers.CloseStash(game);
        return result;
    }
}
