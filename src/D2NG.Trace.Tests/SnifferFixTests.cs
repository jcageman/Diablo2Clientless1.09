using D2NG.Core.Capture;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.Tracing;
using D2NG.Trace.Capture;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using TraceSource = D2NG.Core.Tracing.TraceSource;

namespace D2NG.Trace.Tests;

/// <summary>Regressions found by testing the sniffer's trace writing against real traffic.</summary>
public class SnifferFixTests
{
    private static readonly DateTime Start = CapturedBytes.Start;

    private static List<string> GameFiles(CaptureSession session) => [.. session.Stats.Files.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}realm{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

    [Fact]
    public void TwoClientsJoiningAtOnceWithTheSameRequestIdKeepTheirOwnGames()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var realmA = new CaptureConnection("10.0.0.2:50000", "10.0.0.1:6113");
        var realmB = new CaptureConnection("10.0.0.3:50000", "10.0.0.1:6113");
        session.Open(realmA, CapturedProtocol.Mcp, Start, fromStart: true);
        session.Open(realmB, CapturedProtocol.Mcp, Start, fromStart: true);
        session.Data(realmA, CapturedProtocol.Mcp, false, [0x01, .. CapturedBytes.JoinRequest(3, "game-a")], Start);
        session.Data(realmB, CapturedProtocol.Mcp, false, [0x01, .. CapturedBytes.JoinRequest(3, "game-b")], Start.AddMilliseconds(5));
        session.Data(realmA, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(3, 0x10, CapturedBytes.Hash), Start.AddMilliseconds(20));
        session.Data(realmB, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(3, 0x20, CapturedBytes.Hash), Start.AddMilliseconds(25));
        foreach (var (name, token, port) in new[] { ("alpha", (ushort)0x10, 1), ("bravo", (ushort)0x20, 2) })
        {
            var connection = new CaptureConnection($"10.0.0.{port}:5100{port}", "10.0.0.1:4000");
            session.Open(connection, CapturedProtocol.D2gs, Start.AddMilliseconds(100), fromStart: true);
            session.Data(connection, CapturedProtocol.D2gs, false, CapturedBytes.Logon(name, CharacterClass.Sorceress, token), Start.AddMilliseconds(110));
        }

        session.Dispose();

        var date = HandTrace.DateFolder(root.Path, Start);
        Assert.True(File.Exists(Path.Combine(date, "game-a", "alpha.d2trace")), string.Join(", ", session.Stats.Files));
        Assert.True(File.Exists(Path.Combine(date, "game-b", "bravo.d2trace")), string.Join(", ", session.Stats.Files));
        Assert.Equal(0, session.Stats.UnknownGameFiles);
    }

    [Fact]
    public void ARefusedJoinIsForgottenSoTheNextAnswerWithItsIdDoesNotTakeItsName()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var realm = new CaptureConnection("10.0.0.2:50000", "10.0.0.1:6113");
        session.Open(realm, CapturedProtocol.Mcp, Start, fromStart: false);
        session.Data(realm, CapturedProtocol.Mcp, false, CapturedBytes.JoinRequest(2, "full-game"), Start);
        session.Data(realm, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(2, 0, 0, result: 0x2A), Start.AddMilliseconds(10));
        session.Data(realm, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(2, 0x31, CapturedBytes.Hash), Start.AddMilliseconds(30));
        var connection = new CaptureConnection("10.0.0.2:51001", "10.0.0.1:4000");
        session.Open(connection, CapturedProtocol.D2gs, Start.AddMilliseconds(100), fromStart: true);
        session.Data(connection, CapturedProtocol.D2gs, false, CapturedBytes.Logon("bravo", CharacterClass.Amazon, 0x31), Start.AddMilliseconds(110));
        session.Dispose();

