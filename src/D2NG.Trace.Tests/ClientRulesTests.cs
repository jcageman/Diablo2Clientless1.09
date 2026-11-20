using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.MCP;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Xunit;
using ItemAction = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

[CollectionDefinition(nameof(ReplayRulesOverride), DisableParallelization = true)]
public sealed class ReplayRulesOverride;

[Collection(nameof(ReplayRulesOverride))]
public class ClientRulesTests
{
    private const uint Item = 0x50;

    private static GameData Joined(ClientRules rules)
    {
        var stats = new byte[32];
        stats[13] = 3;
        var data = new GameData(new GameFlags(new D2gsPacket([0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])), new Character(SyntheticTrace.CharacterName, stats), null, rules);
        data.PlayerAssign(new AssignPlayerPacket(new D2gsPacket(SyntheticTrace.Joining().First(p => p[0] == 0x59))));
        return data;
    }

    private static void Receive(GameData data, ItemAction action, uint id) => data.ItemUpdate(new ParseItemPacket(new D2gsPacket(SyntheticGame.Item(action, id))));

    [Theory]
    [InlineData(1, Item)]
    [InlineData(2, null)]
    public void ARemovalTheClientDidNotAskToLiftReachesTheCursorOnlyBeforeCursorNeedsLift(int version, uint? cursor)
    {
        var data = Joined(ClientRules.ForVersion(version));
        Receive(data, ItemAction.PutInContainer, Item);

        Receive(data, ItemAction.RemoveFromContainer, Item);

        Assert.Equal(cursor, data.CursorItem?.Id);
        Assert.Null(data.Inventory.FindItemById(Item));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ALiftTheClientAskedForReachesTheCursorAndASaleDoesNotUnderEitherRule(int version)
    {
        var data = Joined(ClientRules.ForVersion(version));
        Receive(data, ItemAction.PutInContainer, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        Receive(data, ItemAction.RemoveFromContainer, Item);
        Assert.Equal(Item, data.CursorItem?.Id);

        Receive(data, ItemAction.PutInContainer, Item);
        Assert.Null(data.CursorItem);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        ClientActions.Apply(data, ClientAction.Sell, Item);
        Receive(data, ItemAction.RemoveFromContainer, Item);
        Assert.Null(data.CursorItem);
    }

    [Theory]
    [InlineData(0, 100, 100, 31.25)]
    [InlineData(1, 62.5, 50, 0)]
    public void MonsterLifeFollowsStopsAssignsAndDeathsOnlyUnderTheirRules(int version, double afterStop, double assigned, double dying)
    {
        var data = Joined(ClientRules.ForVersion(version));
        data.Act.AddNPC(new AssignNpcPacket(new D2gsPacket(SyntheticGame.AssignNpc(0xE5, NPCCode.Zombie, 5070, 1905))));
        var stop = SyntheticTrace.MonsterStop(0xE5, 5071, 1906);
        stop[9] = 0x50;
        data.Act.UpdateNPCStop(new NPCStopPacket(new D2gsPacket(stop)));
        Assert.Equal(afterStop, data.Act.WorldObjects[(0xE5, EntityType.NPC)].LifePercentage);
        Assert.Equal(new Point(5071, 1906), data.Act.WorldObjects[(0xE5, EntityType.NPC)].LegacyLocation);

        var assign = SyntheticGame.AssignNpc(0xF1, NPCCode.Zombie, 5100, 1920);
        assign[11] = 0x40;
        data.Act.AddNPC(new AssignNpcPacket(new D2gsPacket(assign)));
        Assert.Equal(assigned, data.Act.WorldObjects[(0xF1, EntityType.NPC)].LifePercentage);

        data.Act.UpdateNPCState(new NpcStatePacket(new D2gsPacket(SyntheticGame.NpcState(0xF1, EntityState.Dieing, 0, 0, 40))));
        Assert.Equal(dying, data.Act.WorldObjects[(0xF1, EntityType.NPC)].LifePercentage);
    }

    [Fact]
    public void TheHeaderSaysWhichBuildAndRulesRecordedIt()
    {
        var bot = Read(TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 1", "game-1", "server#1"));
        Assert.Equal(ClientRules.LatestVersion, bot.RulesVersion);
        Assert.Equal(BuildIdentity.Version, bot.Build);
        Assert.Equal(BuildIdentity.Utc, bot.BuildUtc);
        Assert.Equal("server#1", bot.ServerGame);
        Assert.Contains("+", bot.Build, StringComparison.Ordinal);

        var sniffer = Read(TraceFormat.Header(TraceSource.Sniffer, DateTime.UtcNow, "client 1", "game-1"));
        Assert.Equal(TraceFormat.RulesNotApplicable, sniffer.RulesVersion);
        Assert.Same(ClientRules.Latest, ReplayRules.Recorded(sniffer));
    }

    [Fact]
    public void ALiftAnsweredAfterTheNextLiftStillPutsItsItemOnTheCursor()
    {
        var data = Joined(ClientRules.Latest);
        Receive(data, ItemAction.PutInContainer, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item + 1);

        Receive(data, ItemAction.RemoveFromContainer, Item);

        Assert.Equal(Item, data.CursorItem?.Id);
    }

    [Fact]
    public void CursorNeedsLiftAloneKeepsOneOutstandingLift()
    {
        var data = Joined(ClientRules.ForVersion(2));
        Receive(data, ItemAction.PutInContainer, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item + 1);

        Receive(data, ItemAction.RemoveFromContainer, Item);

        Assert.Null(data.CursorItem);
    }

    [Fact]
    public void AnOutstandingLiftEndsOnItsRemovalItsPuttingDownACursorClearAndAnActLoad()
    {
        var data = Joined(ClientRules.Latest);
        Receive(data, ItemAction.PutInContainer, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        Receive(data, ItemAction.RemoveFromContainer, Item);
        Assert.Empty(data.LiftsOutstanding);

        ClientActions.Apply(data, ClientAction.Lift, Item);
        Receive(data, ItemAction.PutInContainer, Item);
        Assert.Empty(data.LiftsOutstanding);

        ClientActions.Apply(data, ClientAction.Lift, Item);
        ClientActions.Apply(data, ClientAction.ClearCursor, 0);
        Assert.Empty(data.LiftsOutstanding);

        ClientActions.Apply(data, ClientAction.Lift, Item);
        data.ActLoaded();
        Assert.Empty(data.LiftsOutstanding);

        for (uint id = 0; id < GameData.MaxLiftsOutstanding + 5; id++)
        {
            ClientActions.Apply(data, ClientAction.Lift, 0x1000 + id);
        }

        Assert.Equal(GameData.MaxLiftsOutstanding, data.LiftsOutstanding.Count);
        Assert.DoesNotContain(0x1000u, data.LiftsOutstanding);
        Assert.Contains(0x1000u + GameData.MaxLiftsOutstanding + 4, data.LiftsOutstanding);
    }

    [Fact]
    public void ALiftTheServerRefusedEndsWhenALaterLiftReachesTheCursor()
    {
        var data = Joined(ClientRules.Latest);
        Receive(data, ItemAction.PutInContainer, Item);
        Receive(data, ItemAction.PutInContainer, Item + 1);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item + 1);

        Receive(data, ItemAction.RemoveFromContainer, Item + 1);
        Assert.Equal(Item + 1, data.CursorItem?.Id);
        Assert.Empty(data.LiftsOutstanding);
        Receive(data, ItemAction.PutInContainer, Item + 1);

        Receive(data, ItemAction.RemoveFromContainer, Item);
        Assert.Null(data.CursorItem);
    }

    [Fact]
    public void AnItemTheServerPutsOnTheCursorUnaskedEndsEveryLift()
    {
        var data = Joined(ClientRules.Latest);
        Receive(data, ItemAction.PutInContainer, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item);

        Receive(data, ItemAction.ToCursor, Item + 7);
        Assert.Equal(Item + 7, data.CursorItem?.Id);
        Assert.Empty(data.LiftsOutstanding);
        Receive(data, ItemAction.PutInContainer, Item + 7);

        Receive(data, ItemAction.RemoveFromContainer, Item);
        Assert.Null(data.CursorItem);
    }

    [Fact]
    public void ADropOrPutDownEndsItsLiftAndEveryOlderOneButNotANewerOne()
    {
        var data = Joined(ClientRules.Latest);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item + 1);
        ClientActions.Apply(data, ClientAction.Lift, Item + 2);

        Receive(data, ItemAction.DropToGround, Item + 1);
        Assert.Equal([Item + 2], data.LiftsOutstanding);

        ClientActions.Apply(data, ClientAction.Lift, Item + 3);
        Receive(data, ItemAction.PutInContainer, Item + 2);
        Assert.Equal([Item + 3], data.LiftsOutstanding);
    }

    [Fact]
    public void ASoldItemsLiftEndsWithItsRemoval()
    {
        var data = Joined(ClientRules.Latest);
        Receive(data, ItemAction.PutInContainer, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        ClientActions.Apply(data, ClientAction.Sell, Item);

        Receive(data, ItemAction.RemoveFromContainer, Item);

        Assert.Null(data.CursorItem);
        Assert.Empty(data.LiftsOutstanding);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void AQuiverWornEmptyIsGoneWhenTheServerTakesItOffUnderEveryRulesVersion(int version)
    {
        var data = Joined(ClientRules.ForVersion(version));
        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(ItemPackets.Stack(ItemAction.Equip, Item, "aqv", 0, 1, D2NG.Core.D2GS.Items.DirectoryType.LeftHand))));

        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(ItemPackets.Stack(ItemAction.Unequip, Item, "aqv", 0, 1, D2NG.Core.D2GS.Items.DirectoryType.LeftHand))));

        Assert.Null(data.CursorItem);
        Assert.False(data.Me.Equipment.ContainsKey(D2NG.Core.D2GS.Items.DirectoryType.LeftHand));
    }

