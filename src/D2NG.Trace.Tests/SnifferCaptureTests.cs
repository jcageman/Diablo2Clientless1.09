using D2NG.Core.Capture;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

internal static class CapturedBytes
{
    public static readonly DateTime Start = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A game server frame as the server sends it: length header, then the packets Huffman coded.</summary>
    public static byte[] Frame(params byte[][] packets)
    {
        var content = packets.SelectMany(p => p).ToArray();
        var length = (int)Huffman.Compress(content, out var output);
        var compressed = output[..length];
        if (compressed.Length + 1 < 0xF0)
        {
            return [(byte)(compressed.Length + 1), .. compressed];
        }

        var size = compressed.Length + 2;
        return [(byte)(0xF0 | (size >> 8)), (byte)(size & 0xFF), .. compressed];
    }

    public static byte[] JoinAnswer(ushort request, ushort token, uint hash, uint result = 0)
        => Packets.McpRequest(0x04, [.. BitConverter.GetBytes(request), .. BitConverter.GetBytes(token), 0x00, 0x00, 0x7F, 0x00, 0x00, 0x01, .. BitConverter.GetBytes(hash), .. BitConverter.GetBytes(result)]);

    public static byte[] JoinRequest(ushort request, string game)
        => Packets.McpRequest(0x04, [.. BitConverter.GetBytes(request), .. System.Text.Encoding.ASCII.GetBytes(game + "\0"), 0x00]);

    /// <summary>The bot's logon packet with the hash the join answer above carries for <paramref name="token"/>.</summary>
    public static byte[] Logon(string name, CharacterClass characterClass, ushort token) => SyntheticTrace.Logon(name, characterClass, token);

    public const uint Hash = 0x12345678;

    public static List<TraceRecord> Records(string path)
    {
        using var reader = new TraceReader(path);
        return [.. reader.Records()];
    }
}

public class SnifferStreamTests
{
    [Fact]
    public void FramesSplitAcrossSegmentsComeOutAsThePacketsTheBotWouldSee()
    {
        var first = CapturedBytes.Frame([.. SyntheticTrace.Joining()]);
        var second = CapturedBytes.Frame(SyntheticTrace.MonsterHit(0xE9, 40), SyntheticTrace.MonsterStop(0xE9, 7900, 8300));
        byte[] stream = [0xA7, 0x01, .. first, .. second];
        var decoder = new D2gsServerStream(fromStart: true);
        var packets = new List<byte[]>();
        for (var offset = 0; offset < stream.Length; offset += 7)
        {
            decoder.Append(stream.AsSpan(offset, Math.Min(7, stream.Length - offset)), packets);
        }

        var expected = SyntheticTrace.Joining().Append(SyntheticTrace.MonsterHit(0xE9, 40)).Append(SyntheticTrace.MonsterStop(0xE9, 7900, 8300)).ToList();
        Assert.Equal(expected.Count, packets.Count);
        Assert.All(expected.Zip(packets), pair => Assert.Equal(pair.First, pair.Second));
        Assert.Equal(0, decoder.BadFrames);
        Assert.Equal(0, decoder.Pending);
    }

    [Fact]
    public void AStreamJoinedMidwayFindsTheNextFrame()
    {
        var first = CapturedBytes.Frame([.. SyntheticTrace.Joining()]);
        var second = CapturedBytes.Frame([.. SyntheticTrace.Playing(30)]);
        var third = CapturedBytes.Frame([.. SyntheticTrace.Playing(12)]);
        var fourth = CapturedBytes.Frame([.. SyntheticTrace.Playing(5)]);
        var decoder = new D2gsServerStream(fromStart: false);
        var packets = new List<byte[]>();
        decoder.Append(first.AsSpan(first.Length / 2), packets);
        decoder.Append(second, packets);
        decoder.Append(third, packets);
        Assert.Empty(packets);
        decoder.Append(fourth, packets);

        Assert.Equal(17, packets.Count);
        Assert.Equal(1, decoder.DroppedFirstFrames);
        Assert.Equal(1, decoder.Resyncs);
        Assert.True(decoder.SkippedBytes > 0);
    }

    [Fact]
    public void ASearchItsTimeBudgetCutShortRunsToTheEndWhenTheConnectionCloses()
    {
        var playing = CapturedBytes.Frame([.. SyntheticTrace.Playing(20)]);
        var decoder = new D2gsServerStream(fromStart: false) { ResyncBudget = TimeSpan.FromSeconds(-1) };
        var packets = new List<byte[]>();
        decoder.Append([.. new byte[200], .. playing, .. playing, .. playing], packets, [], CapturedBytes.Start, []);
        Assert.Empty(packets);

        decoder.Finish(packets, [], CapturedBytes.Start, []);

        Assert.Equal(40, packets.Count);
        Assert.Equal(1, decoder.DroppedFirstFrames);
    }

