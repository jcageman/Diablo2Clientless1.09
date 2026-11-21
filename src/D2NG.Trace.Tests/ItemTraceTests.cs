using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

public class ItemTraceTests
{
    private const uint Self = 1;
    private const uint Npc = 9;
    private const byte GoldOnPerson = 0x0E;
    private const byte GoldInStash = 0x0F;

    /// <summary>The items a character joins with: a cube and a tome in the inventory, a potion in the belt, an amulet and a sash worn, a ring and an amulet in the cube; and its gold, which the realm sends only when there is some.</summary>
    private static HandTrace Joined(string label = "client1", uint gold = 1000)
    {
        var trace = new HandTrace(label, "party1", Game1.Start).Join(Self)
            .Received(0.10, ItemPackets.Item(Action.PutInContainer, 0x10, "box", ContainerType.Inventory, 0, 0))
            .Received(0.11, ItemPackets.Item(Action.PutInContainer, 0x11, "tbk", ContainerType.Inventory, 2, 0))
            .Received(0.12, ItemPackets.Owned(Action.Equip, 0x14, "lbl", Self, ContainerType.Unspecified, slot: DirectoryType.Belt))
            .Received(0.13, ItemPackets.Owned(Action.Equip, 0x13, "amu", Self, ContainerType.Unspecified, slot: DirectoryType.Amulet))
            .Received(0.14, ItemPackets.Belt(Action.PutInBelt, 0x12, 0, 0))
            .Received(0.15, ItemPackets.Item(Action.PutInContainer, 0x15, "rin", ContainerType.Cube, 0, 0))
            .Received(0.16, ItemPackets.Item(Action.PutInContainer, 0x16, "amu", ContainerType.Cube, 1, 0));
        return gold == 0 ? trace : trace.Received(0.17, ItemPackets.Gold(GoldOnPerson, gold));
    }

    /// <summary>A town visit: a pick up, a stash, a drink, a sale, a gamble and its identify, a transmute, an unanswered pick up, a ring worn and taken off, and a trade.</summary>
    private static string TownGame(TempRoot root)
    {
        var folder = Game1.Folder(root);
        Joined()
            .Received(3.0, ItemPackets.OnGround(0x20, "rin"))
            .Sent(3.1, Packets.Pickup(0x20))
            .Received(3.2, ItemPackets.Removed(0x20))
            .Received(3.201, ItemPackets.Item(Action.PutInContainer, 0x20, "rin", ContainerType.Inventory, 4, 0))
            .Sent(4.0, ItemPackets.Lift(0x20))
            .Add(TraceRecordKind.ClientAction, 4.0, ItemPackets.ClientDid(ClientAction.Lift, 0x20))
            .Received(4.1, ItemPackets.Owned(Action.RemoveFromContainer, 0x20, "rin", Self, ContainerType.Inventory, 4, 0))
            .Sent(4.2, ItemPackets.Put(0x20, 1, 0, 4))
            .Received(4.3, ItemPackets.Item(Action.PutInContainer, 0x20, "rin", ContainerType.Stash, 1, 0))
            .Sent(5.0, ItemPackets.UseBelt(0x12))
            .Add(TraceRecordKind.ClientAction, 5.0, ItemPackets.ClientDid(ClientAction.Use, 0x12))
            .Received(5.1, ItemPackets.Used(0x12))
            .Received(5.101, ItemPackets.Belt(Action.RemoveFromBelt, 0x12, 0, 0))
            .Sent(6.0, ItemPackets.Sell(Npc, 0x11))
            .Add(TraceRecordKind.ClientAction, 6.0, ItemPackets.ClientDid(ClientAction.Sell, 0x11))
            .Received(6.1, ItemPackets.Owned(Action.RemoveFromContainer, 0x11, "tbk", Self, ContainerType.Inventory, 2, 0))
            .Received(6.101, ItemPackets.Vendor(3, 1, 0x11, 1500))
            .Received(6.5, ItemPackets.Gold(GoldInStash, 100000))
            .Received(6.8, ItemPackets.Item(Action.AddToShop, 0x30, "rin", ContainerType.Unspecified, 0, 0))
            .Sent(7.0, ItemPackets.Buy(Npc, 0x30, gamble: true))
            .Received(7.1, ItemPackets.Vendor(4, 0, 0x31, 0))
            .Received(7.101, ItemPackets.Item(Action.PutInContainer, 0x31, "rin", ContainerType.Inventory, 6, 0, identified: false))
            .Received(7.102, ItemPackets.Gold(GoldInStash, 90000))
            .Sent(7.3, ItemPackets.IdentifyGambled(0x31))
            .Received(7.4, ItemPackets.Owned(Action.UpdateStats, 0x31, "rin", Self, ContainerType.Inventory, 6, 0))
            .Sent(8.0, ItemPackets.Transmute(0x10))
            .Received(8.1, ItemPackets.Owned(Action.RemoveFromContainer, 0x15, "rin", Self, ContainerType.Cube, 0, 0))
            .Received(8.101, ItemPackets.Owned(Action.RemoveFromContainer, 0x16, "amu", Self, ContainerType.Cube, 1, 0))
            .Received(8.2, ItemPackets.Item(Action.PutInContainer, 0x40, "amu", ContainerType.Cube, 0, 0))
            .Received(9.9, ItemPackets.OnGround(0x50, "rin"))
            .Sent(10.0, Packets.Pickup(0x50))
            .Sent(11.0, ItemPackets.Lift(0x31))
            .Add(TraceRecordKind.ClientAction, 11.0, ItemPackets.ClientDid(ClientAction.Lift, 0x31))
            .Received(11.1, ItemPackets.Owned(Action.RemoveFromContainer, 0x31, "rin", Self, ContainerType.Inventory, 6, 0))
            .Sent(11.2, ItemPackets.Equip(0x31, DirectoryType.RightHandRing))
            .Received(11.3, ItemPackets.Owned(Action.Equip, 0x31, "rin", Self, ContainerType.Unspecified, slot: DirectoryType.RightHandRing))
            .Sent(12.0, ItemPackets.TakeOff(DirectoryType.RightHandRing))
            .Received(12.1, ItemPackets.Owned(Action.Unequip, 0x31, "rin", Self, ContainerType.Unspecified, slot: DirectoryType.RightHandRing))
            .Sent(13.0, ItemPackets.Put(0x31, 6, 0, 0))
            .Received(13.1, ItemPackets.Item(Action.PutInContainer, 0x31, "rin", ContainerType.Inventory, 6, 0))
            .Sent(13.9, ItemPackets.Put(0x60, 0, 0, 2))
            .Received(14.0, ItemPackets.Item(Action.PutInContainer, 0x60, "rin", ContainerType.ForTrade, 0, 0))
            .Sent(14.5, ItemPackets.AcceptTrade())
            .Received(14.6, ItemPackets.Owned(Action.RemoveFromContainer, 0x60, "rin", Self, ContainerType.ForTrade, 0, 0))
            .Ticks(16, 20, 5057, 1902)
            .WriteTo(folder);
        return folder;
    }

    private static List<ItemEvent> Events(string folder) => ItemMoves.Replay(GameQuery.Open(folder), ["client1"]).Single().Ledger.Events.ToList();