    [Theory]
    [InlineData(3, 2u, Item)]
    [InlineData(4, 0u, null)]
    public void AWornQuiversCountFollowsTheServerOnlyUnderQuiverCountFollowsServer(int version, uint amount, uint? cursor)
    {
        var data = Joined(ClientRules.ForVersion(version));
        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(ItemPackets.Stack(ItemAction.Equip, Item, "aqv", 2, 1, D2NG.Core.D2GS.Items.DirectoryType.LeftHand))));
        Assert.Equal(2u, data.Me.Equipment[D2NG.Core.D2GS.Items.DirectoryType.LeftHand].Amount);
        data.UpdateItemStats(new UpdateItemStatsPacket(new D2gsPacket(ItemPackets.Quantity(Item, 1))));
        data.UpdateItemStats(new UpdateItemStatsPacket(new D2gsPacket(ItemPackets.Quantity(Item, 0))));
        Assert.Equal(amount, data.Me.Equipment[D2NG.Core.D2GS.Items.DirectoryType.LeftHand].Amount);

        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(ItemPackets.Stack(ItemAction.Unequip, Item, "aqv", 2, 1, D2NG.Core.D2GS.Items.DirectoryType.LeftHand))));

        Assert.Equal(cursor, data.CursorItem?.Id);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void ATomesCountFollowsTheServerOnlyUnderCarriedCountFollowsServer(int version, bool follows)
    {
        var data = Joined(ClientRules.ForVersion(version));
        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(ItemPackets.Stack(ItemAction.PutInContainer, Item, "tbk", 2, 1, container: D2NG.Core.D2GS.Items.ContainerType.Inventory))));
        var joined = data.Inventory.FindItemById(Item).Amount;

        // A stack buy of portal scrolls: the server's answer is the tome's new count.
        data.UpdateItemStats(new UpdateItemStatsPacket(new D2gsPacket(ItemPackets.Quantity(Item, 100))));

        Assert.Equal(follows ? 100u : joined, data.Inventory.FindItemById(Item).Amount);
    }

    [Theory]
    [InlineData(4, 800_000_000u + 1_000)]
    [InlineData(5, 400_000_000u + 1_000)]
    public void TheLifetimeExperienceSetsTheTotalOnlyUnderExperienceTotalSets(int version, uint total)
    {
        var data = Joined(ClientRules.ForVersion(version));

        data.AddExperience(new AddExpPacket(new D2gsPacket([0x1C, .. BitConverter.GetBytes(399_999_000u)])));
        data.AddExperience(new AddExpPacket(new D2gsPacket([0x1B, .. BitConverter.GetBytes((ushort)1_000)])));
        data.AddExperience(new AddExpPacket(new D2gsPacket([0x1C, .. BitConverter.GetBytes(400_000_000u)])));
        data.AddExperience(new AddExpPacket(new D2gsPacket([0x1B, .. BitConverter.GetBytes((ushort)1_000)])));

        Assert.Equal(total, data.Me.Experience);
    }

    [Theory]
    [InlineData(5, Item)]
    [InlineData(6, null)]
    public void ASocketedCursorItemLeavesTheCursorOnlyUnderSocketingClearsCursor(int version, uint? cursor)
    {
        const uint Shield = Item + 1;
        var data = Joined(ClientRules.ForVersion(version));
        Receive(data, ItemAction.PutInContainer, Shield);
        Receive(data, ItemAction.PutInContainer, Item);
        ClientActions.Apply(data, ClientAction.Lift, Item);
        Receive(data, ItemAction.RemoveFromContainer, Item);

        // The server's answer to a socket request: the gem, its owner the item it now sits in.
        data.ItemUpdate(new ParseItemPacket(new D2gsPacket(ItemPackets.Owned(ItemAction.ItemInSocket, Item, "gpw", Shield, D2NG.Core.D2GS.Items.ContainerType.Item))));

        Assert.Equal(cursor, data.CursorItem?.Id);
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("4", false)]
    public void TheStateCheckFindsAnEmptiedQuiverOnTheCursorOnlyUnderTheRulesThatPutItThere(string rules, bool found)
    {
        using var root = new TempRoot();
        var folder = Game1.Folder(root);
        new HandTrace("client1", "party1", Game1.Start).Join(1)
            .Received(0.18, ItemPackets.Stack(ItemAction.Equip, 0x94, "aqv", 2, 1, D2NG.Core.D2GS.Items.DirectoryType.LeftHand))
            .Received(4.0, ItemPackets.Quantity(0x94, 1))
            .Received(5.0, ItemPackets.Quantity(0x94, 0))
            .Received(5.833, ItemPackets.Stack(ItemAction.Unequip, 0x94, "aqv", 0, 1, D2NG.Core.D2GS.Items.DirectoryType.LeftHand))
            .Ticks(12, 13, 5057, 1902)
            .WriteTo(folder);

        try
        {
            ReplayRules.Use(rules);
            var (_, _, check) = ItemMoves.Replay(GameQuery.Open(folder), ["client1"]).Single();
            string[] expected = found ? [ItemStateCheck.CursorNotGiven] : [];
            Assert.Equal(expected, check.Found.Select(f => f.Kind));
        }
        finally
        {
            ReplayRules.Use(null);
        }
    }

    private static readonly DateTime BeforeLifeRules = new(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterLifeRules = new(2026, 11, 14, 13, 0, 0, DateTimeKind.Utc);

    /// <summary>A header as builds before the stamp wrote it: it ends after the server game id.</summary>
    private static byte[] Unstamped(TraceSource source, DateTime start)
    {
        var header = TraceFormat.Header(source, start, "client 1", "game-1", "server#1");
        var old = 18 + 2 + "client 1".Length + 2 + "game-1".Length + 2 + "server#1".Length;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)old);
        return header.AsSpan(0, old).ToArray();
    }

    private static string UnstampedFile(TempRoot root, string name, DateTime start, params (TraceRecordKind Kind, byte[] Payload)[] records)
    {
        Directory.CreateDirectory(root.Path);
        var path = Path.Combine(root.Path, name + TraceFormat.Extension);
        using var file = File.Create(path);
        file.Write(Unstamped(TraceSource.Bot, start));
        var elapsed = 0L;
        foreach (var (kind, payload) in records)
        {
            var recordHeader = new byte[TraceFormat.RecordHeaderSize];
            TraceFormat.WriteRecordHeader(recordHeader, kind, elapsed += TimeSpan.TicksPerSecond, payload.Length);
            file.Write(recordHeader);
            file.Write(payload);
        }

        return path;
    }

    [Fact]
    public void AHeaderWrittenBeforeTheStampNamesNoRulesAndIsReplayedUnderAnEstimateSaidToBeOne()
    {
        var read = Read(Unstamped(TraceSource.Bot, BeforeLifeRules));

        Assert.Null(read.RulesVersion);
        Assert.Null(read.Build);
        Assert.Null(read.BuildUtc);
        Assert.Equal("server#1", read.ServerGame);
        var choice = ReplayRules.Resolve(read);
        Assert.True(choice.Estimated);
        Assert.Same(ClientRules.None, choice.Rules);
        var note = Assert.Single(RulesNotes.Of([("client 1", read)]));
        Assert.StartsWith("recorded by an unstamped build, rules not stamped; replayed under rules 0, an estimate - from its start time", note, StringComparison.Ordinal);
        Assert.DoesNotContain("client rules 0", note, StringComparison.Ordinal);
        Assert.StartsWith("replayed under rules 0 (none), an estimate", TraceHeaderIdentity.InForce(read), StringComparison.Ordinal);

        var later = Read(Unstamped(TraceSource.Bot, AfterLifeRules));
        Assert.Same(ClientRules.ForVersion(1), ReplayRules.Recorded(later));
        Assert.Contains("cannot be told from a trace", ReplayRules.Resolve(later).Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnstampedTraceIsEstimatedFromItsOwnRecordsBeforeItsStartTime()
    {
        using var root = new TempRoot();
        byte[] trade = [0x38, 0x01, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] openShop = [(byte)ClientAction.OpenShop, 0x2A, 0x00, 0x00, 0x00];
        var shopEarly = UnstampedFile(root, "shop-early", BeforeLifeRules, (TraceRecordKind.ClientAction, openShop), (TraceRecordKind.D2gsSent, trade));
        var vendorLate = UnstampedFile(root, "vendor-late", AfterLifeRules, (TraceRecordKind.D2gsSent, trade));
        var nothingLate = UnstampedFile(root, "nothing-late", AfterLifeRules, (TraceRecordKind.D2gsSent, [0x6D, 0x00]));

        Assert.Equal(1, Choice(shopEarly).Rules.Version);
        Assert.Contains("shop opening", Choice(shopEarly).Basis, StringComparison.Ordinal);
        Assert.Equal(0, Choice(vendorLate).Rules.Version);
        Assert.Contains("no shop opening", Choice(vendorLate).Basis, StringComparison.Ordinal);
        Assert.Equal(1, Choice(nothingLate).Rules.Version);
        Assert.Contains("from its start time", Choice(nothingLate).Basis, StringComparison.Ordinal);

        using var replayer = new Replayer(vendorLate);
        Assert.Same(ClientRules.None, replayer.Rules);
        Assert.Contains("replayed under rules 0, an estimate - the trace asks vendors", Assert.Single(RulesNotes.OfFiles([vendorLate])), StringComparison.Ordinal);

        static RulesChoice Choice(string path)
        {
            using var reader = new TraceReader(path);
            return ReplayRules.Resolve(reader.Header, path);
        }
    }

    [Fact]
    public void ASnifferCaptureWrittenBeforeTheStampReplaysUnderTheLatest()
    {
        var read = Read(Unstamped(TraceSource.Sniffer, BeforeLifeRules));

        Assert.Null(read.RulesVersion);
        Assert.Same(ClientRules.Latest, ReplayRules.Recorded(read));
        Assert.Same(ClientRules.Latest, ReplayRules.For(read));
        Assert.Equal($"recorded by an unstamped build, client rules not applicable (a real client); replayed under rules {ClientRules.LatestVersion}", Assert.Single(RulesNotes.Of([("client 1", read)])));
    }

    [Fact]
    public void ATraceFromANewerRulesVersionReplaysUnderTheLatestAndSaysSoOnce()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "newer.d2trace");
        Directory.CreateDirectory(root.Path);
        using (var file = File.Create(path))
        {
            file.Write(TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 1", "game-1", rulesVersion: ClientRules.LatestVersion + 1));
        }

        using var replayer = new Replayer(path);
        replayer.ReplayToEnd();

        Assert.Same(ClientRules.Latest, replayer.Rules);
        Assert.Equal(ReplayRules.NewerRulesNote(ClientRules.LatestVersion + 1), Assert.Single(replayer.Result.Notes));
        var notes = RulesNotes.OfFiles([path]);
        Assert.Equal(2, notes.Count);
        Assert.Contains($"client rules {ClientRules.LatestVersion + 1}; replayed under rules {ClientRules.LatestVersion}", notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ClientsRecordedUnderDifferentRulesAreNamedApart()
    {
        var older = Read(TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 1", "game-1", rulesVersion: 1));
        var newer = Read(TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 2", "game-1"));

        var note = Assert.Single(RulesNotes.Of([("client 1", older), ("client 2", newer)]));

        Assert.StartsWith("the clients of this game were recorded by different builds or rules - client 1: ", note, StringComparison.Ordinal);
        Assert.Contains("client rules 1, replayed under rules 1; client 2: ", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRulesOptionOverridesEveryReplayUntilSetBack()
    {
        var old = new TraceHeader { RulesVersion = 0, ClientLabel = "client 1" };
        try
        {
            ReplayRules.UseOption(["replay", "x", "--rules", "latest"]);
            Assert.Same(ClientRules.Latest, ReplayRules.For(old));
            Assert.EndsWith($"replayed under rules {ClientRules.LatestVersion} (--rules latest)", RulesNotes.Of([("client 1", old)])[0], StringComparison.Ordinal);
            ReplayRules.UseOption(["replay", "x", "--rules", "1"]);
            Assert.Same(ClientRules.ForVersion(1), ReplayRules.For(old));
            Assert.Throws<ArgumentException>(() => ReplayRules.Use((ClientRules.LatestVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Throws<ArgumentException>(() => ReplayRules.UseOption(["replay", "x", "--rules"]));
            ReplayRules.UseOption(["replay", "x", "--rules", "recorded"]);
            Assert.Same(ClientRules.None, ReplayRules.For(old));
        }
        finally
        {
            ReplayRules.Use(null);
        }
    }

    [Fact]
    public void EverySidecarFileStartsWithTheBuildThatWroteIt()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        recorder.Events.Step("client 1", "game-1", "run", StepPhase.Start);
        recorder.FlushNow();

        using var stream = new FileStream(recorder.Sidecar.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var first = new StreamReader(stream).ReadLine();
        Assert.Contains("\"k\":\"build\"", first, StringComparison.Ordinal);
        Assert.Contains($"\"rules\":{ClientRules.LatestVersion}", first, StringComparison.Ordinal);
        Assert.Contains(BuildIdentity.Version, first, StringComparison.Ordinal);
        Assert.Equal(["step"], SidecarReader.Read([recorder.Sidecar.Path], DateTime.MinValue.ToUniversalTime(), DateTime.MaxValue.ToUniversalTime()).Select(e => e.Kind));
    }

    [Fact]
    public void ARuleCanBeSwitchedOffOnItsOwn()
    {
        var without = ClientRules.Latest.Without(ClientRule.CursorNeedsLift);
        Assert.False(without.CursorNeedsLift);
        Assert.True(without.MonsterLifeFromStopsAndAssigns);
        Assert.True(without.Partial);
        Assert.Equal([ClientRule.MonsterLifeFromStopsAndAssigns, ClientRule.LifeZeroForDyingAndDead], ClientRules.ForVersion(1).InForce);
        Assert.Empty(ClientRules.None.InForce);
    }

    private static TraceHeader Read(byte[] header)
    {
        using var reader = new TraceReader(new MemoryStream(header));
        return reader.Header;
    }
}
