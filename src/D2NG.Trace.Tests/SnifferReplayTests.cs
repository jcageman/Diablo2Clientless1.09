using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

public class SnifferReplayTests
{
    private const uint Lifted = 0x1A;
    private const uint Sold = 0x1B;
    private const uint Used = 0x1C;

    /// <summary>
    /// The bot's own trace of a lift, a sale, a use, a merchant chat ending and a move, and the same records
    /// as a real client's trace would hold them - no client actions, no digests, source sniffer.
    /// </summary>
    private static async Task<(string Bot, string Sniffer)> SameSessionTwice(TempRoot root)
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
        game.SelectCharacter(new Core.MCP.Character(SyntheticTrace.CharacterName, stats));
        foreach (var packet in SyntheticTrace.Joining())
        {
            Receive(packet);
        }

        var npc = game.WorldObjects[(0xE9, EntityType.NPC)];
        // The game is not connected, so its sends are not traced: each is written where the live send happens,
        // after the client action for a lift, sale or use, before it for a chat ending or a move.
        void Sent(D2gsPacket packet) => channel.Write(TraceRecordKind.D2gsSent, packet.Raw);

        Receive(SyntheticGame.Item(Action.PutInContainer, Lifted));
        var lifted = game.Inventory.FindItemById(Lifted);
        game.RemoveItemFromContainer(lifted);
        Sent(new Core.D2GS.Packet.Outgoing.RemoveItemFromContainerPacket(lifted));
        Receive(SyntheticGame.Item(Action.RemoveFromContainer, Lifted));
        Assert.Equal(Lifted, game.CursorItem?.Id);
        Receive(SyntheticGame.Item(Action.PutInContainer, Lifted));
        Receive(SyntheticGame.Item(Action.RemoveFromContainer, Lifted));
        Receive(SyntheticGame.Item(Action.PutInContainer, Sold));
        var sold = game.Inventory.FindItemById(Sold);
        game.SellItem(npc, sold);
        Sent(new Core.D2GS.Packet.Outgoing.SellItemPacket(npc, sold));
        Receive(SyntheticGame.Item(Action.RemoveFromContainer, Sold));
        Receive(SyntheticGame.Item(Action.PutInContainer, Used));
        var used = game.Inventory.FindItemById(Used);
        game.ActivateInventoryItem(used);
        Sent(new Core.D2GS.Packet.Outgoing.ActivateBufferItemPacket(game.Me, used));
        Receive(SyntheticGame.Item(Action.RemoveFromContainer, Used));
        Receive(SyntheticGame.Item(Action.AddToShop, 0x30));
        Receive(SyntheticGame.Item(Action.AddToShop, 0x31));
        Sent(new Core.D2GS.Packet.Outgoing.TerminateEntityChatPacket(npc));
        game.TerminateEntityChat(npc);
        Receive(SyntheticTrace.LifeManaUpdate);
        Sent(new Core.D2GS.Packet.Outgoing.RunToLocationPacket(new Point(5070, 1910)));
        Assert.True(await game.MoveToAsync(new Point(5070, 1910)));
        Receive(SyntheticTrace.MonsterHit(0xE5, 64));
        Sent(new Core.D2GS.Packet.Outgoing.UpdatePlayerLocationPacket(new Point(5072, 1911)));
        Assert.True(await game.MoveToAsync(new Point(5072, 1911)));
        Receive(SyntheticTrace.MonsterHit(0xE5, 60));
        channel.Close();
        recorder.FlushNow();
        recorder.FlushNow();

