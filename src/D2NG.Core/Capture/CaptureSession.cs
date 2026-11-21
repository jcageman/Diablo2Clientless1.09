using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace D2NG.Core.Capture;

/// <summary>A TCP connection as the capture saw it: the client's and the server's endpoint.</summary>
public readonly record struct CaptureConnection(string Client, string Server)
{
    public override string ToString() => $"{Client} -> {Server}";
}

public sealed class CaptureStats
{
    public Dictionary<TraceRecordKind, long> Packets { get; } = [];

    public int GameFiles { get; internal set; }

    public int RealmFiles { get; internal set; }

    /// <summary>Game connections whose start the capture missed: no logon, so neither game nor character is known.</summary>
    public int MidGameFiles { get; internal set; }

    /// <summary>Game connections whose logon named a game the capture never saw joined.</summary>
    public int UnknownGameFiles { get; internal set; }

    public int Gaps { get; internal set; }

    public int BadFrames { get; internal set; }

    public long SkippedBytes { get; internal set; }

    /// <summary>Received packet ids the bot's size table could not size, which ended their frame.</summary>
    public Dictionary<byte, int> UnsizedReceived { get; } = [];

    /// <summary>Sent packet ids whose size is not known, by how their length was decided.</summary>
    public Dictionary<(byte Id, UnsizedCut How), int> UnsizedSent { get; } = [];

    /// <summary>Sent packets written after a mark that their connection was chosen without evidence (text captures only).</summary>
    public long SentWithoutEvidence { get; internal set; }

    /// <summary>Trace files that could not be opened or written; their connection's records are dropped, every other file goes on.</summary>
    public int FileFailures { get; internal set; }

    /// <summary>Records whose capture time ran backwards and were written with the time of the record before them.</summary>
    public long ClampedRecords { get; internal set; }

    public List<string> Files { get; } = [];

    internal void Count(TraceRecordKind kind) => Packets[kind] = Packets.GetValueOrDefault(kind) + 1;
}

