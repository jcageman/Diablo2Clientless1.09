using D2NG.Core.Capture;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using TraceSource = D2NG.Core.Tracing.TraceSource;

namespace D2NG.Trace.Tests;

/// <summary>Regressions of the sniffer's trace writing and of the tools reading its traces that need the API the fixes added.</summary>
public class SnifferFixApiTests
{
    private static readonly DateTime Start = CapturedBytes.Start;
    private static readonly CaptureConnection Game = new("10.0.0.2:50001", "10.0.0.1:4000");

    private sealed class Capture : IDisposable
    {
        private uint _toServer = 1001;
        private uint _toClient = 5001;

        public Capture(string root)
        {
            Session = new CaptureSession(new CaptureTraceWriter(root));
            Tcp = new TcpCapture(Session);
            Tcp.Accept(Game, CapturedProtocol.D2gs, false, 1000, true, false, false, false, [], Start);
            Tcp.Accept(Game, CapturedProtocol.D2gs, true, 5000, true, true, false, false, [], Start);
        }

        public CaptureSession Session { get; }

        public TcpCapture Tcp { get; }

        /// <summary>A client segment: its sequence is taken whether or not the capture sees it.</summary>
        public void Sent(byte[] bytes, double ms, bool seen = true)
        {
            if (seen)
            {
                Tcp.Accept(Game, CapturedProtocol.D2gs, false, _toServer, false, true, false, false, bytes, Start.AddMilliseconds(ms));
            }

            _toServer += (uint)bytes.Length;
        }

        /// <summary>A server segment: its sequence is taken whether or not the capture sees it.</summary>
        public uint Received(byte[] bytes, double ms, bool seen = true)
        {
            var sequence = _toClient;
            if (seen)
            {
                Tcp.Accept(Game, CapturedProtocol.D2gs, true, sequence, false, true, false, false, bytes, Start.AddMilliseconds(ms));
            }

            _toClient += (uint)bytes.Length;
            return sequence;
        }

        public void Dispose() => Tcp.Dispose();
    }

    private static byte[] Ping(int i) => [0x6A, (byte)i, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] HitFrame(int i) => CapturedBytes.Frame(SyntheticTrace.MonsterHit(0xE9, (byte)(i % 128)));

    [Fact]
    public void BytesReleasedByGivingUpAHoleKeepTheirOwnCaptureTimeAndTheFileStaysInOrder()
    {
        using var root = new TempRoot();
        string path;
        using (var capture = new Capture(root.Path))
        {
            capture.Received([0xA7, 0x01], 1);
            capture.Sent(CapturedBytes.Logon("alpha", CharacterClass.Amazon, 7), 2);
            capture.Received(HitFrame(0), 100, seen: false);
            for (var i = 1; i <= 70; i++)
            {
                capture.Received(HitFrame(i), 100 + 10 * i);
                capture.Sent(Ping(i), 105 + 10 * i);
            }

            path = capture.Session.Stats.Files.Single();
        }

        var records = CapturedBytes.Records(path);
        Assert.Equal(records.Select(r => r.Elapsed).Order(), records.Select(r => r.Elapsed));
        var hits = records.Where(r => r.Kind == TraceRecordKind.D2gsReceived).ToList();
        Assert.Equal(70, hits.Count);
        Assert.All(hits, hit => Assert.Equal(TimeSpan.FromMilliseconds(100 + 10 * hit.Payload[8]), hit.Elapsed));
        var order = records.Where(r => r.Kind is TraceRecordKind.D2gsReceived or TraceRecordKind.D2gsSent && r.Payload[0] != 0x65).Select(r => r.Kind == TraceRecordKind.D2gsReceived ? $"hit{r.Payload[8]}" : $"ping{r.Payload[1]}").ToList();
        Assert.Equal(Enumerable.Range(1, 70).SelectMany(i => new[] { $"hit{i}", $"ping{i}" }), order);
        Assert.Equal(TimeSpan.FromMilliseconds(110), records.First(r => r.Kind == TraceRecordKind.Gap).Elapsed);
        Assert.Equal(TraceRecordKind.Gap, records[records.FindIndex(r => r.Kind == TraceRecordKind.D2gsReceived) - 1].Kind);
    }