    [Fact]
    public void TheItemPacketsParseAsWritten()
    {
        var ring = new ParseItemPacket(new D2gsPacket(ItemPackets.Owned(Action.Equip, 0x31, "rin", Self, ContainerType.Unspecified, slot: DirectoryType.RightHandRing))).Item;
        var potion = new ParseItemPacket(new D2gsPacket(ItemPackets.Belt(Action.PutInBelt, 0x12, 2, 1))).Item;
        var stashed = new ParseItemPacket(new D2gsPacket(ItemPackets.Item(Action.PutInContainer, 0x20, "rin", ContainerType.Stash, 5, 3, identified: false))).Item;

        Assert.Equal((ItemName.Ring, 0x31u, Self, DirectoryType.RightHandRing, Action.Equip), (ring.Name, ring.Id, ring.PlayerId, ring.Directory, ring.Action));
        Assert.Equal((ContainerType.Belt, 2, 1), (potion.Container, (int)potion.Location.X, (int)potion.Location.Y));
        Assert.Equal((ContainerType.Stash, 5, 3, false), (stashed.Container, (int)stashed.Location.X, (int)stashed.Location.Y, stashed.IsIdentified));
    }

    [Fact]
    public void EveryMoveOfATownVisitIsAnEventWithItsRequestAndAnswer()
    {
        using var root = new TempRoot();
        var events = Events(TownGame(root));
        string Line(string kind, uint item) => ItemMoves.Format(events.Single(e => e.Kind == kind && e.Item == item), false);

        Assert.Equal("00:00.100 client1      loaded           7 items cube 2, equipped 2, inventory 2, belt 1", ItemMoves.Format(events[0], false));
        Assert.Equal("00:03.201 client1      picked up        Ring 0x20 into inventory at (4, 0) - asked 00:03.100 (pick up (0x16)), answered in 101 ms", Line(ItemKinds.PickedUp, 0x20));
        Assert.Equal("00:04.100 client1      to cursor        Ring 0x20 from inventory at (4, 0) - asked 00:04.000 (lift (0x19)), answered in 100 ms", Line(ItemKinds.ToCursor, 0x20));
        Assert.Equal("00:04.300 client1      put              Ring 0x20 in stash at (1, 0) - asked 00:04.200 (put at (1, 0) in stash (0x18)), answered in 100 ms", Line(ItemKinds.Put, 0x20));
        Assert.Contains("SuperHealingPotion 0x12 from belt slot 0, row 0 - asked 00:05.000 (use from belt (0x26))", Line(ItemKinds.Used, 0x12));
        Assert.Single(events, e => e.Item == 0x12);
        Assert.Contains("TomeOfTownPortal 0x11 +500 gold, 1,500 on the person after - asked 00:06.000 (sell (0x33))", Line(ItemKinds.Sold, 0x11));
        Assert.Contains("Ring 0x31 from listing Ring 0x30, into inventory at (6, 0), -11,500 gold - asked 00:07.000 (gamble (0x32))", Line(ItemKinds.Gambled, 0x31));
        Assert.Contains("asked 00:07.300 (identify gambled (0x37))", Line(ItemKinds.Identified, 0x31));
        Assert.Equal("00:08.200 client1      transmuted       cube Ring 0x15, Amulet 0x16 -> Amulet 0x40 - asked 00:08.000 (transmute (0x4C)), answered in 200 ms",
            ItemMoves.Format(events.Single(e => e.Kind == ItemKinds.Transmuted), false));
        Assert.Equal("00:10.000 client1      UNANSWERED       Ring 0x50 pick up (0x16): the server never answered (waited 5s)", Line(ItemKinds.Unanswered, 0x50));
        Assert.Contains("Ring 0x31 in RightHandRing - asked 00:11.200 (equip in RightHandRing (0x1A))", Line(ItemKinds.Equipped, 0x31));
        Assert.Contains("Ring 0x31 from RightHandRing to the cursor - asked 00:12.000 (take off RightHandRing (0x1C))", Line(ItemKinds.Unequipped, 0x31));
        Assert.Contains("Ring 0x60 off the own trade page as the trade went through - asked 00:14.500 (accept trade (0x4F))", Line(ItemKinds.TradedAway, 0x60));
        Assert.DoesNotContain(events, e => e.Kind is ItemKinds.Removed or ItemKinds.Received or ItemKinds.Moved);
    }

    [Fact]
    public void TheSummaryCountsWhatMovedAndTheGold()
    {
        using var root = new TempRoot();
        var summary = ItemMoves.Summarize("client1", Events(TownGame(root)));

        Assert.Equal(
            "client1: picked 1 items and 0 gold piles (+0 gold), sold 1 (+500 gold), bought 0, gambled 1 and repaired 0 times (-11,500 gold), stashed 1, cubed 0, transmuted 1, used 1, activated 0, dropped 0, equipped 1, identified 1, "
            + "traded away 1, received 0, removed by the server 0, unanswered requests 1",
            ItemMoves.Format(summary));
    }

    private static List<ItemEvent> EventsOf(string folder, string client) => ItemMoves.Replay(GameQuery.Open(folder), [client]).Single().Ledger.Events.ToList();

    /// <summary>
    /// The realm's order (a Travincal game): each pile leaves the ground (0x0A) with the pick-up sound as it is taken, and
    /// the gold attribute rises later, once for one or two piles.
    /// </summary>
    [Fact]
    public void EachPileIsPickedUpByItsOwnRemovalAndTheGoldIsWhatTheAttributeSays()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(3.0, ItemPackets.OnGround(0x70, "gld", gold: 300))
            .Received(3.0, ItemPackets.OnGround(0x71, "gld", gold: 200))
            .Received(3.0, ItemPackets.OnGround(0x72, "gld", gold: 50))
            .Received(3.0, ItemPackets.OnGround(0x73, "gld", gold: 100))
            .Sent(4.000, Packets.Pickup(0x70))
            .Received(4.125, ItemPackets.Removed(0x70))
            .Received(4.125, ItemPackets.PickUpSound(Self))
            .Sent(4.25, Packets.Pickup(0x71))
            .Received(4.375, ItemPackets.Removed(0x71))
            .Received(4.375, ItemPackets.PickUpSound(Self))
            .Sent(4.5, Packets.Pickup(0x72))
            .Received(4.625, ItemPackets.Gold(GoldOnPerson, 1500))
            .Received(4.75, ItemPackets.Removed(0x72))
            .Received(4.75, ItemPackets.PickUpSound(Self))
            .Sent(4.875, Packets.Pickup(0x73))
            .Received(5.0, ItemPackets.Removed(0x73))
            .Received(5.0, ItemPackets.PickUpSound(Self))
            .Received(5.0, ItemPackets.Gold(GoldOnPerson, 1650))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        var gold = Events(folder).Where(e => e.Name == "Gold").ToList();
        var summary = ItemMoves.Summarize("client1", gold);

