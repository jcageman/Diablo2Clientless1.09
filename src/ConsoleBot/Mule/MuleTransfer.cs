using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS.Items;
using D2NG.Mule;
using D2NG.Mule.Packing;
using Destination = D2NG.Mule.Packing.Destination;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Mule;

public enum TransferEnding
{
    GiverEmpty,
    ReceiverFull,
    GiverInventoryBlocks,
    Stuck,
    Failed
}

public sealed record TransferResult(TransferEnding Ending, int Rounds, int ItemsMoved, int CellsMoved, int Rejections);

/// <summary>
/// Moves everything the giver offers to the receiver, round after round, until the giver has nothing left for this
/// receiver or the receiver has no room. Each round is planned against the live grids, the receiver stashes what
/// arrived before the next one, and the receiver's stash is repacked once per visit when that frees a spot.
/// Item ids change whenever a trade ends, so <paramref name="offeredByGiver"/> must read the containers, not a cache.
/// </summary>
public class MuleTransfer
{
    // The accept answer and every item packet of a completed trade arrive within 40 ms (packet log, 6 Sept 2026).
    private static readonly TimeSpan SettleAfterTrade = TimeSpan.FromMilliseconds(300);

    private readonly ILogger _logger;
    private readonly TradeExecutor _trades;
    private readonly StashRepacker _repacker;

    public MuleTransfer(ILogger logger)
    {
        _logger = logger;
        _trades = new TradeExecutor(logger);
        _repacker = new StashRepacker(logger);
    }

    public TradeExecutor Trades => _trades;

    public async Task<TransferResult> Run(Client giver, Client receiver, Func<List<Item>> offeredByGiver)
    {
        var rounds = 0;
        var items = 0;
        var cells = 0;
        var rejections = 0;
        var repackTried = false;
        var giverName = giver.Game.Me.Name;
        var receiverName = receiver.Game.Me.Name;

        DropStarterScrolls(receiver);
        DropStarterScrolls(giver);

        while (true)
        {
            if (StashReceiverInventory(receiver) == MoveItemResult.Failed)
            {
                return new TransferResult(TransferEnding.Failed, rounds, items, cells, rejections);
            }

            var offered = offeredByGiver();
            if (offered.Count == 0)
            {
                return new TransferResult(TransferEnding.GiverEmpty, rounds, items, cells, rejections);
            }

            var receiverStash = MuleGrids.Stash(receiver.Game);
            var receiverInventory = MuleGrids.Inventory(receiver.Game);
            var round = TradePlanner.Plan(MuleGrids.Candidates(offered), MuleGrids.Inventory(giver.Game), receiverInventory, receiverStash);

            // The stash is short when something has to stay in the inventory, found no room at all, or cannot even
            // enter the inventory because residents fill it. The repack only pays off when more of what we want in the
            // stash, offered items and residents alike, fits afterwards.
            var residents = receiver.Game.Inventory.Items.Where(i => D2NG.Pickit.Pickit.CanTouchInventoryItem(receiver.Game, i)).ToList();
            var stashShort = round.Skipped.Any(s => s.Reason is SkipReason.NoRoomOnMule or SkipReason.ServerRefuses)
                || round.Items.Any(i => i.Destination == Destination.MuleInventory);
            if (!repackTried && stashShort && receiverStash.FreeCells > 0)
            {
                repackTried = true;
                var wanted = round.Items.Select(i => i.Item.Shape)
                    .Concat(round.Skipped.Where(s => s.Reason is SkipReason.NoRoomOnMule or SkipReason.ServerRefuses).Select(s => s.Item.Shape))
                    .Concat(residents.Select(MuleGrids.ShapeOf))
                    .ToList();
                var plan = RepackPlanner.Plan(MuleGrids.StashItems(receiver.Game), MuleGrids.StashSize, receiverInventory, wanted);
                if (plan == null)
                {
                    _logger.LogInformation("{Receiver}: stash has {Free} free cells but no rearrangement fits more of {Wanted}",
                        receiverName, receiverStash.FreeCells, Summarize(wanted));
                }
                else
                {
                    _logger.LogInformation("{Receiver}: repacking stash in {Moves} moves so {After} instead of {Before} of {Wanted} fit",
                        receiverName, plan.Moves.Count, plan.WantedFittingAfter, plan.WantedFittingBefore, Summarize(wanted));
                    if (_repacker.Execute(receiver.Game, plan) == MoveItemResult.Failed)
                    {
                        return new TransferResult(TransferEnding.Failed, rounds, items, cells, rejections);
                    }
                    continue;
                }
            }

            if (round.IsEmpty)
            {
                var reasons = string.Join(", ", round.Skipped.GroupBy(s => s.Reason).Select(g => $"{g.Key}:{g.Count()}"));
                var ending = round.MuleIsFull ? TransferEnding.ReceiverFull
                    : round.FarmerInventoryBlocks ? TransferEnding.GiverInventoryBlocks
                    : TransferEnding.Stuck;
                _logger.LogInformation("{Giver} -> {Receiver}: nothing more to offer ({Ending}; {Reasons}); receiver has {StashFree} stash and {InventoryFree} inventory cells free",
                    giverName, receiverName, ending, reasons, receiverStash.FreeCells, receiverInventory.FreeCells);
                return new TransferResult(ending, rounds, items, cells, rejections);
            }

            var fromStash = round.Items
                .Where(i => i.Item.Origin == Origin.FarmerStash)
                .Select(i => giver.Game.Stash.FindItemById(i.Item.Id))
                .Where(i => i != null)
                .ToList();
            if (fromStash.Count > 0)
            {
                var pulled = InventoryHelpers.MoveStashItemsToInventory(giver.Game, fromStash);
                InventoryHelpers.CleanupCursorItem(giver.Game);
                if (pulled == MoveItemResult.Failed)
                {
                    return new TransferResult(TransferEnding.Failed, rounds, items, cells, rejections);
                }
            }

            var offer = round.Items
                .Select(i => (Planned: i, Item: giver.Game.Inventory.FindItemById(i.Item.Id)))
                .Where(x => x.Item != null)
                .Select(x => new TradeOffer(x.Item, x.Planned.TradeCell))
                .ToList();
            if (offer.Count == 0)
            {
                _logger.LogWarning("{Giver}: none of the {Planned} planned items reached the inventory", giverName, round.Items.Count);
                return new TransferResult(TransferEnding.Stuck, rounds, items, cells, rejections);
            }

            var outcome = await _trades.Trade(giver, receiver, offer);
            rejections += outcome.Rejections;
            _logger.LogInformation("{Giver} -> {Receiver} round {Round}: planned {Planned} items/{Cells} cells ({ToStash} to stash, {ToInventory} staying in inventory), traded {Traded}, refused {Refused} times, {Skipped} left for later: {Result}",
                giverName, receiverName, rounds + 1, round.Items.Count, round.Cells,
                round.Items.Count(i => i.Destination == Destination.MuleStash), round.Items.Count(i => i.Destination == Destination.MuleInventory),
                outcome.ItemsTraded, outcome.Rejections, round.Skipped.Count, outcome.Result);

            if (outcome.Result == MoveItemResult.Failed)
            {
                return new TransferResult(TransferEnding.Failed, rounds, items, cells, rejections);
            }
            if (outcome.ItemsTraded == 0)
            {
                // A refusal contradicts the simulated room check. Keep the receiver's grids and the offer as a fixture so
                // the case can be replayed, then leave this receiver alone for the visit rather than loop on it.
                SaveRefusalFixture(receiver, offer);
                StashReceiverInventory(receiver);
                return new TransferResult(TransferEnding.ReceiverFull, rounds, items, cells, rejections);
            }

            rounds++;
            items += outcome.ItemsTraded;
            cells += offer.Take(outcome.ItemsTraded).Sum(o => o.Item.Width * o.Item.Height);
            await Task.Delay(SettleAfterTrade);
        }
    }

