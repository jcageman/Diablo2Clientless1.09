using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Mule.Packing;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ConsoleBot.Mule;

public sealed record TradeOffer(Item Item, Cell TradeCell);

public sealed record TradeOutcome(MoveItemResult Result, int ItemsTraded, int Rejections);

public enum TradeInitiator
{
    Receiver,
    Giver
}

/// <summary>
/// Runs one trade window between two clients we control. The receiver asks, the giver accepts, the giver fills the
/// grid, both press accept. When the server finds no room on the receiver it closes the window by itself and resends
/// every item of both players under new ids, so a refusal ends the round: nothing can be pulled back or retried in
/// the same window. The outcome then says NoSpace with one rejection.
/// </summary>
public class TradeExecutor
{
    // Measured 6 Sept 2026 over 9 rounds: requests up to 4.7 s after a completed trade were ignored, from 5.08 s answered,
    // whichever side asks.
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMilliseconds(5200);
    private const int RequestAttempts = 6;
    private static readonly TimeSpan RequestRetryAfter = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ResendSettle = TimeSpan.FromMilliseconds(1500);

    private readonly ILogger _logger;
    private DateTime _lastTradeCompleted = DateTime.MinValue;

    /// <summary>
    /// Who sends the trade request. The receiver by default; settable for measuring which side the cooldown binds.
    /// </summary>
    public TradeInitiator Initiator { get; set; } = TradeInitiator.Receiver;

    /// <summary>
    /// Wait out the measured cooldown before the first request. Off only to measure.
    /// </summary>
    public bool RespectCooldown { get; set; } = true;