        Assert.Equal(
            [
                "00:04.125 client1      picked up        Gold pile 0x70 of 300 gold, gone from the ground (0x0A) - asked 00:04.000 (pick up (0x16)), answered in 125 ms",
                "00:04.375 client1      picked up        Gold pile 0x71 of 200 gold, gone from the ground (0x0A) - asked 00:04.250 (pick up (0x16)), answered in 125 ms",
                "00:04.625 client1      gold received    Gold +500 on the person (1,500 after) for piles 0x70, 0x71 - asked 00:04.000 (pick up (0x16)), answered in 625 ms",
                "00:04.750 client1      picked up        Gold pile 0x72 of 50 gold, gone from the ground (0x0A) - asked 00:04.500 (pick up (0x16)), answered in 250 ms",
                "00:05.000 client1      picked up        Gold pile 0x73 of 100 gold, gone from the ground (0x0A) - asked 00:04.875 (pick up (0x16)), answered in 125 ms",
                "00:05.000 client1      gold received    Gold +150 on the person (1,650 after) for piles 0x72, 0x73 - asked 00:04.500 (pick up (0x16)), answered in 500 ms",
            ],
            gold.Select(e => ItemMoves.Format(e, false)));
        Assert.Equal((4, 650L, 650L, 0L), (summary.GoldPiles, summary.GoldPicked, summary.PileGold, summary.GoldUnasked));
        Assert.StartsWith("client1: picked 0 items and 4 gold piles (+650 gold), sold", ItemMoves.Format(summary));
    }

    /// <summary>
    /// A party game: every member sees the pile leave the ground, only the one who took it hears the pick-up sound, and
    /// the realm hands each member a share - by the gold attribute when it is large, by a gold add (0x19) when small.
    /// </summary>
    [Fact]
    public void InAPartyEachClientIsCreditedItsOwnShareAndOnlyThePickerPickedThePileUp()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        HandTrace Party(HandTrace trace) => trace
            .Received(3.0, ItemPackets.OnGround(0x74, "gld", gold: 1940))
            .Received(3.0, ItemPackets.OnGround(0x75, "gld", gold: 229));
        Party(Joined())
            .Sent(4.75, Packets.Pickup(0x74))
            .Received(9.5, ItemPackets.Removed(0x74))
            .Received(9.5, ItemPackets.Gold(GoldOnPerson, 1485))
            .Sent(10.75, Packets.Pickup(0x75))
            .Received(10.875, ItemPackets.Removed(0x75))
            .Received(10.875, ItemPackets.PickUpSound(Self))
            .Received(11.25, ItemPackets.GoldAdded(58))
            .Ticks(14, 15, 5057, 1902)
            .WriteTo(folder);
        Party(new HandTrace("client2", "party1", Game1.Start).Join(2).Received(0.17, ItemPackets.Gold(GoldOnPerson, 5000)))
            .Sent(9.375, Packets.Pickup(0x74))
            .Received(9.5, ItemPackets.Removed(0x74))
            .Received(9.5, ItemPackets.PickUpSound(2))
            .Received(9.5, ItemPackets.Gold(GoldOnPerson, 5485))
            .Received(10.875, ItemPackets.Removed(0x75))
            .Received(11.375, ItemPackets.GoldAdded(58))
            .Ticks(14, 15, 5057, 1902)
            .WriteTo(folder);

        var first = EventsOf(folder, "client1").Where(e => e.Name == "Gold").Select(e => ItemMoves.Format(e, false)).ToList();
        var second = EventsOf(folder, "client2").Where(e => e.Name == "Gold").Select(e => ItemMoves.Format(e, false)).ToList();

        Assert.Equal(
            [
                "00:09.500 client1      refused          Gold pick up (0x16): the pile of 1,940 left the ground (0x0A) without the pick-up sound - another player took it - asked 00:04.750 (pick up (0x16)), answered in 4750 ms",
                "00:09.500 client1      gold received    Gold +485 on the person (1,485 after): a share of pile 0x74 (1,940 on the ground) another player picked up - unasked",
                "00:10.875 client1      picked up        Gold pile 0x75 of 229 gold, gone from the ground (0x0A) - asked 00:10.750 (pick up (0x16)), answered in 125 ms",
                "00:11.250 client1      gold received    Gold +58 on the person (1,543 after) for pile 0x75, which held 229 on the ground - asked 00:10.750 (pick up (0x16)), answered in 500 ms",
            ],
            first);
        Assert.Equal(
            [
                "00:09.500 client2      picked up        Gold pile 0x74 of 1,940 gold, gone from the ground (0x0A) - asked 00:09.375 (pick up (0x16)), answered in 125 ms",
                "00:09.500 client2      gold received    Gold +485 on the person (5,485 after) for pile 0x74, which held 1,940 on the ground - asked 00:09.375 (pick up (0x16)), answered in 125 ms",
                "00:11.375 client2      gold received    Gold +58 on the person (5,543 after): a share of pile 0x75 (229 on the ground) another player picked up - unasked",
            ],
            second);
        Assert.StartsWith("client1: picked 0 items and 1 gold piles (+58 gold, though the piles held 229), got +485 gold from piles it did not pick up, sold",
            ItemMoves.Format(ItemMoves.Summarize("client1", EventsOf(folder, "client1"))));
    }

    [Fact]
    public void RepairingEverythingIsAnsweredWithoutAnItemAndCostsGoldWhileARefusedBuyIsRefused()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Sent(3.0, ItemPackets.RepairAll(Npc))
            .Received(3.2, ItemPackets.StatUpdated(0x13, 0x48, 18))
            .Received(3.2, ItemPackets.StatUpdated(0x14, 0x48, 12))
            .Received(3.2, ItemPackets.Vendor(1, 2, 0xFFFFFFFF, 860))
            .Received(3.2, ItemPackets.Gold(GoldOnPerson, 860))
            .Received(3.8, ItemPackets.Item(Action.AddToShop, 0x30, "rin", ContainerType.Unspecified, 0, 0))
            .Sent(4.0, ItemPackets.Buy(Npc, 0x30, gamble: false))
            .Received(4.023, ItemPackets.Vendor(0, 7, 0x30, 860))
            .Received(4.183, ItemPackets.Gold(GoldOnPerson, 860))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        var events = Events(folder);

        Assert.Equal("00:03.200 client1      repaired         all items 2 items updated (0x3E), -140 gold - asked 00:03.000 (repair (0x35)), answered in 200 ms",
            ItemMoves.Format(events.Single(e => e.Kind == ItemKinds.Repaired), false));
        Assert.Equal("00:04.023 client1      refused          Ring 0x30 the vendor refused it (transaction 0, result 7) - asked 00:04.000 (buy (0x32)), answered in 23 ms",
            ItemMoves.Format(events.Single(e => e.Kind == ItemKinds.Refused), false));
        Assert.Contains("repaired 1 times (-140 gold)", ItemMoves.Format(ItemMoves.Summarize("client1", events)));
        Assert.DoesNotContain(events, e => e.Kind == ItemKinds.Unanswered || e.Detail.Contains("price unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void APotionSentToTheBeltIsOnePutFromTheInventoryAndTheBeltEmptyingIntoTheInventoryIsAMove()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(0.18, ItemPackets.Item(Action.PutInContainer, 0x21, "hp5", ContainerType.Inventory, 9, 7))
            .Sent(3.0, ItemPackets.ToBelt(0x21))
            .Received(3.244, ItemPackets.Owned(Action.RemoveFromContainer, 0x21, "hp5", Self, ContainerType.Inventory, 9, 7))
            .Received(3.244, ItemPackets.Belt(Action.PutInBelt, 0x21, 1, 0))
            .Received(5.0, ItemPackets.Belt(Action.RemoveFromBelt, 0x12, 0, 0))
            .Received(5.0, ItemPackets.Item(Action.PutInContainer, 0x12, "hp5", ContainerType.Inventory, 2, 3))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        var events = Events(folder).Where(e => e.Kind != ItemKinds.Loaded).Select(e => ItemMoves.Format(e, false)).ToList();

        Assert.Equal(
            [
                "00:03.244 client1      put              SuperHealingPotion 0x21 in belt slot 1, row 0, from inventory at (9, 7) - asked 00:03.000 (to belt (0x63)), answered in 244 ms",
                "00:05.000 client1      moved by server  SuperHealingPotion 0x12 from belt slot 0, row 0 to inventory at (2, 3) - unasked",
            ],
            events);
    }

    /// <summary>Opening the cube or a tome gets a 0x3F each time, asked or not, and the item stays to be lifted, put and sold.</summary>
    [Fact]
    public void TheCubeAndATomeAreActivatedNotUsedUpAndStayToBeLiftedAndSold()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Sent(5.0, ItemPackets.Use(0x10))
            .Received(5.1, ItemPackets.Used(0x10))
            .Sent(5.3, ItemPackets.Lift(0x10))
            .Add(TraceRecordKind.ClientAction, 5.3, ItemPackets.ClientDid(ClientAction.Lift, 0x10))
            .Received(5.319, ItemPackets.Owned(Action.RemoveFromContainer, 0x10, "box", Self, ContainerType.Inventory, 0, 0))
            .Sent(5.4, ItemPackets.Put(0x10, 0, 0, 0))
            .Received(5.44, ItemPackets.Item(Action.PutInContainer, 0x10, "box", ContainerType.Inventory, 0, 0))
            .Received(6.0, ItemPackets.Used(0x11))
            .Sent(8.0, ItemPackets.Sell(Npc, 0x11))
            .Add(TraceRecordKind.ClientAction, 8.0, ItemPackets.ClientDid(ClientAction.Sell, 0x11))
            .Received(8.1, ItemPackets.Owned(Action.RemoveFromContainer, 0x11, "tbk", Self, ContainerType.Inventory, 2, 0))
            .Received(8.101, ItemPackets.Vendor(3, 1, 0x11, 1500))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        var (_, ledger, check) = ItemMoves.Replay(GameQuery.Open(folder), ["client1"]).Single();
        var events = ledger.Events.Where(e => e.Kind != ItemKinds.Loaded).Select(e => ItemMoves.Format(e, false)).ToList();

        Assert.Equal(
            [
                "00:05.100 client1      activated        HoradricCube 0x10 in inventory at (0, 0) - asked 00:05.000 (use (0x20)), answered in 100 ms",
                "00:05.319 client1      to cursor        HoradricCube 0x10 from inventory at (0, 0) - asked 00:05.300 (lift (0x19)), answered in 19 ms",
                "00:05.440 client1      put              HoradricCube 0x10 in inventory at (0, 0) - asked 00:05.400 (put at (0, 0) in inventory (0x18)), answered in 40 ms",
                "00:06.000 client1      activated        TomeOfTownPortal 0x11 in inventory at (2, 0) - unasked",
                "00:08.101 client1      sold             TomeOfTownPortal 0x11 +500 gold, 1,500 on the person after - asked 00:08.000 (sell (0x33)), answered in 101 ms",
            ],
            events);
        Assert.Contains("used 0, activated 2,", ItemMoves.Format(ItemMoves.Summarize("client1", ledger.Events)));
        Assert.Empty(check.Found);
    }

    /// <summary>The bot transmutes by clicking the cube's button (0x4F 0x18); the server takes the inputs out unasked.</summary>
    [Fact]
    public void ATransmuteByTheCubesButtonTakesItsInputsWithoutAnIncident()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Sent(5.0, [0x4F, ItemLedger.TransmuteButton, 0, 0, 0, 0, 0])
            .Received(5.25, ItemPackets.Owned(Action.RemoveFromContainer, 0x15, "rin", Self, ContainerType.Cube, 0, 0))
            .Received(5.25, ItemPackets.Owned(Action.RemoveFromContainer, 0x16, "amu", Self, ContainerType.Cube, 1, 0))
            .Received(5.25, ItemPackets.Item(Action.PutInContainer, 0x40, "amu", ContainerType.Cube, 0, 0))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        var (_, ledger, check) = ItemMoves.Replay(GameQuery.Open(folder), ["client1"]).Single();

        Assert.Equal("00:05.250 client1      transmuted       cube Ring 0x15, Amulet 0x16 -> Amulet 0x40 - asked 00:05.000 (transmute (0x4F 0x18)), answered in 250 ms",
            ItemMoves.Format(ledger.Events.Single(e => e.Kind != ItemKinds.Loaded), false));
        Assert.Empty(check.Found);
    }

    [Fact]
    public void ARequestSentAgainLateIsAnsweredWithinTheWindowOfItsLastSend()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(3.0, ItemPackets.OnGround(0x50, "rvl"))
            .Sent(3.845, Packets.Pickup(0x50))
            .Sent(8.624, Packets.Pickup(0x50))
            .Received(8.958, ItemPackets.Removed(0x50))
            .Received(8.958, ItemPackets.Item(Action.PutInContainer, 0x50, "rvl", ContainerType.Inventory, 9, 2))
            .Ticks(15, 16, 5057, 1902)
            .WriteTo(folder);

        var events = Events(folder);

        Assert.Equal("00:08.958 client1      picked up        FullRejuvenationPotion 0x50 into inventory at (9, 2) - asked 00:03.845 (pick up (0x16), sent 2 times), answered in 5113 ms",
            ItemMoves.Format(events.Single(e => e.Item == 0x50), false));
    }

    [Fact]
    public void ARequestRepeatedAfterTheServerAnsweredItIsARepeatNotAnUnansweredRequest()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(0.18, ItemPackets.Item(Action.PutInContainer, 0x31, "rin", ContainerType.Inventory, 6, 0, identified: false))
            .Sent(3.0, ItemPackets.IdentifyGambled(0x31))
            .Received(3.1, ItemPackets.Owned(Action.UpdateStats, 0x31, "rin", Self, ContainerType.Inventory, 6, 0))
            .Sent(3.125, ItemPackets.IdentifyGambled(0x31))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        var events = Events(folder);

        Assert.Equal("00:03.125 client1      repeat           Ring 0x31 identify gambled (0x37): sent again after the server had answered the same request at 00:03.100; it answers once",
            ItemMoves.Format(events.Single(e => e.Kind == ItemKinds.Repeat), false));
        Assert.Equal(0, ItemMoves.Summarize("client1", events).Unanswered);
    }

    [Fact]
    public void AnEmptyQuiverTheServerTakesOffToRefillTheSlotIsUsedUpAndTheRefillIsAMove()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(0.18, ItemPackets.Owned(Action.Equip, 0x94, "aqv", Self, ContainerType.Unspecified, slot: DirectoryType.LeftHand))
            .Received(0.19, ItemPackets.Item(Action.PutInContainer, 0x95, "aqv", ContainerType.Inventory, 3, 5))
            .Received(5.833, ItemPackets.Owned(Action.Unequip, 0x94, "aqv", Self, ContainerType.Unspecified, slot: DirectoryType.LeftHand))
            .Received(5.834, ItemPackets.Owned(Action.RefillFromInventory, 0x95, "aqv", Self, ContainerType.Unspecified, slot: DirectoryType.LeftHand))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        var (_, ledger, check) = ItemMoves.Replay(GameQuery.Open(folder), ["client1"]).Single();
        var events = ledger.Events.Where(e => e.Kind != ItemKinds.Loaded).Select(e => ItemMoves.Format(e, false)).ToList();

        Assert.Equal(
            [
                "00:05.833 client1      used             Arrows 0x94 from LeftHand: used up, the server took it off to refill the slot - unasked",
                "00:05.834 client1      moved by server  Arrows 0x95 from inventory at (3, 5) to LeftHand: the server refilled the slot - unasked",
            ],
            events);
        Assert.Empty(check.Found);
    }

    [Fact]
    public void ARequirementPercentReducesStrengthAndAnUnreadableLevelIsSaidEvenWithoutOtherRequirements()
    {
        Assert.Equal(101, ItemView.Reduced(169, -40));
        Assert.Equal(169, ItemView.Reduced(169, 0));
        Assert.Contains("\"levelUnknown\"", System.Text.Json.JsonSerializer.Serialize(ItemView.Requirement(null, 0, 0)));
        Assert.Null(ItemView.Requirement(0, 0, 0));
    }

    [Fact]
    public void StateShowsTheBodySlotsTheCursorAndEachContainer()
    {
        using var root = new TempRoot();
        var query = GameQuery.Open(TownGame(root));

        var worn = StateQuery.Render(query, "client1", TimeSpan.FromSeconds(11.5), "equipped,cursor");
        var lifted = StateQuery.Render(query, "client1", TimeSpan.FromSeconds(12.5), "equipped,cursor,bag,cube,stash");

        Assert.Contains("{\"slot\":\"RightHandRing\",\"id\":\"0x31\",\"name\":\"Ring\",\"quality\":\"Normal\"}", worn);
        Assert.Contains("\"cursor\": null", worn);
        Assert.Contains("{\"slot\":\"Amulet\",\"id\":\"0x13\",\"name\":\"Amulet\",\"quality\":\"Normal\"}", lifted);
        Assert.DoesNotContain("RightHandRing", lifted);
        Assert.Contains("\"cursor\": {\"id\":\"0x31\",\"name\":\"Ring\",\"quality\":\"Normal\"", lifted);
        Assert.Contains("\"bag\": [\n    {\"id\":\"0x10\",\"name\":\"HoradricCube\"".Replace("\n", Environment.NewLine), lifted);
        Assert.Contains("\"cube\": [\n    {\"id\":\"0x40\",\"name\":\"Amulet\"".Replace("\n", Environment.NewLine), lifted);
        Assert.Contains("\"stash\": [\n    {\"id\":\"0x20\",\"name\":\"Ring\",\"quality\":\"Normal\",\"container\":\"Stash\",\"at\":[1,0]}".Replace("\n", Environment.NewLine), lifted);
    }

    /// <summary>A real magic bone shield's packet, its id replaced.</summary>
    private static readonly byte[] BoneShield = [0x9C, 0x04, 0x38, 0x07, 0x77, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x01, 0x00, 0x04, 0x22, 0x36, 0x87, 0x06, 0x82, 0xAF, 0x21, 0x6C, 0x75, 0x00, 0x31, 0x90, 0x05, 0x29, 0xF1, 0x06, 0x23, 0x90, 0x02, 0x0A, 0x0A, 0x04, 0x0C, 0x14, 0x28, 0x33, 0xB2, 0x13, 0x22, 0x29, 0x26, 0x56, 0x4C, 0xB4, 0x98, 0x00, 0x80, 0x72, 0x62, 0xF8, 0x0F];

    private static readonly System.Text.Json.JsonSerializerOptions WithoutNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public void FullItemsCarryWhatThePacketDecoded()
    {
        var shield = new ParseItemPacket(new D2gsPacket(BoneShield)).Item;
        var json = System.Text.Json.JsonSerializer.Serialize(ItemView.Details(shield, null));

        Assert.Contains("\"name\":\"BoneShield\"", json);
        Assert.Contains("\"durability\":\"40/40\"", json);
        Assert.Contains("\"defense\":31", json);
        Assert.Contains("FasterBlockRate : 30", json);
        Assert.Contains("FireResistance : 34", json);
        Assert.Contains("durability 40/40", ItemLedger.DetailLine(shield));
        Assert.Equal("{\"id\":\"0x77\",\"name\":\"BoneShield\",\"quality\":\"" + shield.Quality + "\",\"container\":\"" + shield.Container + "\",\"at\":[" + shield.Location.X + "," + shield.Location.Y + "]}",
            System.Text.Json.JsonSerializer.Serialize(ItemView.Compact(shield), WithoutNulls));
    }

    [Fact]
    public void AClientWhoseItemsFollowTheServerRaisesNoItemIncident()
    {
        using var root = new TempRoot();
        var (_, _, check) = ItemMoves.Replay(GameQuery.Open(TownGame(root)), ["client1"]).Single();

        Assert.Empty(check.Found);
        Assert.True(check.Checked >= 10, $"only {check.Checked} verdicts");
    }

    private static List<Incident> Check(TempRoot root, Func<HandTrace, HandTrace> events)
    {
        var folder = Game1.Folder(root);
        events(Joined()).Ticks(20, 21, 5057, 1902).WriteTo(folder);
        return [.. ItemMoves.Replay(GameQuery.Open(folder), ["client1"]).Single().Check.Found];
    }

    [Fact]
    public void AnItemPutWhereTheClientStillHoldsAnotherIsAnOverlap()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.Item(Action.PutInContainer, 0x80, "rin", ContainerType.Inventory, 5, 5))
            .Received(6, ItemPackets.Item(Action.PutInContainer, 0x81, "amu", ContainerType.Inventory, 5, 5)));

        var overlap = Assert.Single(found);
        Assert.Equal(ItemStateCheck.Overlap, overlap.Kind);
        Assert.Equal(TimeSpan.FromSeconds(6), overlap.Offset);
        Assert.Contains("the server put Amulet 0x81 in the inventory at (5, 5), cells the client still held Ring 0x80 in at (5, 5)", overlap.Detail);
    }

    [Fact]
    public void AnItemPutOutsideTheGridOrBelowTheBeltsRowsIsReported()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.Item(Action.PutInContainer, 0x82, "lsd", ContainerType.Inventory, 9, 6))
            .Received(6, ItemPackets.Belt(Action.PutInBelt, 0x83, 0, 3)));

        found = [.. found.Where(i => i.Kind == ItemStateCheck.OutsideGrid)];
        Assert.Equal([ItemStateCheck.OutsideGrid, ItemStateCheck.OutsideGrid], found.Select(i => i.Kind));
        Assert.Contains("LongSword 0x82 (2x3) in the inventory at (9, 6), outside the 10x8 grid", found[0].Detail);
        Assert.Contains("SuperHealingPotion 0x83 (1x1) in the belt at (0, 3), outside the 4x2 grid the client keeps (rows from the belt it wears, Sash 0x14)", found[1].Detail);
    }

    [Fact]
    public void AnItemTheServerRemovedButTheClientStillHoldsIsReported()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.Item(Action.PutInContainer, 0x84, "rin", ContainerType.Inventory, 3, 3))
            .Received(6, ItemPackets.Removed(0x84)));

        var held = Assert.Single(found);
        Assert.Equal(ItemStateCheck.HeldAfterRemoval, held.Kind);
        Assert.Equal("the client still holds Ring 0x84 in inventory at (3, 3); the server: removed from the game (0x0A) at 00:06.000", held.Detail);
    }

    [Fact]
    public void AnIdTheServerGivesAnotherItemWhileTheClientHoldsTheFirstIsReused()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.Item(Action.PutInContainer, 0x85, "rin", ContainerType.Inventory, 3, 3))
            .Received(6, ItemPackets.Item(Action.PutInContainer, 0x85, "amu", ContainerType.Inventory, 7, 3)));

        var reused = Assert.Single(found, i => i.Kind == ItemStateCheck.IdReused);
        Assert.Contains("the server put Amulet 0x85 in the inventory at (7, 3) under id 0x85, which the client still held as Ring 0x85 in inventory at (3, 3)", reused.Detail);
    }

    [Fact]
    public void ACursorItemTheServerPutOnTheBodyIsNotGiven()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.Item(Action.PutInContainer, 0x86, "rin", ContainerType.Inventory, 3, 3))
            .Sent(6, ItemPackets.Lift(0x86))
            .Add(TraceRecordKind.ClientAction, 6, ItemPackets.ClientDid(ClientAction.Lift, 0x86))
            .Received(6.1, ItemPackets.Owned(Action.RemoveFromContainer, 0x86, "rin", Self, ContainerType.Inventory, 3, 3))
            .Received(7, ItemPackets.Owned(Action.SwapBodyItem, 0x86, "rin", Self, ContainerType.Unspecified, slot: DirectoryType.LeftHandRing)));

        var cursor = Assert.Single(found);
        Assert.Equal(ItemStateCheck.CursorNotGiven, cursor.Kind);
        Assert.Equal("the client holds Ring 0x86 on its cursor; the server: worn in LeftHandRing (SwapBodyItem) at 00:07.000", cursor.Detail);
    }

    [Fact]
    public void AnItemTheServerPutOnTheCursorButTheClientDidNotIsNotTaken()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.OnGround(0x90, "rin"))
            .Sent(5.5, Packets.Pickup(0x90))
            .Received(5.6, ItemPackets.Item(Action.GroundToCursor, 0x90, "rin", ContainerType.Unspecified, 0, 0)));

        var cursor = Assert.Single(found);
        Assert.Equal(ItemStateCheck.CursorNotTaken, cursor.Kind);
        Assert.Equal("the client holds Ring 0x90 nowhere, not on its cursor; the server: to the cursor (GroundToCursor) at 00:05.600", cursor.Detail);
    }

    [Fact]
    public void AnItemWornInAnotherSlotThanTheServerSaysIsMisplaced()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.Owned(Action.Equip, 0x93, "rin", Self, ContainerType.Unspecified, slot: DirectoryType.RightHandRing))
            .Received(6, ItemPackets.Owned(Action.SwapBodyItem, 0x93, "rin", Self, ContainerType.Unspecified, slot: DirectoryType.LeftHandRing)));

        var misplaced = Assert.Single(found);
        Assert.Equal(ItemStateCheck.Misplaced, misplaced.Kind);
        Assert.Equal("the client holds Ring 0x93 in RightHandRing; the server: worn in LeftHandRing (SwapBodyItem) at 00:06.000", misplaced.Detail);
    }

    /// <summary>
    /// The realm moves an item by taking it out (to the cursor) and putting it back, and the client clears and marks the
    /// cells with it. After a trade it hands out ids again with no removal first: an id given to another item at another
    /// cell replaces the old item in the client's list and leaves the old item's cell marked.
    /// </summary>
    [Fact]
    public void ACellTheClientStillMarksTakenAfterAnItemMovedIsReported()
    {
        using var root = new TempRoot();
        var moved = Check(root, t => t
            .Received(5, ItemPackets.Item(Action.PutInContainer, 0x91, "rin", ContainerType.Inventory, 3, 3))
            .Sent(5.5, ItemPackets.Lift(0x91))
            .Add(TraceRecordKind.ClientAction, 5.5, ItemPackets.ClientDid(ClientAction.Lift, 0x91))
            .Received(5.6, ItemPackets.Owned(Action.RemoveFromContainer, 0x91, "rin", Self, ContainerType.Inventory, 3, 3))
            .Sent(5.8, ItemPackets.Put(0x91, 6, 3, 0))
            .Received(5.9, ItemPackets.Item(Action.PutInContainer, 0x91, "rin", ContainerType.Inventory, 6, 3)));
        using var second = new TempRoot();
        var renumbered = Check(second, t => t
            .Received(5, ItemPackets.Item(Action.PutInContainer, 0x91, "rin", ContainerType.Inventory, 3, 3))
            .Received(6, ItemPackets.Item(Action.PutInContainer, 0x91, "amu", ContainerType.Inventory, 6, 3)));

        Assert.Empty(moved);
        var cells = Assert.Single(renumbered, i => i.Kind == ItemStateCheck.GridCells);
        Assert.StartsWith("the client's inventory grid marks 1 cell taken that none of its items covers: (3, 3); it holds ", cells.Detail);
    }

    private static List<string> Lines(string folder, Func<ItemEvent, bool> which = null)
        => [.. Events(folder).Where(e => e.Kind != ItemKinds.Loaded && (which == null || which(e))).Select(e => ItemMoves.Format(e, false))];

    /// <summary>
    /// The realm's order (a leveling game): the gold attribute reports what moved since its last report, as an add (0x19)
    /// when that is under 256, and a vendor answer has already said so - in its own instant or a quarter second later, and
    /// one add for two sales. A party member's pile a while later is a share of its own.
    /// </summary>
    [Fact]
    public void AGoldAddEchoingSmallSalesIsNotGoldReceivedInsideOrOutsideTheVendorsInstant()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Sent(6.0, ItemPackets.Sell(Npc, 0x11))
            .Received(6.041, ItemPackets.Owned(Action.RemoveFromContainer, 0x11, "tbk", Self, ContainerType.Inventory, 2, 0))
            .Received(6.041, ItemPackets.Vendor(3, 1, 0x11, 1125))
            .Sent(6.2, ItemPackets.Sell(Npc, 0x10))
            .Received(6.241, ItemPackets.Owned(Action.RemoveFromContainer, 0x10, "box", Self, ContainerType.Inventory, 0, 0))
            .Received(6.241, ItemPackets.Vendor(3, 1, 0x10, 1250))
            .Received(6.241, ItemPackets.GoldAdded(250))
            .Sent(6.386, ItemPackets.Sell(Npc, 0x15))
            .Received(6.401, ItemPackets.Owned(Action.RemoveFromContainer, 0x15, "rin", Self, ContainerType.Cube, 0, 0))
            .Received(6.401, ItemPackets.Vendor(3, 1, 0x15, 1375))
            .Received(6.640, ItemPackets.GoldAdded(125))
            .Received(9.0, ItemPackets.OnGround(0x74, "gld", gold: 1940))
            .Received(10.0, ItemPackets.Removed(0x74))
            .Received(10.0, ItemPackets.Gold(GoldOnPerson, 1860))
            .Ticks(14, 15, 5057, 1902)
            .WriteTo(folder);

        Assert.Equal(
            [
                "00:06.041 client1      sold             TomeOfTownPortal 0x11 +125 gold, 1,125 on the person after - asked 00:06.000 (sell (0x33)), answered in 41 ms",
                "00:06.241 client1      sold             HoradricCube 0x10 +125 gold, 1,250 on the person after - asked 00:06.200 (sell (0x33)), answered in 41 ms",
                "00:06.401 client1      sold             Ring 0x15 +125 gold, 1,375 on the person after - asked 00:06.386 (sell (0x33)), answered in 15 ms",
                "00:10.000 client1      gold received    Gold +485 on the person (1,860 after): a share of pile 0x74 (1,940 on the ground) another player picked up - unasked",
            ],
            Lines(folder));
    }

    /// <summary>A sale's gain of 256 or more is reported as the new figure; a party member's share arriving as an add in the same instant is still gold received.</summary>
    [Fact]
    public void APartyShareAddedRightAfterAVendorAnswerIsGoldReceived()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(3.0, ItemPackets.OnGround(0x75, "gld", gold: 229))
            .Sent(6.0, ItemPackets.Sell(Npc, 0x11))
            .Received(6.1, ItemPackets.Owned(Action.RemoveFromContainer, 0x11, "tbk", Self, ContainerType.Inventory, 2, 0))
            .Received(6.1, ItemPackets.Vendor(3, 1, 0x11, 1500))
            .Received(6.14, ItemPackets.Gold(GoldOnPerson, 1500))
            .Received(6.15, ItemPackets.Removed(0x75))
            .Received(6.16, ItemPackets.GoldAdded(58))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        Assert.Equal(
            [
                "00:06.100 client1      sold             TomeOfTownPortal 0x11 +500 gold, 1,500 on the person after - asked 00:06.000 (sell (0x33)), answered in 100 ms",
                "00:06.160 client1      gold received    Gold +58 on the person (1,558 after): a share of pile 0x75 (229 on the ground) another player picked up - unasked",
            ],
            Lines(folder));
    }

    /// <summary>
    /// The realm's order (a leveling game): sales take the gold on the person past what the character carries, then the bot
    /// drops the excess (0x50) and the gold attribute falls by it; a barbarian drops gold for the party pile by pile, each
    /// answered by its own fall. Gold moved into the stash in the same moment is no drop.
    /// </summary>
    [Fact]
    public void GoldTheClientDropsIsDroppedPileByPileAndADepositIsNot()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined(gold: 248000)
            .Sent(5.25, ItemPackets.DropGold(400))
            .Received(5.328, ItemPackets.Gold(GoldOnPerson, 247600))
            .Sent(5.75, ItemPackets.DropGold(400))
            .Received(6.249, ItemPackets.Gold(GoldOnPerson, 247200))
            .Sent(25.25, ItemPackets.Sell(Npc, 0x11))
            .Received(25.296, ItemPackets.Owned(Action.RemoveFromContainer, 0x11, "tbk", Self, ContainerType.Inventory, 2, 0))
            .Received(25.296, ItemPackets.Vendor(3, 1, 0x11, 262200))
            .Sent(26.75, ItemPackets.DropGold(12200))
            .Received(27.094, ItemPackets.Gold(GoldOnPerson, 250000))
            .Received(40.0, ItemPackets.Gold(GoldInStash, 50000))
            .Received(40.32, ItemPackets.Gold(GoldOnPerson, 200000))
            .Ticks(44, 45, 5057, 1902)
            .WriteTo(folder);

        var events = Events(folder);

        Assert.Equal(
            [
                "00:05.328 client1      dropped          Gold -400 gold to the ground, 247,600 on the person after - asked 00:05.250 (drop 400 gold (0x50)), answered in 78 ms",
                "00:06.249 client1      dropped          Gold -400 gold to the ground, 247,200 on the person after - asked 00:05.750 (drop 400 gold (0x50)), answered in 499 ms",
                "00:25.296 client1      sold             TomeOfTownPortal 0x11 +15,000 gold, 262,200 on the person after - asked 00:25.250 (sell (0x33)), answered in 46 ms",
                "00:27.094 client1      dropped          Gold -12,200 gold to the ground, 250,000 on the person after - asked 00:26.750 (drop 12,200 gold (0x50)), answered in 344 ms",
            ],
            Lines(folder));
        Assert.Equal(15000L - 13000, events.Sum(e => e.Gold ?? 0));
        Assert.EndsWith("unanswered requests 0, dropped 13,000 gold", ItemMoves.Format(ItemMoves.Summarize("client1", events)));
    }

    /// <summary>The realm's order on a death: the "was slain by" message, then the gold on the person set to 0, which nothing the client asked for explains.</summary>
    [Fact]
    public void GoldTheServerTakesOnADeathIsLost()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined(gold: 187287)
            .Received(55.0, Round6Packets.Slain("client1", 83))
            .Received(55.362, ItemPackets.Gold(GoldOnPerson, 0))
            .Ticks(58, 59, 5057, 1902)
            .WriteTo(folder);

        var events = Events(folder);

        Assert.Equal(["00:55.362 client1      gold lost        Gold -187,287 on the person on the death, 0 after - unasked"], Lines(folder));
        Assert.EndsWith("unanswered requests 0, lost 187,287 gold (deaths or unexplained)", ItemMoves.Format(ItemMoves.Summarize("client1", events)));
    }

    /// <summary>
    /// The realm's order (a Travincal game): the pick-up is answered, the bot sends it again, then lifts the item and sends it
    /// to the cube. The put answers the cube request, since an item the client holds cannot be picked up.
    /// </summary>
    [Fact]
    public void APickUpSentAgainAfterItsAnswerDoesNotTakeTheAnswerOfALaterPut()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(39.0, ItemPackets.OnGround(0x63, "rin"))
            .Sent(39.731, Packets.Pickup(0x63))
            .Received(39.961, ItemPackets.Removed(0x63))
            .Received(39.961, ItemPackets.Item(Action.PutInContainer, 0x63, "rin", ContainerType.Inventory, 8, 0))
            .Sent(40.225, Packets.Pickup(0x63))
            .Sent(40.286, ItemPackets.Lift(0x63))
            .Add(TraceRecordKind.ClientAction, 40.286, ItemPackets.ClientDid(ClientAction.Lift, 0x63))
            .Received(40.401, ItemPackets.Owned(Action.RemoveFromContainer, 0x63, "rin", Self, ContainerType.Inventory, 8, 0))
            .Sent(40.405, ItemPackets.ToCube(0x63, 0x10))
            .Received(40.521, ItemPackets.Item(Action.PutInContainer, 0x63, "rin", ContainerType.Cube, 2, 2))
            .Ticks(48, 49, 5057, 1902)
            .WriteTo(folder);

        Assert.Equal(
            [
                "00:39.961 client1      picked up        Ring 0x63 into inventory at (8, 0) - asked 00:39.731 (pick up (0x16)), answered in 230 ms",
                "00:40.225 client1      repeat           Ring 0x63 pick up (0x16): sent again after the server had answered the same request at 00:39.961; it answers once",
                "00:40.401 client1      to cursor        Ring 0x63 from inventory at (8, 0) - asked 00:40.286 (lift (0x19)), answered in 115 ms",
                "00:40.521 client1      put              Ring 0x63 in cube at (2, 2) - asked 00:40.405 (to cube (0x2A)), answered in 116 ms",
            ],
            Lines(folder));
    }

    /// <summary>
    /// A take-off answers only the slot it asked for: the body the server strips on a death two seconds after the client
    /// took its amulet off goes nowhere, so a client that keeps it on the cursor is wrong.
    /// </summary>
    [Fact]
    public void ADeathSoonAfterATakeOffIsNotAnsweredByIt()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Sent(5.0, ItemPackets.TakeOff(DirectoryType.Amulet))
            .Received(5.1, ItemPackets.Owned(Action.Unequip, 0x13, "amu", Self, ContainerType.Unspecified, slot: DirectoryType.Amulet))
            .Sent(5.3, ItemPackets.Put(0x13, 6, 0, 0))
            .Received(5.4, ItemPackets.Item(Action.PutInContainer, 0x13, "amu", ContainerType.Inventory, 6, 0))
            .Received(7.0, Round6Packets.Slain("client1", 83))
            .Received(7.1, ItemPackets.Owned(Action.Unequip, 0x14, "lbl", Self, ContainerType.Unspecified, slot: DirectoryType.Belt)));

        var cursor = Assert.Single(found);
        Assert.Equal(ItemStateCheck.CursorNotGiven, cursor.Kind);
        Assert.Equal("the client still holds Sash 0x14 in the cursor; the server: taken off Belt with no request asking it (Unequip): to the corpse on a death, or used up at 00:07.100", cursor.Detail);
    }

    /// <summary>The realm's order (a leveling game): Cain identifies everything in one go; the vendor's answer names no item and each item's update follows in the same instant.</summary>
    [Fact]
    public void IdentifyingAllAtAnNpcAnswersEveryItemItUpdates()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        Joined()
            .Received(0.18, ItemPackets.Item(Action.PutInContainer, 0x30, "rin", ContainerType.Inventory, 8, 0, identified: false))
            .Received(0.19, ItemPackets.Item(Action.PutInContainer, 0x35, "amu", ContainerType.Inventory, 3, 0, identified: false))
            .Sent(22.356, ItemPackets.IdentifyAll(Npc))
            .Received(22.434, ItemPackets.Vendor(0, 3, 0xFFFFFFFF, 1000))
            .Received(22.434, ItemPackets.Owned(Action.UpdateStats, 0x30, "rin", Self, ContainerType.Inventory, 8, 0))
            .Received(22.434, ItemPackets.Owned(Action.UpdateStats, 0x35, "amu", Self, ContainerType.Inventory, 3, 0))
            .Ticks(26, 27, 5057, 1902)
            .WriteTo(folder);

        Assert.Equal(
            [
                "00:22.434 client1      identified       Ring 0x30 Normal - asked 00:22.356 (identify all at an NPC (0x34)), answered in 78 ms",
                "00:22.434 client1      identified       Amulet 0x35 Normal - asked 00:22.356 (identify all at an NPC (0x34)), answered in 78 ms",
            ],
            Lines(folder));
    }

    /// <summary>
    /// Purchases paid from the stash leave the vendor's figure for the person where it was, so the stash's one fall pays
    /// for every purchase answered in that instant; the price table splits it by what each costs.
    /// </summary>
    [Fact]
    public void ADebitForPurchasesAnsweredTogetherIsSplitByTheirPrices()
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        var potion = new ParseItemPacket(new D2gsPacket(ItemPackets.Item(Action.PutInContainer, 0x41, "hp1", ContainerType.Inventory, 0, 2))).Item;
        var ring = new ParseItemPacket(new D2gsPacket(ItemPackets.Item(Action.PutInContainer, 0x42, "rin", ContainerType.Inventory, 1, 2))).Item;
        var table = D2NG.Core.ItemData.ItemPriceTable.Load(D2NG.Core.GameDataLocation.Resolve(D2NG.Core.GameDataLocation.ItemPricesFile));
        var (potionCost, ringCost) = (D2NG.Core.ItemData.ItemPrice.BuyCost(potion, table).Value, D2NG.Core.ItemData.ItemPrice.BuyCost(ring, table).Value);
        Joined(gold: 0)
            .Received(0.18, ItemPackets.Gold(GoldInStash, 100000))
            .Received(3.8, ItemPackets.Item(Action.AddToShop, 0x30, "hp1", ContainerType.Unspecified, 0, 0))
            .Received(3.8, ItemPackets.Item(Action.AddToShop, 0x31, "rin", ContainerType.Unspecified, 1, 0))
            .Sent(4.0, ItemPackets.Buy(Npc, 0x30, gamble: false))
            .Sent(4.0, ItemPackets.Buy(Npc, 0x31, gamble: false))
            .Received(4.1, ItemPackets.Vendor(4, 0, 0x41, 0))
            .Received(4.1, ItemPackets.Item(Action.PutInContainer, 0x41, "hp1", ContainerType.Inventory, 0, 2))
            .Received(4.1, ItemPackets.Vendor(4, 0, 0x42, 0))
            .Received(4.1, ItemPackets.Item(Action.PutInContainer, 0x42, "rin", ContainerType.Inventory, 1, 2))
            .Received(4.15, ItemPackets.Gold(GoldInStash, (uint)(100000 - potionCost - ringCost)))
            .Ticks(8, 9, 5057, 1902)
            .WriteTo(folder);

        var bought = Events(folder).Where(e => e.Kind == ItemKinds.Bought).ToList();

        Assert.True(ringCost != potionCost, "the test needs two prices");
        Assert.Equal([-(long)potionCost, -(long)ringCost], bought.Select(e => e.Gold ?? 0));
        Assert.EndsWith($"one debit of {potionCost + ringCost:N0} for 2 answered together, split by their prices", bought[0].Detail);
    }

    [Fact]
    public void ABeltPotionTheServerMovedButTheClientDidNotIsMisplaced()
    {
        using var root = new TempRoot();
        var found = Check(root, t => t
            .Received(5, ItemPackets.Belt(Action.PutInBelt, 0x87, 1, 0))
            .Received(6, ItemPackets.Belt(Action.SwapInBelt, 0x87, 2, 0)));

        var misplaced = Assert.Single(found);
        Assert.Equal(ItemStateCheck.Misplaced, misplaced.Kind);
        Assert.Equal("the client holds SuperHealingPotion 0x87 in belt slot 1, row 0; the server: put in the belt slot 2, row 0 (SwapInBelt) at 00:06.000", misplaced.Detail);
    }
}

