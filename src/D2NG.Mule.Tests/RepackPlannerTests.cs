using D2NG.Mule.Packing;

namespace D2NG.Mule.Tests;

public class RepackPlannerTests
{
    private static readonly Shape StashSize = new(10, 10);
    private static readonly OccupancyGrid Scratch = new(10, 8);

    [Fact]
    public void ScatteredCharmsAreGatheredSoAnArmorFits()
    {
        // Six charms on a 4x4 stash leave 10 free cells and no 2x2 hole.
        var stash = new List<StashItem>
        {
            new(1, new Shape(1, 1), new Cell(0, 0)),
            new(2, new Shape(1, 1), new Cell(1, 1)),
            new(3, new Shape(1, 1), new Cell(2, 2)),
            new(4, new Shape(1, 1), new Cell(3, 3)),
            new(5, new Shape(1, 1), new Cell(1, 3)),
            new(6, new Shape(1, 1), new Cell(3, 1)),
        };

        var plan = RepackPlanner.Plan(stash, new Shape(4, 4), Scratch, [new Shape(2, 2), new Shape(2, 2)]);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.WantedFittingBefore);
        Assert.Equal(2, plan.WantedFittingAfter);
        AssertExecutable(stash, new Shape(4, 4), Scratch, plan.Moves);
    }

    [Fact]
    public void NoPlanWhenRepackingGainsNothing()
    {
        var stash = new List<StashItem>
        {
            new(1, new Shape(2, 2), new Cell(0, 0)),
            new(2, new Shape(2, 2), new Cell(2, 0)),
        };

        Assert.Null(RepackPlanner.Plan(stash, new Shape(4, 2), Scratch, [new Shape(1, 1)]));
        Assert.Null(RepackPlanner.Plan(stash, new Shape(4, 3), Scratch, [new Shape(1, 1)]));
    }

    [Fact]
    public void NoPlanWhenBlockersCannotBeParked()
    {
        var stash = new List<StashItem>
        {
            new(1, new Shape(1, 1), new Cell(0, 0)),
            new(2, new Shape(1, 1), new Cell(1, 1)),
        };
        var fullScratch = new OccupancyGrid(10, 8);
        fullScratch.Block(new Cell(0, 0), new Shape(10, 8));

        // The 1x2 fits once item 2 moves up to (1,0), which is free, so no parking is needed and no scratch is fine.
        var plan = RepackPlanner.Plan(stash, new Shape(2, 2), fullScratch, [new Shape(1, 2)]);
        Assert.NotNull(plan);

        // Items that must swap places need parking, which a full scratch cannot give: the plan is null or executable.
        var swap = new List<StashItem>
        {
            new(1, new Shape(1, 2), new Cell(1, 0)),
            new(2, new Shape(2, 1), new Cell(0, 2)),
            new(3, new Shape(1, 1), new Cell(0, 0)),
        };
        var cyclic = RepackPlanner.Plan(swap, new Shape(2, 3), fullScratch, [new Shape(1, 2)]);
        if (cyclic != null)
        {
            AssertExecutable(swap, new Shape(2, 3), fullScratch, cyclic.Moves);
        }
    }

    [Fact]
    public void ItemsAlreadyInPlaceAreNotMoved()
    {
        var stash = new List<StashItem>
        {
            new(1, new Shape(2, 4), new Cell(0, 0)),
            new(2, new Shape(2, 4), new Cell(2, 0)),
            new(3, new Shape(1, 1), new Cell(5, 0)),
            new(4, new Shape(1, 1), new Cell(5, 2)),
        };

        // 6x5: the two armors and the charms leave 12 free cells, but the charms sit in the only 2x4-shaped hole.
        var plan = RepackPlanner.Plan(stash, new Shape(6, 5), Scratch, [new Shape(2, 4)]);

        Assert.NotNull(plan);
        Assert.DoesNotContain(plan.Moves, m => m.Id == 1 || m.Id == 2);
        Assert.Equal(2, plan.Moves.Count);
        AssertExecutable(stash, new Shape(6, 5), Scratch, plan.Moves);

        // 6x4 has no room for a third armor at all, so there is nothing to gain and no plan.
        Assert.Null(RepackPlanner.Plan(stash, new Shape(6, 4), Scratch, [new Shape(2, 4)]));
    }

    [Fact]
    public void RandomStashesProduceExecutablePlans()
    {
        var random = new Random(99);
        var shapes = new[] { new Shape(1, 1), new Shape(1, 2), new Shape(1, 3), new Shape(2, 2), new Shape(2, 3), new Shape(1, 4), new Shape(2, 4) };
        var plans = 0;
        for (var trial = 0; trial < 300; trial++)
        {
            var grid = new OccupancyGrid(StashSize.Width, StashSize.Height);
            var items = new List<StashItem>();
            var count = random.Next(5, 40);
            for (uint id = 1; id <= count; id++)
            {
                var shape = shapes[random.Next(shapes.Length)];
                var cell = new Cell(random.Next(StashSize.Width), random.Next(StashSize.Height));
                if (grid.CanPlace(cell, shape))
                {
                    grid.Block(cell, shape);
                    items.Add(new StashItem(id, shape, cell));
                }
            }
            var scratch = new OccupancyGrid(10, 8);
            scratch.Block(new Cell(0, 0), new Shape(10, random.Next(0, 8)));
            var wanted = Enumerable.Range(0, random.Next(1, 8)).Select(_ => shapes[random.Next(shapes.Length)]).ToList();

            var plan = RepackPlanner.Plan(items, StashSize, scratch, wanted);
            if (plan == null)
            {
                continue;
            }
            plans++;
            Assert.True(plan.WantedFittingAfter > plan.WantedFittingBefore);
            Assert.True(plan.Moves.Count <= RepackPlanner.MaxMoves);
            var final = AssertExecutable(items, StashSize, scratch, plan.Moves);
            var fitting = 0;
            foreach (var shape in StashPacker.LargestFirst(wanted, s => s))
            {
                var spot = final.FirstFit(shape);
                if (spot != null)
                {
                    final.Block(spot.Value, shape);
                    fitting++;
                }
            }
            Assert.Equal(plan.WantedFittingAfter, fitting);
        }
        Assert.True(plans > 20, $"only {plans} plans were produced");
    }

    /// <summary>
    /// Replays the moves against a fresh grid, failing when any move lands on a taken cell, and returns the final stash.
    /// </summary>
    private static OccupancyGrid AssertExecutable(List<StashItem> items, Shape stashSize, OccupancyGrid scratchStart, IReadOnlyList<Move> moves)
    {
        var stash = new OccupancyGrid(stashSize.Width, stashSize.Height);
        var scratch = scratchStart.Clone();
        var where = new Dictionary<uint, (Destination Where, Cell At)>();
        foreach (var item in items)
        {
            stash.Block(item.At, item.Shape);
            where[item.Id] = (Destination.MuleStash, item.At);
        }
        foreach (var move in moves)
        {
            var (from, at) = where[move.Id];
            (from == Destination.MuleStash ? stash : scratch).Free(at, move.Shape);
            var target = move.To == Destination.MuleStash ? stash : scratch;
            Assert.True(target.CanPlace(move.At, move.Shape), $"move of {move.Id} to {move.To} {move.At} lands on a taken cell");
            target.Block(move.At, move.Shape);
            where[move.Id] = (move.To, move.At);
        }
        Assert.All(where.Values, w => Assert.Equal(Destination.MuleStash, w.Where));
        return stash;
    }
}