    [Fact]
    public void LostBytesDropTheHalfFrameAndTheStreamPicksUpAtTheNext()
    {
        var first = CapturedBytes.Frame([.. SyntheticTrace.Playing(30)]);
        var second = CapturedBytes.Frame([.. SyntheticTrace.Playing(9)]);
        var third = CapturedBytes.Frame([.. SyntheticTrace.Playing(6)]);
        var fourth = CapturedBytes.Frame([.. SyntheticTrace.Playing(3)]);
        var fifth = CapturedBytes.Frame([.. SyntheticTrace.Playing(4)]);
        var decoder = new D2gsServerStream(fromStart: true);
        var packets = new List<byte[]>();
        decoder.Append([0xA7, 0x01, .. first[..10]], packets);
        decoder.Lost();
        decoder.Append(second.AsSpan(5), packets);
        decoder.Append(third, packets);
        decoder.Append(fourth, packets);
        Assert.Empty(packets);
        decoder.Append(fifth, packets);

        // Garbage that decodes into a frame ending exactly where a real one starts cannot be told from a frame, so the
        // first frame a search finds away from where it began is dropped: here the real third one.
        Assert.Equal(SyntheticTrace.Playing(3).Concat(SyntheticTrace.Playing(4)).ToList(), packets);
        Assert.Equal(0, decoder.BadFrames);
    }

    /// <summary>
    /// A gap counts what the stream really lost once it has found a frame again: before, every gap said one, though the
    /// search after it also skips bytes and drops the first frame it finds away from where it began.
    /// </summary>
    [Fact]
    public void AGapCountsTheFramesAndBytesItReallyCost()
    {
        var first = CapturedBytes.Frame([.. SyntheticTrace.Playing(30)]);
        var bad = CapturedBytes.Frame([0xEE, 0x01, 0x02, 0x03]);
        var second = CapturedBytes.Frame([.. SyntheticTrace.Playing(9)]);
        var third = CapturedBytes.Frame([.. SyntheticTrace.Playing(6)]);
        var fourth = CapturedBytes.Frame([.. SyntheticTrace.Playing(3)]);
        var fifth = CapturedBytes.Frame([.. SyntheticTrace.Playing(4)]);
        byte[] garbage = [0x00, 0x00, 0x00];

        var decoder = new D2gsServerStream(fromStart: true);
        var packets = new List<byte[]>();
        var gaps = new List<StreamGap>();
        decoder.Append([0xA7, 0x01, .. first, .. bad, .. garbage, .. second, .. third, .. fourth, .. fifth], packets, gaps, CapturedBytes.Start, []);

        Assert.Equal(30 + 6 + 3 + 4, packets.Count);
        var gap = Assert.Single(gaps);
        Assert.Equal(new StreamGap(30, 2, garbage.Length + second.Length), gap);

        decoder = new D2gsServerStream(fromStart: true);
        packets.Clear();
        gaps.Clear();
        decoder.Append([0xA7, 0x01, .. first[..10]], packets, gaps, CapturedBytes.Start, []);
        decoder.Lost(40);
        decoder.Append([.. second.AsSpan(5), .. third, .. fourth, .. fifth], packets, gaps, CapturedBytes.Start, []);

        Assert.Equal(3 + 4, packets.Count);
        Assert.Equal(new StreamGap(0, 2, 10 + 40 + second.Length - 5 + third.Length), Assert.Single(gaps));
        Assert.Null(decoder.OpenGap);
    }

    /// <summary>
    /// A capture that began mid-stream starts at an arbitrary byte: a frame found right there is the tail of a real one
    /// as often as a real one, and garbage decoding into packets of known ids came through in 2 of 1,000 resyncs of a
    /// recorded game. Its first frame is now dropped like any found away from where a search began.
    /// </summary>
    [Fact]
    public void AStreamJoinedMidwayTrustsNoFrameAtItsFirstByte()
    {
        var decoder = new D2gsServerStream(fromStart: false);
        var packets = new List<byte[]>();
        decoder.Append([.. CapturedBytes.Frame([.. SyntheticTrace.Playing(9)]), .. CapturedBytes.Frame([.. SyntheticTrace.Playing(6)]),
            .. CapturedBytes.Frame([.. SyntheticTrace.Playing(3)]), .. CapturedBytes.Frame([.. SyntheticTrace.Playing(4)])], packets);

        Assert.Equal(SyntheticTrace.Playing(6).Concat(SyntheticTrace.Playing(3)).Concat(SyntheticTrace.Playing(4)).ToList(), packets);
        Assert.Equal(1, decoder.DroppedFirstFrames);
    }