[Collection(nameof(TraceCacheCollection))]
public sealed class ItemCacheTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly string _previousRoot = TraceCache.Root;

    public ItemCacheTests()
    {
        TraceCache.Root = Path.Combine(_root.Path, "cache");
        TraceCache.Reset();
    }

    public void Dispose()
    {
        TraceCache.Enabled = false;
        TraceCache.Root = _previousRoot;
        TraceCache.Reset();
        _root.Dispose();
    }

    private static List<string> Answers(string folder)
        => [.. ItemMoves.Replay(GameQuery.Open(folder), ["client1"]).SelectMany(r => r.Ledger.Events.Select(e => $"{e} {e.Details}").Append($"checked {r.Check.Checked}, found {r.Check.Found.Count}"))];

    private static void Forget(string folder)
    {
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            TraceFileSummary.Forget(file);
            SidecarReader.Forget(file);
        }

        TraceCache.Reset();
    }

    [Fact]
    public void ItemEventsFromTheCacheAreTheSameColdAndWarm()
    {
        var folder = Game1.Folder(_root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(0.10, ItemPackets.Item(Action.PutInContainer, 0x10, "box", ContainerType.Inventory, 0, 0))
            .Received(3.0, ItemPackets.OnGround(0x20, "rin"))
            .Sent(3.1, Packets.Pickup(0x20))
            .Received(3.2, ItemPackets.Item(Action.PutInContainer, 0x20, "rin", ContainerType.Inventory, 4, 0))
            .Sent(4.0, Packets.Pickup(0x21))
            .Ticks(10, 12, 5057, 1902)
            .WriteTo(folder);
        var fresh = Answers(folder);

        TraceCache.Enabled = true;
        Forget(folder);
        var cold = Answers(folder);
        TraceCache.Flush();
        Forget(folder);
        var warm = Answers(folder);

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(TraceCache.Root, "derived"), "*.bin", SearchOption.AllDirectories));
        Assert.Equal(fresh, cold);
        Assert.Equal(fresh, warm);
        Assert.Contains(fresh, line => line.Contains("UNANSWERED", StringComparison.Ordinal));
    }
}
