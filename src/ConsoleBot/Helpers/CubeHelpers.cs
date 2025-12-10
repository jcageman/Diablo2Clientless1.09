using ConsoleBot.Enums;
using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ConsoleBot.Helpers;

public static class CubeHelpers
{
    private static readonly HashSet<ItemName> flawlessGems = [ItemName.FlawlessAmethyst, ItemName.FlawlessDiamond, ItemName.FlawlessEmerald, ItemName.FlawlessRuby, ItemName.FlawlessSapphire, ItemName.FlawlessSkull, ItemName.FlawlessTopaz];

    public static bool AnyGemsToTransmuteInStash(Game game)
    {
        var groupedGems = game.Stash.Items.Where(i => i.Classification == ClassificationType.Gem && flawlessGems.Contains(i.Name)).GroupBy(i => i.Name);
        if (groupedGems.Any())
        {
            return groupedGems.Max(g => g.Count()) >= 3;
        }

        return false;
    }

    public static void TransmuteGems(Game game, ILogger logger)
    {
        TransmuteFlawlessWithName(game, ItemName.FlawlessDiamond, logger);
        TransmuteFlawlessWithName(game, ItemName.FlawlessSkull, logger);
        TransmuteFlawlessWithName(game, ItemName.FlawlessRuby, logger);
        TransmuteFlawlessWithName(game, ItemName.FlawlessEmerald, logger);
        TransmuteFlawlessWithName(game, ItemName.FlawlessAmethyst, logger);
        TransmuteFlawlessWithName(game, ItemName.FlawlessTopaz, logger);
        TransmuteFlawlessWithName(game, ItemName.FlawlessSapphire, logger);
    }

    private static void TransmuteFlawlessWithName(Game game, ItemName flawlessName, ILogger logger)
    {
        var flawlessGems = game.Stash.Items.Where(i => i.Name == flawlessName)
                                 .ToList();
        if (flawlessGems.Count < 3)
        {
            return;
        }

        if (game.Cube.Items.Count != 0)
        {
            return;
        }

        var stashes = game.GetEntityByCode(EntityCode.Stash);
        if (stashes.Count == 0)
        {
            return;
        }

        var stash = stashes.Single();

        bool result = GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            if (game.Me.Location.Distance(stash.Location) >= 5)
            {
                if (game.Me.HasSkill(D2NG.Core.D2GS.Players.Skill.Teleport))
                {
                    game.TeleportToLocation(stash.Location);
                }
                else
                {
                    game.MoveTo(stash);
                }
            }
            else
            {
                return game.OpenStash(stash);
            }

            return false;
        }, TimeSpan.FromSeconds(4));

        if (!result)
        {
            logger.LogError("Failed to open stash");
            Thread.Sleep(300);
            game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            game.ClickButton(ClickType.CloseStash);
            return;
        }

        var flawlessGemsInInventory = new List<uint>();
        foreach (var gem in flawlessGems)
        {
            if (InventoryHelpers.MoveItemFromStashToInventory(game, gem) == MoveItemResult.Succes)
            {
                flawlessGemsInInventory.Add(gem.Id);
            }
        }

        Thread.Sleep(300);
        game.ClickButton(ClickType.CloseStash);
        Thread.Sleep(100);
        game.ClickButton(ClickType.CloseStash);

        logger.LogInformation("Moved {GemName} to inventory for transmuting", flawlessName);

        var remainingGems = flawlessGemsInInventory;
        while (remainingGems.Count > 2)
        {
            logger.LogInformation("Transmuting 3 {GemName} to perfect", flawlessName);
            var gemsToTransmute = remainingGems.Take(3);
            remainingGems = remainingGems.Skip(3).ToList();
            bool moveSucceeded = true;
            foreach (var gem in gemsToTransmute)
            {
                var inventoryItem = game.Inventory.FindItemById(gem);
                if (inventoryItem == null)
                {
                    logger.LogWarning("Gem to be transmuted not found in inventory");
                    break;
                }
                var freeSpace = game.Cube.FindFreeSpace(inventoryItem);
                if (freeSpace == null)
                {
                    moveSucceeded = false;
                    break;
                }

                if (InventoryHelpers.PutInventoryItemInCube(game, inventoryItem, freeSpace) != MoveItemResult.Succes)
                {
                    moveSucceeded = false;
                    break;
                }
            }

            if (!moveSucceeded)
            {
                logger.LogError("Transmuting items failed due not all items being moved to cube");
                return;
            }

            if (!InventoryHelpers.TransmuteItemsInCube(game, true))
            {
                logger.LogError("Transmuting items failed");
                return;
            }

            var newCubeItems = game.Cube.Items;
            foreach (var item in newCubeItems)
            {
                if (InventoryHelpers.PutCubeItemInInventory(game, item) != MoveItemResult.Succes)
                {
                    logger.LogError("Couldn't move transmuted items out of cube");
                    continue;
                }
            }
        }

        logger.LogInformation("Moving items back to stash");

        if (!game.OpenStash(stash))
        {
            logger.LogError("{ClientName}: Opening stash failed", game.Me.Name);
            return;
        }

        var inventoryItemsToKeep = game.Inventory.Items.Where(i => i.IsIdentified && Pickit.Pickit.ShouldKeepItem(game, i) && Pickit.Pickit.CanTouchInventoryItem(game, i))
                                                       .ToList();
        foreach (var item in inventoryItemsToKeep)
        {
            InventoryHelpers.MoveItemToStash(game, item);
        }

        logger.LogInformation("Closing stash");

        Thread.Sleep(300);
        game.ClickButton(ClickType.CloseStash);
        Thread.Sleep(100);
        game.ClickButton(ClickType.CloseStash);

        logger.LogInformation("Transmuting items succeeded");
    }
}
