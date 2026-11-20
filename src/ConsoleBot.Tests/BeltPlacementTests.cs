using ConsoleBot.Helpers;

namespace ConsoleBot.Tests;

public class BeltPlacementTests
{
    private static readonly int[] Left = [0, 1];
    private static readonly int[] Right = [2, 3];

    /// <summary>One string per column, bottom row first: H healing, M mana, R rejuvenation, O anything else.</summary>
    private static List<BeltPotion> Belt(params string[] columns)
        => columns.SelectMany((column, x) => column.Select((cell, y) => new BeltPotion(x, y, cell switch
        {
            'H' => PotionKind.Healing,
            'M' => PotionKind.Mana,
            'R' => PotionKind.Rejuvenation,
            _ => PotionKind.Other,
        }))).ToList();

    [Theory]
    [InlineData(PotionKind.Healing, 0, "", "", "", "")]
    [InlineData(PotionKind.Healing, 1, "HHH", "", "", "")]
    [InlineData(PotionKind.Healing, 1, "", "H", "", "")]
    [InlineData(PotionKind.Healing, 2, "", "HHH", "H", "")]
    [InlineData(PotionKind.Healing, 3, "HHH", "HHH", "MMM", "")]
    [InlineData(PotionKind.Healing, 1, "R", "", "M", "M")]
    [InlineData(PotionKind.Mana, 0, "", "H", "", "")]
    [InlineData(PotionKind.Mana, 3, "", "", "MMM", "M")]
    public void APotionGoesToAColumnOfItsKindWithARowFreeBeforeTheLeftmostEmptyColumn(PotionKind kind, int expected, string c0, string c1, string c2, string c3)
        => Assert.Equal(expected, BeltPlacement.Column(Belt(c0, c1, c2, c3), 3, kind));

    [Fact]
    public void AColumnNeverTakesAnotherKind()
    {
        Assert.Null(BeltPlacement.Column(Belt("M", "M", "M", "M"), 3, PotionKind.Healing));
        Assert.Null(BeltPlacement.Column(Belt("HHH", "HHH", "MMM", "MMM"), 3, PotionKind.Healing));
        Assert.Equal(0, BeltPlacement.Column(Belt("M", "M", "M", "M"), 3, PotionKind.Mana));
    }

    [Theory]
    [InlineData(6, 6, "", "", "", "")]
    [InlineData(3, 6, "HH", "H", "", "")]
    [InlineData(0, 6, "HHH", "HHH", "", "")]
    [InlineData(3, 6, "M", "", "", "")]
    [InlineData(1, 6, "MM", "HH", "", "")]
    [InlineData(0, 6, "HHH", "R", "MMM", "MMM")]
    [InlineData(6, 6, "", "", "HHH", "HHH")]
    [InlineData(3, 4, "H", "", "", "")]
    [InlineData(1, 4, "HH", "H", "", "")]
    public void HealingRoomIsTheFreeRowsOfHealthColumnsThatAreEmptyOrHoldHealingCappedByTheTarget(int expected, int target, string c0, string c1, string c2, string c3)
        => Assert.Equal(expected, BeltPlacement.Room(Belt(c0, c1, c2, c3), 3, Left, target, PotionKind.Healing));

    [Fact]
    public void RoomForManaCountsOnlyTheManaColumns()
    {
        Assert.Equal(5, BeltPlacement.Room(Belt("HHH", "HHH", "M", ""), 3, Right, 6, PotionKind.Mana));
        Assert.Equal(2, BeltPlacement.Room(Belt("", "", "H", "M"), 3, Right, 6, PotionKind.Mana));
    }

    [Fact]
    public void FillingStopsWhereTheNextPotionWouldLandInAnotherKindsColumn()
    {
        Assert.Equal(BeltFillStep.Fill, BeltPlacement.Next(Belt("", "", "", ""), 3, Left, 6, PotionKind.Healing));
        Assert.Equal(BeltFillStep.Full, BeltPlacement.Next(Belt("HHH", "HHH", "", ""), 3, Left, 6, PotionKind.Healing));
        // A healing potion in a mana column with a row free takes the next one before the empty health column does.
        Assert.Equal(BeltFillStep.Blocked, BeltPlacement.Next(Belt("HHH", "", "H", ""), 3, Left, 6, PotionKind.Healing));
        // Health columns right of the mana columns: an empty belt puts healing into a mana column.
        Assert.Equal(BeltFillStep.Blocked, BeltPlacement.Next(Belt("", "", "", ""), 3, Right, 6, PotionKind.Healing));
        Assert.Equal(BeltFillStep.Fill, BeltPlacement.Next(Belt("MMM", "MMM", "", ""), 3, Right, 6, PotionKind.Healing));
    }