/// <summary>
/// Turns captured TCP traffic of real clients into the trace files the bot writes, source sniffer: per game
/// one file per game server connection, labelled with the character from its logon, in the game's folder
/// (named by the game the realm joined it to, claimed by the server's id for the game), and one realm file
/// per chat or realm connection. A game connection's records wait in memory until its first sent packet
/// says whether the capture saw the logon.
///
/// Records reach a file in capture time order by construction: while a direction holds bytes behind a hole
/// (<see cref="HoldBefore"/>), every record of the connection stamped at or after the earliest held byte waits,
/// so bytes released late with their own earlier time still go in before them. What the file still clamps
/// is time that really runs backwards in what it is given: capture timestamps out of order between packets,
/// or a text log's lines.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private const byte JoinGame = 0x04;
    private const byte CharLogon = 0x07;
    private const byte GameLogon = 0x65;
    private const int LogonNameOffset = 13;
    private const int LogonNameLength = 16;
    private const int ReceivedBeforeMidGame = 200;
    private const int RealmPacketsBeforeLabel = 32;

    private readonly CaptureTraceWriter _writer;
    private readonly Dictionary<CaptureConnection, Connection> _connections = [];
    private readonly Dictionary<(CaptureConnection Realm, ushort Request), string> _joinRequests = [];
    private readonly Dictionary<(uint Hash, ushort Token), (string Game, string Address)> _joins = [];
    private readonly List<byte[]> _scratch = [];
    private readonly List<StreamGap> _gapsBefore = [];
    private readonly List<DateTime> _times = [];
    private int _midGameCount;
    private int _realmCount;
    private long _order;

    public CaptureSession(CaptureTraceWriter writer)
    {
        _writer = writer;
    }

    public CaptureStats Stats { get; } = new();

    /// <summary>Called with every packet as it is recorded, before any file it goes to is open.</summary>
    public Action<CaptureConnection, TraceRecordKind, byte[], DateTime> Recorded { get; set; }

    /// <summary>Called with a line for everything worth telling the person running the capture.</summary>
    public Action<string> Report { get; set; }

    public bool IsOpen(CaptureConnection id) => _connections.ContainsKey(id);

    /// <summary>Bytes of a game server connection's server side waiting for the rest of their frame.</summary>
    public int PendingServerBytes(CaptureConnection id)
        => _connections.TryGetValue(id, out var connection) && connection.ServerGame != null ? connection.ServerGame.Pending : 0;

    /// <param name="label">For a realm connection: the file label, instead of one taken from the character logon.</param>
    public void Open(CaptureConnection id, CapturedProtocol protocol, DateTime atUtc, bool fromStart, string label = null)
    {
        if (_connections.ContainsKey(id))
        {
            Close(id, atUtc);
        }

        _connections[id] = new Connection(id, protocol, atUtc, fromStart) { Label = label };
    }

    /// <summary>
    /// Bytes of the connection captured at <paramref name="earliestUtc"/> or later may still come out of order
    /// (a direction holds them behind a hole): records from then on wait until the hold is lifted with null.
    /// </summary>
    public void HoldBefore(CaptureConnection id, DateTime? earliestUtc)
    {
        if (_connections.TryGetValue(id, out var connection))
        {
            connection.HoldBefore = earliestUtc;
            FlushWaiting(connection);
        }
    }

    /// <summary>Bytes in stream order for one direction. A connection not opened first is opened here, as one the capture joined midway.</summary>
    /// <param name="withoutEvidence">For sent bytes: the capture could not tell which connection sent them, and this one was a guess.</param>
    public void Data(CaptureConnection id, CapturedProtocol protocol, bool fromServer, ReadOnlySpan<byte> bytes, DateTime atUtc, bool withoutEvidence = false)
    {
        if (!_connections.TryGetValue(id, out var connection))
        {
            connection = new Connection(id, protocol, atUtc, fromStart: false);
            _connections[id] = connection;
        }

        _scratch.Clear();
        _gapsBefore.Clear();
        _times.Clear();
        if (connection.Protocol == CapturedProtocol.D2gs)
        {
            if (fromServer)
            {
                var badBefore = connection.ServerGame.BadFrames;
                connection.ServerGame.Append(bytes, _scratch, _gapsBefore, atUtc, _times);
                CountServerStream(connection, badBefore);
            }
            else
            {
                var stream = connection.ClientGame;
                stream.Append(bytes, _scratch);
                foreach (var (key, count) in stream.UnsizedHow)
                {
                    if (connection.UnsizedSentSeen.GetValueOrDefault(key) < count)
                    {
                        Stats.UnsizedSent[key] = Stats.UnsizedSent.GetValueOrDefault(key) + count - connection.UnsizedSentSeen.GetValueOrDefault(key);
                        connection.UnsizedSentSeen[key] = count;
                    }
                }
            }
        }
        else
        {
            (fromServer ? connection.ServerRealm : connection.ClientRealm).Append(bytes, _scratch);
        }

        RecordScratch(connection, Kind(connection.Protocol, fromServer), atUtc, withoutEvidence);
    }

    private void CountServerStream(Connection connection, int badBefore)
    {
        var stream = connection.ServerGame;
        foreach (var (type, count) in stream.UnsizedPackets)
        {
            if (connection.UnsizedSeen.GetValueOrDefault(type) < count)
            {
                Stats.UnsizedReceived[type] = Stats.UnsizedReceived.GetValueOrDefault(type) + count - connection.UnsizedSeen.GetValueOrDefault(type);
                connection.UnsizedSeen[type] = count;
            }
        }

        Stats.BadFrames += stream.BadFrames - badBefore;
    }

    /// <summary>The packets a stream just completed, each after the gaps found before it.</summary>
    private void RecordScratch(Connection connection, TraceRecordKind kind, DateTime atUtc, bool withoutEvidence)
    {
        var gap = 0;
        for (var i = 0; i < _scratch.Count; i++)
        {
            var at = i < _times.Count ? _times[i] : atUtc;
            for (; gap < _gapsBefore.Count && _gapsBefore[gap].PacketIndex == i; gap++)
            {
                Gap(connection, _gapsBefore[gap].Frames, _gapsBefore[gap].Bytes, at);
            }

            if (withoutEvidence && kind == TraceRecordKind.D2gsSent)
            {
                Stats.SentWithoutEvidence++;
                Emit(connection, TraceRecordKind.SentWithoutEvidence, [0], atUtc);
            }

            Record(connection, kind, _scratch[i], at);
        }

        for (; gap < _gapsBefore.Count; gap++)
        {
            Gap(connection, _gapsBefore[gap].Frames, _gapsBefore[gap].Bytes, atUtc);
        }
    }

    /// <summary>
    /// Bytes of one direction went missing (<paramref name="missedBytes"/> of them where known): what was half read is
    /// dropped and a gap record marks the spot. A game server's side writes it with the packets after it, once its
    /// stream has found a frame again, so the record counts the frames that search dropped too.
    /// </summary>
    public void Lost(CaptureConnection id, bool fromServer, DateTime atUtc, long missedBytes = 0)
    {
        if (!_connections.TryGetValue(id, out var connection))
        {
            return;
        }

        if (connection.Protocol == CapturedProtocol.D2gs)
        {
            if (fromServer)
            {
                connection.ServerGame.Lost(missedBytes);
                return;
            }

            connection.ClientGame.Lost();
        }
        else
        {
            (fromServer ? connection.ServerRealm : connection.ClientRealm).Lost();
        }

        Gap(connection, 1, missedBytes, atUtc);
    }

    public void Close(CaptureConnection id, DateTime atUtc)
    {
        if (!_connections.Remove(id, out var connection))
        {
            return;
        }

        if (connection.Protocol == CapturedProtocol.D2gs)
        {
            _scratch.Clear();
            _gapsBefore.Clear();
            _times.Clear();
            var badBefore = connection.ServerGame.BadFrames;
            connection.ServerGame.Finish(_scratch, _gapsBefore, atUtc, _times);
            CountServerStream(connection, badBefore);
            RecordScratch(connection, TraceRecordKind.D2gsReceived, atUtc, withoutEvidence: false);
            _scratch.Clear();
            connection.ClientGame.Flush(_scratch);
            foreach (var packet in _scratch)
            {
                Record(connection, TraceRecordKind.D2gsSent, packet, atUtc);
            }

            Stats.SkippedBytes += connection.ServerGame.SkippedBytes;
            if (connection.ServerGame.OpenGap is { } open)
            {
                Gap(connection, open.Frames, open.Bytes, atUtc);
            }
        }

        connection.HoldBefore = null;
        FlushWaiting(connection);
        if (connection.File == null && connection.Held.Count > 0)
        {
            OpenFile(connection);
        }

        if (connection.File != null)
        {
            connection.File.Dispose();
            Stats.ClampedRecords += connection.File.Clamped;
        }
    }

    public void Dispose()
    {
        foreach (var id in _connections.Keys.ToList())
        {
            Close(id, _connections[id].LastUtc);
        }
    }

    private static TraceRecordKind Kind(CapturedProtocol protocol, bool fromServer) => protocol switch
    {
        CapturedProtocol.D2gs => fromServer ? TraceRecordKind.D2gsReceived : TraceRecordKind.D2gsSent,
        CapturedProtocol.Bncs => fromServer ? TraceRecordKind.BncsReceived : TraceRecordKind.BncsSent,
        _ => fromServer ? TraceRecordKind.McpReceived : TraceRecordKind.McpSent,
    };

    private void Record(Connection connection, TraceRecordKind kind, byte[] packet, DateTime atUtc)
    {
        Stats.Count(kind);
        connection.LastUtc = atUtc;
        Recorded?.Invoke(connection.Id, kind, packet, atUtc);
        if (connection.Protocol == CapturedProtocol.Mcp)
        {
            NoteRealm(connection, kind, packet);
        }

        Emit(connection, kind, packet, atUtc);
        if (connection.File != null || connection.Failed)
        {
            return;
        }

        if (connection.Protocol == CapturedProtocol.D2gs)
        {
            if (kind == TraceRecordKind.D2gsSent)
            {
                if (packet[0] == GameLogon && packet.Length >= LogonNameOffset)
                {
                    connection.Logon = packet;
                }

                OpenFile(connection);
            }
            else if (connection.Held.Count + connection.Waiting.Count >= ReceivedBeforeMidGame)
            {
                OpenFile(connection);
            }
        }
        else if (connection.Label != null || connection.Character != null || connection.Held.Count + connection.Waiting.Count >= RealmPacketsBeforeLabel)
        {
            OpenFile(connection);
        }
    }

    /// <summary>A record goes to the file (or the records held until it opens) unless a hold makes it wait for earlier ones.</summary>
    private void Emit(Connection connection, TraceRecordKind kind, byte[] payload, DateTime atUtc)
    {
        if (connection.HoldBefore == null && connection.Waiting.Count == 0)
        {
            Write(connection, kind, payload, atUtc);
            return;
        }

        var record = (atUtc, _order++, kind, payload);
        var index = connection.Waiting.BinarySearch(record, WaitingOrder.Instance);
        connection.Waiting.Insert(~index, record);
        FlushWaiting(connection);
    }

    private void FlushWaiting(Connection connection)
    {
        var flushed = 0;
        while (flushed < connection.Waiting.Count && (connection.HoldBefore is not { } hold || connection.Waiting[flushed].At < hold))
        {
            var (at, _, kind, payload) = connection.Waiting[flushed];
            Write(connection, kind, payload, at);
            flushed++;
        }

        connection.Waiting.RemoveRange(0, flushed);
    }

    private void Write(Connection connection, TraceRecordKind kind, byte[] payload, DateTime atUtc)
    {
        if (connection.Failed)
        {
            return;
        }

        if (connection.File == null)
        {
            connection.Held.Add((kind, payload, atUtc));
            return;
        }

        connection.File.Write(kind, payload, atUtc);
        if (connection.File.Failure is { } failure)
        {
            Fail(connection, $"writing {connection.File.Path} failed ({failure}); the rest of this connection is not recorded");
        }
    }

    private void Fail(Connection connection, string why)
    {
        connection.Failed = true;
        connection.Held.Clear();
        connection.Waiting.Clear();
        Stats.FileFailures++;
        Report?.Invoke($"{connection.Id}: {why}");
    }

    /// <summary>A gap record: the frames (or for a sent or realm stream, the holes) known lost, then the bytes skipped where known.</summary>
    private void Gap(Connection connection, int frames, long bytes, DateTime atUtc)
    {
        Stats.Gaps++;
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)frames);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)Math.Min(bytes, uint.MaxValue));
        Emit(connection, TraceRecordKind.Gap, payload, atUtc);
    }

    private void NoteRealm(Connection connection, TraceRecordKind kind, byte[] packet)
    {
        if (packet.Length < 3)
        {
            return;
        }

        if (kind == TraceRecordKind.McpSent)
        {
            if (packet[2] == JoinGame && packet.Length >= 6)
            {
                _joinRequests[(connection.Id, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3)))] = Text(packet, 5);
            }
            else if (packet[2] == CharLogon && connection.Character == null)
            {
                connection.Character = Text(packet, 3);
            }
        }
        else if (packet[2] == JoinGame && packet.Length >= 21)
        {
            // Request ids are per realm connection and every client starts them low, so two clients joining at
            // once use the same ones: the answer is paired with the request on its own connection.
            var request = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3));
            var token = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(5));
            var address = new IPAddress(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(9))).ToString();
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(13));
            var result = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(17));
            if (_joinRequests.Remove((connection.Id, request), out var game) && result == 0)
            {
                _joins[(hash, token)] = (game, address);
            }
        }
    }

    private void OpenFile(Connection connection)
    {
        var start = connection.OpenedUtc;
        try
        {
            if (connection.Protocol == CapturedProtocol.D2gs)
            {
                connection.File = OpenGameFile(connection, start);
                Stats.GameFiles++;
            }
            else
            {
                var number = ++_realmCount;
                var label = connection.Label ?? (connection.Character != null
                    ? $"{connection.Character}-{(connection.Protocol == CapturedProtocol.Mcp ? "mcp" : "bncs")}"
                    : $"{(connection.Protocol == CapturedProtocol.Mcp ? "mcp" : "bncs")}{number}");
                connection.File = _writer.OpenRealm(start, label);
                Stats.RealmFiles++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Fail(connection, $"no trace file could be opened ({e.Message}); this connection is not recorded");
            return;
        }

        Stats.Files.Add(connection.File.Path);
        foreach (var (kind, payload, at) in connection.Held)
        {
            Write(connection, kind, payload, at);
            if (connection.Failed)
            {
                return;
            }
        }

        connection.Held.Clear();
    }

    private CaptureTraceFile OpenGameFile(Connection connection, DateTime start)
    {
        if (connection.Logon is not { } logon)
        {
            Stats.MidGameFiles++;
            var number = ++_midGameCount;
            // A capture that joined game connections midway cannot tell their games apart (no logon, no join), so
            // each gets a folder of its own rather than letting two games that started in one second read as one.
            var midGame = $"midgame-{start.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture)}-{number.ToString(CultureInfo.InvariantCulture)}";
            Report?.Invoke($"{connection.Id}: the capture joined this game connection after its logon; character and game are unknown, written to {midGame}");
            return _writer.OpenGame(start, midGame, $"midgame-client{number}", null);
        }

        var character = Text(logon, LogonNameOffset, LogonNameLength);
        var hash = BinaryPrimitives.ReadUInt32LittleEndian(logon.AsSpan(1));
        var token = BinaryPrimitives.ReadUInt16LittleEndian(logon.AsSpan(5));
        string game;
        string serverGame;
        if (_joins.TryGetValue((hash, token), out var joined))
        {
            game = joined.Game;
            serverGame = $"{joined.Address}#{token.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            Stats.UnknownGameFiles++;
            game = "unknown-game-" + start.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture);
            serverGame = $"{ServerAddress(connection.Id)}#{token.ToString(CultureInfo.InvariantCulture)}";
            Report?.Invoke($"{connection.Id}: logon of {character} names a game whose join the capture did not see; written to {game}");
        }

        return _writer.OpenGame(start, game, character.Length > 0 ? character : "unnamed", serverGame);
    }

    private static string ServerAddress(CaptureConnection id)
    {
        var colon = id.Server.LastIndexOf(':');
        return colon > 0 ? id.Server[..colon] : id.Server;
    }

    private static string Text(byte[] packet, int offset, int max = int.MaxValue)
    {
        if (offset >= packet.Length)
        {
            return "";
        }

        var length = Math.Min(packet.Length - offset, max);
        var end = Array.IndexOf(packet, (byte)0, offset, length);
        return Encoding.ASCII.GetString(packet, offset, (end < 0 ? offset + length : end) - offset);
    }

    private sealed class WaitingOrder : IComparer<(DateTime At, long Order, TraceRecordKind Kind, byte[] Payload)>
    {
        public static readonly WaitingOrder Instance = new();

        public int Compare((DateTime At, long Order, TraceRecordKind Kind, byte[] Payload) x, (DateTime At, long Order, TraceRecordKind Kind, byte[] Payload) y)
        {
            var byTime = x.At.CompareTo(y.At);
            return byTime != 0 ? byTime : x.Order.CompareTo(y.Order);
        }
    }

    private sealed class Connection(CaptureConnection id, CapturedProtocol protocol, DateTime openedUtc, bool fromStart)
    {
        public CaptureConnection Id { get; } = id;
        public CapturedProtocol Protocol { get; } = protocol;
        public DateTime OpenedUtc { get; } = openedUtc;
        public DateTime LastUtc { get; set; } = openedUtc;
        public D2gsServerStream ServerGame { get; } = protocol == CapturedProtocol.D2gs ? new D2gsServerStream(fromStart) : null;
        public D2gsClientStream ClientGame { get; } = protocol == CapturedProtocol.D2gs ? new D2gsClientStream() : null;
        public RealmStream ServerRealm { get; } = protocol != CapturedProtocol.D2gs ? new RealmStream(protocol, clientSide: false, fromStart) : null;
        public RealmStream ClientRealm { get; } = protocol != CapturedProtocol.D2gs ? new RealmStream(protocol, clientSide: true, fromStart) : null;
        public List<(TraceRecordKind Kind, byte[] Payload, DateTime At)> Held { get; } = [];
        public List<(DateTime At, long Order, TraceRecordKind Kind, byte[] Payload)> Waiting { get; } = [];
        public DateTime? HoldBefore { get; set; }
        public Dictionary<byte, int> UnsizedSeen { get; } = [];
        public Dictionary<(byte, UnsizedCut), int> UnsizedSentSeen { get; } = [];
        public byte[] Logon { get; set; }
        public string Character { get; set; }
        public string Label { get; set; }
        public CaptureTraceFile File { get; set; }
        public bool Failed { get; set; }
    }
}
