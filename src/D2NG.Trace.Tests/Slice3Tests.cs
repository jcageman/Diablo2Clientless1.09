using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

/// <summary>The reviewer's findings on slice 2, one regression test each.</summary>
public class SameNameGameTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static string GameFolder(TempRoot root) => Path.Combine(HandTrace.DateFolder(root.Path, Start), "party1");

    [Fact]
    public void TwoGamesOfOneNameInOneFolderAreTwoGamesByTheirLogonToken()
    {
        using var root = new TempRoot();
        var folder = GameFolder(root);
        new HandTrace("client1", "party1", Start).Join(1, token: 9).Received(60, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        new HandTrace("client2", "party1", Start.AddSeconds(1)).Join(2, token: 9).Received(59, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        new HandTrace("client1", "party1", Start.AddMinutes(1).AddSeconds(5)).Join(1, token: 25).Received(30, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder, "client1-2");
        new HandTrace("client2", "party1", Start.AddMinutes(1).AddSeconds(6)).Join(2, token: 25).Received(30, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder, "client2-2");

        var games = GameTrace.OpenAll(folder);

        Assert.Equal(2, games.Count);
        Assert.All(games, g => Assert.Equal(2, g.Files.Count));
        Assert.All(games, g => Assert.All(g.Files, f => Assert.Equal(1, f.Part)));
        Assert.True(games[0].EndUtc < games[1].StartUtc);
        var refused = Assert.Throws<ArgumentException>(() => GameTrace.Open(folder));
        Assert.Contains("#2", refused.Message);
        Assert.Equal(games[1].StartUtc, GameTrace.Open(folder + "#2").StartUtc);
        Assert.Equal(folder + "#2", games[1].Id);
    }

    [Fact]
    public void WithoutAnyIdAFileStartingAfterEveryEarlierFileEndedStartsANewGame()
    {
        using var root = new TempRoot();
        var folder = GameFolder(root);
        static HandTrace NoLogon(string label, DateTime start, double last) => new HandTrace(label, "party1", start).Received(0.1, [0x01, 0x02, 0, 0, 0, 0, 0, 0]).Received(last, [0x6A]);
        NoLogon("client1", Start, 50).WriteTo(folder);
        NoLogon("client2", Start, 40).WriteTo(folder);
        NoLogon("client2", Start.AddSeconds(45), 20).WriteTo(folder, "client2-2");
        NoLogon("client1", Start.AddMinutes(10), 20).WriteTo(folder, "client1-2");

        var games = GameTrace.OpenAll(folder);

        Assert.Equal(2, games.Count);
        Assert.Equal(3, games[0].Files.Count);
        Assert.Equal(2, games[0].FilesOf("client2").Last().Part);
        Assert.Single(games[1].Files);
    }

    [Fact]
    public void TheRecorderGivesANewGameOfAnOldNameItsOwnFolderAndARejoinTheSameOne()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        string Record(string id, string label)
        {
            var channel = recorder.OpenGame("party1", label, id);
            channel.Write(TraceRecordKind.D2gsReceived, [0x01]);
            recorder.FlushNow();
            channel.Close();
            recorder.FlushNow();
            return channel.Path;
        }

        var first = Record("10.0.0.1#9", "client1");
        var rejoin = Record("10.0.0.1#9", "client1");
        var other = Record("10.0.0.1#25", "client1");

        Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(rejoin));
        Assert.EndsWith("client1-2.d2trace", rejoin);
        Assert.EndsWith("party1~2", Path.GetDirectoryName(other));
        using var reader = new TraceReader(other);
        Assert.Equal("10.0.0.1#25", reader.Header.ServerGame);
    }

    [Fact]
    public void SilenceIsNotMeasuredAcrossARejoin()
    {
        using var root = new TempRoot();
        var folder = GameFolder(root);
        new HandTrace("client1", "party1", Start).Join(1).Received(5, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        new HandTrace("client1", "party1", Start.AddSeconds(25)).Join(1).Received(5, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder, "client1-2");
        new HandTrace("client2", "party1", Start).Join(2).Received(40, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        var game = GameTrace.Open(folder);

        var silence = new SilenceCheck();
        ClientReplay.Run(game, "client1", TimeSpan.MaxValue, [silence])?.Replayer.Dispose();

        Assert.Equal(2, game.FilesOf("client1").Count());
        Assert.Empty(silence.Found);
    }

    [Fact]
    public void ARefusalLongBeforeTheGameIsNotItsIncident()
    {
        using var root = new TempRoot();
        var folder = GameFolder(root);
        new HandTrace("client1", "party1", Start).Join(1).Received(30, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        var realm = Path.Combine(HandTrace.DateFolder(root.Path, Start), "realm");
        new HandTrace("instance1", "", Start.AddMinutes(-20))
            .Add(TraceRecordKind.McpSent, 0, Packets.CharacterLogon("client1"))
            .Add(TraceRecordKind.McpSent, 1, Packets.JoinRequest("party1"))
            .Add(TraceRecordKind.McpReceived, 1.1, Packets.JoinAnswer(0x2A))
            .Add(TraceRecordKind.McpSent, 1160, Packets.JoinRequest("party1"))
            .Add(TraceRecordKind.McpReceived, 1160.1, Packets.JoinAnswer(0x2B))
            .Add(TraceRecordKind.McpSent, 1199, Packets.JoinRequest("party1"))
            .Add(TraceRecordKind.McpReceived, 1199.1, Packets.JoinAnswer(0))
            .WriteTo(realm, "instance1-115000");

        var answers = GameQuery.Open(folder).RealmAnswers().ToList();

        Assert.Equal([0x2Bu, 0u], answers.Select(a => a.Result));
        Assert.DoesNotContain(Incidents.Find(GameQuery.Open(folder)), i => i.Detail.Contains("0x2A", StringComparison.Ordinal));
    }

    [Fact]
    public void NextCommandsNeverEndBeforeTheyStart()
    {
        var next = Incidents.NextCommand("game", new Incident(TimeSpan.FromSeconds(-90), null, "join refused", "x", "realm"));
        Assert.Contains("--from 00:00.000 --to 00:22.000", next);
    }

    [Fact]
    public void LogLinesOfAnotherBotProcessAreNotThisGamesIncidents()
    {
        using var root = new TempRoot();
        var folder = GameFolder(root);
        var date = HandTrace.DateFolder(root.Path, Start);
        new HandTrace("client1", "party1", Start).Join(1).Received(30, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        new SidecarFile(Start)
            .Typed(1, "move", "client1", "party1", ("kind", "walk"), ("outcome", "arrived"))
            .Log(5, LogEventLevel.Error, "{Character} lost its way", ("Character", "client1"))
            .WriteTo(date, "115900");
        new SidecarFile(Start)
            .Typed(1, "move", "mephsorc", "meph5", ("kind", "teleport"), ("outcome", "arrived"))
            .Log(6, LogEventLevel.Error, "Mephisto run threw")
            .WriteTo(date, "113000");

        var query = GameQuery.Open(folder);
        var errors = Incidents.Find(query).Where(i => i.Kind == "error").ToList();

        Assert.Equal(["115900"], query.Processes);
        var error = Assert.Single(errors);
        Assert.Contains("lost its way", error.Detail);
    }

    [Fact]
    public void OnlyTrueDuplicatesMerge()
    {
        var merged = Incidents.Merge(
        [
            new Incident(TimeSpan.FromSeconds(10), "client1", "error", "first failure", "sidecar"),
            new Incident(TimeSpan.FromSeconds(11), "client1", "error", "second failure", "sidecar"),
            new Incident(TimeSpan.FromSeconds(20), "client1", "death", "corpse assigned", "replay"),
            new Incident(TimeSpan.FromSeconds(20.5), "client1", "death", "the bot saw its corpse", "sidecar"),
            new Incident(TimeSpan.FromSeconds(30), "client1", "error", "same", "sidecar"),
            new Incident(TimeSpan.FromSeconds(31), "client1", "error", "same", "sidecar"),
        ]);

        Assert.Equal(["first failure", "second failure", "corpse assigned", "same"], merged.Select(i => i.Detail));
    }

    [Fact]
    public void TheQuestSnapshotAtJoinIsNotAChange()
    {
        using var root = new TempRoot();
        var folder = GameFolder(root);
        new HandTrace("client1", "party1", Start).Join(1)
            .Received(1, SyntheticGame.QuestInfo(1, (1, 0x1001), (2, 0x1001), (3, 0x0001)))
            .Received(5, SyntheticGame.QuestInfo(1, (1, 0x1001), (2, 0x1003), (3, 0x0001)))
            .WriteTo(folder);
        var game = GameTrace.Open(folder);

        var events = new GameEvents(25);
        ClientReplay.Run(game, "client1", TimeSpan.MaxValue, [events])?.Replayer.Dispose();

        var quest = Assert.Single(events.Entries, e => e.Text.StartsWith("quest ", StringComparison.Ordinal));
        Assert.Contains("0x1001 -> 0x1003", quest.Text);
    }

    [Fact]
    public void AGroupThatStartedBeforeFromIsKept()
    {
        using var root = new TempRoot();
        var folder = GameFolder(root);
        var file = new HandTrace("client1", "party1", Start).Join(1);
        for (var at = 10.0; at <= 14; at += 0.5)
        {
            file.Sent(at, SyntheticGame.Location(0x0C, 5070, 1910));
        }

        file.Received(20, SyntheticGame.LifeMana(1000, 100, 5057, 1902)).WriteTo(folder);
        var query = GameQuery.Open(folder);

        var entries = Timeline.Build(query, new TimelineOptions { Client = "client1", From = TimeSpan.FromSeconds(12), To = TimeSpan.FromSeconds(20) });

        var casts = Assert.Single(entries, e => e.Text.StartsWith("x9 ", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(10), casts.Offset);
        Assert.Equal(TimeSpan.FromSeconds(14), casts.End);
    }
}

public class UnitTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnitsCarryTheirEntityTypeSoOneIdNamesTwoUnits()
    {
        var npcHit = new TraceRecord(TraceRecordKind.D2gsReceived, TimeSpan.Zero, SyntheticGame.NpcHit(1, 50));
        var reassign = new TraceRecord(TraceRecordKind.D2gsReceived, TimeSpan.Zero, SyntheticGame.Reassign(1, 5000, 5000));

        Assert.Equal((EntityType.NPC, 1u, "unit"), PacketQueries.UnitsIn(npcHit).Single());
        Assert.Equal((EntityType.Player, 1u, "unit"), PacketQueries.UnitsIn(reassign).Single());
        Assert.True(PacketQueries.TryUnit("npc:0x1C9", out var type, out var id));
        Assert.Equal((EntityType.NPC, 0x1C9u), (type.Value, id));
        Assert.True(PacketQueries.TryUnit("player:0x1", out type, out id));
        Assert.Equal((EntityType.Player, 1u), (type.Value, id));
        Assert.True(PacketQueries.TryUnit("object:0x2E", out type, out _));
        Assert.Equal(EntityType.Object, type);
        Assert.True(PacketQueries.TryUnit("0x2E", out type, out _));
        Assert.Null(type);
    }

    [Fact]
    public void AHistoryOfAnNpcLeavesOutThePlayerOfTheSameId()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "party1");
        new HandTrace("client1", "party1", Start).Join(1)
            .Received(1, SyntheticGame.AssignNpc(1, NPCCode.Zombie, 5060, 1905))
            .Received(2, SyntheticGame.Reassign(1, 5058, 1903))
            .Received(3, SyntheticGame.NpcHit(1, 50))
            .WriteTo(folder);

        var history = new UnitHistory(EntityType.NPC, 1, TimeSpan.Zero, TimeSpan.MaxValue);
        ClientReplay.Run(GameTrace.Open(folder), "client1", TimeSpan.MaxValue, [history])?.Replayer.Dispose();

        Assert.Equal(2, history.Entries.Count);
        Assert.DoesNotContain(history.Entries, e => e.Packet.Contains("ReassignPlayer", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExactCodeNameComesFirstAndAShortPacketIsTheLastMention()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "party1");
        new HandTrace("client1", "party1", Start).Join(1)
            .Received(1, SyntheticGame.AssignNpc(0x31, NPCCode.MephistoGhost, 5061, 1905))
            .Received(2, SyntheticGame.AssignNpc(0x30, NPCCode.Mephisto, 5060, 1905))
            .Received(3, SyntheticGame.NpcHit(0x30, 40))
            .Received(4, SyntheticGame.Remove(EntityType.NPC, 0x30))
            .WriteTo(folder);

        var found = PacketQueries.FindUnits(GameQuery.Open(folder), "client1", "Mephisto");

        Assert.Equal(2, found.Count);
        Assert.True(found[0].Exact);
        Assert.Equal(0x30u, found[0].Id);
        Assert.Equal(TimeSpan.FromSeconds(4), found[0].Last);
    }
}

public class RobustnessTests
{
    [Fact]
    public void TheSidecarRecoversAfterAWriteFailureAndCountsWhatItLost()
    {
        using var root = new TempRoot();
        var directory = Path.Combine(root.Path, "events");
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(directory, "a file where the folder should be");
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var sidecar = new TraceSidecar(directory, "events-120000", 1000) { Clock = () => now };

        sidecar.Enqueue(new TraceEvent("move", "client1", "party1", [("kind", "walk")]));
        sidecar.Drain();
        sidecar.Enqueue(new TraceEvent("move", "client1", "party1", [("kind", "walk")]));
        sidecar.Drain();
        File.Delete(directory);
        now = now.AddSeconds(31);
        sidecar.Enqueue(new TraceEvent("move", "client1", "party1", [("kind", "teleport")]));
        sidecar.Drain();
        sidecar.Close();

        var lines = File.ReadAllLines(sidecar.Path);
        Assert.Contains(lines, l => l.Contains("\"k\":\"gap\"", StringComparison.Ordinal) && l.Contains("\"n\":2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("teleport", StringComparison.Ordinal));
    }

    [Fact]
    public void AContinuationSidecarIsTheSameProcess()
        => Assert.Equal("213857", SidecarReader.ProcessOf(Path.Combine("x", "events-213857-2.jsonl")));

    [Fact]
    public void TrimmingSurvivesAFolderThatVanishesMidway()
    {
        using var root = new TempRoot();
        foreach (var date in new[] { "20261101", "20261102" })
        {
            var game = Path.Combine(root.Path, date, "game1");
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "c.d2trace"), new byte[1000]);
            Directory.CreateDirectory(Path.Combine(root.Path, date, "realm"));
            File.WriteAllBytes(Path.Combine(root.Path, date, "realm", "r.d2trace"), new byte[1000]);
        }

        var deleted = TraceRecorder.EnforceCap(root.Path, 0, folder =>
        {
            Directory.Delete(folder, recursive: true);
            var other = Path.Combine(root.Path, "20261102");
            if (Directory.Exists(other))
            {
                Directory.Delete(other, recursive: true);
            }
        });

        Assert.True(deleted >= 1);
    }

    [Fact]
    public void ARealmFileDeletedBeforeItIsReadIsSkipped()
        => Assert.Empty(RealmTraces.Read([Path.Combine(Path.GetTempPath(), "no-such-realm-" + Guid.NewGuid().ToString("N") + ".d2trace")]).Answers);
}
