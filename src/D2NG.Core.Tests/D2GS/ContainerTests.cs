using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class ContainerTests
{
    private static Item ItemAt(uint id, ushort x, ushort y, ushort width, ushort height, ContainerType container = ContainerType.Stash)
    {
        return new Item { Id = id, Location = new Point(x, y), Width = width, Height = height, Container = container };
    }

    [Fact]
    public void EmptyContainerHasEveryCellFree()
    {
        Assert.Equal(80, new Inventory().FreeCellCount());
        Assert.Equal(100, new Stash().FreeCellCount());
    }

    [Fact]
    public void FreeCellCountSubtractsItemFootprint()
    {
        var inventory = new Inventory();
        inventory.Add(ItemAt(1, 0, 0, 2, 3, ContainerType.Inventory));
        inventory.Add(ItemAt(2, 9, 7, 1, 1, ContainerType.Inventory));

        Assert.Equal(80 - 6 - 1, inventory.FreeCellCount());
    }

    [Fact]
    public void SecondStashPageCountsTowardsTheSameGrid()
    {
        var stash = new Stash();
        stash.Add(ItemAt(1, 0, 0, 1, 1, ContainerType.Stash2));

        Assert.Equal(99, stash.FreeCellCount());
    }

    [Fact]
    public void FullContainerHasZeroFreeCells()
    {
        var inventory = new Inventory();
        uint id = 1;
        for (ushort y = 0; y < 8; y++)
        {
            for (ushort x = 0; x < 10; x++)
            {
                inventory.Add(ItemAt(id++, x, y, 1, 1, ContainerType.Inventory));
            }
        }

        Assert.Equal(0, inventory.FreeCellCount());
        Assert.False(inventory.HasAnyFreeSpace());
    }

    /// <summary>
    /// When a trade ends the server resends every item under a new id at the same spot. The old record must go, or
    /// the container reports the cell twice and the bot later tries to move an id the server no longer knows.
    /// </summary>
    [Fact]
    public void ResentItemAtTheSameSpotReplacesTheStaleRecord()
    {
        var inventory = new Inventory();
        inventory.Add(ItemAt(84, 3, 2, 2, 2, ContainerType.Inventory));
        inventory.Add(ItemAt(200, 3, 2, 2, 2, ContainerType.Inventory));

        Assert.Single(inventory.Items);
        Assert.Null(inventory.FindItemById(84));
        Assert.NotNull(inventory.FindItemById(200));
        Assert.Equal(80 - 4, inventory.FreeCellCount());
    }

    [Fact]
    public void PartialOverlapAlsoEvictsTheStaleRecord()
    {
        var stash = new Stash();
        stash.Add(ItemAt(1, 0, 0, 2, 3));
        stash.Add(ItemAt(2, 1, 1, 1, 1, ContainerType.Stash2));
        stash.Add(ItemAt(3, 1, 1, 2, 2));

        Assert.Equal([2u, 3u], stash.Items.Select(i => i.Id).Order().ToList());
        Assert.Equal(100 - 1 - 4, stash.FreeCellCount());
    }

    [Fact]
    public void ReAddingTheSameIdKeepsOneRecord()
    {
        var inventory = new Inventory();
        inventory.Add(ItemAt(7, 0, 0, 1, 1, ContainerType.Inventory));
        inventory.Add(ItemAt(7, 0, 0, 1, 1, ContainerType.Inventory));

        Assert.Single(inventory.Items);
    }
}