        var sniffer = Path.Combine(Path.GetDirectoryName(channel.Path), "sniffer.d2trace");
        using (var reader = new TraceReader(channel.Path))
        {
            var header = reader.Header;
            TraceFile.Write(sniffer, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = header.StartUtc, ClientLabel = header.ClientLabel, GameName = header.GameName },
                reader.Records().Where(r => r.Kind is not TraceRecordKind.ClientAction and not TraceRecordKind.Digest));
        }

        return (channel.Path, sniffer);
    }

    private static (Replayer Replayer, List<TraceDigest> States) ReplayWithStates(string path)
    {
        var replayer = new Replayer(path);
        var states = new List<TraceDigest>();
        replayer.Applied = record =>
        {
            if (record.Kind == TraceRecordKind.D2gsReceived)
            {
                states.Add(TraceDigest.Capture(replayer.Game));
            }
        };
        replayer.ReplayToEnd();
        return (replayer, states);
    }

    [Fact]
    public async Task ARealClientsTraceReplaysToTheSameStateAsTheBotsTraceOfTheSamePackets()
    {
        using var root = new TempRoot();
        var (bot, sniffer) = await SameSessionTwice(root);

        var (botReplay, botStates) = ReplayWithStates(bot);
        var (snifferReplay, snifferStates) = ReplayWithStates(sniffer);
        using (botReplay)
        using (snifferReplay)
        {
            Assert.Equal(0, botReplay.Result.MismatchedDigests);
            Assert.True(botReplay.Result.ClientActions >= 5);
            Assert.Equal(0, snifferReplay.Result.ClientActions);
            Assert.Equal(0, snifferReplay.Result.HandlerFailures);
            Assert.Equal(botStates.Count, snifferStates.Count);
            for (var i = 0; i < botStates.Count; i++)
            {
                Assert.Equal(botStates[i] == null, snifferStates[i] == null);
                Assert.Empty(botStates[i]?.Differences(snifferStates[i]) ?? []);
            }

            var derived = snifferReplay.Result.DerivedClientActions;
            Assert.Equal(1, derived[ClientAction.Lift]);
            Assert.Equal(1, derived[ClientAction.Sell]);
            Assert.Equal(1, derived[ClientAction.Use]);
            Assert.Equal(1, derived[ClientAction.RemoveMerchantItems]);
            Assert.Equal(2, derived[ClientAction.Move]);
            Assert.Null(snifferReplay.Game.CursorItem);
            Assert.Equal(new Point(5072, 1911), snifferReplay.Game.Me.Location);
            Assert.DoesNotContain(snifferReplay.Game.Items.Values, item => item.IsInMerchantTab());
        }
    }

    [Fact]
    public async Task WithoutDerivingTheActionsTheRealClientsStateWouldDrift()
    {
        using var root = new TempRoot();
        var (bot, sniffer) = await SameSessionTwice(root);
        var asBot = Path.Combine(Path.GetDirectoryName(sniffer), "as-bot.d2trace");
        using (var reader = new TraceReader(sniffer))
        {
            TraceFile.Write(asBot, new TraceHeader { Source = TraceSource.Bot, StartUtc = reader.Header.StartUtc, ClientLabel = "client 1", GameName = "game-1" }, reader.Records());
        }

        var (botReplay, botStates) = ReplayWithStates(bot);
        var (plain, plainStates) = ReplayWithStates(asBot);
        using (botReplay)
        using (plain)
        {
            Assert.Contains(botStates.Zip(plainStates), pair => pair.First?.Differences(pair.Second).Any() == true);
            Assert.Empty(plain.Result.DerivedClientActions);
        }
    }

    [Fact]
    public void InteractingWithAWaypointForgetsTheLastSelectionAsTheBotDoes()
    {
        using var root = new TempRoot();
        var waypoint = EntityConstants.WayPointEntityCodes.First();
        var trace = new HandTrace("walker", "game-1", CapturedBytes.Start)
            .Join(1, characterClass: CharacterClass.Sorceress)
            .Received(1, SyntheticGame.AssignObject(0x40, waypoint, 5060, 1905))
            .Received(2, [0x63, 0x40, 0x00, 0x00, 0x00, .. new byte[16]])
            .Sent(3, SyntheticGame.Unit(0x13, EntityType.Object, 0x40));
        var path = trace.WriteTo(root.Path);
        var sniffer = Path.Combine(root.Path, "sniffer.d2trace");
        using (var reader = new TraceReader(path))
        {
            TraceFile.Write(sniffer, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = reader.Header.StartUtc, ClientLabel = "walker", GameName = "game-1" }, reader.Records());
        }

        using var asBot = new Replayer(path);
        asBot.ReplayToEnd();
        using var replayer = new Replayer(sniffer);
        replayer.ReplayToEnd();

        Assert.Equal(0x40u, asBot.Game.Me.LastSelectedWaypointId);
        Assert.Equal(0u, replayer.Game.Me.LastSelectedWaypointId);
        Assert.Equal(1, replayer.Result.DerivedClientActions[ClientAction.ForgetWaypointSelection]);
    }

    [Fact]
    public void ACaptureThatStartedMidGameReplaysWithTheFirstAssignedPlayerAndSaysSo()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "midgame.d2trace");
        Directory.CreateDirectory(root.Path);
        var records = new List<TraceRecord>
        {
            new(TraceRecordKind.D2gsReceived, TimeSpan.Zero, [0x2C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00]),
            new(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(10), SyntheticGame.AssignPlayer(4, CharacterClass.Paladin, "alpha", 5100, 5100)),
            new(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(20), SyntheticGame.LifeMana(300, 50, 5101, 5102)),
            new(TraceRecordKind.D2gsSent, TimeSpan.FromMilliseconds(30), SyntheticGame.Location(0x03, 5110, 5102)),
            new(TraceRecordKind.D2gsReceived, TimeSpan.FromSeconds(2), [0x2C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00]),
        };
        TraceFile.Write(path, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = CapturedBytes.Start, ClientLabel = "midgame-client1", GameName = "midgame-120000" }, records);

        using var replayer = new Replayer(path);
        var result = replayer.ReplayToEnd();

        Assert.True(result.HandlerFailures == 0, string.Join("; ", result.FirstHandlerFailures));
        Assert.Equal("alpha", replayer.Game.Me.Name);
        Assert.Equal(2, result.Notes.Count);
        Assert.Contains(result.Notes, note => note.Contains("first player assign", StringComparison.Ordinal));
        Assert.Contains(result.Notes, note => note.Contains("game flags", StringComparison.Ordinal));
        Assert.Equal(new Point(5110, 5102), replayer.Game.Me.Location);
    }

    [Fact]
    public void PacketsTheBotNeverSendsOrHandlesAreCountedNotThrown()
    {
        using var root = new TempRoot();
        var unknownReceived = (byte)Enumerable.Range(0, 256).First(i => !Enum.IsDefined(typeof(InComingPacket), (byte)i));
        var unknownSent = (byte)Enumerable.Range(1, 255).First(i => !Enum.IsDefined(typeof(OutGoingPacket), (byte)i));
        var trace = new HandTrace("alpha", "game-1", CapturedBytes.Start)
            .Join(1)
            .Received(1, [unknownReceived, 1, 2, 3])
            .Sent(2, [unknownSent, 9, 9])
            .Received(3, [0x2C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00]);
        var path = trace.WriteTo(root.Path);
        var sniffer = Path.Combine(root.Path, "sniffer.d2trace");
        using (var reader = new TraceReader(path))
        {
            TraceFile.Write(sniffer, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = reader.Header.StartUtc, ClientLabel = "alpha", GameName = "game-1" }, reader.Records());
        }

        using var replayer = new Replayer(sniffer);
        var result = replayer.ReplayToEnd();

        Assert.Equal(0, result.HandlerFailures);
        Assert.Equal(1, result.UnknownReceived[unknownReceived]);
        Assert.Equal(1, result.UnknownSent[unknownSent]);
        Assert.Equal(1, result.UnhandledReceived[0x2C]);
    }
}