    public TradeExecutor(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<TradeOutcome> Trade(Client giver, Client receiver, IReadOnlyList<TradeOffer> offer)
    {
        var giverSignals = TradeSignals.For(giver);
        var receiverSignals = TradeSignals.For(receiver);
        giverSignals.Reset();
        receiverSignals.Reset();

        var (asker, asked, askerSignals, askedSignals) = Initiator == TradeInitiator.Receiver
            ? (receiver, giver, receiverSignals, giverSignals)
            : (giver, receiver, giverSignals, receiverSignals);
        var askedInAskersGame = asker.Game.Players.FirstOrDefault(p => p.Id == asked.Game.Me.Id);
        if (askedInAskersGame == null)
        {
            _logger.LogError("{Asker} does not see {Asked} in the game", asker.Game.Me.Name, asked.Game.Me.Name);
            return new TradeOutcome(MoveItemResult.Failed, 0, 0);
        }

        // The server drops trade requests for about five seconds after a trade completes; ask once that has passed,
        // and keep asking in case the moment is off.
        var cooldownLeft = _lastTradeCompleted + RequestCooldown - DateTime.UtcNow;
        if (RespectCooldown && cooldownLeft > TimeSpan.Zero)
        {
            await Task.Delay(cooldownLeft);
        }
        var requested = false;
        for (var attempt = 0; attempt < RequestAttempts && !requested; attempt++)
        {
            asker.Game.InteractWithPlayer(askedInAskersGame);
            requested = await WaitFor(() => askedSignals.Has(ButtonAction.APlayerWantsToTrade), RequestRetryAfter);
            if (!requested)
            {
                _logger.LogInformation("{Asked} has not seen the trade request from {Asker} yet, asking again", asked.Game.Me.Name, asker.Game.Me.Name);
            }
        }
        if (!requested)
        {
            _logger.LogError("{Asked} never saw the trade request from {Asker}", asked.Game.Me.Name, asker.Game.Me.Name);
            return new TradeOutcome(MoveItemResult.Failed, 0, 0);
        }

        asked.Game.ClickButton(ClickType.AcceptTradeRequest);
        if (!await WaitFor(() => askedSignals.TradeAccepted || askerSignals.TradeAccepted, HandshakeTimeout))
        {
            _logger.LogError("Trade window between {Giver} and {Receiver} did not open", giver.Game.Me.Name, receiver.Game.Me.Name);
            return new TradeOutcome(MoveItemResult.Failed, 0, 0);
        }

        // The window-open acknowledgement arrives within 40 ms; a short pause keeps the first item move behind it.
        await Task.Delay(150);

        var placed = 0;
        foreach (var entry in offer)
        {
            var moved = await PlaceOnTradeGrid(giver, entry);
            if (moved == MoveItemResult.Failed)
            {
                await CancelTrade(giver, receiver);
                return new TradeOutcome(MoveItemResult.Failed, 0, 0);
            }
            if (moved == MoveItemResult.Succes)
            {
                placed++;
            }
        }

        if (placed == 0)
        {
            await CancelTrade(giver, receiver);
            return new TradeOutcome(MoveItemResult.NoSpace, 0, 0);
        }

        giverSignals.Reset();
        receiverSignals.Reset();
        giver.Game.ClickButton(ClickType.PressAcceptButton);
        receiver.Game.ClickButton(ClickType.PressAcceptButton);

        var answered = await WaitFor(() => Traded(giverSignals, receiverSignals) || Refused(giverSignals, receiverSignals), AcceptTimeout);
        if (Traded(giverSignals, receiverSignals))
        {
            _lastTradeCompleted = DateTime.UtcNow;
            return new TradeOutcome(MoveItemResult.Succes, placed, 0);
        }

        if (Refused(giverSignals, receiverSignals))
        {
            _logger.LogWarning("Server refused the offer to {Receiver} ({Count} items: {Shapes}); the simulation expected it to fit. Receiver inventory:\n{Inventory}",
                receiver.Game.Me.Name, placed, string.Join(",", offer.Select(o => $"{o.Item.Width}x{o.Item.Height}")), receiver.Game.Inventory);
            await Task.Delay(ResendSettle);
            return new TradeOutcome(MoveItemResult.NoSpace, 0, 1);
        }

        _logger.LogError("Trade between {Giver} and {Receiver} got no answer to accept; giver saw {GiverActions}, receiver saw {ReceiverActions}",
            giver.Game.Me.Name, receiver.Game.Me.Name, giverSignals.Describe(), receiverSignals.Describe());
        await CancelTrade(giver, receiver);
        return new TradeOutcome(MoveItemResult.Failed, 0, 0);
    }

    private static bool Traded(TradeSignals giver, TradeSignals receiver)
    {
        return giver.Has(ButtonAction.YouHaveTradedSomeItems) || receiver.Has(ButtonAction.YouHaveTradedSomeItems);
    }

    private static bool Refused(TradeSignals giver, TradeSignals receiver)
    {
        return giver.Has(ButtonAction.YouDontHaveRoomToAcceptTheItems) || receiver.Has(ButtonAction.YouDontHaveRoomToAcceptTheItems)
            || giver.Has(ButtonAction.TheOtherPlayerDoesNotHaveRoom) || receiver.Has(ButtonAction.TheOtherPlayerDoesNotHaveRoom);
    }

    private async Task<MoveItemResult> PlaceOnTradeGrid(Client giver, TradeOffer entry)
    {
        var item = giver.Game.Inventory.FindItemById(entry.Item.Id);
        if (item == null)
        {
            _logger.LogWarning("{Giver}: item {ItemId} {ItemName} is no longer in the inventory, skipping it", giver.Game.Me.Name, entry.Item.Id, entry.Item.Name);
            return MoveItemResult.NoSpace;
        }

        giver.Game.RemoveItemFromContainer(item);
        if (!GeneralHelpers.TryWithTimeout((_) => giver.Game.CursorItem?.Id == item.Id, MoveTimeout))
        {
            _logger.LogError("{Giver}: moving item {ItemId} {ItemName} to the cursor failed", giver.Game.Me.Name, item.Id, item.Name);
            return MoveItemResult.Failed;
        }

        giver.Game.InsertItemIntoContainer(item, new Point((ushort)entry.TradeCell.X, (ushort)entry.TradeCell.Y), ItemContainer.Trade);
        Item onGrid = null;
        if (!GeneralHelpers.TryWithTimeout(
            (_) => giver.Game.CursorItem == null && giver.Game.Items.TryGetValue(item.Id, out onGrid) && onGrid.Container == ContainerType.ForTrade,
            MoveTimeout))
        {
            _logger.LogError("{Giver}: moving item {ItemId} {ItemName} to the trade grid failed", giver.Game.Me.Name, item.Id, item.Name);
            return MoveItemResult.Failed;
        }

        return MoveItemResult.Succes;
    }

    /// <summary>
    /// Closing the window makes the server hand every item back and resend both players' items under new ids.
    /// </summary>
    private static async Task CancelTrade(Client giver, Client receiver)
    {
        giver.Game.ClickButton(ClickType.CancelTrade);
        receiver.Game.ClickButton(ClickType.CancelTrade);
        await Task.Delay(ResendSettle);
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>
    /// The trade-related packets a client has seen since the last reset. Registered once per client, because packet
    /// handlers cannot be removed again.
    /// </summary>
    private sealed class TradeSignals
    {
        private static readonly ConditionalWeakTable<Client, TradeSignals> Registry = new();

        private readonly ConcurrentQueue<ButtonAction> _actions = new();
        private volatile bool _tradeAccepted;

        public static TradeSignals For(Client client)
        {
            return Registry.GetValue(client, c =>
            {
                var signals = new TradeSignals();
                c.OnReceivedPacketEvent(InComingPacket.ButtonAction, packet => signals._actions.Enqueue(new ButtonActionPacket(packet).Action));
                c.OnReceivedPacketEvent(InComingPacket.TradeAccepted, _ => signals._tradeAccepted = true);
                return signals;
            });
        }

        public bool TradeAccepted => _tradeAccepted;

        public bool Has(ButtonAction action) => _actions.Contains(action);

        public string Describe() => string.Join(",", _actions);

        public void Reset()
        {
            _actions.Clear();
            _tradeAccepted = false;
        }
    }
}
