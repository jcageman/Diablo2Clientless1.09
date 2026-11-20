using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class RecorderTests
{
    private static readonly TraceRecordKind[] PacketKinds =
    [
        TraceRecordKind.D2gsReceived, TraceRecordKind.D2gsSent,
        TraceRecordKind.BncsReceived, TraceRecordKind.BncsSent,
        TraceRecordKind.McpReceived, TraceRecordKind.McpSent,
    ];

    [Fact]
    public void EveryKindReadsBackAsWritten()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenGame("game-1", "client 1");
        var payloads = PacketKinds.Select((kind, i) => Enumerable.Range(0, 5 + i).Select(b => (byte)(b + i)).ToArray()).ToArray();
        for (var i = 0; i < PacketKinds.Length; i++)
        {
            channel.Write(PacketKinds[i], payloads[i]);
        }

        var digest = new TraceDigest { X = 5057, Y = 1902, Life = 1616, Area = Area.RogueEncampment, MapId = 0x1234, Npcs = 3, Items = 9 };
        channel.WriteDigest(digest);
        channel.Close();
        recorder.FlushNow();

        using var reader = new TraceReader(channel.Path);
        Assert.Equal(TraceSource.Bot, reader.Header.Source);
        Assert.Equal("client 1", reader.Header.ClientLabel);
        Assert.Equal("game-1", reader.Header.GameName);
        Assert.Equal(TraceFormat.Version, reader.Header.Version);
        Assert.Equal(channel.StartUtc, reader.Header.StartUtc);
        var records = reader.Records().ToList();
        Assert.Equal([.. PacketKinds, TraceRecordKind.Digest], records.Select(r => r.Kind));
        for (var i = 0; i < PacketKinds.Length; i++)
        {
            Assert.Equal(payloads[i], records[i].Payload);
        }

        Assert.Equal(digest, TraceDigest.FromBytes(records[^1].Payload));
        Assert.True(records.Zip(records.Skip(1)).All(pair => pair.First.Elapsed <= pair.Second.Elapsed));
        Assert.False(reader.Truncated);
        Assert.Equal(0, reader.Gaps);
    }

    [Fact]
    public void AFullBufferDropsRecordsAndLeavesAGapRecordWhenThereIsRoomAgain()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder(bufferBytes: 256);
        var channel = recorder.OpenGame("game-1", "client 1");
        var payload = new byte[30];
        for (var i = 0; i < 20; i++)
        {
            payload[0] = (byte)i;
            channel.Write(TraceRecordKind.D2gsReceived, payload);
        }

        recorder.FlushNow();
        payload[0] = 99;
        channel.Write(TraceRecordKind.D2gsReceived, payload);
        channel.Close();
        recorder.FlushNow();
        recorder.FlushNow();

        using var reader = new TraceReader(channel.Path);
        var records = reader.Records().ToList();
        var kept = 256 / (TraceFormat.RecordHeaderSize + 30);
        Assert.Equal(20 - kept, channel.DroppedRecords);
        Assert.Equal(Enumerable.Range(0, kept).Select(i => (byte)i), records.Take(kept).Select(r => r.Payload[0]));
        Assert.Equal(TraceRecordKind.Gap, records[kept].Kind);
        Assert.Equal((uint)(20 - kept), records[kept].DroppedCount);
        Assert.Equal(99, records[kept + 1].Payload[0]);
        Assert.Equal(1, reader.Gaps);
        Assert.Equal(20 - kept, reader.DroppedRecords);
    }

    [Fact]
    public void ARecordCutShortEndsTheFileAndIsReported()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenGame("game-1", "client 1");
        channel.Write(TraceRecordKind.D2gsReceived, new byte[] { 1, 2, 3 });
        channel.Write(TraceRecordKind.D2gsReceived, new byte[] { 4, 5, 6, 7, 8, 9 });
        channel.Close();
        recorder.FlushNow();
        using (var file = new FileStream(channel.Path, FileMode.Open))
        {
            file.SetLength(file.Length - 4);
        }

        using var reader = new TraceReader(channel.Path);
        var records = reader.Records().ToList();
        Assert.Equal([1, 2, 3], Assert.Single(records).Payload);
        Assert.True(reader.Truncated);
    }

    [Fact]
    public void AnUnknownKindIsSkippedByItsLength()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        var path = Path.Combine(root.Path, "future.d2trace");
        using (var file = File.Create(path))
        {
            file.Write(TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 1", "game-1"));
            var record = new byte[TraceFormat.RecordHeaderSize + 7];
            TraceFormat.WriteRecordHeader(record, (TraceRecordKind)200, 10, 7);
            file.Write(record);
            var known = new byte[TraceFormat.RecordHeaderSize + 2];
            TraceFormat.WriteRecordHeader(known, TraceRecordKind.D2gsReceived, 20, 2);
            known[^2] = 0xAB;
            known[^1] = 0xCD;
            file.Write(known);
        }

        using var reader = new TraceReader(path);
        var records = reader.Records().ToList();
        Assert.Equal([(TraceRecordKind)200, TraceRecordKind.D2gsReceived], records.Select(r => r.Kind));
        Assert.Equal([0xAB, 0xCD], records[1].Payload);
        Assert.False(reader.Truncated);

        using var replayer = new Replayer(path);
        Assert.Equal(1, replayer.ReplayToEnd().PacketsReplayed);
    }

    [Fact]
    public void RejoiningAGameWritesASecondFile()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        for (var attempt = 1; ; attempt++)
        {
            var game = $"game-{attempt}";
            var dateBefore = Today();
            var paths = new List<string>();
            for (var join = 0; join < 3; join++)
            {
                var channel = recorder.OpenGame(game, "client 1");
                channel.Write(TraceRecordKind.D2gsReceived, new byte[] { 0x01 });
                channel.Close();
                recorder.FlushNow();
                paths.Add(channel.Path);
            }

            if (Today() != dateBefore && attempt == 1)
            {
                continue;
            }

            var folder = Path.Combine(root.Path, dateBefore, game);
            Assert.Equal(
                [Path.Combine(folder, "client 1.d2trace"), Path.Combine(folder, "client 1-2.d2trace"), Path.Combine(folder, "client 1-3.d2trace")],
                paths);
            return;
        }
    }

    private static string Today() => DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(".", "_")]
    [InlineData("..", "__")]
    [InlineData("game.", "game_")]
    [InlineData("game. .", "game___")]
    [InlineData("a.b", "a.b")]
    public void AGameNameWindowsWouldAlterGetsAFolderOfItsOwnInsideTheDateFolder(string game, string expected)
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenGame(game, "client 1");
        channel.Write(TraceRecordKind.D2gsReceived, new byte[] { 0x01 });
        channel.Close();
        recorder.FlushNow();

        var gameFolder = Path.GetDirectoryName(channel.Path);
        Assert.Equal(expected, Path.GetFileName(gameFolder));
        Assert.Equal(Path.GetFullPath(root.Path), Path.GetDirectoryName(Path.GetDirectoryName(gameFolder)));
        using var reader = new TraceReader(channel.Path);
        Assert.Equal(game, reader.Header.GameName);
    }

    [Fact]
    public void AChannelAllocatesItsBuffersOnlyOnItsFirstWrite()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder(bufferBytes: 16 * 1024 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var channel = recorder.OpenRealm("instance1");
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);

        channel.Write(TraceRecordKind.BncsSent, new byte[] { 0xFF, 0x50, 0x04, 0x00 });
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before >= 2 * 16 * 1024 * 1024);
        channel.Close();
        recorder.FlushNow();
    }

    [Fact]
    public void AClientClosesItsRealmTraceOnDisconnectAndAReconnectWritesANewRealmFile()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var client = new Client();
        client.TraceRealm(recorder);
        var first = client.RealmTrace;
        client.TraceRealm(recorder);
        Assert.Same(first, client.RealmTrace);
        first.Write(TraceRecordKind.BncsSent, new byte[] { 0xFF, 0x50, 0x04, 0x00 });

        client.Disconnect();
        recorder.FlushNow();
        Assert.Null(client.RealmTrace);
        Assert.Equal(0, recorder.OpenChannels);

        client.TraceRealm(recorder);
        var second = client.RealmTrace;
        Assert.NotSame(first, second);
        second.Write(TraceRecordKind.BncsSent, new byte[] { 0xFF, 0x51, 0x04, 0x00 });
        client.Disconnect();
        recorder.FlushNow();

        Assert.Equal(0, recorder.OpenChannels);
        Assert.Equal(Path.GetDirectoryName(first.Path), Path.GetDirectoryName(second.Path));
        Assert.Equal(Path.GetFileNameWithoutExtension(first.Path) + "-2", Path.GetFileNameWithoutExtension(second.Path));
        using var reader = new TraceReader(second.Path);
        Assert.Equal(0x51, Assert.Single(reader.Records()).Payload[1]);
    }

    [Fact]
    public void AClientActionDuringADigestCaptureIsRecordedAfterTheDigest()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder(digestInterval: TimeSpan.Zero);
        var channel = recorder.OpenGame("game-1", "client 1");
        var gameServer = new GameServer { Trace = channel };
        var game = new Game(gameServer);
        var stats = new byte[32];
        stats[13] = 3;
        game.SelectCharacter(new D2NG.Core.MCP.Character(SyntheticTrace.CharacterName, stats));
        foreach (var packet in SyntheticTrace.Joining())
        {
            gameServer.OnPacketReceived(new D2gsPacket(packet));
        }

        using var captureStarted = new System.Threading.ManualResetEventSlim();
        using var actionDone = new System.Threading.ManualResetEventSlim();
        gameServer.DigestSource = () =>
        {
            captureStarted.Set();
            actionDone.Wait(TimeSpan.FromMilliseconds(500));
            return new TraceDigest { Life = 1 };
        };
        var action = new System.Threading.Thread(() =>
        {
            captureStarted.Wait();
            game.Apply(ClientAction.Use, 7);
            actionDone.Set();
        });
        action.Start();

        gameServer.OnPacketReceived(new D2gsPacket([0xFE]));
        action.Join();
        channel.Close();
        recorder.FlushNow();

        using var reader = new TraceReader(channel.Path);
        Assert.Equal(
            [TraceRecordKind.Digest, TraceRecordKind.ClientAction],
            reader.Records().Where(r => r.Kind != TraceRecordKind.D2gsReceived).Select(r => r.Kind));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AChannelThatCannotWriteNeitherThrowsNorStopsTheOthersFromFlushing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "byte range locks only fail writes on Windows");
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var failing = recorder.OpenGame("game-1", "client 1");
        failing.Write(TraceRecordKind.D2gsReceived, new byte[] { 0x01 });
        recorder.FlushNow();
        using var locker = new FileStream(failing.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        locker.Lock(0, 1 << 30);
        failing.Write(TraceRecordKind.D2gsReceived, new byte[] { 0x02 });
        var healthy = recorder.OpenGame("game-1", "client 2");
        healthy.Write(TraceRecordKind.D2gsReceived, new byte[] { 0x03 });

        Assert.Null(Record.Exception(() => failing.Drain()));
        failing.Write(TraceRecordKind.D2gsReceived, new byte[] { 0x04 });
        recorder.FlushNow();
        failing.Close();
        healthy.Close();
        recorder.FlushNow();
        locker.Unlock(0, 1 << 30);

        using var reader = new TraceReader(healthy.Path);
        Assert.Equal(0x03, Assert.Single(reader.Records()).Payload[0]);
        Assert.Equal(0, recorder.OpenChannels);
    }

    [Fact]
    public void RealmTrafficGoesToARealmFolder()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenRealm("instance1");
        channel.Write(TraceRecordKind.BncsSent, new byte[] { 0xFF, 0x50, 0x04, 0x00 });
        recorder.FlushNow();

        Assert.Equal("realm", Path.GetFileName(Path.GetDirectoryName(channel.Path)));
        Assert.Matches(@"^instance1-\d{6}\.d2trace$", Path.GetFileName(channel.Path));
    }

    [Fact]
    public void WithoutARecorderAGameServerTracesNothing()
    {
        Assert.Null(TraceRecorder.Current);
        var gameServer = new GameServer();
        gameServer.TraceGame("game-1", "client 1");
        Assert.Null(gameServer.Trace);
        gameServer.OnPacketReceived(new D2gsPacket([0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void AChannelNothingWasWrittenToLeavesNoFile()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenGame("game-1", "client 1");
        channel.Close();
        recorder.FlushNow();

        Assert.Null(channel.Path);
        Assert.False(Directory.Exists(root.Path));
    }

    [Fact]
    public void TheLiveReceivePathWritesADigestRightAfterThePacketWhoseHandlersRan()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder(digestInterval: TimeSpan.Zero);
        var path = SyntheticTrace.Record(recorder, "game-1", playingPackets: 3);

        using var reader = new TraceReader(path);
        var records = reader.Records().ToList();
        var assignPlayer = records.FindIndex(r => r.Kind == TraceRecordKind.D2gsReceived && r.Payload[0] == 0x59);
        Assert.Equal(TraceRecordKind.Digest, records[assignPlayer + 1].Kind);
        Assert.DoesNotContain(records.Take(assignPlayer), r => r.Kind == TraceRecordKind.Digest);
        var lastLifeUpdate = records.FindLastIndex(r => r.Kind == TraceRecordKind.D2gsReceived && r.Payload[0] == 0x95);
        var digest = TraceDigest.FromBytes(records[lastLifeUpdate + 1].Payload);
        Assert.Equal(1616, digest.Life);
        Assert.Equal((ushort)5057, digest.X);
    }

    [Fact]
    public void TheCapDeletesTheOldestGameFoldersFirst()
    {
        using var root = new TempRoot();
        var oldest = Path.Combine(root.Path, "20260101", "game-a");
        var middle = Path.Combine(root.Path, "20260102", "game-b");
        var newest = Path.Combine(root.Path, "20260103", "game-c");
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var folder in new[] { oldest, middle, newest })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "client.d2trace"), new byte[1000]);
            Directory.SetLastWriteTimeUtc(folder, written);
            written = written.AddDays(1);
        }

        Assert.Equal(2, TraceRecorder.EnforceCap(root.Path, 1500));

        Assert.False(Directory.Exists(Path.Combine(root.Path, "20260101")));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "20260102")));
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public void AFolderAnotherProcessDeletedFirstCountsAsGone()
    {
        using var root = new TempRoot();
        var oldest = Path.Combine(root.Path, "20260101", "game-a");
        var middle = Path.Combine(root.Path, "20260102", "game-b");
        var newest = Path.Combine(root.Path, "20260103", "game-c");
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var folder in new[] { oldest, middle, newest })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "client.d2trace"), new byte[1000]);
            Directory.SetLastWriteTimeUtc(folder, written);
            written = written.AddDays(1);
        }

        TraceRecorder.EnforceCap(root.Path, 2500, folder =>
        {
            Directory.Delete(folder, recursive: true);
            Directory.Delete(folder, recursive: true);
        });

        Assert.False(Directory.Exists(oldest));
        Assert.True(Directory.Exists(middle));
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public void TheCapLeavesEverythingButDateFoldersAlone()
    {
        using var root = new TempRoot();
        var other = Path.Combine(root.Path, "notes", "drafts");
        var almostDate = Path.Combine(root.Path, "2026-01-01", "game-a");
        var empty = Path.Combine(root.Path, "empty");
        var game = Path.Combine(root.Path, "20260101", "game-a");
        foreach (var folder in new[] { other, almostDate, game })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "client.d2trace"), new byte[1000]);
            Directory.SetLastWriteTimeUtc(folder, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        Directory.CreateDirectory(empty);

        Assert.Equal(1, TraceRecorder.EnforceCap(root.Path, 0));

        Assert.False(Directory.Exists(Path.Combine(root.Path, "20260101")));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(almostDate));
        Assert.True(Directory.Exists(empty));
    }

    [Fact]
    public void ATraceEventsCallWhoseGameStateThrowsIsCountedAndDoesNotReachTheBot()
    {
        using var root = new TempRoot();
        using var recorder = TraceRecorder.Start(new TraceRecorderOptions { Root = root.Path, BackgroundWriter = false });
        var game = new Game(new GameServer());

        TraceEvents.Chicken(null, "life low");
        TraceEvents.Death(null, 1, damagedRecently: true, at: null);
        TraceEvents.Decision((Game)null, "stranger", ("what", "x"));
        TraceEvents.Move(game, null, null, MoveKind.Walk, () => throw new InvalidOperationException("outcome"));
        recorder.FlushNow();

        var entries = D2NG.Trace.Query.SidecarReader.Read([recorder.Sidecar.Path], DateTime.MinValue.ToUniversalTime(), DateTime.MaxValue.ToUniversalTime());
        Assert.Equal(4, entries.Single(e => e.Kind == "trace-failed").Num("n"));
    }

    [Fact]
    public void ADatesSidecarsAndRealmFolderCountAndGoWithItsLastGameFolder()
    {
        using var root = new TempRoot();
        var oldDate = Path.Combine(root.Path, "20260101");
        var newGame = Path.Combine(root.Path, "20260102", "game-b");
        Directory.CreateDirectory(Path.Combine(oldDate, "game-a"));
        Directory.CreateDirectory(Path.Combine(oldDate, "realm"));
        Directory.CreateDirectory(newGame);
        File.WriteAllBytes(Path.Combine(oldDate, "game-a", "client.d2trace"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(oldDate, "realm", "instance1-010203.d2trace"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(oldDate, "events-010203.jsonl"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(newGame, "client.d2trace"), new byte[1000]);
        Directory.SetLastWriteTimeUtc(Path.Combine(oldDate, "game-a"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(newGame, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, TraceRecorder.EnforceCap(root.Path, 1500));

        Assert.False(Directory.Exists(oldDate));
        Assert.True(Directory.Exists(newGame));
    }

    [Fact]
    public void ADateWithOnlySidecarsLeftIsDeletedAsOneUnit()
    {
        using var root = new TempRoot();
        var oldDate = Path.Combine(root.Path, "20260101");
        var newGame = Path.Combine(root.Path, "20260102", "game-b");
        Directory.CreateDirectory(oldDate);
        Directory.CreateDirectory(newGame);
        File.WriteAllBytes(Path.Combine(oldDate, "events-010203.jsonl"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(newGame, "client.d2trace"), new byte[1000]);
        Directory.SetLastWriteTimeUtc(oldDate, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(newGame, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, TraceRecorder.EnforceCap(root.Path, 1500));

        Assert.False(Directory.Exists(oldDate));
        Assert.True(Directory.Exists(newGame));
    }
}
