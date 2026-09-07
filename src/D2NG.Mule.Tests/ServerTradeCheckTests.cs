using D2NG.Mule.Packing;

namespace D2NG.Mule.Tests;

public class ServerTradeCheckTests
{
    [Fact]
    public void FullTradeGridIntoEmptyInventoryIsAccepted()
    {
        var inventory = new OccupancyGrid(10, 8);
        var offer = Enumerable.Repeat(new Shape(2, 4), 5).ToList();

        Assert.True(ServerTradeCheck.Accepts(inventory, offer));
    }

    [Fact]
    public void OfferLargerThanFreeCellsIsRefused()
    {
        var inventory = OccupancyGrid.Parse("""
            ##########
            ##########
            ##########
            ##########
            ##########
            ##########
            ##########
            ###.......
            """);

        Assert.True(ServerTradeCheck.Accepts(inventory, [new Shape(2, 1), new Shape(2, 1), new Shape(2, 1), new Shape(1, 1)]));
        Assert.False(ServerTradeCheck.Accepts(inventory, [new Shape(2, 1), new Shape(2, 1), new Shape(2, 1), new Shape(2, 1)]));
    }

    [Fact]
    public void ServerScanOrderCanRefuseWhatRowMajorPackingWouldFit()
    {
        // Two free 1x2 slots stand in columns 0 and 2; a 2x1 bar and a 1x2 charm are offered.
        // Row-major would put the 2x1... nowhere either, so use a case where order of placement matters:
        // free cells: (0,0),(1,0) in row 0 and (0,1) in row 1. Offer 1x2 then 2x1.
        var inventory = OccupancyGrid.Parse("""
            ..#
            .##
            """);

        // 1x2 goes column-major from the left: (0,0). Then 2x1 finds nothing.
        Assert.False(ServerTradeCheck.Accepts(inventory, [new Shape(1, 2), new Shape(2, 1)]));
        // Offered the other way round the 2x1 takes row 0, leaving (0,1) for nothing 1x2 — still refused.
        Assert.False(ServerTradeCheck.Accepts(inventory, [new Shape(2, 1), new Shape(1, 2)]));
        // A single 1x1 lands in the rightmost free column first: (1,0).
        var copy = inventory.Clone();
        Assert.Equal(new Cell(1, 0), copy.ServerFreePosition(new Shape(1, 1)));
    }

    [Fact]
    public void OneHighItemsFillFromTheRightSoTheyDoNotStealTheLeftColumns()
    {
        var inventory = OccupancyGrid.Parse("""
            ....
            ##..
            """);

        // 1x1 first: server puts it at (3,0), leaving room for the 2x2 at (0,0)? No: (0,1),(1,1) are taken,
        // so a 2x2 fits only at (2,0)-(3,1). With the 1x1 at (3,0) the 2x2 is refused.
        Assert.False(ServerTradeCheck.Accepts(inventory, [new Shape(1, 1), new Shape(2, 2)]));
        // 2x2 first takes (2,0); the 1x1 then lands at (1,0).
        Assert.True(ServerTradeCheck.Accepts(inventory, [new Shape(2, 2), new Shape(1, 1)]));
    }
}