    [Fact]
    public void ClientSegmentsSplitIntoPacketsByTheClientSizes()
    {
        var stream = new D2gsClientStream();
        var packets = new List<byte[]>();
        byte[] run = [0x03, 0xC4, 0x13, 0x6E, 0x07];
        byte[] chat = [0x15, 0x01, 0x00, (byte)'h', (byte)'i', 0x00, 0x00, 0x00];
        byte[] ping = [0x6A, 1, 2, 3, 4, 0, 0, 0, 0];
        stream.Append([.. run, .. chat, .. ping[..4]], packets);
        stream.Append(ping.AsSpan(4), packets);
        stream.Append([0xFD, 1, 2, 3], packets);

        Assert.Equal([run, chat, ping, [0xFD, 1, 2, 3]], packets);
        Assert.Equal(1, stream.Unsized[0xFD]);
    }

    [Fact]
    public void TheClientSizesMatchThePacketsTheBotBuilds()
    {
        var item = new Core.D2GS.Packet.Incoming.ParseItemPacket(new D2gsPacket(SyntheticGame.Item(Core.D2GS.Items.Action.PutInContainer, 7))).Item;
        var built = new D2gsPacket[]
        {
            new Core.D2GS.Packet.Outgoing.RunToLocationPacket(new Core.D2GS.Point(10, 20)),
            new Core.D2GS.Packet.Outgoing.UpdatePlayerLocationPacket(new Core.D2GS.Point(10, 20)),
            new Core.D2GS.Packet.Outgoing.InteractWithEntityPacket(3, Core.D2GS.Objects.EntityType.NPC),
            new Core.D2GS.Packet.Outgoing.RemoveItemFromContainerPacket(item),
            new Core.D2GS.Packet.Outgoing.QuestMessagePacket(3, 0x2AC),
            new Core.D2GS.Packet.Outgoing.RequestQuestDataPacket(),
            new Core.D2GS.Packet.Outgoing.TakeWaypointPacket(5, 0u),
            new Core.D2GS.Packet.Outgoing.PingPacket(),
            new Core.D2GS.Packet.Outgoing.SendChatMessagePacket("hello there"),
        };

        Assert.All(built, packet => Assert.Equal(packet.Raw.Length, D2gsClientStream.Size(packet.Raw)));
    }

    [Fact]
    public void TcpPutsSegmentsBackInOrderDropsRetransmissionsAndReportsHoles()
    {
        var tcp = new TcpReassembler(maxHeldSegments: 2);
        var delivered = new List<byte>();
        var lost = 0;
        void Accept(uint sequence, bool syn, params byte[] payload) => tcp.Accept(sequence, syn, payload, bytes => delivered.AddRange(bytes), () => lost++);

        Accept(100, true);
        Accept(101, false, 1, 2, 3);
        Accept(107, false, 7, 8);
        Accept(104, false, 4, 5, 6);
        Accept(102, false, 2, 3, 4, 5);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], delivered);
        Assert.Equal(0, lost);
        Assert.True(tcp.FromStart);

        Accept(120, false, 20);
        Accept(121, false, 21);
        Accept(122, false, 22);
        Assert.Equal(1, lost);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 20, 21, 22], delivered);
    }
}

public class CaptureSessionTests
{
    private static readonly CaptureConnection Realm = new("10.0.0.2:50000", "10.0.0.1:6113");

    [Fact]
    public void TwoClientsOfOneGameGetOneFileEachInTheGamesFolderStampedWithCaptureTime()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var start = CapturedBytes.Start;
        session.Open(Realm, CapturedProtocol.Mcp, start, fromStart: true);
        session.Data(Realm, CapturedProtocol.Mcp, false, [0x01, .. CapturedBytes.JoinRequest(7, "game-7")], start.AddMilliseconds(10));
        session.Data(Realm, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(7, 0x33, CapturedBytes.Hash), start.AddMilliseconds(20));

        var first = new CaptureConnection("10.0.0.2:50001", "10.0.0.1:4000");
        var second = new CaptureConnection("10.0.0.3:50002", "10.0.0.1:4000");
        foreach (var (connection, name, offset) in new[] { (first, "alpha", 100), (second, "bravo", 300) })
        {
            var at = start.AddMilliseconds(offset);
            session.Open(connection, CapturedProtocol.D2gs, at, fromStart: true);
            session.Data(connection, CapturedProtocol.D2gs, true, [0xA7, 0x01], at);
            session.Data(connection, CapturedProtocol.D2gs, false, CapturedBytes.Logon(name, CharacterClass.Sorceress, 0x33), at.AddMilliseconds(5));
            session.Data(connection, CapturedProtocol.D2gs, true, CapturedBytes.Frame([.. SyntheticTrace.Joining()]), at.AddMilliseconds(40));
        }

