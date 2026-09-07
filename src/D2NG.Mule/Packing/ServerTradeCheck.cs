using System.Collections.Generic;

namespace D2NG.Mule.Packing;

/// <summary>
/// Reproduces D2Common INVENTORY_CanItemsBeTraded for the receiving side: the offered items are dropped one by one,
/// in trade-window placement order, into a copy of the receiver's inventory using the server's scan. The trade is
/// refused as soon as one finds no spot.
/// </summary>
public static class ServerTradeCheck
{
    public static bool Accepts(OccupancyGrid receiverInventory, IEnumerable<Shape> offerInPlacementOrder)
    {
        var copy = receiverInventory.Clone();
        foreach (var shape in offerInPlacementOrder)
        {
            var spot = copy.ServerFreePosition(shape);
            if (spot == null)
            {
                return false;
            }
            copy.Block(spot.Value, shape);
        }
        return true;
    }
}
