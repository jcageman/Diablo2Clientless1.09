using D2NG.Mule.Packing;

namespace D2NG.Mule.Tests;

public class TradePlannerTests
{
    private static readonly OccupancyGrid EmptyInventory = new(10, 8);
    private static readonly OccupancyGrid EmptyStash = new(10, 10);

    private static OccupancyGrid Filled(int width, int height)
    {
        var grid = new OccupancyGrid(width, height);
        grid.Block(new Cell(0, 0), new Shape(width, height));
        return grid;
    }

    private static List<Candidate> Candidates(Origin origin, params Shape[] shapes)
    {
        return shapes.Select((s, i) => new Candidate((uint)(i + 1), s, origin)).ToList();
    }

    [Fact]
    public void EmptyMuleTakesAFullTradeGridInOneRound()
    {
        var candidates = Candidates(Origin.FarmerStash, Enumerable.Repeat(new Shape(2, 4), 6).ToArray());

        var round = TradePlanner.Plan(candidates, EmptyInventory, EmptyInventory, EmptyStash);

        Assert.Equal(5, round.Items.Count);
        Assert.Equal(40, round.Cells);
        Assert.Single(round.Skipped);
        Assert.Equal(SkipReason.TradeGridFull, round.Skipped[0].Reason);
        Assert.All(round.Items, i => Assert.Equal(Destination.MuleStash, i.Destination));
    }

    [Fact]
    public void LargestItemsGoFirst()
    {
        var candidates = Candidates(Origin.FarmerInventory, new Shape(1, 1), new Shape(2, 3), new Shape(1, 3), new Shape(2, 4));

        var round = TradePlanner.Plan(candidates, EmptyInventory, EmptyInventory, EmptyStash);

        Assert.Equal([new Shape(2, 4), new Shape(2, 3), new Shape(1, 3), new Shape(1, 1)], round.ShapesInPlacementOrder.ToList());
    }

    [Fact]
    public void StashItemsNeedRoomInTheFarmersInventory()
    {
        var farmerInventory = Filled(10, 8);
        farmerInventory.Free(new Cell(0, 0), new Shape(1, 1));
        var candidates = Candidates(Origin.FarmerStash, new Shape(1, 1), new Shape(1, 1));

        var round = TradePlanner.Plan(candidates, farmerInventory, EmptyInventory, EmptyStash);

        Assert.Single(round.Items);
        Assert.Equal(new Cell(0, 0), round.Items[0].FarmerInventoryCell);
        Assert.Equal(SkipReason.FarmerInventoryFull, round.Skipped[0].Reason);
        Assert.False(round.MuleIsFull);
    }

    [Fact]
    public void FarmerInventoryItemsNeedNoFarmerRoom()
    {
        var candidates = Candidates(Origin.FarmerInventory, new Shape(2, 3));

        var round = TradePlanner.Plan(candidates, Filled(10, 8), EmptyInventory, EmptyStash);

        Assert.Single(round.Items);
        Assert.Null(round.Items[0].FarmerInventoryCell);
    }

    [Fact]
    public void ItemsStayInTheMulesInventoryWhenTheStashIsFull()
    {
        var candidates = Candidates(Origin.FarmerInventory, new Shape(2, 3), new Shape(1, 1));

        var round = TradePlanner.Plan(candidates, EmptyInventory, EmptyInventory, Filled(10, 10));

        Assert.Equal(2, round.Items.Count);
        Assert.All(round.Items, i => Assert.Equal(Destination.MuleInventory, i.Destination));
    }

    [Fact]
    public void ResidentsShrinkTheRoundThroughTheServerCheck()
    {
        var muleInventory = Filled(10, 8);
        muleInventory.Free(new Cell(0, 0), new Shape(3, 1));
        var candidates = Candidates(Origin.FarmerInventory, new Shape(2, 1), new Shape(1, 1), new Shape(1, 1));

        var round = TradePlanner.Plan(candidates, EmptyInventory, muleInventory, EmptyStash);

        Assert.Equal(2, round.Items.Count);
        Assert.Equal(SkipReason.ServerRefuses, round.Skipped[0].Reason);
    }

    [Fact]
    public void MuleWithNoRoomAnywhereIsReportedFull()
    {
        var candidates = Candidates(Origin.FarmerInventory, new Shape(1, 1));

        var round = TradePlanner.Plan(candidates, EmptyInventory, Filled(10, 8), Filled(10, 10));

        Assert.True(round.IsEmpty);
        Assert.True(round.MuleIsFull);
    }

    [Fact]
    public void FullStashWithFreeInventoryIsNotFull()
    {
        var candidates = Candidates(Origin.FarmerInventory, new Shape(1, 1));

        var round = TradePlanner.Plan(candidates, EmptyInventory, EmptyInventory, Filled(10, 10));

        Assert.False(round.IsEmpty);
        Assert.False(round.MuleIsFull);
    }

    [Fact]
    public void NoCandidatesIsNotFull()
    {
        var round = TradePlanner.Plan([], EmptyInventory, EmptyInventory, EmptyStash);

        Assert.True(round.IsEmpty);
        Assert.False(round.MuleIsFull);
    }

    [Fact]
    public void PlannedRoundAlwaysPassesTheServerCheck()
    {
        var random = new Random(1234);
        var shapes = new[] { new Shape(1, 1), new Shape(1, 2), new Shape(1, 3), new Shape(1, 4), new Shape(2, 2), new Shape(2, 3), new Shape(2, 4) };
        for (var trial = 0; trial < 300; trial++)
        {
            var muleInventory = RandomGrid(random, 10, 8, random.Next(0, 70));
            var muleStash = RandomGrid(random, 10, 10, random.Next(0, 100));
            var farmerInventory = RandomGrid(random, 10, 8, random.Next(0, 60));
            var candidates = Enumerable.Range(1, random.Next(1, 30))
                .Select(i => new Candidate((uint)i, shapes[random.Next(shapes.Length)], random.Next(2) == 0 ? Origin.FarmerInventory : Origin.FarmerStash))
                .ToList();

            var round = TradePlanner.Plan(candidates, farmerInventory, muleInventory, muleStash);

            Assert.True(ServerTradeCheck.Accepts(muleInventory, round.ShapesInPlacementOrder));
            Assert.True(round.Cells <= 40);
            Assert.Equal(candidates.Count, round.Items.Count + round.Skipped.Count);
            var stashBound = round.Items.Where(i => i.Destination == Destination.MuleStash).Select(i => new PackItem(i.Item.Id, i.Item.Shape)).ToList();
            Assert.Empty(StashPacker.Pack(muleStash.Clone(), stashBound).Unplaced);
            if (round.MuleIsFull)
            {
                Assert.All(candidates, c => Assert.True(
                    !ServerTradeCheck.Accepts(muleInventory, [c.Shape]) || (!muleStash.Fits(c.Shape) && !muleInventory.Fits(c.Shape)),
                    $"{c.Shape} could still land on the mule"));
            }
        }
    }

    private static OccupancyGrid RandomGrid(Random random, int width, int height, int taken)
    {
        var grid = new OccupancyGrid(width, height);
        var cells = Enumerable.Range(0, width * height).OrderBy(_ => random.Next()).Take(taken);
        foreach (var index in cells)
        {
            grid.Block(new Cell(index % width, index / width), new Shape(1, 1));
        }
        return grid;
    }
}
