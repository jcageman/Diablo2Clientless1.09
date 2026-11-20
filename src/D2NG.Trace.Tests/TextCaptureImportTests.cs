using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.Tracing;
using D2NG.Trace.Capture;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class TextCaptureImportTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 13, 0, 0, TimeSpan.FromHours(1));

    private sealed class Log
    {
        private readonly List<string> _lines = [];

        public Log Line(double at, string level, string message)
        {
            _lines.Add($"{Start.AddMilliseconds(Math.Round(at * 1000)).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{level}] {message}");
            return this;
        }

        public string WriteTo(string folder)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "capture.txt");
            File.WriteAllLines(path, _lines);
            return path;
        }
    }

    private static byte[] Second() =>
    [
        .. new byte[] { 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        .. SyntheticGame.AssignPlayer(2, CharacterClass.Sorceress, "bravo", 5200, 5300),
        .. SyntheticGame.LifeMana(400, 100, 5200, 5300),
    ];

    /// <summary>
    /// Two clients in one game, in the sniffer's text format: realm join, both game connections, a frame split over
    /// two segments (the first followed by no "Instance" line), and sends of both clients the log does not attribute.
    /// </summary>
    private static string WriteSample(string folder)
    {
        var first = CapturedBytes.Frame([.. SyntheticTrace.Joining()]);
        var second = CapturedBytes.Frame(Second());
        var hits = CapturedBytes.Frame(SyntheticTrace.MonsterHit(0xE9, 50), SyntheticTrace.MonsterHit(0xE5, 40));
        return new Log()
            .Line(0, "INF", "Capture options: device=1 d2gsPort=4000 protocols=bncs,d2gs,mcp direction=both raw=True")
            .Line(0.1, "DBG", $"Send MCP packet of type: JOINGAME with data {CapturedBytes.JoinRequest(3, "game-1").ToPrintString()}")
            .Line(0.11, "DBG", $"MCP Full packet received: {Convert.ToHexString(CapturedBytes.JoinAnswer(3, 0x44, CapturedBytes.Hash))}")
            .Line(0.11, "DBG", "Received MCP packet of type: JOINGAME with data 0x15")
            .Line(1, "DBG", "D2GS Initial packet received: A701")
            .Line(1.001, "DBG", $"Outgoing packet GameLogon with data: {SyntheticTrace.Logon("nomancer", CharacterClass.Necromancer, 0x44).ToPrintString()}")
            .Line(1.01, "DBG", $"D2GS Full packet received: {Convert.ToHexString(first[..20])}")
            .Line(1.02, "DBG", $"D2GS Full packet received: {Convert.ToHexString(first[20..])}")
            .Line(1.02, "VRB", $"Instance 0 Full packet received: {Convert.ToHexString(first)}")
            .Line(1.02, "DBG", "Received D2GS packet of type: GameFlags with data 0x01")
            .Line(2, "DBG", "D2GS Initial packet received: A701")
            .Line(2.001, "DBG", $"Outgoing packet GameLogon with data: {SyntheticTrace.Logon("bravo", CharacterClass.Sorceress, 0x44).ToPrintString()}")
            .Line(2.01, "DBG", $"D2GS Full packet received: {Convert.ToHexString(second)}")
            .Line(2.01, "VRB", $"Instance 1 Full packet received: {Convert.ToHexString(second)}")
            .Line(3, "INF", "RunToLocationPacket -> (5201, 5302)")
            .Line(3.1, "INF", "RunToLocationPacket -> (5060, 1903)")
            .Line(3.2, "INF", "EntityInteract -> ID: 233 Type: 1")
            .Line(3.2, "DBG", "0x13, 0x01, 0x00, 0x00, 0x00, 0xE9, 0x00, 0x00, 0x00")
            .Line(3.3, "INF", "SellItem -> 27")
            .Line(4, "DBG", $"D2GS Full packet received: {Convert.ToHexString(hits)}")
            .Line(4, "VRB", $"Instance 0 Full packet received: {Convert.ToHexString(first)}")
            .WriteTo(folder);
    }

    [Fact]
    public void ATextCaptureBecomesOneTraceFilePerClientInTheGamesFolder()
    {
        using var root = new TempRoot();
        var capture = WriteSample(Path.Combine(root.Path, "in"));
        var traces = Path.Combine(root.Path, "out");

        var result = TextCaptureImport.Import(capture, traces);

        var game = Path.Combine(HandTrace.DateFolder(traces, Start.UtcDateTime), "game-1");
        var first = CapturedBytes.Records(Path.Combine(game, "nomancer.d2trace"));
        var second = CapturedBytes.Records(Path.Combine(game, "bravo.d2trace"));
        Assert.Equal(2, result.GameConnections);
        Assert.Equal(1, result.SegmentsFoundInFrame);
        Assert.Equal(0, result.SegmentsGuessed);
        Assert.Equal(2, result.Rebuilt["Run"]);
        Assert.Equal(SyntheticTrace.Joining().Count() + 2, first.Count(r => r.Kind == TraceRecordKind.D2gsReceived));
        Assert.Equal(3, second.Count(r => r.Kind == TraceRecordKind.D2gsReceived));

        var sentFirst = first.Where(r => r.Kind == TraceRecordKind.D2gsSent).Select(r => r.Payload[0]).ToList();
        var sentSecond = second.Where(r => r.Kind == TraceRecordKind.D2gsSent).Select(r => r.Payload[0]).ToList();
        Assert.Equal([0x65, 0x03, 0x13, 0x33], sentFirst);
        Assert.Equal([0x65, 0x03], sentSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(1), first[0].Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(2100), first.First(r => r.Kind == TraceRecordKind.D2gsSent && r.Payload[0] == 0x03).Elapsed);

        using var reader = new TraceReader(Path.Combine(game, "nomancer.d2trace"));
        Assert.Equal(TraceSource.Sniffer, reader.Header.Source);
        Assert.Equal("127.0.0.1#68", reader.Header.ServerGame);
        Assert.Equal(Start.UtcDateTime.AddSeconds(1), reader.Header.StartUtc);
    }

    [Fact]
    public void TheImportedTracesReplayWithTheirDerivedActions()
    {
        using var root = new TempRoot();
        var traces = Path.Combine(root.Path, "out");
        TextCaptureImport.Import(WriteSample(Path.Combine(root.Path, "in")), traces);
        var game = Path.Combine(HandTrace.DateFolder(traces, Start.UtcDateTime), "game-1");

        using var replayer = new Replayer(Path.Combine(game, "nomancer.d2trace"));
        var result = replayer.ReplayToEnd();

        Assert.Equal(0, result.HandlerFailures);
        Assert.Equal(1, result.DerivedClientActions[ClientAction.Sell]);
        Assert.Equal(new Core.D2GS.Point(5060, 1903), replayer.Game.Me.Location);
    }

    [Fact]
    public void PrintedBytesAndLogTimesAreReadAsTheSnifferWroteThem()
    {
        Assert.Equal([0x65, 0x00, 0xFF], TextCaptureImport.PrintedBytes("0x65, 0x00, 0xFF"));
        Assert.Null(TextCaptureImport.PrintedBytes("0x65, oops"));
        Assert.Equal(new DateTime(2026, 8, 30, 12, 11, 28, 254, DateTimeKind.Utc), TextCaptureImport.Time("2026-08-30 14:11:28.254 +02:00"));
    }
}

