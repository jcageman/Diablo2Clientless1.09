using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Helpers
{
    public static class InventoryHelpers
    {
        private static readonly TimeSpan MoveItemTimeout = TimeSpan.FromSeconds(2);
        public static void CleanupCursorItem(this Game game)
        {
            if (game.CursorItem != null)
            {
                var item = game.CursorItem;
                var freeSpaceCube = game.Cube.FindFreeSpace(item);
                var freeSpaceInventory = game.Inventory.FindFreeSpace(item);
                if (freeSpaceCube != null)
                {
                    game.InsertItemIntoContainer(item, freeSpaceCube, ItemContainer.Cube);
                    bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Cube.FindItemById(item.Id) != null, MoveItemTimeout);
                    if (!resultMove)
                    {
                        Log.Error($"Moving item {item.Id} - {item.Name} to cube failed");
                    }
                }
                else if (freeSpaceInventory != null)
                {
                    game.InsertItemIntoContainer(game.CursorItem, freeSpaceInventory, ItemContainer.Inventory);
                    bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Inventory.FindItemById(item.Id) != null, MoveItemTimeout);
                    if (!resultMove)
                    {
                        Log.Error($"Moving item {item.Id} - {item.Name} to inventory failed");
                    }
                }
            }
        }

        public static MoveItemResult StashItemsToKeep(Game game, IExternalMessagingClient externalMessagingClient)
        {
            var inventoryItemsToKeep = game.Inventory.Items.Where(i => i.IsIdentified && Pickit.Pickit.ShouldKeepItem(i) && Pickit.Pickit.CanTouchInventoryItem(i)).ToList();
            var cubeItemsToKeep = game.Cube.Items.Where(i => i.IsIdentified && Pickit.Pickit.ShouldKeepItem(i)).ToList();
            var goldOnPerson = game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson, 0);
            if (inventoryItemsToKeep.Count == 0 && cubeItemsToKeep.Count == 0 && goldOnPerson < 200000)
            {
                return MoveItemResult.Succes;
            }

            Log.Information($"Stashing {inventoryItemsToKeep.Count + cubeItemsToKeep.Count } items and {goldOnPerson} gold");

            var stashes = game.GetEntityByCode(EntityCode.Stash);
            if (!stashes.Any())
            {
                Log.Error($"No stash found");
                return MoveItemResult.Failed;
            }

            var stash = stashes.Single();

            bool result = GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                if (game.Me.Location.Distance(stash.Location) >= 5)
                {
                    game.MoveTo(stash);
                }
                else
                {
                    return game.OpenStash(stash);
                }

                return false;
            }, TimeSpan.FromSeconds(4));

            if (!result)
            {
                Log.Error($"Failed to open stash while at location {game.Me.Location} with stash at {stash.Location}");
                return MoveItemResult.Failed;
            }

            if (goldOnPerson > 0)
            {
                game.MoveGoldToStash(goldOnPerson);
            }

            Thread.Sleep(100);
            foreach (Item item in inventoryItemsToKeep)
            {
                Log.Information($"Want to keep {item.GetFullDescription()}");
                if(item.Quality == QualityType.Rare)
                {
                    externalMessagingClient.SendMessage($"Want to keep {item.GetFullDescription()}");
                }

                if (game.Stash.FindFreeSpace(item) == null)
                {
                    game.ClickButton(ClickType.CloseStash);
                    Thread.Sleep(100);
                    game.ClickButton(ClickType.CloseStash);
                    return MoveItemResult.NoSpace;
                }

                if (MoveItemToStash(game, item) != MoveItemResult.Succes)
                {
                    return MoveItemResult.Failed;
                };
            }

            foreach (Item item in cubeItemsToKeep)
            {
                Log.Information($"Want to keep {item.GetFullDescription()}");
                if (item.Quality == QualityType.Rare)
                {
                    externalMessagingClient.SendMessage($"Want to keep {item.GetFullDescription()}");
                }

                if (game.Stash.FindFreeSpace(item) == null)
                {
                    game.ClickButton(ClickType.CloseStash);
                    Thread.Sleep(100);
                    game.ClickButton(ClickType.CloseStash);
                    return MoveItemResult.NoSpace;
                }
                
                if (MoveItemToStash(game, item) != MoveItemResult.Succes)
                {
                    return MoveItemResult.Failed;
                };
            }

            game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            game.ClickButton(ClickType.CloseStash);
            return MoveItemResult.Succes;
        }

        public static bool TransmuteItemsInCube(Game game)
        {
            var cube = game.Inventory.FindItemByName("Horadric Cube");
            if (cube != null)
            {
                if (!game.ActivateBufferItem(cube))
                {
                    return false;
                }
                var oldItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
                game.ClickButton(ClickType.TransmuteItems);

                var transmuteResult = GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    var newItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
                    return !newItems.Intersect(oldItems).Any() && newItems.Count > 0;
                }, MoveItemTimeout);

                if (!transmuteResult)
                {
                    Log.Error($"Transmuting items failed");
                    game.ClickButton(ClickType.CloseHoradricCube);
                    return false;
                }

                game.ClickButton(ClickType.CloseHoradricCube);
                return true;
            }

            return false;
        }

        public static MoveItemResult MoveItemFromStashToInventory(Game game, Item item)
        {
            Point location = game.Inventory.FindFreeSpace(item);
            if (location == null)
            {
                return MoveItemResult.NoSpace;
            }

            game.RemoveItemFromContainer(item);
            bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);

            if (!resultToBuffer)
            {
                Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                return MoveItemResult.Failed;
            }
            game.InsertItemIntoContainer(item, location, ItemContainer.Inventory);

            return GeneralHelpers.TryWithTimeout(
                (retryCount) => game.CursorItem == null && game.Inventory.FindItemById(item.Id) != null,
                MoveItemTimeout) ? MoveItemResult.Succes : MoveItemResult.Failed;

        }

        public static MoveItemResult MoveItemToStash(Game game, Item item)
        {
            Point location = game.Stash.FindFreeSpace(item);
            if (location == null)
            {
                return MoveItemResult.NoSpace;
            }

            game.RemoveItemFromContainer(item);
            bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id,
                MoveItemTimeout);
            if (!resultToBuffer)
            {
                Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                return MoveItemResult.Failed;
            }

            game.InsertItemIntoContainer(item, location, ItemContainer.Stash);

            return GeneralHelpers.TryWithTimeout(
                (retryCount) => game.CursorItem == null && game.Stash.FindItemById(item.Id) != null,
               MoveItemTimeout) ? MoveItemResult.Succes : MoveItemResult.Failed;
        }

        public static MoveItemResult PutCubeItemInInventory(Game game, Item item)
        {
            Point location = game.Inventory.FindFreeSpace(item);
            if (location == null)
            {
                return MoveItemResult.NoSpace;
            }

            var cube = game.Inventory.FindItemByName("Horadric Cube");
            if (cube == null)
            {
                Log.Error($"Cube not found");
                return MoveItemResult.Failed;
            }

            if (!game.ActivateBufferItem(cube))
            {
                Log.Error($"Opening cube for {item.Id} - {item.GetFullDescription()} failed with cursor {game.CursorItem?.Id}");
                return MoveItemResult.Failed;
            }

            game.RemoveItemFromContainer(item);

            bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);
            if (!resultToBuffer)
            {
                Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                game.ClickButton(ClickType.CloseHoradricCube);
                return MoveItemResult.Failed;
            }

            game.InsertItemIntoContainer(item, location, ItemContainer.Inventory);

            bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Inventory.FindItemById(item.Id) != null, MoveItemTimeout);
            if (!resultMove)
            {
                Log.Error($"Moving item {item.Id} - {item.Name} to cube failed");
                game.ClickButton(ClickType.CloseHoradricCube);
                return MoveItemResult.Failed;
            }

            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Succes;
        }

        public static MoveItemResult PutInventoryItemInCube(Game game, Item item, Point point)
        {
            var cube = game.Inventory.FindItemByName("Horadric Cube");
            if (cube == null)
            {
                Log.Error($"Cube not found");
                return MoveItemResult.Failed;
            }

            if (!game.ActivateBufferItem(cube))
            {
                Log.Error($"Opening cube for {item.Id} - {item.GetFullDescription()} failed with cursor {game.CursorItem?.Id}");
                return MoveItemResult.Failed;
            }

            game.RemoveItemFromContainer(item);

            bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);
            if (!resultToBuffer)
            {
                Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                game.ClickButton(ClickType.CloseHoradricCube);
                return MoveItemResult.Failed;
            }

            game.InsertItemIntoContainer(item, point, ItemContainer.Cube);

            bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Cube.FindItemById(item.Id) != null, MoveItemTimeout);
            if (!resultMove)
            {
                Log.Error($"Moving item {item.Id} - {item.Name} to cube failed");
                game.ClickButton(ClickType.CloseHoradricCube);
                return MoveItemResult.Failed;
            }

            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Succes;
        }

        public static void MoveInventoryItemsToCube(Game game)
        {
            foreach (var item in game.Inventory.Items)
            {
                if (Pickit.Pickit.CanTouchInventoryItem(item))
                {
                    var freeSpace = game.Cube.FindFreeSpace(item);
                    if (freeSpace != null)
                    {
                        PutInventoryItemInCube(game, item, freeSpace);
                    }
                }
            }
        }
    }
}
