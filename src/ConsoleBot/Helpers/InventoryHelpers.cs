using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Helpers;

public static class InventoryHelpers
{
    private static readonly TimeSpan MoveItemTimeout = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Whether the cursor is only reported as holding this item. The item's own container is the
    /// reliable signal: an update can arrive carrying the new container without going through the
    /// path that registers it and clears the cursor, which leaves CursorItem pointing at something
    /// that is already put away. Acting on that spends the rest of the game trying to place, cube
    /// and drop an item nobody is holding.
    /// </summary>
    private static bool CursorIsStale(Game game, Item cursor)
    {
        return game.Items.TryGetValue(cursor.Id, out var known)
            && known.Container is ContainerType.Inventory or ContainerType.Cube
                or ContainerType.Stash or ContainerType.Stash2 or ContainerType.Belt
                // A trade window counts as placed too. Without these, a mule pass reads its own
                // offered items as stuck on the cursor and tries to cube and then drop them: seven
                // seconds of failed attempts per item, on a perfect skull and an amulet mid trade.
                // The drops were refused, so nothing was lost - but only because they were refused.
                or ContainerType.ForTrade or ContainerType.TradeOffer;
    }

    /// <summary>
    /// Puts whatever is on the cursor away. <paramref name="force"/> skips the staleness check, for
    /// callers that already have proof the cursor is really blocking them - an interaction refused
    /// while standing on top of its target is exactly that proof, and trusting the container field
    /// there left a character stuck on an open portal until the game was lost.
    /// </summary>
    public static void CleanupCursorItem(this Game game, bool force = false)
    {
        if (!force && game.CursorItem != null && CursorIsStale(game, game.CursorItem))
        {
            Log.Debug($"{game.Me.Name}: cursor reports {game.CursorItem.Name} but it is already in a container, ignoring");
            return;
        }

        if (game.CursorItem != null)
        {
            var item = game.CursorItem;
            var freeSpaceCube = game.Cube.FindFreeSpace(item);
            var freeSpaceInventory = game.Inventory.FindFreeSpace(item);
            if (freeSpaceInventory != null)
            {
                game.InsertItemIntoContainer(game.CursorItem, freeSpaceInventory, ItemContainer.Inventory);
                // The destination decides it, not the cursor flag. CursorItem is only cleared when a
                // confirming packet arrives for that exact id, so a missed one leaves it set for
                // good - and then an item that really did reach the inventory reads as a failed
                // move, the cube attempt that follows works on a phantom, and so does the drop.
                bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) =>
                    game.Inventory.FindItemById(item.Id) != null || CursorIsStale(game, item), MoveItemTimeout);
                if (!resultMove)
                {
                    Log.Warning($"{game.Me.Name}: Moving item {item.Id} - {item.Name} from cursor to inventory failed, dropping it instead");
                }
            }

            // Whatever happened above, the cursor has to end up empty. Reporting the failed move and
            // returning left the item held, and a held item makes the server ignore every
            // interaction that character attempts afterwards - it then cannot cast a town portal,
            // take one, or talk to anyone, and the run is lost with no obvious cause.
            if (game.CursorItem == null || game.Inventory.FindItemById(item.Id) != null
                || CursorIsStale(game, game.CursorItem))
            {
                return;
            }

