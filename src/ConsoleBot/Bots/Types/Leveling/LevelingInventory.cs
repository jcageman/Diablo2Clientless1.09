using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using Serilog;
using System;
using System.Collections.Generic;
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

    public sealed record Placed(uint Id, int X, int Y, int Width, int Height);

    /// <summary>
    /// The spot for a <paramref name="width"/> by <paramref name="height"/> item that the fewest moves clear, and where each item
    /// in the way goes instead; null when no spot can be cleared inside the bag. This realm's bag is 10 by 8: the amazon had
    /// some forty free cells, scattered between potions and gear, and no two by four block for a rare Pike or Great Axe.
    /// </summary>
    public static (Point At, List<(uint Id, Point To)> Moves)? HoleFor(int width, int height, int bagWidth, int bagHeight, IReadOnlyList<Placed> items)
    {
        (Point At, List<(uint Id, Point To)> Moves)? best = null;
        var bestCells = int.MaxValue;
        for (var y = 0; y + height <= bagHeight; y++)
        {
            for (var x = 0; x + width <= bagWidth; x++)
            {
                var inWay = items.Where(item => item.X < x + width && x < item.X + item.Width && item.Y < y + height && y < item.Y + item.Height).ToList();
                var cells = inWay.Sum(item => item.Width * item.Height);
                if (best is { } found && (inWay.Count > found.Moves.Count || (inWay.Count == found.Moves.Count && cells >= bestCells)))
                {
                    continue;
                }

                var grid = new bool[bagHeight, bagWidth];
                foreach (var item in items.Except(inWay))
                {
                    Mark(grid, item.X, item.Y, item.Width, item.Height);
                }

                Mark(grid, x, y, width, height);
                var moves = new List<(uint Id, Point To)>();
                foreach (var item in inWay.OrderByDescending(item => item.Width * item.Height))
                {
                    if (FreeSpot(grid, item.Width, item.Height, bagWidth, bagHeight) is not { } to)
                    {
                        moves = null;
                        break;
                    }

                    Mark(grid, to.X, to.Y, item.Width, item.Height);
                    moves.Add((item.Id, to));
                }

                if (moves != null)
                {
                    best = (new Point((ushort)x, (ushort)y), moves);
                    bestCells = cells;
                }
            }
        }

        return best;
    }

    private static void Mark(bool[,] grid, int x, int y, int width, int height)
    {
        for (var dy = 0; dy < height; dy++)
        {
            for (var dx = 0; dx < width; dx++)
            {
                grid[y + dy, x + dx] = true;
            }
        }
    }

    private static Point FreeSpot(bool[,] grid, int width, int height, int bagWidth, int bagHeight)
    {
        for (var y = 0; y + height <= bagHeight; y++)
        {
            for (var x = 0; x + width <= bagWidth; x++)
            {
                var free = true;
                for (var dy = 0; dy < height && free; dy++)
                {
                    for (var dx = 0; dx < width && free; dx++)
                    {
                        free = !grid[y + dy, x + dx];
                    }
                }

                if (free)
                {
                    return new Point((ushort)x, (ushort)y);
                }
            }
        }

        return null;
    }

    /// <summary>Clears a spot in the bag for <paramref name="wanted"/> by moving what lies in the way; true once it fits.</summary>
    public static bool ClearRoomFor(PartyMember member, Item wanted)
    {
        var game = member.Game;
        if (game.CursorItem != null)
        {
            return false;
        }

        var bag = game.Inventory;
        var items = bag.Items.Select(item => new Placed(item.Id, item.Location.X, item.Location.Y, item.Width, item.Height)).ToList();
        if (HoleFor(wanted.Width, wanted.Height, (int)bag.Width, (int)bag.Height, items) is not { } hole)
        {
            return false;
        }

        foreach (var (id, to) in hole.Moves)
        {
            if (bag.Items.FirstOrDefault(item => item.Id == id) is not { } item || !MoveTo(game, item, to))
            {
                break;
            }
        }

        var fits = bag.FindFreeSpace(wanted) != null;
        Log.Information("{Character} moved {Count} items for a {Width}x{Height} {Item}: {Outcome}", member.Name, hole.Moves.Count, wanted.Width, wanted.Height,
            wanted.Name, fits ? "it fits" : "still NO room");
        return fits;
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