        session.Close(first, start.AddSeconds(5));
        session.Dispose();

        var folder = Path.Combine(HandTrace.DateFolder(root.Path, start), "game-7");
        Assert.True(File.Exists(Path.Combine(folder, "alpha.d2trace")));
        Assert.True(File.Exists(Path.Combine(folder, "bravo.d2trace")));
        using var reader = new TraceReader(Path.Combine(folder, "alpha.d2trace"));
        Assert.Equal(TraceSource.Sniffer, reader.Header.Source);
        Assert.Equal("alpha", reader.Header.ClientLabel);
        Assert.Equal("game-7", reader.Header.GameName);
        Assert.Equal("127.0.0.1#51", reader.Header.ServerGame);
        Assert.Equal(start.AddMilliseconds(100), reader.Header.StartUtc);
        var records = reader.Records().ToList();
        Assert.Equal(TraceRecordKind.D2gsSent, records[0].Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(5), records[0].Elapsed);
        Assert.Equal(SyntheticTrace.Joining().Count(), records.Count(r => r.Kind == TraceRecordKind.D2gsReceived));
        Assert.All(records.Where(r => r.Kind == TraceRecordKind.D2gsReceived), r => Assert.Equal(TimeSpan.FromMilliseconds(40), r.Elapsed));
        Assert.Single(Directory.GetFiles(Path.Combine(HandTrace.DateFolder(root.Path, start), "realm")));
        Assert.Equal(2, session.Stats.GameFiles);
    }

    [Fact]
    public void ARejoinLandsBesideTheFirstFileAndANewGameOfTheSameNameInItsOwnFolder()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var start = CapturedBytes.Start;
        session.Open(Realm, CapturedProtocol.Mcp, start, fromStart: false);
        var games = new[] { (Token: (ushort)0x10, Offset: 0), (Token: (ushort)0x10, Offset: 60), (Token: (ushort)0x11, Offset: 120) };
        foreach (var (token, offset) in games)
        {
            var at = start.AddSeconds(offset);
            session.Data(Realm, CapturedProtocol.Mcp, false, CapturedBytes.JoinRequest(1, "same-name"), at);
            session.Data(Realm, CapturedProtocol.Mcp, true, CapturedBytes.JoinAnswer(1, token, CapturedBytes.Hash), at);
            var connection = new CaptureConnection($"10.0.0.2:{50100 + offset}", "10.0.0.1:4000");
            session.Open(connection, CapturedProtocol.D2gs, at, fromStart: true);
            session.Data(connection, CapturedProtocol.D2gs, false, CapturedBytes.Logon("alpha", CharacterClass.Amazon, token), at);
            session.Close(connection, at.AddSeconds(10));
        }

        session.Dispose();
        var date = HandTrace.DateFolder(root.Path, start);
        Assert.Equal(["alpha-2.d2trace", "alpha.d2trace"], Directory.GetFiles(Path.Combine(date, "same-name")).Where(f => f.EndsWith(".d2trace", StringComparison.Ordinal)).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(date, "same-name~2", "alpha.d2trace")));
    }

    [Fact]
    public void AConnectionJoinedAfterItsLogonIsWrittenAsAMidGameCapture()
    {
        using var root = new TempRoot();
        var session = new CaptureSession(new CaptureTraceWriter(root.Path));
        var connection = new CaptureConnection("10.0.0.2:50001", "10.0.0.1:4000");
        var joining = CapturedBytes.Frame([.. SyntheticTrace.Joining()]);
        var playing = CapturedBytes.Frame([.. SyntheticTrace.Playing(20)]);
        session.Data(connection, CapturedProtocol.D2gs, true, [.. joining[3..], .. playing, .. playing, .. playing], CapturedBytes.Start);
        session.Data(connection, CapturedProtocol.D2gs, false, [0x6A, 1, 2, 3, 4, 0, 0, 0, 0], CapturedBytes.Start.AddSeconds(1));
        session.Dispose();

        Assert.Equal(1, session.Stats.MidGameFiles);
        var file = Assert.Single(session.Stats.Files);
        Assert.Contains("midgame-", file);
        using var reader = new TraceReader(file);
        Assert.Equal("midgame-client1", reader.Header.ClientLabel);
        Assert.Null(reader.Header.ServerGame);
        Assert.Equal(41, reader.Records().Count());
    }
}