    [Fact]
    public void AStackBuyIsSafeOnlyWhenEveryColumnItCanEnterIsTheKindsOwnAndItBuysNoMoreThanTheRoom()
    {
        Assert.False(BeltPlacement.StackBuyStays(Belt("", "", "", ""), 3, Left, 6, PotionKind.Healing));
        Assert.True(BeltPlacement.StackBuyStays(Belt("", "", "MMM", "MMM"), 3, Left, 6, PotionKind.Healing));
        Assert.True(BeltPlacement.StackBuyStays(Belt("H", "", "MMM", "MMM"), 3, Left, 6, PotionKind.Healing));
        Assert.False(BeltPlacement.StackBuyStays(Belt("", "", "MMM", "MMM"), 3, Left, 4, PotionKind.Healing));
        Assert.True(BeltPlacement.StackBuyStays(Belt("HHH", "HHH", "MMM", "M"), 3, Right, 6, PotionKind.Mana));
        Assert.False(BeltPlacement.StackBuyStays(Belt("HHH", "HHH", "MMM", "MMM"), 3, Right, 6, PotionKind.Mana));
    }

    [Theory]
    [InlineData(PotionKind.Healing, 0, false)]
    [InlineData(PotionKind.Healing, 2, true)]
    [InlineData(PotionKind.Mana, 3, false)]
    [InlineData(PotionKind.Mana, 1, true)]
    [InlineData(PotionKind.Rejuvenation, 0, true)]
    [InlineData(PotionKind.Rejuvenation, 3, true)]
    [InlineData(PotionKind.Other, 1, true)]
    public void APotionIsMisplacedOutsideItsKindsColumnsAndRejuvenationsEverywhere(PotionKind kind, int column, bool expected)
        => Assert.Equal(expected, BeltPlacement.IsMisplaced(new BeltPotion(column, 0, kind), Left, Right));

    /// <summary>The fill order the town visit uses: healing, then mana, then healing again, each while <see cref="BeltPlacement.Next"/> says Fill.</summary>
    private static List<BeltPotion> Fill(List<BeltPotion> belt, int rows, int[] health, int[] mana)
        => Refill(belt, rows, rows, health, mana, 0, 0).Belt;

    private sealed record RefillResult(List<BeltPotion> Belt, Dictionary<PotionKind, int> Bought, Dictionary<PotionKind, int> Inventory);

    /// <summary>
    /// What InventoryHelpers.RefillBelt does, against the placement model instead of a server: in <see cref="BeltPlacement.FillOrder"/>,
    /// each step chosen by <see cref="BeltPlacement.NextRefill"/>, with an unlimited budget.
    /// </summary>
    private static RefillResult Refill(List<BeltPotion> belt, int rows, int rowsToFill, int[] health, int[] mana, int inventoryHealing, int inventoryMana)
    {
        var inventory = new Dictionary<PotionKind, int> { [PotionKind.Healing] = inventoryHealing, [PotionKind.Mana] = inventoryMana };
        var bought = new Dictionary<PotionKind, int> { [PotionKind.Healing] = 0, [PotionKind.Mana] = 0 };
        foreach (var kind in BeltPlacement.FillOrder)
        {
            var own = kind == PotionKind.Healing ? health : mana;
            while (true)
            {
                var action = BeltPlacement.NextRefill(belt, rows, own, rowsToFill * own.Length, kind, inventory[kind], long.MaxValue);
                if (action == BeltRefillAction.None)
                {
                    break;
                }

                if (action == BeltRefillAction.PutFromInventory)
                {
                    inventory[kind]--;
                }
                else
                {
                    bought[kind]++;
                }

                var column = BeltPlacement.Column(belt, rows, kind)!.Value;
                belt.Add(new BeltPotion(column, belt.Count(p => p.Column == column), kind));
            }
        }

        return new RefillResult(belt, bought, inventory);
    }