        Assert.False(Directory.Exists(Path.Combine(HandTrace.DateFolder(root.Path, Start), "full-game")), string.Join(", ", session.Stats.Files));
        Assert.Equal(1, session.Stats.UnknownGameFiles);
    }

    [Fact]
    public void ALongerRetransmitAtTheSequenceOfAHeldSegmentKeepsItsExtraBytes()
    {
        var data = Enumerable.Range(0, 400).Select(i => (byte)i).ToArray();
        var tcp = new TcpReassembler();
        var delivered = new List<byte>();
        var lost = 0;
        void Accept(uint sequence, int from, int length) => tcp.Accept(sequence, false, data.AsSpan(from, length), bytes => delivered.AddRange(bytes), () => lost++);

        tcp.Accept(999, true, ReadOnlySpan<byte>.Empty, bytes => delivered.AddRange(bytes), () => lost++);
        Accept(1000, 0, 100);
        Accept(1200, 200, 50);
        Accept(1200, 200, 200);
        Accept(1100, 100, 100);
        tcp.Finish(bytes => delivered.AddRange(bytes), () => lost++);

        Assert.Equal(0, lost);
        Assert.Equal(data, delivered);
    }

    [Fact]
    public void HeldSegmentsThatOverlapEachOtherDeliverEveryByteOnce()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var tcp = new TcpReassembler();
        var delivered = new List<byte>();
        var lost = 0;
        void Accept(uint sequence, int from, int length) => tcp.Accept(sequence, false, data.AsSpan(from, length), bytes => delivered.AddRange(bytes), () => lost++);

        tcp.Accept(999, true, ReadOnlySpan<byte>.Empty, bytes => delivered.AddRange(bytes), () => lost++);
        Accept(1000, 0, 100);
        Accept(1150, 150, 50);
        Accept(1120, 120, 60);
        Accept(1190, 190, 110);
        Accept(1100, 100, 30);
        tcp.Finish(bytes => delivered.AddRange(bytes), () => lost++);

        Assert.Equal(0, lost);
        Assert.Equal(data, delivered);
    }

    private static readonly byte[] Run = [0x03, 0x10, 0x13, 0x20, 0x13];
    private static readonly byte[] Interact = [0x13, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00];
    private static readonly byte[] UnknownId = [0x6D, 0xAA, 0xBB, 0xCC, 0xDD];

    private static (List<byte[]> Packets, D2gsClientStream Stream) Cut(params byte[][] segments)
    {
        var stream = new D2gsClientStream();
        var packets = new List<byte[]>();
        foreach (var segment in segments)
        {
            stream.Append(segment, packets);
        }

        stream.Flush(packets);
        return (packets, stream);
    }

    [Fact]
    public void AKnownPacketIsNeverStretchedOverAnUnknownOneAfterIt()
    {
        var (packets, stream) = Cut([.. Run, .. UnknownId]);

        Assert.Equal([Run, UnknownId], packets);
        Assert.Equal(1, stream.Unsized[0x6D]);
    }

    [Fact]
    public void AnUnknownPacketEndsWhereTheRestOfTheSegmentParsesAsKnownPackets()
    {
        var (packets, stream) = Cut([.. UnknownId, .. Run, .. Interact]);

        Assert.Equal([UnknownId, Run, Interact], packets);
        Assert.Equal(1, stream.Unsized[0x6D]);
    }

    [Fact]
    public void AChatPacketWhoseStringsDoNotEndDoesNotSwallowThePacketsAfterIt()
    {
        byte[] chat = [0x15, 0x01, 0x00, (byte)'h', (byte)'i'];
        var (packets, _) = Cut(chat, Run, Interact);

        Assert.Equal([chat, Run, Interact], packets);
    }

    [Fact]
    public void AResyncDoesNotBelieveFramesHoldingPacketsOnlySentWhileJoining()
    {
        var joining = CapturedBytes.Frame([.. SyntheticTrace.Joining().Take(3)]);
        var frames = Enumerable.Range(0, 4).Select(i => CapturedBytes.Frame([.. SyntheticTrace.Playing(5 + i)])).ToList();
        var decoder = new D2gsServerStream(fromStart: false) { ResyncBudget = TimeSpan.FromSeconds(10) };
        var packets = new List<byte[]>();
        decoder.Append([.. joining, .. frames.SelectMany(f => f)], packets);

        Assert.DoesNotContain(packets, p => p[0] == 0x01);
        Assert.Equal(Enumerable.Range(1, 3).SelectMany(i => SyntheticTrace.Playing(5 + i)).ToList(), packets);
    }

    [Fact]
    public void SearchingGarbageForAFrameStartTakesMillisecondsPerCallNotSeconds()
    {
        var random = new Random(3);
        var garbage = new byte[256 * 1024];
        random.NextBytes(garbage);
        var decoder = new D2gsServerStream(fromStart: false);
        var packets = new List<byte[]>();

        var stopwatch = Stopwatch.StartNew();
        decoder.Append(garbage, packets);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"one call took {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(decoder.Pending <= 64 * 1024, $"{decoder.Pending} bytes held");
    }

    [Fact]
    public void TheGapOfABadFrameIsWrittenBeforeThePacketsFoundAfterIt()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var connection = new CaptureConnection("10.0.0.2:50001", "10.0.0.1:4000");
        var unsizable = (byte)Enumerable.Range(0x30, 0xD0).First(i => !D2gsServerStream.Split(CapturedBytes.Frame([(byte)i, 1, 2, 3])[1..], [], out _));
        var good = CapturedBytes.Frame([.. SyntheticTrace.Playing(6)]);
        var bad = CapturedBytes.Frame(SyntheticTrace.MonsterHit(0xE9, 3), [unsizable, 1, 2, 3]);
        var after = Enumerable.Range(0, 3).SelectMany(i => CapturedBytes.Frame([.. SyntheticTrace.Playing(3 + i)])).ToArray();
        session.Open(connection, CapturedProtocol.D2gs, Start, fromStart: true);
        session.Data(connection, CapturedProtocol.D2gs, false, CapturedBytes.Logon("alpha", CharacterClass.Amazon, 7), Start);
        session.Data(connection, CapturedProtocol.D2gs, true, [0xA7, 0x01, .. good, .. bad, .. after], Start.AddSeconds(1));
        session.Dispose();

        var records = CapturedBytes.Records(Assert.Single(session.Stats.Files)).Where(r => r.Kind is TraceRecordKind.D2gsReceived or TraceRecordKind.Gap).ToList();
        var gap = records.FindIndex(r => r.Kind == TraceRecordKind.Gap);
        Assert.Equal(6 + 1, gap);
        Assert.Equal(3 + 4 + 5, records.Count - gap - 1);
    }

    [Fact]
    public void MidGameConnectionsThatStartInTheSameSecondGetAFolderEach()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var playing = CapturedBytes.Frame([.. SyntheticTrace.Playing(20)]);
        foreach (var port in new[] { 1, 2 })
        {
            var connection = new CaptureConnection($"10.0.0.{port}:5000{port}", "10.0.0.1:4000");
            session.Data(connection, CapturedProtocol.D2gs, true, [.. playing, .. playing, .. playing], Start.AddMilliseconds(port * 100));
            session.Data(connection, CapturedProtocol.D2gs, false, [0x6A, 1, 2, 3, 4, 0, 0, 0, 0], Start.AddMilliseconds(port * 100 + 50));
        }

        session.Dispose();

        var folders = session.Stats.Files.Select(Path.GetDirectoryName).ToList();
        Assert.Equal(2, folders.Count);
        Assert.NotEqual(folders[0], folders[1]);
    }

    [Fact]
    public void ARunFarFromEveryClientGoesToTheNearestRatherThanToTheOneThatSentLast()
    {
        using var root = new TempRoot();
        var start = new DateTimeOffset(2026, 11, 14, 13, 0, 0, TimeSpan.FromHours(1));
        string Line(double at, string level, string message) => $"{start.AddMilliseconds(Math.Round(at * 1000)).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{level}] {message}";
        var first = CapturedBytes.Frame([.. SyntheticTrace.Joining()]);
        var second = CapturedBytes.Frame(
            [0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            SyntheticGame.AssignPlayer(2, CharacterClass.Sorceress, "bravo", 5200, 5300),
            SyntheticGame.LifeMana(400, 100, 5200, 5300));
        Directory.CreateDirectory(root.Path);
        var capture = Path.Combine(root.Path, "capture.txt");
        File.WriteAllLines(capture,
        [
            Line(0.1, "DBG", $"Send MCP packet of type: JOINGAME with data {CapturedBytes.JoinRequest(3, "game-1").ToPrintString()}"),
            Line(0.11, "DBG", $"MCP Full packet received: {Convert.ToHexString(CapturedBytes.JoinAnswer(3, 0x44, CapturedBytes.Hash))}"),
            Line(1, "DBG", "D2GS Initial packet received: A701"),
            Line(1.001, "DBG", $"Outgoing packet GameLogon with data: {SyntheticTrace.Logon("nomancer", CharacterClass.Necromancer, 0x44).ToPrintString()}"),
            Line(1.01, "DBG", $"D2GS Full packet received: {Convert.ToHexString(first)}"),
            Line(1.01, "VRB", $"Instance 0 Full packet received: {Convert.ToHexString(first)}"),
            Line(2, "DBG", "D2GS Initial packet received: A701"),
            Line(2.001, "DBG", $"Outgoing packet GameLogon with data: {SyntheticTrace.Logon("bravo", CharacterClass.Sorceress, 0x44).ToPrintString()}"),
            Line(2.01, "DBG", $"D2GS Full packet received: {Convert.ToHexString(second)}"),
            Line(2.01, "VRB", $"Instance 1 Full packet received: {Convert.ToHexString(second)}"),
            Line(3, "INF", "RunToLocationPacket -> (5201, 5302)"),
            Line(3.1, "INF", "RunToLocationPacket -> (5177, 1902)"),
        ]);

        TextCaptureImport.Import(capture, Path.Combine(root.Path, "out"));

        var game = Path.Combine(HandTrace.DateFolder(Path.Combine(root.Path, "out"), start.UtcDateTime), "game-1");
        Assert.Contains(CapturedBytes.Records(Path.Combine(game, "nomancer.d2trace")), r => r.Kind == TraceRecordKind.D2gsSent && r.Payload.AsSpan().SequenceEqual(SyntheticGame.Location(0x03, 5177, 1902)));
        Assert.DoesNotContain(CapturedBytes.Records(Path.Combine(game, "bravo.d2trace")), r => r.Kind == TraceRecordKind.D2gsSent && r.Payload.AsSpan().SequenceEqual(SyntheticGame.Location(0x03, 5177, 1902)));
    }

    [Fact]
    public void WindowsDeviceNamesGetASafeFileNameAndEveryOtherNameKeepsItsOwn()
    {
        var unchanged = new Dictionary<string, string>
        {
            ["game-1"] = "game-1",
            ["Cow 12"] = "Cow 12",
            ["a:b*c"] = "a_b_c",
            ["ends."] = "ends_",
            ["ends "] = "ends_",
            [""] = "unnamed",
            ["  "] = "unnamed",
            ["CONSOLE"] = "CONSOLE",
            ["COM10"] = "COM10",
            ["LPTX"] = "LPTX",
            ["con-1"] = "con-1",
            ["midgame-120000-1"] = "midgame-120000-1",
            ["unknown-game-120000"] = "unknown-game-120000",
            ["NULL"] = "NULL",
        };
        var devices = new Dictionary<string, string>
        {
            ["CON"] = "CON_",
            ["con"] = "con_",
            ["Nul.txt"] = "Nul_.txt",
            ["aux.d2trace"] = "aux_.d2trace",
            ["COM1"] = "COM1_",
            ["lpt9"] = "lpt9_",
            ["PRN"] = "PRN_",
        };

        Assert.All(unchanged.Concat(devices), pair => Assert.Equal(pair.Value, TraceRecorder.Safe(pair.Key)));
    }

    [Fact]
    public void AGameNamedLikeAWindowsDeviceIsTraced()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var realm = new CaptureConnection("10.0.0.2:50000", "10.0.0.1:6113");
        session.Open(realm, CapturedProtocol.Mcp, Start, fromStart: false);
        session.Data(realm, CapturedProtocol.Mcp, false, CapturedBytes.JoinRequest(2, "con"), Start);
        session.Data(realm, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(2, 0x31, CapturedBytes.Hash), Start.AddMilliseconds(10));
        var connection = new CaptureConnection("10.0.0.2:51001", "10.0.0.1:4000");
        session.Open(connection, CapturedProtocol.D2gs, Start.AddMilliseconds(100), fromStart: true);
        session.Data(connection, CapturedProtocol.D2gs, false, CapturedBytes.Logon("aux", CharacterClass.Amazon, 0x31), Start.AddMilliseconds(110));
        session.Dispose();

        var file = Assert.Single(GameFiles(session));
        using var reader = new TraceReader(file);
        Assert.Equal("aux", reader.Header.ClientLabel);
        Assert.Equal("con", reader.Header.GameName);
        Assert.Single(reader.Records());
    }

    [Fact]
    public void TheCurrentLocationOfTheOwnPlayersMoveCountsAsAServerStatedPositionAsTheGameCountsIt()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "game-1");
        new HandTrace("alpha", "game-1", Start).Join(1)
            .Sent(1, SyntheticGame.Location(0x03, 5090, 1930))
            .Received(1.2, SyntheticGame.EntityMove(1, 5090, 1930, 5071, 1915))
            .WriteTo(folder);
        var positions = new ServerPositions();

        ClientReplay.Run(GameTrace.Open(folder), "alpha", TimeSpan.MaxValue, [positions])?.Replayer.Dispose();

        Assert.Contains(positions.Positions, p => p.Position.X == 5071 && p.Position.Y == 1915);
    }

    private static Replayer ReplayAsSniffer(TempRoot root, HandTrace trace)
    {
        var path = trace.WriteTo(Path.Combine(root.Path, "bot"));
        var sniffer = Path.Combine(root.Path, "sniffer.d2trace");
        using (var reader = new TraceReader(path))
        {
            TraceFile.Write(sniffer, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = reader.Header.StartUtc, ClientLabel = reader.Header.ClientLabel, GameName = "game-1" }, reader.Records());
        }

        return new Replayer(sniffer);
    }

    [Fact]
    public void AnItemTheServerTakesWithoutTheClientLiftingItIsNotLeftOnTheCursor()
    {
        using var root = new TempRoot();
        const uint staff = 0x2B;
        using var replayer = ReplayAsSniffer(root, new HandTrace("alpha", "game-1", Start).Join(1)
            .Received(1, SyntheticGame.Item(Core.D2GS.Items.Action.PutInContainer, staff))
            .Sent(2, [0x44, 0x02, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, .. BitConverter.GetBytes(staff), 0x00, 0x00, 0x00, 0x00])
            .Received(2.1, SyntheticGame.Item(Core.D2GS.Items.Action.RemoveFromContainer, staff)));

        var result = replayer.ReplayToEnd();

        Assert.Equal(0, result.HandlerFailures);
        Assert.Null(replayer.Game.CursorItem);
    }

    [Fact]
    public void TheStaffLiftedAndPutIntoTheOrificeLeavesTheCursorEmptyWhenTheServerClearsIt()
    {
        using var root = new TempRoot();
        const uint staff = 0x2B;
        using var replayer = ReplayAsSniffer(root, new HandTrace("alpha", "game-1", Start).Join(1)
            .Received(1, SyntheticGame.Item(Core.D2GS.Items.Action.PutInContainer, staff))
            .Sent(2, [0x19, .. BitConverter.GetBytes(staff)])
            .Received(2.01, SyntheticGame.Item(Core.D2GS.Items.Action.RemoveFromContainer, staff))
            .Sent(3, [0x44, 0x01, 0x00, 0x00, 0x00, 0xC4, 0x01, 0x00, 0x00, .. BitConverter.GetBytes(staff), 0x03, 0x00, 0x00, 0x00])
            .Received(3.01, [0x42, 0x00, 0x01, 0x00, 0x00, 0x00]));

        replayer.ReplayTo(TimeSpan.FromSeconds(2.5));
        var lifted = replayer.Game.CursorItem?.Id;
        replayer.ReplayToEnd();

        Assert.Equal(staff, lifted);
        Assert.Null(replayer.Game.CursorItem);
    }

    [Fact]
    public void ABeltPotionDrunkIsNotMarkedUsedAsTheBotsOwnMethodDoesNot()
    {
        using var root = new TempRoot();
        using var replayer = ReplayAsSniffer(root, new HandTrace("alpha", "game-1", Start).Join(1)
            .Sent(2, [0x26, 0x31, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));

        var result = replayer.ReplayToEnd();

        Assert.False(result.DerivedClientActions.ContainsKey(ClientAction.Use));
    }

    [Fact]
    public void AWalkTakesHalfAsLongAgainAsARun()
    {
        using var root = new TempRoot();
        using var replayer = ReplayAsSniffer(root, new HandTrace("alpha", "game-1", Start).Join(1, x: 5000, y: 5000)
            .Sent(1, SyntheticGame.Location(0x01, 5030, 5000))
            .Received(5, SyntheticTrace.MonsterHit(0xE9, 3)));

        replayer.ReplayTo(TimeSpan.FromSeconds(3.8));
        var halfway = replayer.Game.Me.Location;
        replayer.ReplayToEnd();

        Assert.Equal(new Core.D2GS.Point(5000, 5000), halfway);
        Assert.Equal(new Core.D2GS.Point(5030, 5000), replayer.Game.Me.Location);
    }

    [Fact]
    public void TheSniffersCopyOfABotsTraceIsNotReadAsARejoin()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "game-1");
        var bot = new HandTrace("alpha", "game-1", Start, "127.0.0.1#258").Join(1).Received(5, SyntheticTrace.LifeManaUpdate).WriteTo(folder);
        var sniffed = Path.Combine(folder, "alpha-2.d2trace");
        using (var reader = new TraceReader(bot))
        {
            TraceFile.Write(sniffed, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = Start.AddMilliseconds(3), ClientLabel = "alpha", GameName = "game-1", ServerGame = "127.0.0.1#258" }, reader.Records());
        }

        var game = GameTrace.Open(folder);

        var file = Assert.Single(game.FilesOf("alpha"));
        Assert.Equal(TraceSource.Bot, file.Header.Source);
    }
}
