using ConsoleBot.Enums;
using D2NG.Core;
using D2NG.Core.D2GS.Items;
using Serilog;
using System;
using System.Linq;

namespace ConsoleBot.Helpers;

/// <summary>
/// Quest items in a character's possession: holding one anywhere, getting it into the inventory, and
/// assembling the Horadric Staff in the cube. Shared by the rush bot and the leveling party.
/// </summary>
public static class QuestItems
{
    public static bool Holds(Client client, ItemName itemName)
    {
        return client.Game.Inventory.FindItemByName(itemName) != null
            || client.Game.Cube.FindItemByName(itemName) != null
            || client.Game.Me.Equipment.Values.Any(item => item.Name == itemName)
            || client.Game.CursorItem?.Name == itemName;
    }

    public static bool UnequipIntoInventory(Client client, ItemName itemName)
    {
        var slot = client.Game.Me.Equipment.FirstOrDefault(entry => entry.Value.Name == itemName);
        var worn = slot.Value;
        if (worn == null)
        {
            return true;
        }

        var space = client.Game.Inventory.FindFreeSpace(worn);
        if (space == null)
        {
            Log.Error("No inventory space to remove {Item}", itemName);
            return false;
        }

        client.Game.RemoveBodyItem(slot.Key);
        if (!GeneralHelpers.TryWithTimeout(
            _ => client.Game.CursorItem?.Id == worn.Id,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Removing {Item} from {Slot} did not place it on the cursor", itemName, slot.Key);
            return false;
        }

        client.Game.InsertItemIntoContainer(worn, space, ItemContainer.Inventory);
        if (!GeneralHelpers.TryWithTimeout(
            _ => client.Game.CursorItem == null && client.Game.Inventory.FindItemById(worn.Id) != null,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Moving {Item} into the inventory failed; restoring it to {Slot}", itemName, slot.Key);
            client.Game.EquipItem(worn, slot.Key);
            return false;
        }

        return true;
    }

    public static bool TransmuteStaff(Client client)
    {
        var game = client.Game;
        if (!EnsureItemInInventory(client, ItemName.HoradricStaff))
        {
            return false;
        }

        if (game.Inventory.FindItemByName(ItemName.HoradricStaff) != null)
        {
            return true;
        }

        if (!UnequipIntoInventory(client, ItemName.TopoftheHoradricStaff)
            || !UnequipIntoInventory(client, ItemName.ShaftoftheHoradricStaff))
        {
            return false;
        }

        Item shaft = game.Inventory.FindItemByName(ItemName.ShaftoftheHoradricStaff);
        Item top = game.Inventory.FindItemByName(ItemName.TopoftheHoradricStaff);
        if (shaft == null || top == null)
        {
            Log.Error(
                "{Character} cannot transmute a staff without both pieces; shaft={Shaft}, top={Top}",
                game.Me.Name,
                shaft != null,
                top != null);
            return false;
        }

        Item[] pieces = [shaft, top];
        foreach (var piece in pieces)
        {
            var space = game.Cube.FindFreeSpace(piece);
            if (space == null
                || InventoryHelpers.PutInventoryItemInCube(game, piece, space) != MoveItemResult.Succes)
            {
                Log.Error("Moving {Piece} into the Horadric Cube failed", piece.Name);
                return false;
            }
        }

        if (!InventoryHelpers.TransmuteItemsInCube(game, true))
        {
            Log.Error("Transmuting the Horadric Staff failed");
            return false;
        }

        var staff = game.Cube.FindItemByName(ItemName.HoradricStaff);
        if (staff == null
            || InventoryHelpers.PutCubeItemInInventory(game, staff) != MoveItemResult.Succes)
        {
            Log.Error("The transmuted Horadric Staff could not be moved into inventory");
            return false;
        }

        Log.Information("{Character} assembled the Horadric Staff", game.Me.Name);
        return true;
    }

    public static bool EnsureItemInInventory(Client client, ItemName itemName)
    {
        var game = client.Game;
        if (game.Inventory.FindItemByName(itemName) != null)
        {
            return true;
        }

        if (!UnequipIntoInventory(client, itemName))
        {
            return false;
        }

        if (game.Inventory.FindItemByName(itemName) != null)
        {
            return true;
        }

        var cubeItem = game.Cube.FindItemByName(itemName);
        if (cubeItem != null
            && InventoryHelpers.PutCubeItemInInventory(game, cubeItem) != MoveItemResult.Succes)
        {
            Log.Error("Moving {Item} from the Horadric Cube into inventory failed", itemName);
            return false;
        }

        if (game.CursorItem?.Name == itemName)
        {
            game.CleanupCursorItem();
        }

        return game.Inventory.FindItemByName(itemName) != null
            || !Holds(client, itemName);
    }
}
