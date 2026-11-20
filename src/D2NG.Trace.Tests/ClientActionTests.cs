using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.MCP;
using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

public class ClientActionTests
{
    private const uint Sold = 0x1B;
    private const uint Used = 0x1C;
    private const uint ShopFirst = 0x30;
    private const uint ShopSecond = 0x31;

    private static readonly byte[] HoradricCubeInInventory = Convert.FromHexString("9C0417101B0000001000800001001022F686078286F01F");

    private static byte[] ItemPacket(Action action, uint id)
    {
        var packet = (byte[])HoradricCubeInInventory.Clone();
        packet[1] = (byte)action;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), id);
        return packet;
    }

    [Fact]
    public void TheItemPacketsLandWhereTheTestsNeedThem()
    {
        Assert.Equal(ContainerType.Inventory, new ParseItemPacket(new D2gsPacket(ItemPacket(Action.PutInContainer, Sold))).Item.Container);
        Assert.True(new ParseItemPacket(new D2gsPacket(ItemPacket(Action.AddToShop, ShopFirst))).Item.IsInMerchantTab());
    }

    [Fact]
    public async Task ReplayFollowsLiftsSalesUsesShopClearsAndMoves()
    {
        using var root = new TempRoot();
        var (path, live) = await RecordSession(root);

        using var replayer = new Replayer(path);
        var result = replayer.ReplayToEnd();

        Assert.Equal(0, result.HandlerFailures);
        Assert.Equal(5, result.ClientActions);
        Assert.Equal(14, result.Digests);
        Assert.Equal(0, result.MismatchedDigests);
        Assert.Empty(result.FirstMismatches);
        Assert.Null(replayer.Game.CursorItem);
        Assert.Equal(live.Items, replayer.Game.Items.Count);
        Assert.Equal(new Point(5059, 1902), replayer.Game.Me.Location);
    }

    [Fact]
    public async Task WithoutTheClientActionsTheDigestsCatchTheDifference()
    {
        using var root = new TempRoot();
        var (path, _) = await RecordSession(root);
        var stripped = Path.Combine(Path.GetDirectoryName(path), "stripped.d2trace");
        using (var reader = new TraceReader(path))
        {
            TraceFile.Write(stripped, reader.Header, reader.Records().Where(r => r.Kind != TraceRecordKind.ClientAction));
        }

        // Under CursorNeedsLift a removal without its Lift never reaches the cursor, so a missing Sell changes nothing there.
        using var replayer = new Replayer(stripped, ClientRules.ForVersion(1));
        var result = replayer.ReplayToEnd();

        Assert.Equal(0, result.ClientActions);
        Assert.True(result.MismatchedDigests > 0);
        var fields = result.FirstMismatches.Select(m => m.Field).Distinct().ToList();
        Assert.Contains(nameof(TraceDigest.CursorItemId), fields);
        var cursor = result.FirstMismatches.First(m => m.Field == nameof(TraceDigest.CursorItemId));
        Assert.Equal("0", cursor.Expected);
        Assert.Equal(Sold.ToString(), cursor.Actual);
        Assert.False(cursor.AfterGap);
        Assert.Equal(5, cursor.Preceding.Count);
        Assert.Equal("D2gsReceived 0x9C", cursor.Preceding[^1]);
    }

    [Fact]
    public void AMismatchAfterAGapIsCountedApart()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var path = Path.Combine(root.Path, "gapped.d2trace");
        var header = new TraceHeader { Source = TraceSource.Bot, StartUtc = DateTime.UtcNow, ClientLabel = "client 1", GameName = "game-1" };
        var wrong = new TraceDigest { Life = 1 }.ToBytes();
        var gap = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(gap, 3);
        var records = new List<TraceRecord> { new(TraceRecordKind.D2gsSent, TimeSpan.Zero, SyntheticTrace.Logon()) };
        records.AddRange(SyntheticTrace.Joining().Select(p => new TraceRecord(TraceRecordKind.D2gsReceived, TimeSpan.Zero, p)));
        records.Add(new TraceRecord(TraceRecordKind.Digest, TimeSpan.FromSeconds(1), wrong));
        records.Add(new TraceRecord(TraceRecordKind.Gap, TimeSpan.FromSeconds(2), gap));
        records.Add(new TraceRecord(TraceRecordKind.Digest, TimeSpan.FromSeconds(3), wrong));
        TraceFile.Write(path, header, records);

        using var replayer = new Replayer(path);
        var result = replayer.ReplayToEnd();

        Assert.Equal(1, result.MismatchedDigests);
        Assert.Equal(1, result.MismatchedDigestsAfterGap);
        Assert.All(result.FirstMismatches, m => Assert.False(m.AfterGap));
        Assert.All(result.FirstMismatchesAfterGap, m => Assert.True(m.AfterGap));
        Assert.Equal("Gap", result.FirstMismatchesAfterGap[0].Preceding[^1]);
    }

    /// <summary>
    /// A newer bot records a client action this build has no case for: one handler failure that says so, one note per
    /// file, and the actions this build knows still applied.
    /// </summary>
    [Fact]
    public void AnUnknownClientActionIsNamedOncePerFile()
    {
        const byte Unknown = 200;
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var path = Path.Combine(root.Path, "newer.d2trace");
        var header = new TraceHeader { Source = TraceSource.Bot, StartUtc = DateTime.UtcNow, ClientLabel = "client 1", GameName = "game-1" };
        var records = new List<TraceRecord> { new(TraceRecordKind.D2gsSent, TimeSpan.Zero, SyntheticTrace.Logon()) };
        records.AddRange(SyntheticTrace.Joining().Select(p => new TraceRecord(TraceRecordKind.D2gsReceived, TimeSpan.Zero, p)));
        byte[] Recorded(byte action, uint argument) => [action, .. BitConverter.GetBytes(argument)];
        for (var i = 1; i <= 3; i++)
        {
            records.Add(new TraceRecord(TraceRecordKind.ClientAction, TimeSpan.FromSeconds(i), Recorded(Unknown, 7)));
        }

        records.Add(new TraceRecord(TraceRecordKind.ClientAction, TimeSpan.FromSeconds(4), Recorded((byte)ClientAction.Move, ClientActions.Pack(new Point(5058, 1902)))));
        TraceFile.Write(path, header, records);

        using var replayer = new Replayer(path);
        var result = replayer.ReplayToEnd();

        Assert.Equal(1, result.HandlerFailures);
        var failure = Assert.Single(result.FirstHandlerFailures);
        Assert.Equal(Unknown, failure.PacketType);
        Assert.Contains($"client action {Unknown} is unknown to this build", failure.Error);
        Assert.Contains("written by a newer version", failure.Error);
        Assert.Equal(3, result.UnknownClientActions[Unknown]);
        Assert.Equal(Replayer.UnknownClientActionNote(Unknown), Assert.Single(result.Notes));
        Assert.Equal("this trace uses client action 200, which this build does not know: state after it may differ", result.Notes[0]);
        Assert.Equal(new Point(5058, 1902), replayer.Game.Me.Location);
    }

    /// <summary>A traced client that has joined a game, its packets going through the live receive path.</summary>
    private sealed class Traced : IDisposable
    {
        private readonly TempRoot _root = new();
        private readonly TraceRecorder _recorder;

        public Traced()
        {
            _recorder = _root.Recorder(digestInterval: TimeSpan.Zero);
            Channel = _recorder.OpenGame("game-1", "client 1");
            Server = new GameServer { Trace = Channel };
            Game = new Game(Server);
            Server.DigestSource = () => TraceDigest.Capture(Game);
            Channel.Write(TraceRecordKind.D2gsSent, SyntheticTrace.Logon());
            var stats = new byte[32];
            stats[13] = 3;
            Game.SelectCharacter(new Character(SyntheticTrace.CharacterName, stats));
            foreach (var packet in SyntheticTrace.Joining())
            {
                Receive(packet);
            }
        }

        public TraceChannel Channel { get; }

        public GameServer Server { get; }

        public Game Game { get; }

        public void Receive(byte[] packet) => Server.OnPacketReceived(new D2gsPacket(packet));

        public Task ReceiveAsync(byte[] packet) => Task.Run(() => Receive(packet), TestContext.Current.CancellationToken);

        /// <summary>A client action from another thread, as a bot thread applies one.</summary>
        public Task MoveAsync() => Task.Run(() => Game.Apply(ClientAction.Move, ClientActions.Pack(new Point(5058, 1902))), TestContext.Current.CancellationToken);

        public string Close()
        {
            Channel.Close();
            _recorder.FlushNow();
            _recorder.FlushNow();
            return Channel.Path;
        }

        public void Dispose()
        {
            _recorder.Dispose();
            _root.Dispose();
        }
    }

    /// <summary>
    /// The bot thread closed a shop while the receive thread had recorded a shop item but not yet handled it: live the
    /// item was removed and then added, the trace said added and then removed, and the replay held one item fewer for
    /// the rest of the game. A client action now waits for the core handlers of a packet already recorded.
    /// </summary>
    [Fact]
    public void AClientActionWaitsForTheCoreHandlersOfAPacketAlreadyRecorded()
    {
        using var traced = new Traced();
        using var handling = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        traced.Server.OnCoreReceivedPacketEvent(InComingPacket.WorldItemAction, _ =>
        {
            handling.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        var receive = traced.ReceiveAsync(ItemPacket(Action.AddToShop, ShopFirst));
        Assert.True(handling.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var close = Task.Run(() => traced.Game.Apply(ClientAction.RemoveMerchantItems), TestContext.Current.CancellationToken);

        Assert.False(close.Wait(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        release.Set();
        Assert.True(Task.WaitAll([receive, close], 5000, TestContext.Current.CancellationToken));
        Assert.False(traced.Game.Items.ContainsKey(ShopFirst));

        using var replayer = new Replayer(traced.Close());
        var result = replayer.ReplayToEnd();
        Assert.Equal(0, result.MismatchedDigests);
        Assert.Equal(traced.Game.Items.Count, replayer.Game.Items.Count);
    }

    /// <summary>A bot's handler that blocks must not hold up a client action on another thread: it runs with no lock held.</summary>
    [Fact]
    public void AnExternalHandlerThatBlocksDoesNotDelayAClientAction()
    {
        using var traced = new Traced();
        using var handling = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        traced.Server.OnReceivedPacketEvent(InComingPacket.WorldItemAction, _ =>
        {
            handling.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        var receive = traced.ReceiveAsync(ItemPacket(Action.AddToShop, ShopFirst));
        Assert.True(handling.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var moved = traced.MoveAsync().Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        release.Set();
        Assert.True(receive.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(moved);
    }

    /// <summary>A bot's handler that waits for another thread which applies a client action - a move, say - completes.</summary>
    [Fact]
    public void AnExternalHandlerWaitingForAThreadThatAppliesAClientActionCompletes()
    {
        using var traced = new Traced();
        traced.Server.OnReceivedPacketEvent(InComingPacket.WorldItemAction, _ => traced.MoveAsync().Wait(TimeSpan.FromSeconds(2)));

        Assert.True(traced.ReceiveAsync(ItemPacket(Action.AddToShop, ShopFirst)).Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    /// <summary>Core handlers run first, then the others in the order they were registered, as one delegate ran them before.</summary>
    [Fact]
    public void CoreHandlersRunBeforeTheOthersInTheirOrder()
    {
        using var traced = new Traced();
        var calls = new List<string>();
        traced.Server.OnReceivedPacketEvent(InComingPacket.WorldItemAction, _ => calls.Add($"bot {traced.Game.Items.ContainsKey(ShopFirst)}"));
        traced.Server.OnReceivedPacketEvent(InComingPacket.WorldItemAction, _ => calls.Add("bot 2"));
        traced.Game.OnWorldItemEvent(item => { calls.Add($"game {item.Id}"); return Task.CompletedTask; });
        traced.Server.OnReceivedPacketEvent(InComingPacket.Recv70, _ => calls.Add("only a bot's"));

        traced.Receive(ItemPacket(Action.AddToShop, ShopFirst));
        traced.Receive([0x70]);
        traced.Receive([0x71]);

        Assert.Equal([$"game {ShopFirst}", "bot True", "bot 2", "only a bot's"], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHandlersExceptionReachesTheListenerAndReleasesTheLock(bool core)
    {
        using var traced = new Traced();
        var later = false;
        Action<D2gsPacket> throws = _ => throw new InvalidOperationException("handler");
        if (core)
        {
            traced.Server.OnCoreReceivedPacketEvent(InComingPacket.WorldItemAction, throws);
        }
        else
        {
            traced.Server.OnReceivedPacketEvent(InComingPacket.WorldItemAction, throws);
        }

        traced.Server.OnReceivedPacketEvent(InComingPacket.WorldItemAction, _ => later = true);

        Assert.Equal("handler", Assert.Throws<InvalidOperationException>(() => traced.Receive(ItemPacket(Action.AddToShop, ShopFirst))).Message);
        Assert.False(later);
        Assert.True(traced.MoveAsync().Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A bot opened a vendor's trade screen and closed it 35 ms later, before the listing arrived: the listing stayed in
    /// Game.Items as some 55 phantom merchant items until the next vendor visit. A listing that arrives with no shop
    /// open is now not kept, and the recorded trace replays to the same items.
    /// </summary>
    [Fact]
    public void AListingThatArrivesAfterTheShopClosedIsNotKept()
    {
        using var traced = new Traced();
        var game = traced.Game;
        var vendor = game.WorldObjects[(0xE9, EntityType.NPC)];
        var before = game.Items.Count;

        game.TownFolkAction(vendor, TownFolkActionType.Trade);
        traced.Receive(ItemPacket(Action.AddToShop, ShopFirst));
        Assert.True(game.Items.ContainsKey(ShopFirst));

        game.TerminateEntityChat(vendor);
        traced.Receive(ItemPacket(Action.AddToShop, ShopSecond));
        Assert.False(game.Items.ContainsKey(ShopSecond));
        Assert.Equal(before, game.Items.Count);

        game.TownFolkAction(vendor, TownFolkActionType.Gamble);
        traced.Receive(ItemPacket(Action.AddToShop, ShopSecond));
        game.TownFolkAction(vendor, TownFolkActionType.RefreshGamble);
        Assert.False(game.Items.ContainsKey(ShopSecond));
        traced.Receive(ItemPacket(Action.AddToShop, ShopFirst));
        Assert.True(game.Items.ContainsKey(ShopFirst));

        traced.Receive([0x03, 0x01, 0x73, 0x31, 0x1B, 0x5C, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00]);
        traced.Receive(ItemPacket(Action.AddToShop, ShopSecond));
        Assert.False(game.Items.ContainsKey(ShopSecond));

        using var replayer = new Replayer(traced.Close());
        var result = replayer.ReplayToEnd();
        Assert.Equal(0, result.MismatchedDigests);
        Assert.Equal(game.Items.Keys.Order(), replayer.Game.Items.Keys.Order());
    }

    /// <summary>A trace from before the shop was tracked has no OpenShop: its replay keeps every listing, as its bot did.</summary>
    [Fact]
    public void WithoutAnOpenShopActionEveryListingIsKept()
    {
        using var traced = new Traced();
        traced.Game.Apply(ClientAction.RemoveMerchantItems);
        traced.Receive(ItemPacket(Action.AddToShop, ShopFirst));

        Assert.True(traced.Game.Items.ContainsKey(ShopFirst));
    }

    private static async Task<(string Path, (int Items, uint? Cursor) Live)> RecordSession(TempRoot root)
    {
        using var recorder = root.Recorder(digestInterval: TimeSpan.Zero);
        var channel = recorder.OpenGame("game-1", "client 1");
        var gameServer = new GameServer { Trace = channel };
        var game = new Game(gameServer);
        gameServer.DigestSource = () => TraceDigest.Capture(game);
        void Receive(byte[] packet) => gameServer.OnPacketReceived(new D2gsPacket(packet));

        channel.Write(TraceRecordKind.D2gsSent, SyntheticTrace.Logon());
        var stats = new byte[32];
        stats[13] = 3;
        game.SelectCharacter(new Character(SyntheticTrace.CharacterName, stats));
        foreach (var packet in SyntheticTrace.Joining())
        {
            Receive(packet);
        }

        Receive(ItemPacket(Action.PutInContainer, Sold));
        var sold = game.Inventory.FindItemById(Sold);
        var npc = game.WorldObjects[(0xE9, EntityType.NPC)];

        game.RemoveItemFromContainer(sold);
        Receive(ItemPacket(Action.RemoveFromContainer, Sold));
        Assert.Equal(Sold, game.CursorItem?.Id);
        Receive(ItemPacket(Action.PutInContainer, Sold));
        Assert.Null(game.CursorItem);

        game.SellItem(npc, sold);
        Receive(ItemPacket(Action.RemoveFromContainer, Sold));
        Assert.Null(game.CursorItem);

        Receive(ItemPacket(Action.PutInContainer, Used));
        var used = game.Inventory.FindItemById(Used);
        game.ActivateInventoryItem(used);
        Receive(ItemPacket(Action.RemoveFromContainer, Used));
        Assert.Null(game.CursorItem);

        Receive(ItemPacket(Action.AddToShop, ShopFirst));
        Receive(ItemPacket(Action.AddToShop, ShopSecond));
        var withShop = game.Items.Count;
        game.TerminateEntityChat(npc);
        Assert.Equal(withShop - 2, game.Items.Count);
        Receive(SyntheticTrace.LifeManaUpdate);

        Assert.True(await game.MoveToAsync(new Point(5059, 1902)));
        Receive(SyntheticTrace.MonsterHit(0xE5, 64));

        channel.Close();
        recorder.FlushNow();
        recorder.FlushNow();
        return (channel.Path, (game.Items.Count, game.CursorItem?.Id));
    }
}

internal static class TraceFile
{
    public static void Write(string path, TraceHeader header, IEnumerable<TraceRecord> records)
    {
        using var file = File.Create(path);
        file.Write(TraceFormat.Header(header.Source, header.StartUtc, header.ClientLabel, header.GameName));
        foreach (var record in records)
        {
            var bytes = new byte[TraceFormat.RecordHeaderSize + record.Payload.Length];
            TraceFormat.WriteRecordHeader(bytes, record.Kind, record.Elapsed.Ticks, record.Payload.Length);
            record.Payload.CopyTo(bytes, TraceFormat.RecordHeaderSize);
            file.Write(bytes);
        }
    }
}
