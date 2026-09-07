using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule.Packing;

public enum Origin
{
    FarmerInventory,
    FarmerStash
}

public enum Destination
{
    MuleStash,
    MuleInventory
}

public enum SkipReason
{
    TradeGridFull,
    FarmerInventoryFull,
    ServerRefuses,
    NoRoomOnMule
}

public sealed record Candidate(uint Id, Shape Shape, Origin Origin);

public sealed record PlannedItem(Candidate Item, Cell TradeCell, Cell? FarmerInventoryCell, Destination Destination);

public sealed record Skipped(Candidate Item, SkipReason Reason);

public sealed record TradeRound(IReadOnlyList<PlannedItem> Items, IReadOnlyList<Skipped> Skipped)
{
    public bool IsEmpty => Items.Count == 0;

    public int Cells => Items.Sum(i => i.Item.Shape.Area);

    public IEnumerable<Shape> ShapesInPlacementOrder => Items.Select(i => i.Item.Shape);

    /// <summary>
    /// Nothing more can land on this mule: every leftover was refused by the mule side, not by the farmer or the grid.
    /// </summary>
    public bool MuleIsFull => IsEmpty && Skipped.Count > 0
        && Skipped.All(s => s.Reason is SkipReason.NoRoomOnMule or SkipReason.ServerRefuses);

    public bool FarmerInventoryBlocks => IsEmpty && Skipped.Count > 0
        && Skipped.Any(s => s.Reason == SkipReason.FarmerInventoryFull);
}

/// <summary>
/// One trade round: as many of the remaining items as the trade grid, the farmer's inventory, the server's room check
/// and the mule's final resting places allow. Largest items go first so the mule's stash stays compact; each item is
/// bound for the stash when a spot exists there and stays in the mule's inventory otherwise.
/// </summary>
public static class TradePlanner
{
    public static readonly Shape TradeGrid = new(10, 4);

    public static TradeRound Plan(IReadOnlyList<Candidate> candidates, OccupancyGrid farmerInventory, OccupancyGrid muleInventory, OccupancyGrid muleStash)
    {
        var trade = new OccupancyGrid(TradeGrid.Width, TradeGrid.Height);
        var farmer = farmerInventory.Clone();
        var stash = muleStash.Clone();
        var residents = muleInventory.Clone();
        var offer = new List<Shape>();
        var planned = new List<PlannedItem>();
        var skipped = new List<Skipped>();

        foreach (var candidate in StashPacker.LargestFirst(candidates, c => c.Shape))
        {
            var tradeCell = trade.FirstFit(candidate.Shape);
            if (tradeCell == null)
            {
                skipped.Add(new Skipped(candidate, SkipReason.TradeGridFull));
                continue;
            }

            Cell? farmerCell = null;
            if (candidate.Origin == Origin.FarmerStash)
            {
                farmerCell = farmer.FirstFit(candidate.Shape);
                if (farmerCell == null)
                {
                    skipped.Add(new Skipped(candidate, SkipReason.FarmerInventoryFull));
                    continue;
                }
            }

            offer.Add(candidate.Shape);
            if (!ServerTradeCheck.Accepts(muleInventory, offer))
            {
                offer.RemoveAt(offer.Count - 1);
                skipped.Add(new Skipped(candidate, SkipReason.ServerRefuses));
                continue;
            }

            Destination destination;
            var stashCell = stash.FirstFit(candidate.Shape);
            if (stashCell != null)
            {
                stash.Block(stashCell.Value, candidate.Shape);
                destination = Destination.MuleStash;
            }
            else
            {
                var residentCell = residents.FirstFit(candidate.Shape);
                if (residentCell == null)
                {
                    offer.RemoveAt(offer.Count - 1);
                    skipped.Add(new Skipped(candidate, SkipReason.NoRoomOnMule));
                    continue;
                }
                residents.Block(residentCell.Value, candidate.Shape);
                destination = Destination.MuleInventory;
            }

            trade.Block(tradeCell.Value, candidate.Shape);
            if (farmerCell != null)
            {
                farmer.Block(farmerCell.Value, candidate.Shape);
            }
            planned.Add(new PlannedItem(candidate, tradeCell.Value, farmerCell, destination));
        }

        return new TradeRound(planned, skipped);
    }
}
