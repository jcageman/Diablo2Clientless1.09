using ConsoleBot.Enums;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ConsoleBot.Helpers
{
    public static class CubeHelpers
    {
        static HashSet<ItemName> flawlessGems = new HashSet<ItemName> { ItemName.FlawlessAmethyst, ItemName.FlawlessDiamond, ItemName.FlawlessEmerald, ItemName.FlawlessRuby, ItemName.FlawlessSapphire, ItemName.FlawlessSkull, ItemName.FlawlessTopaz };

        public static bool AnyGemsToTransmuteInStash(Game game)
        {
            var groupedGems = game.Stash.Items.Where(i => i.Classification == D2NG.Core.D2GS.Items.ClassificationType.Gem && flawlessGems.Contains(i.Name)).GroupBy(i => i.Name);
            if (groupedGems.Any())
            {
                return groupedGems.Max(g => g.Count()) >= 3;
            }

            return false;
        }

        public static void TransmuteGems(Game game)
        {
            TransmuteFlawlessWithName(game, ItemName.FlawlessDiamond);
            TransmuteFlawlessWithName(game, ItemName.FlawlessSkull);
            TransmuteFlawlessWithName(game, ItemName.FlawlessRuby);
            TransmuteFlawlessWithName(game, ItemName.FlawlessEmerald);
            TransmuteFlawlessWithName(game, ItemName.FlawlessAmethyst);
            TransmuteFlawlessWithName(game, ItemName.FlawlessTopaz);
            TransmuteFlawlessWithName(game, ItemName.FlawlessSapphire);
        }

        private static void TransmuteFlawlessWithName(Game game, ItemName flawlessName)
        {
            var flawlessGems = game.Stash.Items.Where(i => i.Name == flawlessName)
                                     .ToList();
            if (flawlessGems.Count < 3)
            {
                return;
            }

            if (game.Cube.Items.Any())
            {
                return;
            }

            var stashes = game.GetEntityByCode(EntityCode.Stash);
            if (!stashes.Any())
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
                Log.Error($"Failed to open stash");
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

            Log.Information($"Moved {flawlessName} to inventory for transmuting");

            var remainingGems = flawlessGemsInInventory;
            while (remainingGems.Count() > 2)
            {
                Log.Information($"Transmuting 3 {flawlessName} to perfect");
                var gemsToTransmute = remainingGems.Take(3);
                remainingGems = remainingGems.Skip(3).ToList();
                bool moveSucceeded = true;
                foreach (var gem in gemsToTransmute)
                {
                    var inventoryItem = game.Inventory.FindItemById(gem);
                    if (inventoryItem == null)
                    {
                        Log.Warning($"Gem to be transmuted not found in inventory");
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
                    Log.Error($"Transmuting items failed due not all items being moved to cube");
                    return;
                }

                if (!InventoryHelpers.TransmuteItemsInCube(game, true))
                {
                    Log.Error($"Transmuting items failed");
                    return;
                }

                var newCubeItems = game.Cube.Items;
                foreach (var item in newCubeItems)
                {
                    if (InventoryHelpers.PutCubeItemInInventory(game, item) != MoveItemResult.Succes)
                    {
                        Log.Error($"Couldn't move transmuted items out of cube");
                        continue;
                    }
                }
            }

            Log.Information($"Moving items back to stash");

            if (!game.OpenStash(stash))
            {
                Log.Error($"Opening stash failed");
                return;
            }

            var inventoryItemsToKeep = game.Inventory.Items.Where(i => i.IsIdentified && Pickit.Pickit.ShouldKeepItem(game, i) && Pickit.Pickit.CanTouchInventoryItem(game, i))
                                                           .ToList();
            foreach (var item in inventoryItemsToKeep)
            {
                InventoryHelpers.MoveItemToStash(game, item);
            }

            Log.Information($"Closing stash");

            Thread.Sleep(300);
            game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            game.ClickButton(ClickType.CloseStash);

            Log.Information($"Transmuting items succeeded");
        }
    }
}
