using System.Collections.Generic;
using System.Linq;

namespace ConsoleBot.Helpers;

public enum PotionKind
{
    Healing,
    Mana,
    Rejuvenation,

    /// <summary>Anything else the belt holds: antidote, stamina and thawing potions, scrolls.</summary>
    Other,
}

/// <summary>One item in the belt: its column from the left and its row from the bottom.</summary>
public readonly record struct BeltPotion(int Column, int Row, PotionKind Kind);

public enum BeltRefillAction
{
    None,
    PutFromInventory,
    Buy,
}

/// <summary>
/// Inventory cells for potions taken out of the belt that keep room for a tome of town portal: a character without one has to
/// be able to buy one, or it cannot get back to town.
/// </summary>
public static class InventoryReserve
{
    public const int TomeWidth = 1;
    public const int TomeHeight = 2;

    /// <summary>The first free cell, row by row, whose taking still leaves a free space of the reserve's size; null if none does.</summary>
    public static (int X, int Y)? CellKeeping(System.Func<int, int, bool> occupied, int width, int height, int reserveWidth, int reserveHeight)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!occupied(x, y) && HasSpace(occupied, width, height, reserveWidth, reserveHeight, (x, y)))
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    private static bool HasSpace(System.Func<int, int, bool> occupied, int width, int height, int spaceWidth, int spaceHeight, (int X, int Y) taken)
    {
        for (var y = 0; y + spaceHeight <= height; y++)
        {
            for (var x = 0; x + spaceWidth <= width; x++)
            {
                var free = true;
                for (var dy = 0; dy < spaceHeight && free; dy++)
                {
                    for (var dx = 0; dx < spaceWidth && free; dx++)
                    {
                        free = !occupied(x + dx, y + dy) && (x + dx, y + dy) != taken;
                    }
                }

                if (free)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

public enum BeltFillStep
{
    /// <summary>The next potion of this kind lands in one of its own columns.</summary>
    Fill,

    /// <summary>Its columns hold what they should.</summary>
    Full,

    /// <summary>Its columns have room, but the server would put the next potion into a column of another kind, or nowhere.</summary>
    Blocked,
}

/// <summary>
/// Where the server puts a potion that is bought, picked up or sent to the belt (D2MOO INVENTORY_GetFreeBeltSlot). A column
/// whose bottom potion is of the same kind and that has a free row takes it first, leftmost first; only then the leftmost
/// empty column. A column never takes another kind, and a potion with neither goes to the inventory. A stack buy repeats this
/// until the belt takes no more, so it fills every column the kind may enter, whoever's column that is.
/// </summary>
public static class BeltPlacement
{
    public const int Columns = 4;

    public static int? Column(IReadOnlyCollection<BeltPotion> belt, int rows, PotionKind kind)
    {
        for (var column = 0; column < Columns; column++)
        {
            if (KindIn(belt, column) == kind && CountIn(belt, column) < rows)
            {
                return column;
            }
        }

        for (var column = 0; column < Columns; column++)
        {
            if (CountIn(belt, column) == 0)
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>
    /// Potions of this kind its own columns can still take, up to the target: the free rows of those columns that are empty or
    /// already hold the kind. A column holding another kind is no room until that potion is gone. Past the target, an own
    /// column left empty still takes one: the server fills a column to the top before opening the next, so a target below the
    /// belt's height leaves a column empty, and the other kind's next potion would go into it.
    /// </summary>
    public static int Room(IReadOnlyCollection<BeltPotion> belt, int rows, IReadOnlyCollection<int> ownColumns, int target, PotionKind kind)
    {
        var held = belt.Count(p => p.Kind == kind && ownColumns.Contains(p.Column));
        var free = ownColumns
            .Where(column => CountIn(belt, column) == 0 || KindIn(belt, column) == kind)
            .Sum(column => rows - CountIn(belt, column));
        var empty = ownColumns.Count(column => CountIn(belt, column) == 0);
        var wanted = System.Math.Max(target - held, target > 0 ? empty : 0);
        return System.Math.Max(0, System.Math.Min(wanted, free));
    }

    /// <summary>The kinds in the order the belt is filled: healing, mana, then healing again for a layout whose health columns are right of the mana columns.</summary>
    public static readonly PotionKind[] FillOrder = [PotionKind.Healing, PotionKind.Mana, PotionKind.Healing];

    /// <summary>
    /// The next step of filling the belt with this kind: an inventory potion while there is one, a bought one while the budget
    /// lasts, and nothing once the next potion would not land in one of the kind's own columns.
    /// </summary>
    public static BeltRefillAction NextRefill(IReadOnlyCollection<BeltPotion> belt, int rows, IReadOnlyCollection<int> ownColumns, int target, PotionKind kind,
        int inInventory, long budget)
    {
        if (Next(belt, rows, ownColumns, target, kind) != BeltFillStep.Fill)
        {
            return BeltRefillAction.None;
        }

        return inInventory > 0 ? BeltRefillAction.PutFromInventory : budget > 0 ? BeltRefillAction.Buy : BeltRefillAction.None;
    }

    public static BeltFillStep Next(IReadOnlyCollection<BeltPotion> belt, int rows, IReadOnlyCollection<int> ownColumns, int target, PotionKind kind)
    {
        if (Room(belt, rows, ownColumns, target, kind) == 0)
        {
            return BeltFillStep.Full;
        }

        return Column(belt, rows, kind) is int column && ownColumns.Contains(column) ? BeltFillStep.Fill : BeltFillStep.Blocked;
    }

    /// <summary>Whether a stack buy of this kind stays in its own columns and buys no more than the room.</summary>
    public static bool StackBuyStays(IReadOnlyCollection<BeltPotion> belt, int rows, IReadOnlyCollection<int> ownColumns, int target, PotionKind kind)
    {
        var taking = Enumerable.Range(0, Columns)
            .Where(column => CountIn(belt, column) == 0 || (KindIn(belt, column) == kind && CountIn(belt, column) < rows))
            .ToList();
        return taking.Count > 0
            && taking.All(ownColumns.Contains)
            && taking.Sum(column => rows - CountIn(belt, column)) <= Room(belt, rows, ownColumns, target, kind);
    }

    /// <summary>
    /// Whether an item is in a column it does not belong in. Rejuvenations belong in no column: they are kept in the inventory,
    /// where they are drunk from and do not take a healing or mana potion's place.
    /// </summary>
    public static bool IsMisplaced(BeltPotion potion, IReadOnlyCollection<int> healthColumns, IReadOnlyCollection<int> manaColumns)
        => potion.Kind switch
        {
            PotionKind.Healing => !healthColumns.Contains(potion.Column),
            PotionKind.Mana => !manaColumns.Contains(potion.Column),
            PotionKind.Rejuvenation => true,
            _ => healthColumns.Contains(potion.Column) || manaColumns.Contains(potion.Column),
        };

    private static int CountIn(IReadOnlyCollection<BeltPotion> belt, int column) => belt.Count(p => p.Column == column);

    private static PotionKind? KindIn(IReadOnlyCollection<BeltPotion> belt, int column)
        => belt.Where(p => p.Column == column).OrderBy(p => p.Row).Select(p => (PotionKind?)p.Kind).FirstOrDefault();
}
