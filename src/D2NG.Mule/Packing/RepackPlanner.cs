using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule.Packing;

public sealed record StashItem(uint Id, Shape Shape, Cell At);

public sealed record Move(uint Id, Shape Shape, Destination To, Cell At);

public sealed record RepackPlan(IReadOnlyList<Move> Moves, int WantedFittingBefore, int WantedFittingAfter);

/// <summary>
/// Rearranges a stash so that more of the wanted shapes fit. A target layout is computed first, then a move sequence
/// that reaches it, parking blockers in the mule's inventory when a target spot is still taken. The whole sequence is
/// simulated before anything is returned, so a plan that comes back can be executed move by move.
/// </summary>
public static class RepackPlanner
{
    public const int MaxMoves = 60;

    public static RepackPlan Plan(IReadOnlyList<StashItem> stashItems, Shape stashSize, OccupancyGrid inventoryScratch, IReadOnlyList<Shape> wanted)
    {
        var current = new OccupancyGrid(stashSize.Width, stashSize.Height);
        foreach (var item in stashItems)
        {
            current.Block(item.At, item.Shape);
        }
        var wantedNow = CountFitting(current.Clone(), wanted);

        var anchored = Layout(stashItems, wanted, stashSize, keepPositions: true);
        var compact = Layout(stashItems, wanted, stashSize, keepPositions: false);
        var anchoredFit = CountFitting(Occupancy(stashItems, anchored, stashSize), wanted);
        var compactFit = CountFitting(Occupancy(stashItems, compact, stashSize), wanted);

        var (target, fit) = compactFit > anchoredFit ? (compact, compactFit) : (anchored, anchoredFit);
        if (fit <= wantedNow)
        {
            return null;
        }

        var moves = MovesTowards(stashItems, target, current, inventoryScratch.Clone());
        return moves == null ? null : new RepackPlan(moves, wantedNow, fit);
    }

    private static int CountFitting(OccupancyGrid grid, IReadOnlyList<Shape> wanted)
    {
        var count = 0;
        foreach (var shape in StashPacker.LargestFirst(wanted, s => s))
        {
            var spot = grid.FirstFit(shape);
            if (spot == null)
            {
                continue;
            }
            grid.Block(spot.Value, shape);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Where each stash item should end up. The wanted shapes take part in the packing as placeholders so that the
    /// stash items are arranged around them. When the placeholders crowd a stash item out, the smallest wanted shape
    /// is dropped and the packing is redone; with no placeholders left every stash item fits by definition.
    /// </summary>
    private static Dictionary<uint, Cell> Layout(IReadOnlyList<StashItem> items, IReadOnlyList<Shape> wanted, Shape stashSize, bool keepPositions)
    {
        var placeholders = StashPacker.LargestFirst(wanted, s => s).ToList();
        for (var count = placeholders.Count; count >= 0; count--)
        {
            var target = TryLayout(items, placeholders.Take(count), stashSize, keepPositions);
            if (target != null)
            {
                return target;
            }
        }
        return null;
    }

    private static Dictionary<uint, Cell> TryLayout(IReadOnlyList<StashItem> items, IEnumerable<Shape> placeholders, Shape stashSize, bool keepPositions)
    {
        var grid = new OccupancyGrid(stashSize.Width, stashSize.Height);
        var target = new Dictionary<uint, Cell>();
        var entries = items.Select(i => (Item: i, Shape: i.Shape)).Concat(placeholders.Select(w => (Item: (StashItem)null, Shape: w)));
        foreach (var (item, shape) in StashPacker.LargestFirst(entries, e => e.Shape))
        {
            var spot = item != null && keepPositions && grid.CanPlace(item.At, shape) ? item.At : grid.FirstFit(shape);
            if (spot == null)
            {
                return null;
            }
            grid.Block(spot.Value, shape);
            if (item != null)
            {
                target[item.Id] = spot.Value;
            }
        }
        return target;
    }

    private static OccupancyGrid Occupancy(IReadOnlyList<StashItem> items, Dictionary<uint, Cell> layout, Shape stashSize)
    {
        var grid = new OccupancyGrid(stashSize.Width, stashSize.Height);
        foreach (var item in items)
        {
            grid.Block(layout[item.Id], item.Shape);
        }
        return grid;
    }

    private static List<Move> MovesTowards(IReadOnlyList<StashItem> items, Dictionary<uint, Cell> target, OccupancyGrid stash, OccupancyGrid scratch)
    {
        var shapes = items.ToDictionary(i => i.Id, i => i.Shape);
        var inStash = items.ToDictionary(i => i.Id, i => i.At);
        var parked = new Dictionary<uint, Cell>();
        var pending = items.Where(i => target[i.Id] != i.At).Select(i => i.Id).ToList();
        var moves = new List<Move>();

        while (pending.Count > 0)
        {
            if (moves.Count >= MaxMoves)
            {
                return null;
            }

            var movable = pending.Where(TargetIsFree).Select(id => (uint?)id).FirstOrDefault();
            if (movable != null)
            {
                var id = movable.Value;
                Lift(id);
                stash.Block(target[id], shapes[id]);
                inStash[id] = target[id];
                pending.Remove(id);
                moves.Add(new Move(id, shapes[id], Destination.MuleStash, target[id]));
                continue;
            }

            var blocker = pending
                .Where(id => inStash.ContainsKey(id))
                .Where(id => pending.Any(other => other != id && Overlaps(inStash[id], shapes[id], target[other], shapes[other])))
                .OrderBy(id => shapes[id].Area)
                .Select(id => (uint?)id)
                .FirstOrDefault();
            if (blocker == null)
            {
                return null;
            }

            var parkingSpot = scratch.FirstFit(shapes[blocker.Value]);
            if (parkingSpot == null)
            {
                return null;
            }
            Lift(blocker.Value);
            scratch.Block(parkingSpot.Value, shapes[blocker.Value]);
            parked[blocker.Value] = parkingSpot.Value;
            moves.Add(new Move(blocker.Value, shapes[blocker.Value], Destination.MuleInventory, parkingSpot.Value));
        }

        return moves;

        bool TargetIsFree(uint id)
        {
            if (inStash.TryGetValue(id, out var at))
            {
                stash.Free(at, shapes[id]);
                var free = stash.CanPlace(target[id], shapes[id]);
                stash.Block(at, shapes[id]);
                return free;
            }
            return stash.CanPlace(target[id], shapes[id]);
        }

        void Lift(uint id)
        {
            if (inStash.TryGetValue(id, out var at))
            {
                stash.Free(at, shapes[id]);
                inStash.Remove(id);
            }
            else
            {
                scratch.Free(parked[id], shapes[id]);
                parked.Remove(id);
            }
        }
    }

    private static bool Overlaps(Cell a, Shape sa, Cell b, Shape sb)
    {
        return a.X < b.X + sb.Width && b.X < a.X + sa.Width && a.Y < b.Y + sb.Height && b.Y < a.Y + sa.Height;
    }
}
