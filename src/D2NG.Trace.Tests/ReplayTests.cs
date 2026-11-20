using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using System;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class ReplayTests
{
    [Fact]
    public void ReplayBuildsTheStateThePacketsDescribe()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var path = SyntheticTrace.Record(recorder, "game-1", playingPackets: 0);

        using var replayer = new Replayer(path);
        var result = replayer.ReplayToEnd();

        Assert.Equal(7, result.PacketsReplayed);
        Assert.Equal(0, result.HandlerFailures);
        var game = replayer.Game;
        Assert.Equal(SyntheticTrace.CharacterName, game.Me.Name);
        Assert.Equal(CharacterClass.Necromancer, game.Me.Class);
        Assert.Equal(Difficulty.Hell, game.Difficulty);
        Assert.Equal(SyntheticTrace.MapId, game.MapId);
        Assert.Equal(Act.Act1, game.Act);
        Assert.Equal(Area.RogueEncampment, game.Area);
        Assert.Equal(new Point(5057, 1902), game.Me.Location);
        Assert.Equal(1616, game.Me.Life);
        Assert.Equal(72, game.Me.Mana);
        Assert.Equal(2, game.WorldObjects.Values.Count(o => o.Type == EntityType.NPC));
        Assert.Single(game.Players);
    }

    [Fact]
    public void LaterPacketsMoveTheMonstersAndTakeTheirLife()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenGame("game-1", "client 1");
        channel.Write(TraceRecordKind.D2gsSent, SyntheticTrace.Logon());
        foreach (var packet in SyntheticTrace.Joining())
        {
            channel.Write(TraceRecordKind.D2gsReceived, packet);
        }

        channel.Write(TraceRecordKind.D2gsReceived, SyntheticTrace.MonsterStop(0xE9, 7000, 8000));
        channel.Write(TraceRecordKind.D2gsReceived, SyntheticTrace.MonsterHit(0xE5, 64));
        channel.Close();
        recorder.FlushNow();

        using var replayer = new Replayer(channel.Path);
        replayer.ReplayToEnd();
        var monsters = replayer.Game.WorldObjects;
        Assert.Equal(new Point(7000, 8000), monsters[(0xE9, EntityType.NPC)].Location);
        Assert.Equal(50, monsters[(0xE5, EntityType.NPC)].LifePercentage);
    }

    [Fact]
    public void LiveDigestsMatchTheReplay()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder(bufferBytes: 1024 * 1024, digestInterval: TimeSpan.Zero);
        var path = SyntheticTrace.Record(recorder, "game-1", playingPackets: 300);

        using var replayer = new Replayer(path);
        var result = replayer.ReplayToEnd();

        Assert.Equal(304, result.Digests);
        Assert.Equal(0, result.MismatchedDigests);
        Assert.Empty(result.FirstMismatches);
    }

    [Fact]
    public void ACorruptedDigestIsReportedAsAMismatch()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenGame("game-1", "client 1");
        channel.Write(TraceRecordKind.D2gsSent, SyntheticTrace.Logon());
        foreach (var packet in SyntheticTrace.Joining())
        {
            channel.Write(TraceRecordKind.D2gsReceived, packet);
        }

        var truth = new TraceDigest
        {
            X = 5057, Y = 1902, Life = 1616, Mana = 72, Area = Area.RogueEncampment, Act = Act.Act1,
            Difficulty = Difficulty.Hell, MapId = SyntheticTrace.MapId, Npcs = 2, Players = 1,
        };
        channel.WriteDigest(truth);
        channel.WriteDigest(truth with { Life = 1, Npcs = 7 });
        channel.Close();
        recorder.FlushNow();

        using var replayer = new Replayer(channel.Path);
        var result = replayer.ReplayToEnd();

        Assert.Equal(2, result.Digests);
        Assert.Equal(1, result.MismatchedDigests);
        Assert.Equal(
            [("Life", "1", "1616"), ("Npcs", "7", "2")],
            result.FirstMismatches.Select(m => (m.Field, m.Expected, m.Actual)));
    }

    [Fact]
    public void ReplayStopsAtATimeAndCarriesOnFromThere()
    {
        using var root = new TempRoot();
        System.IO.Directory.CreateDirectory(root.Path);
        var path = System.IO.Path.Combine(root.Path, "timed.d2trace");
        using (var file = System.IO.File.Create(path))
        {
            file.Write(TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 1", "game-1"));
            var at = 0L;
            void Record(TraceRecordKind kind, byte[] payload, long ticks)
            {
                var record = new byte[TraceFormat.RecordHeaderSize + payload.Length];
                TraceFormat.WriteRecordHeader(record, kind, ticks, payload.Length);
                payload.CopyTo(record, TraceFormat.RecordHeaderSize);
                file.Write(record);
            }

            Record(TraceRecordKind.D2gsSent, SyntheticTrace.Logon(), at);
            foreach (var packet in SyntheticTrace.Joining())
            {
                Record(TraceRecordKind.D2gsReceived, packet, at);
            }

            Record(TraceRecordKind.D2gsReceived, SyntheticTrace.MonsterStop(0xE9, 7000, 8000), TimeSpan.FromSeconds(10).Ticks);
            Record(TraceRecordKind.D2gsReceived, SyntheticTrace.MonsterStop(0xE9, 7100, 8100), TimeSpan.FromSeconds(20).Ticks);
        }

        using var replayer = new Replayer(path);
        replayer.ReplayTo(TimeSpan.FromSeconds(15));
        Assert.Equal(new Point(7000, 8000), replayer.Game.WorldObjects[(0xE9, EntityType.NPC)].Location);
        Assert.Equal(TimeSpan.FromSeconds(10), replayer.Result.ReachedElapsed);

        replayer.ReplayToEnd();
        Assert.Equal(new Point(7100, 8100), replayer.Game.WorldObjects[(0xE9, EntityType.NPC)].Location);
        Assert.Equal(9, replayer.Result.PacketsReplayed);
    }
}
