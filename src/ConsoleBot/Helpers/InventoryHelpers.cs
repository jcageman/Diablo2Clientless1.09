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
                    Log.Warning($"{game.Me.Name}: Moving item {item.Id} - {item.Name} from cursor to inventory failed, trying the cube");
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
                    Log.Warning($"{game.Me.Name}: Moving item {item.Id} - {item.Name} from cursor to cube failed, trying the stash");
                }
            }

            if (game.CursorItem == null || game.Cube.FindItemById(item.Id) != null
                || CursorIsStale(game, game.CursorItem))
            {
                return;
            }

            // Never dropped (user decision 2026-10-10: dropping risks losing the item). The stash is the
            // last place to try: while an item is held the server opens it without the confirmation
            // OpenStash waits for, but an insert into a free stash cell is still accepted - that is how
            // a mule left holding a gem after a failed swap was put right. An item that fits nowhere
            // stays on the cursor; the character then cannot interact until it is put away, which
            // costs the run, never the item.
            item = game.CursorItem;
            var freeSpaceStash = game.Stash.FindFreeSpace(item);
            if (freeSpaceStash != null)
            {
                var stash = game.GetEntityByCode(EntityCode.Stash).FirstOrDefault();
                if (stash != null)
                {
                    game.OpenStash(stash);
                }

                game.InsertItemIntoContainer(item, freeSpaceStash, ItemContainer.Stash);
                GeneralHelpers.TryWithTimeout((retryCount) =>
                    game.Stash.FindItemById(item.Id) != null || CursorIsStale(game, item), MoveItemTimeout);
                game.ClickButton(ClickType.CloseStash);
            }

            if (game.CursorItem == null || game.Stash.FindItemById(item.Id) != null
                || CursorIsStale(game, game.CursorItem))
            {
                return;
            }

            // Traced, because "still on the cursor" is exactly what a stale CursorItem looks like
            // too. If the item is reported here as sitting in a container or on the ground, then it
            // was put away and only the flag is wrong.
            var cursor = game.CursorItem;
            game.RequestUpdate(game.Me.Id);
            // "Known" is the field that decides it. An item the game no longer knows about at all has
            // been sold or consumed, and the cursor is merely stale; one the game still knows about
            // is really being held.
            var isKnown = game.Items.TryGetValue(cursor.Id, out var known);
            Log.Error("{Character}: {Item} id {Id} fits in no inventory, cube or stash cell and stays on the cursor (never dropped) - known to the game {Known}, container {Container}, ground {OnGround}, inventory {InInventory}, cube {InCube}, stash {InStash}, cursor id now {CursorNow}",
                game.Me.Name, cursor.Name, cursor.Id,
                isKnown,
                isKnown ? known.Container.ToString() : "n/a",
                isKnown && known.Ground,
                game.Inventory.FindItemById(cursor.Id) != null,
                game.Cube.FindItemById(cursor.Id) != null,
                game.Stash.FindItemById(cursor.Id) != null,
                game.CursorItem?.Id.ToString() ?? "none");
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

    /// <summary>The cube items a vendor visit sells: what the pickit does not keep and nothing reserved.</summary>
    public static List<Item> CubeItemsToSell(Game game)
        => [.. game.Cube.Items.Where(i => !D2NG.Pickit.Pickit.ShouldKeepItem(game, i) && !D2NG.Pickit.Pickit.IsReservedItem(i))];

    /// <summary>
    /// How many items a vendor visit would sell, counted as <see cref="InventoryItemsToSell"/> and <see cref="CubeItemsToSell"/>
    /// pick them. Counting every untouched inventory item instead took the spare healing potions for junk: thirteen
    /// spares made a barbarian with nothing to sell walk to the vendor every game to sell nothing.
    /// </summary>
    public static int ItemsToSellCount(Game game) => InventoryItemsToSell(game).Count + CubeItemsToSell(game).Count;

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

    /// <summary>
    /// Transmutes with the cube already open and leaves it open, so a run of transmutes pays for
    /// opening the cube once. True when the cube holds something new afterwards.
    /// </summary>
    public static bool TransmuteInOpenCube(Game game)
    {
        var oldItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
        game.ClickButton(ClickType.TransmuteItems);
        return GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            var newItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
            return newItems.Count > 0 && !newItems.Intersect(oldItems).Any();
        }, MoveItemTimeout);
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

        var moved = MoveIntoOpenCube(game, item, point);
        game.ClickButton(ClickType.CloseHoradricCube);
        return moved ? MoveItemResult.Succes : MoveItemResult.Failed;
    }

    /// <summary>
    /// Puts an inventory item in the closed cube by dropping it on it (0x2A); the server picks the cell.
    /// Opening the cube for every item cost a transmute of three ingredients about two seconds more.
    /// </summary>
    public static MoveItemResult DropInventoryItemOnCube(Game game, Item item)
    {
        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube == null)
        {
            Log.Error($"{game.Me.Name}: Cube not found");
            return MoveItemResult.Failed;
        }

        return DropOnCube(game, item, cube) ? MoveItemResult.Succes : MoveItemResult.Failed;
    }

    private static bool DropOnCube(Game game, Item item, Item cube)
    {
        var from = item.Location;
        game.RemoveItemFromContainer(item);
        if (!GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout))
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            return false;
        }

        game.PutCursorItemInCube(item, cube);
        if (GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Cube.FindItemById(item.Id) != null, MoveItemTimeout))
        {
            return true;
        }

        Log.Error($"{game.Me.Name}: Dropping item {item.Id} - {item.Name} on the cube failed, putting it back at {from}");
        if (game.CursorItem?.Id == item.Id)
        {
            game.InsertItemIntoContainer(item, from, ItemContainer.Inventory);
        }

        return false;
    }

    public static bool MoveIntoOpenCube(Game game, Item item, Point point)
    {
        game.RemoveItemFromContainer(item);

        bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem?.Id == item.Id, MoveItemTimeout);
        if (!resultToBuffer)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to buffer failed");
            return false;
        }

        game.InsertItemIntoContainer(item, point, ItemContainer.Cube);

        bool resultMove = GeneralHelpers.TryWithTimeout((retryCount) => game.CursorItem == null && game.Cube.FindItemById(item.Id) != null, MoveItemTimeout);
        if (!resultMove)
        {
            Log.Error($"{game.Me.Name}: Moving item {item.Id} - {item.Name} to cube failed");
            return false;
        }

        return true;
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
        // Cubing the reserve is how characters reached the cow level with a full belt and nothing to
        // fall back on: it went in the cube on the first item they picked up.
        var items = game.Inventory.Items
            .Where(item => !IsDrinkable(item) && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, item) && game.Cube.FindFreeSpace(item) != null)
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
        if (cube == null)
        {
            Log.Error($"{game.Me.Name}: Cube not found");
            return;
        }

        // Dropped on the cube (0x2A) rather than put in an opened one: about 0.24s an item instead of
        // 0.5s, verified on the realm over 391 drops.
        foreach (var item in items)
        {
            if (game.Cube.FindFreeSpace(item) == null)
            {
                continue;
            }

            if (!DropOnCube(game, item, cube))
            {
                break;
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

    /// <summary>The inventory only, with the tome: true when nothing magic is left unidentified after it.</summary>
    public static bool IdentifyInventoryWithTome(Game game)
    {
        var unidentified = game.Inventory.Items.Count(item => !item.IsIdentified);
        if (game.Inventory.Items.FirstOrDefault(item => item.Name == ItemName.TomeofIdentify) is not { } tome || tome.Amount < unidentified)
        {
            return false;
        }

        IdentifyWithTome(game, tome, game.Inventory.Items);
        // Items in the rows kept for charms are left alone by the tome: Cain still does those.
        return !game.Inventory.Items.Any(item => !item.IsIdentified && item.Quality >= QualityType.Magical);
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

            foreach (var item in pending)
            {
                game.ActivateTomeOfIdentify(tomeOfIdentify);
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

        // By id against the live dictionary, not against the captured items: an update replaces the
        // Item rather than mutating it, so the references gathered above never flip and the wait ran
        // its full length every time while the items were in fact identified.
        var pendingIds = pending.Select(i => i.Id).ToList();
        bool StillPending(uint id)
        {
            var live = game.Inventory.FindItemById(id) ?? game.Cube.FindItemById(id);
            return live != null && !live.IsIdentified;
        }

        // Each activation of the tome is good for one identify: the server deactivates it after every
        // item (UseStackableItem 0xFF) and ignores an identify without a fresh one. Sent in a burst it
        // identified four of eight, and the rest brought the character back to identify next visit.
        var started = DateTime.Now;
        foreach (var item in pending)
        {
            if (DateTime.Now - started > IdentifyTimeout * 3)
            {
                break;
            }

            Log.Information($"{game.Me.Name}: Identifying {item.Quality} item {item.Id} - {item.Name}");
            game.ActivateTomeOfIdentify(tomeOfIdentify);
            game.SendIdentifyItem(tomeOfIdentify, item);
            GeneralHelpers.TryWithTimeout((retryCount) => !StillPending(item.Id), TimeSpan.FromMilliseconds(500));
        }

        var unfinished = pendingIds.Count(StillPending);
        if (unfinished > 0)
        {
            Log.Warning($"{game.Me.Name}: {unfinished} of {pending.Count} items still unidentified after {(DateTime.Now - started).TotalSeconds:F1}s");
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

    public static MoveItemResult MoveBeltItemToInventory(Game game, Item item, Point location = null)
    {
        location ??= game.Inventory.FindFreeSpace(item);
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

        // The item is on the cursor now, where it blocks portals, trades and purchases: a refused insert is tried once more
        // at a fresh cell, and failing that the cursor cleanup puts it wherever it can.
        for (var attempt = 0; attempt < 2 && location != null; attempt++)
        {
            game.InsertItemIntoContainer(item, location, ItemContainer.Inventory);
            if (GeneralHelpers.TryWithTimeout((retryCount) => game.Inventory.FindItemById(item.Id) != null, MoveItemTimeout))
            {
                return MoveItemResult.Succes;
            }

            location = game.Inventory.FindFreeSpace(item);
        }

        Log.Error($"{game.Me.Name}: Inserting item {item.Id} - {item.Name} into Inventory failed, clearing the cursor");
        game.CleanupCursorItem();
        return MoveItemResult.Failed;
    }

    /// <summary>
    /// Takes potions out of belt columns that are not theirs, then refills the belt from the inventory. Nothing is drunk: a
    /// potion with no inventory cell to go to stays for the vendor, who makes room by selling first.
    /// </summary>
    public static void CleanupPotionsInBelt(Game game, AccountConfig accountConfig)
    {
        var (moved, left) = MoveMisplacedPotionsToInventory(game, accountConfig);
        LogMisplacedPotions(game, moved, left, []);
        FillBeltFromInventory(game, accountConfig);
    }

    /// <summary>
    /// Healing potions outside the health columns: the inventory's, and those in the mana columns, which the next town visit
    /// moves to the inventory.
    /// </summary>
    public static int SpareHealthPotions(Game game, AccountConfig account)
        => GetTotalHealthPotions(game) - game.Belt.GetHealthPotionsInSlots([.. account.BeltColumns(PotionKind.Healing)]).Count;

    public static PotionKind KindOf(Item item) => item.Classification switch
    {
        ClassificationType.HealthPotion => PotionKind.Healing,
        ClassificationType.ManaPotion => PotionKind.Mana,
        ClassificationType.RejuvenationPotion => PotionKind.Rejuvenation,
        _ => PotionKind.Other,
    };

    public static List<BeltPotion> BeltPotions(Game game)
        => game.Belt.Items.Select(i => new BeltPotion(i.Location.X, i.Location.Y, KindOf(i))).ToList();

    /// <summary>Potions of this kind its own belt columns can still take; see <see cref="BeltPlacement.Room"/>.</summary>
    public static int BeltRoom(Game game, AccountConfig account, PotionKind kind)
        => BeltPlacement.Room(BeltPotions(game), (int)game.Belt.Height, account.BeltColumns(kind), account.BeltTarget(kind, game.Belt.Height), kind);

    public static BeltFillStep NextBeltFill(Game game, AccountConfig account, PotionKind kind)
        => BeltPlacement.Next(BeltPotions(game), (int)game.Belt.Height, account.BeltColumns(kind), account.BeltTarget(kind, game.Belt.Height), kind);

    public static List<Item> MisplacedBeltPotions(Game game, AccountConfig account)
    {
        var health = account.BeltColumns(PotionKind.Healing);
        var mana = account.BeltColumns(PotionKind.Mana);
        return game.Belt.Items
            .Where(i => BeltPlacement.IsMisplaced(new BeltPotion(i.Location.X, i.Location.Y, KindOf(i)), health, mana))
            .OrderByDescending(i => i.Location.Y)
            .ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Character, uint Id), bool> MisplacedGivenUp = new();

    /// <summary>
    /// Misplaced healing and mana potions a vendor visit can still do something about. One that was neither moved nor drunk
    /// at an earlier visit is left out, so it does not send the character to the vendor on every trip.
    /// </summary>
    public static bool HasMisplacedPotionsForVendor(Game game, AccountConfig account)
        => MisplacedBeltPotions(game, account).Any(i => KindOf(i) is PotionKind.Healing or PotionKind.Mana && !MisplacedGivenUp.ContainsKey((game.Me.Name, i.Id)));

    /// <summary>
    /// An inventory cell for a potion taken out of the belt. Without a tome of town portal it keeps a tome's space free, so the
    /// tome can still be bought.
    /// </summary>
    public static Point FreeCellForPotion(Game game)
    {
        var hasTome = game.Inventory.Items.Any(i => i.Name == ItemName.TomeOfTownPortal);
        var cell = InventoryReserve.CellKeeping(game.Inventory.IsOccupied, (int)game.Inventory.Width, (int)game.Inventory.Height,
            hasTome ? 0 : InventoryReserve.TomeWidth, hasTome ? 0 : InventoryReserve.TomeHeight);
        return cell is var (x, y) ? new Point((ushort)x, (ushort)y) : null;
    }

    /// <summary>Moves every potion in a belt column not its own to the inventory while there is a cell for it that keeps the tome's room.</summary>
    public static (List<Item> Moved, List<Item> Left) MoveMisplacedPotionsToInventory(Game game, AccountConfig account)
    {
        var moved = new List<Item>();
        var left = new List<Item>();
        foreach (var potion in MisplacedBeltPotions(game, account))
        {
            var cell = FreeCellForPotion(game);
            if (cell != null && MoveBeltItemToInventory(game, potion, cell) == MoveItemResult.Succes)
            {
                moved.Add(potion);
            }
            else if (game.Belt.FindItemById(potion.Id) != null)
            {
                left.Add(potion);
            }
        }

        return (moved, left);
    }

    /// <summary>
    /// Drinks the healing and mana potions among these that are still in the belt: the last resort for a potion in the wrong
    /// column when the inventory has no cell for it. Rejuvenations and anything else are left where they are. One that will
    /// not go down is given up on, so it no longer calls for a vendor visit.
    /// </summary>
    public static List<Item> DrinkMisplacedPotions(Game game, IEnumerable<Item> potions)
    {
        var drunk = new List<Item>();
        foreach (var potion in potions.Where(p => KindOf(p) is PotionKind.Healing or PotionKind.Mana))
        {
            if (game.Belt.FindItemById(potion.Id) == null)
            {
                continue;
            }

            game.UseBeltItem(potion);
            if (GeneralHelpers.TryWithTimeout((_) => game.Belt.FindItemById(potion.Id) == null, MoveItemTimeout))
            {
                drunk.Add(potion);
            }
            else
            {
                MisplacedGivenUp[(game.Me.Name, potion.Id)] = true;
            }
        }

        return drunk;
    }

    public static void LogMisplacedPotions(Game game, List<Item> moved, List<Item> left, List<Item> drunk)
    {
        if (moved.Count == 0 && left.Count == 0 && drunk.Count == 0)
        {
            return;
        }

        static string Counts(IEnumerable<Item> potions) => string.Join(", ", potions.GroupBy(KindOf).Select(g => $"{g.Count()} {g.Key}"));
        Log.Information("{Character} found potions in belt columns not theirs: moved to the inventory [{Moved}], drunk for want of a cell [{Drunk}], still in the belt [{Left}]",
            game.Me.Name, Counts(moved), Counts(drunk), Counts(left.Except(drunk)));
    }

    /// <summary>Moves inventory potions into their own belt columns, in <see cref="BeltPlacement.FillOrder"/>.</summary>
    public static void FillBeltFromInventory(Game game, AccountConfig account) => RefillBelt(game, account, 0, 0);

    /// <summary>
    /// Fills the belt in <see cref="BeltPlacement.FillOrder"/>, each kind from the inventory first and then by buying while the
    /// budget lasts, one step at a time and only while the next potion lands in one of the kind's own columns. The buy gets the
    /// kind and the budget left and returns how many potions it gained; without one nothing is bought.
    /// </summary>
    public static (long Healing, long Mana) RefillBelt(Game game, AccountConfig account, long healingBudget, long manaBudget, Func<PotionKind, long, long> buy = null)
    {
        long boughtHealing = 0;
        long boughtMana = 0;
        foreach (var kind in BeltPlacement.FillOrder)
        {
            while (true)
            {
                var budget = buy == null ? 0 : kind == PotionKind.Healing ? healingBudget - boughtHealing : manaBudget - boughtMana;
                var action = BeltPlacement.NextRefill(BeltPotions(game), (int)game.Belt.Height, account.BeltColumns(kind),
                    account.BeltTarget(kind, game.Belt.Height), kind, game.Inventory.Items.Count(i => KindOf(i) == kind), budget);
                if (action == BeltRefillAction.PutFromInventory)
                {
                    var potion = game.Inventory.Items.First(i => KindOf(i) == kind);
                    if (!game.PutItemInBelt(potion)
                        || !GeneralHelpers.TryWithTimeout((_) => game.Belt.FindItemById(potion.Id) != null, MoveItemTimeout))
                    {
                        break;
                    }
                }
                else if (action == BeltRefillAction.Buy)
                {
                    var gained = buy(kind, budget);
                    if (gained <= 0)
                    {
                        break;
                    }

                    if (kind == PotionKind.Healing)
                    {
                        boughtHealing += gained;
                    }
                    else
                    {
                        boughtMana += gained;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        return (boughtHealing, boughtMana);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, PotionKind), bool> BlockedFillLogged = new();

    /// <summary>Logs, once per character and kind, that the belt cannot be filled with this kind without it spilling.</summary>
    public static void LogBlockedBeltFill(Game game, AccountConfig account, PotionKind kind)
    {
        if (NextBeltFill(game, account, kind) != BeltFillStep.Blocked || !BlockedFillLogged.TryAdd((game.Me.Name, kind), true))
        {
            return;
        }

        var column = BeltPlacement.Column(BeltPotions(game), (int)game.Belt.Height, kind);
        Log.Warning("{Character} cannot fill its {Kind} columns {Columns}: the server would put the next one in column {Column}, so the belt is left short",
            game.Me.Name, kind, string.Join(",", account.BeltColumns(kind)), column?.ToString() ?? "none");
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
