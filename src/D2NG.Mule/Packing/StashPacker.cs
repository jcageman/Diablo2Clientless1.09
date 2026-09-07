using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule.Packing;

public sealed record PackItem(uint Id, Shape Shape);

public sealed record Placement(uint Id, Shape Shape, Cell At);

public sealed record PackResult(IReadOnlyList<Placement> Placed, IReadOnlyList<PackItem> Unplaced);

/// <summary>
/// Largest items first, each at the first free spot scanning rows. Big items claim the open rows while they exist and
/// the small ones fill the holes, so the grid fragments far less than it does when items land in pickup order.
/// </summary>
public static class StashPacker
{
    public static IEnumerable<T> LargestFirst<T>(IEnumerable<T> items, Func<T, Shape> shape)
    {
        return items
            .OrderByDescending(i => shape(i).Area)
            .ThenByDescending(i => shape(i).Height)
            .ThenByDescending(i => shape(i).Width);
    }

    /// <summary>
    /// Places into <paramref name="grid"/>, mutating it.
    /// </summary>
    public static PackResult Pack(OccupancyGrid grid, IEnumerable<PackItem> items)
    {
        var placed = new List<Placement>();
        var unplaced = new List<PackItem>();
        foreach (var item in LargestFirst(items, i => i.Shape))
        {
            var spot = grid.FirstFit(item.Shape);
            if (spot == null)
            {
                unplaced.Add(item);
                continue;
            }
            grid.Block(spot.Value, item.Shape);
            placed.Add(new Placement(item.Id, item.Shape, spot.Value));
        }
        return new PackResult(placed, unplaced);
    }
}