            item = game.CursorItem;
            freeSpaceCube = game.Cube.FindFreeSpace(item);
            if (freeSpaceCube != null && !D2NG.Pickit.Pickit.IsReservedItem(item))
            {
                // Reserved items stay out of the cube. A tome or a rejuvenation hidden in there is out of
                // reach of the code that looks for it in the inventory, and the character behaves as
                // though it never had one.
                game.InsertItemIntoContainer(item, freeSpaceCube, ItemContainer.Cube);
                bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) =>
                    game.Cube.FindItemById(item.Id) != null || CursorIsStale(game, item), MoveItemTimeout);
                if (!resultMove)
                {
                    Log.Warning($"{game.Me.Name}: Moving item {item.Id} - {item.Name} from cursor to cube failed, dropping it instead");
                }
            }

            if (game.CursorItem == null || game.Cube.FindItemById(item.Id) != null
                || CursorIsStale(game, game.CursorItem))
            {
                return;
            }

            // Dropped rather than carried. An item left on the cursor makes the server ignore every
            // interaction the character attempts afterwards, so holding on to it costs the whole
            // run: the character stands on an open town portal, or in front of a merchant, and is
            // refused over and over with nothing in the log to say why. One belt on the floor is far
            // cheaper than that.
            item = game.CursorItem;

            // Traced, because "still on the cursor" is exactly what a stale CursorItem looks like
            // too. If the item is reported here as sitting in a container or on the ground, then it
            // was put away and only the flag is wrong - and every drop that follows is a no-op on
            // something that is not there.
            var cursor = game.CursorItem;
            game.RequestUpdate(game.Me.Id);
            // "Known" is the field that decides it. An item the game no longer knows about at all has
            // been sold or consumed, and the cursor is merely stale; one the game still knows about
            // is really being held and the drops are being refused. Reporting only "on the ground"
            // could not tell those apart - it reads false for both.
            var isKnown = game.Items.TryGetValue(cursor.Id, out var known);
            Log.Warning("{Character}: cursor still shows {Item} id {Id} - known to the game {Known}, container {Container}, ground {OnGround}, inventory {InInventory}, cube {InCube}, stash {InStash}, cursor id now {CursorNow}",
                game.Me.Name, cursor.Name, cursor.Id,
                isKnown,
                isKnown ? known.Container.ToString() : "n/a",
                isKnown && known.Ground,
                game.Inventory.FindItemById(cursor.Id) != null,
                game.Cube.FindItemById(cursor.Id) != null,
                game.Stash.FindItemById(cursor.Id) != null,
                game.CursorItem?.Id.ToString() ?? "none");

            if (game.CursorItem == null)
            {
                return;
            }

            Log.Warning($"{game.Me.Name}: {item.Name} could not be put away, dropping it rather than letting it block every interaction");

            // Windows first. This runs in the middle of stash and cube work, and the server refuses
            // a drop while one of those is open - every drop attempted here was refused, and the
            // item stayed on the cursor blocking the character exactly as before. Closing something
            // that is not open is ignored, so this is safe either way.
            game.ClickButton(ClickType.CloseHoradricCube);
            game.ClickButton(ClickType.CloseStash);
            game.DropItem(item);
            if (!GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null, MoveItemTimeout))
            {
                Log.Error($"{game.Me.Name}: {item.Name} is still on the cursor after dropping it, the character will refuse interactions until this clears");
            }
        }
    }

    /// <summary>
    /// Walks to the stash and opens it. On failure the stash window is closed again so the next attempt starts clean.
    /// </summary>
    public static bool OpenStash(Game game)
    {
        var stashes = game.GetEntityByCode(EntityCode.Stash);
        if (stashes.Count == 0)
        {
            Log.Error($"{game.Me.Name}: No stash found");
            return false;
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
            Log.Error($"{game.Me.Name}: Failed to open stash while at location {game.Me.Location} with stash at {stash.Location}");
            Thread.Sleep(300);
            CloseStash(game);
            return false;
        }

        return true;
    }

    public static void CloseStash(Game game)
    {
        game.ClickButton(ClickType.CloseStash);
        Thread.Sleep(100);
        game.ClickButton(ClickType.CloseStash);
    }

    /// <summary>
    /// What a vendor may be handed from the inventory: junk the pickit does not want, less the
    /// potions being drunk from and anything reserved.
    /// </summary>
    /// <remarks>
    /// Shared because it drifted. The gambling loop had only the pickit test, so it sold the town
    /// portal tome and the whole healing reserve along with the junk - and the portal character,
    /// the only one with the gold to gamble at all, is the one whose missing tome leaves the party
    /// waiting in town for a portal that cannot come.
    /// </remarks>
    public static List<Item> InventoryItemsToSell(Game game)
    {
        return [.. game.Inventory.Items
            .Where(i => !IsDrinkable(i)
                && !D2NG.Pickit.Pickit.ShouldKeepItem(game, i)
                && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i))];
    }

    public static MoveItemResult StashItemsAndGold(Game game, List<Item> items, int gold)
    {
        if (!OpenStash(game))
        {
            return MoveItemResult.Failed;
        }

        if (gold > 0)
        {
            game.MoveGoldToStash(gold);
        }

        var moveResult = MoveItemResult.Succes;

        Thread.Sleep(100);
        foreach (Item item in items)
        {
            if (game.Stash.FindFreeSpace(item) == null)
            {
                moveResult = MoveItemResult.NoSpace;
                continue;
            }

            var currentMoveResult = MoveItemToStash(game, item);
            if (currentMoveResult != MoveItemResult.Succes)
            {
                moveResult = currentMoveResult;
                break;
            };
        }

        CloseStash(game);
        return moveResult;
    }

    public static MoveItemResult MoveStashItemsToInventory(Game game, List<Item> items)
    {
        if (!OpenStash(game))
        {
            return MoveItemResult.Failed;
        }

        var moveItemResult = MoveItemResult.Succes;

        Thread.Sleep(100);
        foreach (Item item in items)
        {
            var currentMoveResult = MoveItemFromStashToInventory(game, item);
            if (currentMoveResult == MoveItemResult.Failed)
            {
                break;
            }
            else if (currentMoveResult == MoveItemResult.NoSpace)
            {
                moveItemResult = currentMoveResult;
                continue;
            };
        }

        CloseStash(game);
        return moveItemResult;
    }

    public static bool ShouldStashItems(Game game)
    {
        var itemsToKeepInInventory = game.Inventory.Items.Where(i => i.IsIdentified && D2NG.Pickit.Pickit.ShouldKeepItem(game, i) && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i));
        var itemstoKeepInCube = game.Cube.Items.Where(i => i.IsIdentified && D2NG.Pickit.Pickit.ShouldKeepItem(game, i));
        return (itemsToKeepInInventory.Sum(i => i.Width * i.Height) + itemstoKeepInCube.Sum(i => i.Width * i.Height) > 6)
            || game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson, 0) > 1000000;
    }

    /// <summary>
    /// Everything the character is carrying that the pickit says to keep, from the inventory and
    /// the cube both.
    /// </summary>
    public static List<Item> ItemsWorthKeeping(Game game)
    {
        var itemsToKeep = game.Inventory.Items
            .Where(i => i.IsIdentified && D2NG.Pickit.Pickit.ShouldKeepItem(game, i) && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i))
            .ToList();
        itemsToKeep.AddRange(game.Cube.Items.Where(i => i.IsIdentified && D2NG.Pickit.Pickit.ShouldKeepItem(game, i)));
        return itemsToKeep;
    }

    public static MoveItemResult StashItemsToKeep(Game game, IExternalMessagingClient externalMessagingClient)
    {
        if (!ShouldStashItems(game))
        {
            return MoveItemResult.Succes;
        }

        var itemsToKeep = ItemsWorthKeeping(game);
        var goldOnPerson = game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson, 0);
        foreach (var item in itemsToKeep)
        {
            Log.Information($"{game.Me.Name}: Want to keep {item.GetFullDescription()}");
            if (D2NG.Pickit.Pickit.SendItemToKeepToExternalClient(item))
            {
                externalMessagingClient.SendMessage($"{game.Me.Name}: Want to keep {item.GetFullDescription()}");
            }
        }

        return StashItemsAndGold(game, itemsToKeep, goldOnPerson);
    }

    public static bool TransmuteItemsInCube(Game game, bool newItemsSpawn)
    {
        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube != null)
        {
            if (!game.ActivateCube(cube))
            {
                return false;
            }
            var oldItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
            game.ClickButton(ClickType.TransmuteItems);

            var transmuteResult = GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                var newItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
                return !newItems.Intersect(oldItems).Any() && (!newItemsSpawn || newItems.Count > 0);
            }, MoveItemTimeout);

            if (!transmuteResult)
            {
                Log.Error($"{game.Me.Name}: Transmuting items failed");
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
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
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
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            return MoveItemResult.Failed;
        }

        game.InsertItemIntoContainer(item, location, ItemContainer.Stash);

        if (!GeneralHelpers.TryWithTimeout(
            (retryCount) => game.CursorItem == null && game.Stash.FindItemById(item.Id) != null,
           MoveItemTimeout))
        {
            Log.Error($"{game.Me.Name}: Inserting item {item.Id} - {item.Name} into stash failed");
            return MoveItemResult.Failed;
        }

        return MoveItemResult.Succes;
    }

    public static MoveItemResult DropItemFromInventory(Game game, Item item)
    {
        game.RemoveItemFromContainer(item);

        bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);
        if (!resultToBuffer)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            return MoveItemResult.Failed;
        }

        game.DropItem(item);

        bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null, MoveItemTimeout);
        if (!resultMove)
        {
            Log.Error($"{game.Me.Name}: Dropping item {item.Id} - {item.Name} failed");
            return MoveItemResult.Failed;
        }

        return MoveItemResult.Succes;
    }

    public static MoveItemResult DropItemFromCube(Game game, Item item)
    {
        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube == null)
        {
            Log.Error($"{game.Me.Name}: Cube not found");
            return MoveItemResult.Failed;
        }
        if (!game.ActivateCube(cube))
        {
            Log.Error($"{game.Me.Name}: Opening cube for {item.Id} - {item.GetFullDescription()} failed with cursor {game.CursorItem?.Id}");
            return MoveItemResult.Failed;
        }
        game.RemoveItemFromContainer(item);
        bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);
        if (!resultToBuffer)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Failed;
        }
        game.DropItem(item);
        bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null, MoveItemTimeout);
        if (!resultMove)
        {
            Log.Error($"{game.Me.Name}: Dropping item {item.Id} - {item.Name} failed");
            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Failed;
        }
        game.ClickButton(ClickType.CloseHoradricCube);
        return MoveItemResult.Succes;
    }

    public static MoveItemResult PutCubeItemInInventory(Game game, Item item)
    {
        Point location = game.Inventory.FindFreeSpace(item);
        if (location == null)
        {
            return MoveItemResult.NoSpace;
        }

        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube == null)
        {
            Log.Error($"{game.Me.Name}: Cube not found");
            return MoveItemResult.Failed;
        }

        if (!game.ActivateCube(cube))
        {
            Log.Error($"{game.Me.Name}: Opening cube for {item.Id} - {item.GetFullDescription()} failed with cursor {game.CursorItem?.Id}");
            return MoveItemResult.Failed;
        }

        game.RemoveItemFromContainer(item);

        bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);
        if (!resultToBuffer)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Failed;
        }

        game.InsertItemIntoContainer(item, location, ItemContainer.Inventory);

        bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Inventory.FindItemById(item.Id) != null, MoveItemTimeout);
        if (!resultMove)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to cube failed");
            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Failed;
        }

        game.ClickButton(ClickType.CloseHoradricCube);
        return MoveItemResult.Succes;
    }

    public static MoveItemResult PutInventoryItemInCube(Game game, Item item, Point point)
    {
        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube == null)
        {
            Log.Error($"{game.Me.Name}: Cube not found");
            return MoveItemResult.Failed;
        }

        if (!game.ActivateCube(cube))
        {
            Log.Error($"{game.Me.Name}: Opening cube for {item.Id} - {item.GetFullDescription()} failed with cursor {game.CursorItem?.Id}");
            return MoveItemResult.Failed;
        }

        game.RemoveItemFromContainer(item);

        bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);
        if (!resultToBuffer)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Failed;
        }

        game.InsertItemIntoContainer(item, point, ItemContainer.Cube);

        bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Cube.FindItemById(item.Id) != null, MoveItemTimeout);
        if (!resultMove)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to cube failed");
            game.ClickButton(ClickType.CloseHoradricCube);
            return MoveItemResult.Failed;
        }

        game.ClickButton(ClickType.CloseHoradricCube);
        return MoveItemResult.Succes;
    }

    /// <summary>
    /// Whether the character has to be able to reach this item where it stands. Potions are drunk
    /// out of the inventory and the belt, so a potion in the cube is a potion that cannot be drunk.
    /// </summary>
    public static bool IsDrinkable(Item item)
    {
        return item.Classification == ClassificationType.HealthPotion
            || item.Classification == ClassificationType.ManaPotion
            || item.Classification == ClassificationType.RejuvenationPotion;
    }

    public static void MoveInventoryItemsToCube(Game game)
    {
        foreach (var item in game.Inventory.Items)
        {
            if (IsDrinkable(item))
            {
                // Cubing the reserve is how characters reached the cow level with a full belt and
                // nothing to fall back on: it went in the cube on the first item they picked up.
                continue;
            }

            if (D2NG.Pickit.Pickit.CanTouchInventoryItem(game, item))
            {
                var freeSpace = game.Cube.FindFreeSpace(item);
                if (freeSpace != null)
                {
                    PutInventoryItemInCube(game, item, freeSpace);
                }
            }
        }
    }

    public static void IdentifyItems(Game game)
    {
        var tomeOfIdentify = game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.TomeofIdentify);
        if (tomeOfIdentify == null)
        {
            return;
        }

        IdentifyWithTome(game, tomeOfIdentify, game.Inventory.Items);

        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube == null)
        {
            Log.Error($"{game.Me.Name}: Cube not found");
            return;
        }

        if (!game.ActivateCube(cube))
        {
            Log.Error($"{game.Me.Name}: Opening cube failed with cursor {game.CursorItem?.Id}");
            return;
        }

        IdentifyWithTome(game, tomeOfIdentify, game.Cube.Items);
        game.ClickButton(ClickType.CloseHoradricCube);
    }

    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Asks for everything to be identified without waiting for any of it. Called before leaving the
    /// level, the answers arrive while the character is taking its portal and walking through town,
    /// so the town step finds nothing left to identify and skips both the identify and the walk to
    /// Deckard Cain.
    /// </summary>
    public static void SendIdentifyRequests(Game game)
    {
        var tomeOfIdentify = game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.TomeofIdentify);
        if (tomeOfIdentify == null)
        {
            return;
        }

        Send(game.Inventory.Items);

        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube == null || !game.ActivateCube(cube))
        {
            return;
        }

        Send(game.Cube.Items);
        game.ClickButton(ClickType.CloseHoradricCube);

        void Send(List<Item> items)
        {
            var pending = items
                .Where(i => !i.IsIdentified && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            game.ActivateTomeOfIdentify(tomeOfIdentify);
            foreach (var item in pending)
            {
                game.SendIdentifyItem(tomeOfIdentify, item);
            }

            Log.Information($"{game.Me.Name}: asked for {pending.Count} items to be identified while leaving");
        }
    }

    private static void IdentifyWithTome(Game game, Item tomeOfIdentify, List<Item> items)
    {
        var pending = items
            .Where(i => !i.IsIdentified && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        game.ActivateTomeOfIdentify(tomeOfIdentify);
        foreach (var item in pending)
        {
            Log.Information($"{game.Me.Name}: Identifying {item.Quality} item {item.Id} - {item.Name}");
            game.SendIdentifyItem(tomeOfIdentify, item);
        }

        // By id against the live dictionary, not against the captured items: an update replaces the
        // Item rather than mutating it, so the references gathered above never flip and the wait ran
        // its full length every time while the items were in fact identified.
        var pendingIds = pending.Select(i => i.Id).ToList();
        bool StillPending(uint id)
        {
            var live = game.Inventory.FindItemById(id) ?? game.Cube.FindItemById(id);
            return live != null && !live.IsIdentified;
        }

        GeneralHelpers.TryWithTimeout((retryCount) => !pendingIds.Exists(StillPending), IdentifyTimeout);

        var unfinished = pendingIds.Count(StillPending);
        if (unfinished > 0)
        {
            Log.Warning($"{game.Me.Name}: {unfinished} of {pending.Count} items still unidentified after {IdentifyTimeout.TotalSeconds}s");
        }
    }

    public static bool MoveCubeItemsToInventory(Game game)
    {
        foreach (var item in game.Cube.Items)
        {
            if (PutCubeItemInInventory(game, item) != MoveItemResult.Succes)
            {
                return false;
            }
        }

        return true;
    }

    public static MoveItemResult MoveBeltItemToInventory(Game game, Item item)
    {
        Point location = game.Inventory.FindFreeSpace(item);
        if (location == null)
        {
            return MoveItemResult.NoSpace;
        }

        game.RemoveItemFromBelt(item);
        bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.Belt.FindItemById(item.Id) == null,
            MoveItemTimeout);
        if (!resultToBuffer)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            return MoveItemResult.Failed;
        }

        game.InsertItemIntoContainer(item, location, ItemContainer.Inventory);

        if (!GeneralHelpers.TryWithTimeout(
            (retryCount) => game.Inventory.FindItemById(item.Id) != null,
           MoveItemTimeout))
        {
            Log.Error($"{game.Me.Name}: Inserting item {item.Id} - {item.Name} into Inventory failed");
            return MoveItemResult.Failed;
        }

        return MoveItemResult.Succes;
    }

    public static void CleanupPotionsInBelt(Game game, AccountConfig accountConfig)
    {
        var manaPotionsInWrongSlot = game.Belt.GetManaPotionsInSlots(accountConfig.HealthSlots);
        foreach (var manaPotion in manaPotionsInWrongSlot)
        {
            game.UseBeltItem(manaPotion);
        }

        var healthPotionsInWrongSlot = game.Belt.GetHealthPotionsInSlots(accountConfig.ManaSlots);
        foreach (var healthPotion in healthPotionsInWrongSlot)
        {
            game.UseBeltItem(healthPotion);
        }

        var revPotions = game.Belt.GetRejuvenationPotions();
        foreach (var revPotion in revPotions)
        {
            MoveBeltItemToInventory(game, revPotion);
        }

        var missingHealthPotionsInBelt = game.Belt.Height * accountConfig.HealthSlots.Count - game.Belt.GetHealthPotionsInSlots(accountConfig.HealthSlots).Count;
        if (missingHealthPotionsInBelt > 0)
        {
            var healthPotionsToAdd = game.Inventory.Items
                .Where(i => i.Classification == ClassificationType.HealthPotion)
                .Take((int)missingHealthPotionsInBelt);
            foreach (var healthPotion in healthPotionsToAdd)
            {
                game.PutItemInBelt(healthPotion);
            }
        }

        var missingManaPotionsInBelt = game.Belt.Height * accountConfig.ManaSlots.Count - game.Belt.GetManaPotionsInSlots(accountConfig.ManaSlots).Count;
        if (missingManaPotionsInBelt > 0)
        {
            var manaPotionsToAdd = game.Inventory.Items
                .Where(i => i.Classification == ClassificationType.ManaPotion)
                .Take((int)missingManaPotionsInBelt);
            foreach (var manaPotion in manaPotionsToAdd)
            {
                game.PutItemInBelt(manaPotion);
            }
        }
    }

    public static int GetTotalHealthPotions(Game game)
    {
        return game.Belt.NumOfHealthPotions()
            + game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion);
    }

    public static int GetTotalManaPotions(Game game)
    {
        return game.Belt.NumOfManaPotions()
            + game.Inventory.Items.Count(i => i.Classification == ClassificationType.ManaPotion);
    }
}