    /// <summary>
    /// The capture missed one segment the client sent, and the client then went quiet while the server kept talking.
    /// The server acknowledged the segment, so it never comes again: before, the hole was given up only after 64 more
    /// client segments, and every record of the connection waited for it - for as long as the client stayed idle.
    /// </summary>
    [Fact]
    public void AHoleInAnIdleDirectionIsGivenUpOnceTheConnectionRunsAFewSecondsPastIt()
    {
        using var root = new TempRoot();
        string path;
        using (var capture = new Capture(root.Path))
        {
            capture.Received([0xA7, 0x01], 1);
            capture.Sent(CapturedBytes.Logon("alpha", CharacterClass.Amazon, 7), 2);
            capture.Sent(Ping(1), 100, seen: false);
            capture.Sent(Ping(2), 200);
            for (var i = 1; i <= 10; i++)
            {
                capture.Received(HitFrame(i), 200 + 500 * i);
                Assert.Equal(i <= 6 ? 0 : 1, capture.Session.Stats.Gaps);
            }

            path = capture.Session.Stats.Files.Single();
        }

        var records = CapturedBytes.Records(path);
        Assert.Equal(records.Select(r => r.Elapsed).Order(), records.Select(r => r.Elapsed));
        var gap = records.FindIndex(r => r.Kind == TraceRecordKind.Gap);
        Assert.Equal(TimeSpan.FromMilliseconds(200), records[gap].Elapsed);
        Assert.Equal(1u, records[gap].DroppedCount);
        Assert.Equal((uint)Ping(1).Length, records[gap].SkippedBytes);
        Assert.Equal(TraceRecordKind.D2gsSent, records[gap + 1].Kind);
        Assert.Equal(2, records[gap + 1].Payload[1]);
        Assert.Equal(10, records.Count(r => r.Kind == TraceRecordKind.D2gsReceived && r.Payload[0] == 0x0C));
    }

    [Fact]
    public void BytesWaitingForARetransmitAreStampedWhenItArrivesAndAFrameWithItsLastSegment()
    {
        using var root = new TempRoot();
        string path;
        using (var capture = new Capture(root.Path))
        {
            capture.Received([0xA7, 0x01], 1);
            capture.Sent(CapturedBytes.Logon("alpha", CharacterClass.Amazon, 7), 2);
            var lostFrame = HitFrame(0);
            var resend = capture.Received(lostFrame, 100, seen: false);
            capture.Received(HitFrame(1), 110);
            capture.Received(HitFrame(2), 120);
            capture.Tcp.Accept(Game, CapturedProtocol.D2gs, true, resend, false, true, false, false, lostFrame, Start.AddMilliseconds(300));
            var split = CapturedBytes.Frame([.. SyntheticTrace.Playing(9)]);
            capture.Received(split[..5], 400);
            capture.Received(split[5..], 450);
            path = capture.Session.Stats.Files.Single();
        }

        var received = CapturedBytes.Records(path).Where(r => r.Kind == TraceRecordKind.D2gsReceived).ToList();
        Assert.Equal(3 + 9, received.Count);
        Assert.All(received.Take(3), r => Assert.Equal(TimeSpan.FromMilliseconds(300), r.Elapsed));
        Assert.All(received.Skip(3), r => Assert.Equal(TimeSpan.FromMilliseconds(450), r.Elapsed));
        Assert.Equal(0, CapturedBytes.Records(path).Count(r => r.Kind == TraceRecordKind.Gap));
    }

