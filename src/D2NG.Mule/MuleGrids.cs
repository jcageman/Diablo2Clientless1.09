using D2NG.Core;
using D2NG.Core.D2GS.Items;
using D2NG.Mule.Packing;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule;

/// <summary>
/// Reads the packing model out of a live game: grids, stash items with their cells, and candidates.
/// </summary>
public static class MuleGrids
{
    public static readonly Shape StashSize = new(10, 10);

    public static Shape ShapeOf(Item item) => new(item.Width, item.Height);

    /// <summary>
    /// The second stash page sits below the first in the 10x10 stash model.
    /// </summary>
    public static Cell StashCell(Item item)
    {
        return new Cell(item.Location.X, item.Container == ContainerType.Stash2 ? item.Location.Y + 8 : item.Location.Y);
    }

    public static List<StashItem> StashItems(Game game)
    {
        return game.Stash.Items.Select(i => new StashItem(i.Id, ShapeOf(i), StashCell(i))).ToList();
    }

    public static List<Candidate> Candidates(IEnumerable<Item> items)
    {
        return items
            .Select(i => new Candidate(i.Id, ShapeOf(i), i.Container == ContainerType.Inventory ? Origin.FarmerInventory : Origin.FarmerStash))
            .ToList();
    }

    public static OccupancyGrid Inventory(Game game) => OccupancyGrid.FromContainer(game.Inventory);

    public static OccupancyGrid Stash(Game game) => OccupancyGrid.FromContainer(game.Stash);
}
