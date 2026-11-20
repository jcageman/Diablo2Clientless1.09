using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using Serilog;
using System;
using System.Linq;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// The things a member always carries sit in fixed places - the two tomes side by side in the top left corner, the cube
/// under them, as the user lays out an inventory, potions in the two right-hand columns - so the free cells are one block in the
/// middle, where a two by four bow or a quest item still fits. Scattered where they were bought or picked up, they left
/// no room for either.
/// </summary>
public static class LevelingInventory
{
    private static readonly Point CubeSpot = new(0, 2);
    private static readonly Point PortalTomeSpot = new(0, 0);
    private static readonly Point IdentifyTomeSpot = new(1, 0);
    private const int FirstPotionColumn = 8;
    private const int LastPotionColumn = 9;

    public static void Tidy(PartyMember member)
    {
        var game = member.Game;
        if (game.CursorItem != null)
        {
            return;
        }

        var moved = 0;
        moved += MoveTo(game, game.Inventory.FindItemByName(ItemName.HoradricCube), CubeSpot) ? 1 : 0;
        moved += MoveTo(game, game.Inventory.FindItemByName(ItemName.TomeOfTownPortal), PortalTomeSpot) ? 1 : 0;
        moved += MoveTo(game, game.Inventory.FindItemByName(ItemName.TomeofIdentify), IdentifyTomeSpot) ? 1 : 0;

        foreach (var potion in game.Inventory.Items.Where(IsPotion).Where(item => item.Location.X < FirstPotionColumn).ToList())
        {
            if (FreePotionCell(game) is not { } cell || !MoveTo(game, potion, cell))
            {
                break;
            }

            moved++;
        }

        if (moved > 0)
        {
            Log.Information("{Character} tidied {Count} items into place, {Free} cells free", member.Name, moved, game.Inventory.FreeCellCount());
        }
    }

    private static bool IsPotion(Item item)
        => item.Classification is ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion;

    private static Point FreePotionCell(D2NG.Core.Game game)
    {
        for (var x = LastPotionColumn; x >= FirstPotionColumn; x--)
        {
            for (ushort y = 0; y < 4; y++)
            {
                var cell = new Point((ushort)x, y);
                if (game.Inventory.SpaceIsFree(cell, 1, 1))
                {
                    return cell;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Lifts the item and puts it down at <paramref name="spot"/>, back where it was when the spot is taken. True when
    /// it ends up at the spot it was not at before.
    /// </summary>
    private static bool MoveTo(D2NG.Core.Game game, Item item, Point spot)
    {
        if (item == null || (item.Location.X == spot.X && item.Location.Y == spot.Y))
        {
            return false;
        }

        var from = item.Location;
        game.RemoveItemFromContainer(item);
        if (!GeneralHelpers.TryWithTimeout(_ => game.CursorItem?.Id == item.Id, TimeSpan.FromSeconds(2)))
        {
            return false;
        }

        var target = game.Inventory.SpaceIsFree(spot, item.Width, item.Height) ? spot : from;
        game.InsertItemIntoContainer(item, target, ItemContainer.Inventory);
        if (!GeneralHelpers.TryWithTimeout(_ => game.CursorItem == null, TimeSpan.FromSeconds(2)))
        {
            Log.Warning("{Character} could not put {Item} down at {Target}", game.Me.Name, item.Name, target);
            return false;
        }

        return target.X == spot.X && target.Y == spot.Y;
    }
}