    [Fact]
    public void ACaptureCallbackThatThrowsIsCountedAndTheCaptureGoesOn()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path)) { Recorded = (_, kind, _, _) => throw new InvalidOperationException("broken observer") };
        var reports = new List<string>();
        session.Report = reports.Add;
        using var tcp = new TcpCapture(session);

        tcp.Accept(Game, CapturedProtocol.D2gs, false, 1000, true, false, false, false, [], Start);
        tcp.Accept(Game, CapturedProtocol.D2gs, false, 1001, false, true, false, false, CapturedBytes.Logon("alpha", CharacterClass.Amazon, 7), Start);

        Assert.Equal(1, tcp.Failures);
        Assert.Contains(reports, r => r.Contains("broken observer", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileThatCannotBeOpenedFailsAloneAndEveryOtherConnectionIsStillTraced()
    {
        using var root = new TempRoot();
        var date = HandTrace.DateFolder(root.Path, Start);
        Directory.CreateDirectory(Path.Combine(date, "blocked", "client1.d2trace"));
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var reports = new List<string>();
        session.Report = reports.Add;
        var realm = new CaptureConnection("10.0.0.2:50000", "10.0.0.1:6113");
        session.Open(realm, CapturedProtocol.Mcp, Start, fromStart: false);
        foreach (var (game, request, token) in new[] { ("blocked", (ushort)2, (ushort)0x31), ("fine", (ushort)3, (ushort)0x32) })
        {
            session.Data(realm, CapturedProtocol.Mcp, false, CapturedBytes.JoinRequest(request, game), Start);
            session.Data(realm, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(request, token, CapturedBytes.Hash), Start);
        }

        foreach (var (port, token) in new[] { (1, (ushort)0x31), (2, (ushort)0x32) })
        {
            var connection = new CaptureConnection($"10.0.0.2:5100{port}", "10.0.0.1:4000");
            session.Open(connection, CapturedProtocol.D2gs, Start.AddMilliseconds(100), fromStart: true);
            session.Data(connection, CapturedProtocol.D2gs, false, CapturedBytes.Logon($"client{port}", CharacterClass.Amazon, token), Start.AddMilliseconds(110));
            session.Data(connection, CapturedProtocol.D2gs, true, [0xA7, 0x01, .. CapturedBytes.Frame([.. SyntheticTrace.Joining()])], Start.AddMilliseconds(120));
        }

        session.Dispose();

        Assert.Equal(1, session.Stats.FileFailures);
        Assert.Contains(reports, r => r.Contains("not recorded", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(date, "fine", "client2.d2trace")));
        Assert.Equal(SyntheticTrace.Joining().Count(), CapturedBytes.Records(Path.Combine(date, "fine", "client2.d2trace")).Count(r => r.Kind == TraceRecordKind.D2gsReceived));
    }

    [Fact]
    public void ASendGivenWithoutEvidenceIsMarkedJustBeforeIt()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        session.Open(Game, CapturedProtocol.D2gs, Start, fromStart: true);
        session.Data(Game, CapturedProtocol.D2gs, false, CapturedBytes.Logon("alpha", CharacterClass.Amazon, 7), Start);
        session.Data(Game, CapturedProtocol.D2gs, false, [.. SyntheticGame.Location(0x03, 5060, 1905), .. Ping(1)], Start.AddSeconds(1), withoutEvidence: true);
        session.Data(Game, CapturedProtocol.D2gs, false, Ping(2), Start.AddSeconds(2));
        session.Dispose();

        var kinds = CapturedBytes.Records(Assert.Single(session.Stats.Files)).Select(r => r.Kind).ToList();
        Assert.Equal([TraceRecordKind.D2gsSent, TraceRecordKind.SentWithoutEvidence, TraceRecordKind.D2gsSent, TraceRecordKind.SentWithoutEvidence, TraceRecordKind.D2gsSent, TraceRecordKind.D2gsSent], kinds);
        Assert.Equal(2, session.Stats.SentWithoutEvidence);
    }

    /// <summary>A sniffer trace of the hand trace, with a SentWithoutEvidence record before the sends at the given seconds.</summary>
    private static string AsSniffer(string botPath, string snifferPath, params double[] guessedAt)
    {
        using var reader = new TraceReader(botPath);
        var records = new List<TraceRecord>();
        foreach (var record in reader.Records())
        {
            if (record.Kind == TraceRecordKind.D2gsSent && guessedAt.Any(at => Math.Abs(record.Elapsed.TotalSeconds - at) < 0.0001))
            {
                records.Add(new TraceRecord(TraceRecordKind.SentWithoutEvidence, record.Elapsed, [0]));
            }

            records.Add(record);
        }

        TraceFile.Write(snifferPath, new TraceHeader { Source = TraceSource.Sniffer, StartUtc = reader.Header.StartUtc, ClientLabel = reader.Header.ClientLabel, GameName = reader.Header.GameName }, records);
        return snifferPath;
    }

    private static HandTrace Talk(string label) => new HandTrace(label, "game-1", Start)
        .Join(1)
        .Received(0.5, SyntheticGame.AssignNpc(0x03, NPCCode.TyraelAct4, 5060, 1905))
        .Sent(2.0, SyntheticGame.Unit(0x13, EntityType.NPC, 3))
        .Sent(2.1, SyntheticGame.Unit(0x2F, EntityType.NPC, 3))
        .Sent(2.3, SyntheticGame.Unit(0x30, EntityType.NPC, 3));

    [Fact]
    public void PacketsAndCompareMarkSendsGivenWithoutEvidenceAndCompareCanLeaveThemOut()
    {
        using var root = new TempRoot();
        var a = AsSniffer(Talk("alpha").Sent(2.2, SyntheticGame.Location(0x03, 5070, 1910)).WriteTo(Path.Combine(root.Path, "a")), Path.Combine(root.Path, "a", "alpha-sniffer.d2trace"), 2.2);
        var b = Talk("bravo").WriteTo(Path.Combine(root.Path, "b"));

        var all = TraceCompare.Compare(a, b, new TraceCompareOptions { SentOnly = true });
        var evidenceOnly = TraceCompare.Compare(a, b, new TraceCompareOptions { SentOnly = true, EvidenceOnly = true });

        var only = Assert.Single(Assert.Single(all.Divergences).OnlyA);
        Assert.True(only.Guessed);
        Assert.StartsWith("~ ", only.Text, StringComparison.Ordinal);
        Assert.Equal(1, all.GuessedA);
        Assert.Empty(evidenceOnly.Divergences);
        Assert.Equal(1, evidenceOnly.LeftOutA);

        var game = Path.Combine(root.Path, "game");
        Directory.CreateDirectory(game);
        File.Copy(a, Path.Combine(game, "alpha.d2trace"));
        var lines = PacketQueries.Packets(GameQuery.Open(game), "alpha", new PacketOptions { Sent = true }).ToList();
        Assert.Single(lines, l => l.Contains(PacketQueries.WithoutEvidenceMark, StringComparison.Ordinal));
        Assert.Contains(PacketQueries.WithoutEvidenceMark + " Run", lines.Single(l => l.Contains("Run", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNotesOfAReplayAreThereForEveryCommandNotJustReplay()
    {
        using var root = new TempRoot();
        var game = Path.Combine(root.Path, "midgame-120000-1");
        Directory.CreateDirectory(game);
        TraceFile.Write(Path.Combine(game, "midgame-client1.d2trace"), new TraceHeader { Source = TraceSource.Sniffer, StartUtc = Start, ClientLabel = "midgame-client1", GameName = "midgame-120000-1" },
        [
            new(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(10), SyntheticGame.AssignPlayer(4, CharacterClass.Paladin, "alpha", 5100, 5100)),
            new(TraceRecordKind.D2gsReceived, TimeSpan.FromMilliseconds(20), SyntheticGame.LifeMana(300, 50, 5101, 5102)),
        ]);

        var notes = ReplayNotes.Of(GameTrace.Open(game), "midgame-client1");

        Assert.Contains(notes, n => n.StartsWith("source: sniffer", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("first player assign", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("game flags", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSniffersCopyOfABotsClientIsNamedAsASecondView()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(HandTrace.DateFolder(root.Path, Start), "game-1");
        var bot = new HandTrace("alpha", "game-1", Start, "127.0.0.1#258").Join(1).Received(5, SyntheticTrace.LifeManaUpdate).WriteTo(folder);
        using (var reader = new TraceReader(bot))
        {
            TraceFile.Write(Path.Combine(folder, "alpha-2.d2trace"), new TraceHeader { Source = TraceSource.Sniffer, StartUtc = Start.AddMilliseconds(3), ClientLabel = "alpha", GameName = "game-1", ServerGame = "127.0.0.1#258" }, reader.Records());
        }

        var game = GameTrace.Open(folder);

        Assert.Equal("alpha-2.d2trace", Path.GetFileName(Assert.Single(game.SecondViews).Path));
        Assert.Contains(ReplayNotes.Of(game), n => n.Contains("alpha-2.d2trace", StringComparison.Ordinal) && n.Contains("second view", StringComparison.Ordinal));
    }

    [Fact]
    public void LongComparisonsAreAlignedInBoundedMemoryAndSayWhatMatchedByKindAlone()
    {
        using var root = new TempRoot();
        string Long(string label, uint first)
        {
            var trace = new HandTrace(label, "game-1", Start).Join(1);
            for (var i = 0; i < 6000; i++)
            {
                trace.Sent(1 + i * 0.01, SyntheticGame.Unit(0x13, EntityType.NPC, first + (uint)i));
            }

            return trace.WriteTo(Path.Combine(root.Path, label));
        }

        var a = Long("alpha", 100);
        var b = Long("bravo", 50_000);
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = TraceCompare.Compare(a, b, new TraceCompareOptions { SentOnly = true });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(6001, result.Matched);
        Assert.Equal(6000, result.MatchedUnseen);
        Assert.True(result.Windowed);
        Assert.True(allocated < 100_000_000, $"{allocated:N0} bytes allocated");
    }
}