public class TraceCompareTests
{
    private static string Write(string folder, string label, uint message, byte[] offer)
    {
        var trace = new HandTrace(label, "game-1", CapturedBytes.Start)
            .Join(1)
            .Received(0.5, SyntheticGame.AssignNpc(0x03, Core.D2GS.Objects.NPCCode.TyraelAct4, 5060, 1905))
            .Sent(1.0, SyntheticGame.Location(0x03, 5058, 1903))
            .Sent(1.1, SyntheticGame.Location(0x03, 5059, 1904))
            .Sent(1.2, [0x6A, 1, 2, 3, 4, 0, 0, 0, 0])
            .Sent(2.0, SyntheticGame.Unit(0x13, Core.D2GS.Objects.EntityType.NPC, 3))
            .Received(2.01, offer)
            .Sent(2.1, SyntheticGame.Unit(0x2F, Core.D2GS.Objects.EntityType.NPC, 3))
            .Sent(2.2, [0x31, 0x03, 0x00, 0x00, 0x00, .. BitConverter.GetBytes(message)])
            .Sent(2.3, SyntheticGame.Unit(0x30, Core.D2GS.Objects.EntityType.NPC, 3));
        var path = trace.WriteTo(folder);
        var sniffer = Path.Combine(folder, label + "-sniffer.d2trace");
        using (var reader = new TraceReader(path))
        {
            TraceFile.Write(sniffer, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = reader.Header.StartUtc, ClientLabel = label, GameName = "game-1" }, reader.Records());
        }

        return sniffer;
    }

    private static byte[] Offer(ushort message)
    {
        var packet = new byte[40];
        packet[0] = 0x27;
        packet[1] = 0x01;
        packet[2] = 0x03;
        packet[6] = 0x01;
        BitConverter.GetBytes(message).CopyTo(packet, 8);
        return packet;
    }

    [Fact]
    public void TheOneDifferentQuestMessageIsWhereTheSequencesPart()
    {
        using var root = new TempRoot();
        var a = Write(Path.Combine(root.Path, "a"), "alpha", 0x2AC, Offer(0x2AC));
        var b = Write(Path.Combine(root.Path, "b"), "bravo", 0x298, Offer(0x2AC));

        var result = TraceCompare.Compare(a, b, new TraceCompareOptions());

        var divergence = Assert.Single(result.Divergences);
        Assert.Equal("different steps", divergence.Kind);
        Assert.Contains("message=0x2AC", Assert.Single(divergence.OnlyA).Text, StringComparison.Ordinal);
        Assert.Contains("message=0x298", Assert.Single(divergence.OnlyB).Text, StringComparison.Ordinal);
        Assert.Contains("TyraelAct4", divergence.OnlyA[0].Key, StringComparison.Ordinal);
        Assert.Equal(5, result.Matched);
        Assert.Equal(6, result.StepsA);
    }

    [Fact]
    public void TheSameStepAnsweredDifferentlyIsReportedUnlessOnlySendsAreCompared()
    {
        using var root = new TempRoot();
        var a = Write(Path.Combine(root.Path, "a"), "alpha", 0x2AC, Offer(0x2AC));
        var b = Write(Path.Combine(root.Path, "b"), "bravo", 0x2AC, Offer(0x298));

        var result = TraceCompare.Compare(a, b, new TraceCompareOptions());
        var sentOnly = TraceCompare.Compare(a, b, new TraceCompareOptions { SentOnly = true });

        var divergence = Assert.Single(result.Divergences);
        Assert.Equal("same step, different answer", divergence.Kind);
        Assert.Contains(divergence.AroundA, p => !p.Sent && p.Text.StartsWith("NPCInfo", StringComparison.Ordinal));
        Assert.Empty(sentOnly.Divergences);
    }
}