    /// <summary>
    /// Directory for fixtures written when the server refuses an offer the simulation accepted. Relative paths resolve
    /// against the working directory.
    /// </summary>
    public string RefusalFixtureDirectory { get; set; } = "mule-refusals";

    private void SaveRefusalFixture(Client receiver, List<TradeOffer> offer)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var fixture = MuleFixture.From(receiver.Game, i => D2NG.Pickit.Pickit.CanTouchInventoryItem(receiver.Game, i));
            var path = System.IO.Path.Combine(RefusalFixtureDirectory, $"{stamp}-{receiver.Game.Me.Name}.json");
            fixture.Save(path);
            System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(path, ".offer.txt"),
                string.Join(Environment.NewLine, offer.Select(o => $"{o.Item.Id} {o.Item.Name} {o.Item.Width}x{o.Item.Height} at {o.TradeCell}")));
            _logger.LogWarning("Refusal fixture written to {Path}", path);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not write the refusal fixture");
        }
    }

    /// <summary>
    /// A fresh character carries a scroll of town portal and a scroll of identify in its inventory. On a mule those two
    /// cells are worth more than the scrolls, so they are dropped. Nothing else is ever dropped.
    /// </summary>
    private void DropStarterScrolls(Client client)
    {
        var scrolls = client.Game.Inventory.Items
            .Where(i => i.Name is ItemName.ScrollofTownPortal or ItemName.ScrollofIdentify)
            .ToList();
        foreach (var scroll in scrolls)
        {
            if (InventoryHelpers.DropItemFromInventory(client.Game, scroll) == MoveItemResult.Succes)
            {
                _logger.LogInformation("{Name}: dropped starter {Scroll}", client.Game.Me.Name, scroll.Name);
            }
        }
    }

    private static string Summarize(IEnumerable<Shape> shapes)
    {
        return string.Join(",", shapes.GroupBy(s => s).OrderByDescending(g => g.Key.Area).Select(g => $"{g.Count()}x{g.Key}"));
    }

    /// <summary>
    /// The receiver puts whatever it can carry into its stash, largest items first so the stash stays compact.
    /// </summary>
    private static MoveItemResult StashReceiverInventory(Client receiver)
    {
        var movable = receiver.Game.Inventory.Items.Where(i => D2NG.Pickit.Pickit.CanTouchInventoryItem(receiver.Game, i)).ToList();
        if (movable.Count == 0)
        {
            return MoveItemResult.Succes;
        }
        var ordered = StashPacker.LargestFirst(movable, MuleGrids.ShapeOf).ToList();
        return InventoryHelpers.StashItemsAndGold(receiver.Game, ordered, 0);
    }
}