    [Fact]
    public void ARowTargetBelowTheBeltHeightStillPutsAPotionInEveryOwnColumnSoTheOtherKindIsNotBlocked()
    {
        Assert.Equal(1, BeltPlacement.Room(Belt("HHHH", "", "", ""), 4, Left, 4, PotionKind.Healing));
        Assert.Equal(BeltFillStep.Full, BeltPlacement.Next(Belt("HHHH", "H", "", ""), 4, Left, 4, PotionKind.Healing));

        var result = Refill([], 4, 2, Left, Right, 0, 0);
        Assert.All(result.Belt, p => Assert.False(BeltPlacement.IsMisplaced(p, Left, Right)));
        Assert.True(result.Belt.Count(p => p.Kind == PotionKind.Mana) >= 4);
        Assert.Equal(4, result.Belt.Select(p => p.Column).Distinct().Count());
    }

    [Fact]
    public void InventoryManaBlockedByAnEmptyHealthColumnIsUsedOnceTheHealingIsBoughtInsteadOfBuyingMoreMana()
    {
        var result = Refill(Belt("HHH", "", "", ""), 3, 3, Left, Right, 0, 3);
        Assert.Equal(0, result.Inventory[PotionKind.Mana]);
        Assert.Equal(3, result.Bought[PotionKind.Mana]);
        Assert.Equal(3, result.Bought[PotionKind.Healing]);
        Assert.Equal(12, result.Belt.Count);
    }

    [Fact]
    public void TheSecondHealingPassUsesTheInventoryBeforeBuying()
    {
        // Health columns right of the mana columns: healing is blocked until the mana columns are filled.
        var result = Refill([], 3, 3, Right, Left, 2, 0);
        Assert.Equal(0, result.Inventory[PotionKind.Healing]);
        Assert.Equal(4, result.Bought[PotionKind.Healing]);
        Assert.Equal(6, result.Bought[PotionKind.Mana]);
        Assert.All(result.Belt, p => Assert.False(BeltPlacement.IsMisplaced(p, Right, Left)));
    }

    [Fact]
    public void AMovedOutPotionLeavesATomesSpaceFreeWhenThereIsNoTome()
    {
        // A 2x2 inventory with its top left cell taken: the free cell beside it would leave no 1x2 space, the one below it does.
        bool[,] grid = { { true, false }, { false, false } };
        Assert.Equal((0, 1), InventoryReserve.CellKeeping((x, y) => grid[y, x], 2, 2, 1, 2));
        Assert.Equal((1, 0), InventoryReserve.CellKeeping((x, y) => grid[y, x], 2, 2, 0, 0));

        bool[,] tight = { { true, false }, { true, false } };
        Assert.Null(InventoryReserve.CellKeeping((x, y) => tight[y, x], 2, 2, 1, 2));
        Assert.Equal((1, 0), InventoryReserve.CellKeeping((x, y) => tight[y, x], 2, 2, 0, 0));
    }

    [Theory]
    [InlineData(new[] { 0, 1 }, new[] { 2, 3 })]
    [InlineData(new[] { 2, 3 }, new[] { 0, 1 })]
    [InlineData(new[] { 0, 3 }, new[] { 1, 2 })]
    [InlineData(new[] { 1, 2, 3 }, new[] { 0 })]
    public void TheOrderedFillFillsEveryColumnWithItsOwnKindWhateverTheLayout(int[] health, int[] mana)
    {
        var belt = Fill([], 4, health, mana);
        Assert.Equal(16, belt.Count);
        Assert.All(belt, p => Assert.False(BeltPlacement.IsMisplaced(p, health, mana)));
    }

    [Fact]
    public void TheOrderedFillTopsUpAPartBeltWithoutSpilling()
    {
        var belt = Fill(Belt("H", "", "M", ""), 3, Left, Right);
        Assert.Equal(12, belt.Count);
        Assert.All(belt, p => Assert.False(BeltPlacement.IsMisplaced(p, Left, Right)));
    }

    [Fact]
    public void AMisplacedPotionBlocksItsColumnAndDrawsItsOwnKindUntilItIsTakenOut()
    {
        // The mana potion in health column 0 takes the next mana potion before the empty mana columns would.
        var belt = Fill(Belt("M", "", "", ""), 3, Left, Right);
        Assert.Equal(3, belt.Count(p => p.Kind == PotionKind.Healing));
        Assert.Equal(1, belt.Count(p => p.Kind == PotionKind.Mana));

        var cleaned = Fill(belt.Where(p => !BeltPlacement.IsMisplaced(p, Left, Right)).ToList(), 3, Left, Right);
        Assert.Equal(12, cleaned.Count);
        Assert.All(cleaned, p => Assert.False(BeltPlacement.IsMisplaced(p, Left, Right)));
    }
}
