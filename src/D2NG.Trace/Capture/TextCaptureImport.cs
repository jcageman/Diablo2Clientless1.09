using D2NG.Core.Capture;
using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace D2NG.Trace.Capture;

public sealed class TextCaptureImportResult
{
    public long Lines { get; internal set; }

    public long UsedLines { get; internal set; }

    public int GameConnections { get; internal set; }

    /// <summary>Received segments of a game connection the log named the connection of (an "Instance N" line followed).</summary>
    public long SegmentsNamed { get; internal set; }

    /// <summary>Received segments found inside a frame a later "Instance N" line printed whole.</summary>
    public long SegmentsFoundInFrame { get; internal set; }

    /// <summary>Received segments given, without evidence, to a connection waiting for the rest of a frame.</summary>
    public long SegmentsGuessed { get; internal set; }

    /// <summary>Sent lines that carry no packet bytes and were rebuilt from their decoded fields (run, sell, interact).</summary>
    public Dictionary<string, long> Rebuilt { get; } = [];

    /// <summary>Sent lines with no bytes and nothing to rebuild them from.</summary>
    public long UnusableSentLines { get; internal set; }

    public Dictionary<string, long> SentAttribution { get; internal set; } = [];

    public long SentInconsistent { get; internal set; }

    public CaptureStats Stats { get; internal set; }
}

/// <summary>
/// Reads the sniffer's text log (Serilog lines, raw payloads on) into trace files, source sniffer. The game
/// server side is decoded again from the raw segments: the log's own decoding re-printed a connection's
/// first frame on every segment once a frame it could not size had stuck it, which is what reads as a
/// periodic resync in old captures. Timestamps are the log's, which the capture delivered in batches
/// about a second apart.
/// </summary>
public static class TextCaptureImport
{
    private static readonly Regex LinePattern = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(\w+)\] (.*)$", RegexOptions.Compiled);
    private static readonly Regex InstancePattern = new(@"^Instance (\d+) Full packet received: ([0-9A-F]+)$", RegexOptions.Compiled);
    private static readonly Regex RunPattern = new(@"^RunToLocationPacket -> \((\d+), (\d+)\)$", RegexOptions.Compiled);
    private static readonly Regex SellPattern = new(@"^SellItem -> (\d+)$", RegexOptions.Compiled);
    private static readonly Regex InteractPattern = new(@"^EntityInteract -> ID: (\d+) Type: (\d+)$", RegexOptions.Compiled);
    private const int ContainedMinimumBytes = 8;
    private const int GiveUpLines = 20_000;
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromMilliseconds(1500);

    public static TextCaptureImportResult Import(string textFile, string traceRoot, Action<string> report = null)
    {
        var lastSeen = LastSeenPerInstance(textFile);
        using var importer = new Importer(traceRoot, lastSeen, report);
        foreach (var line in File.ReadLines(textFile))
        {
            importer.Line(line);
        }

        return importer.Finish();
    }

    /// <summary>When each game connection's server side last sent, by the log's instance numbers.</summary>
    private static Dictionary<int, (long Line, DateTime Utc)> LastSeenPerInstance(string textFile)
    {
        var last = new Dictionary<int, (long, DateTime)>();
        long number = 0;
        foreach (var line in File.ReadLines(textFile))
        {
            number++;
            var match = LinePattern.Match(line);
            if (!match.Success || !match.Groups[3].Value.StartsWith("Instance ", StringComparison.Ordinal))
            {
                continue;
            }

            var space = match.Groups[3].Value.IndexOf(' ', 9);
            if (space > 9 && int.TryParse(match.Groups[3].Value.AsSpan(9, space - 9), NumberStyles.None, CultureInfo.InvariantCulture, out var instance))
            {
                last[instance] = (number, Time(match.Groups[1].Value));
            }
        }

        return last;
    }

    /// <summary>A Serilog timestamp of the sniffer's log as UTC.</summary>
    public static DateTime Time(string text)
        => DateTimeOffset.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture).UtcDateTime;

    /// <summary>Bytes as the sniffer prints them, "0x65, 0x00"; null when the text is anything else.</summary>
    public static byte[] PrintedBytes(string text)
    {
        var parts = text.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !byte.TryParse(part.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    private sealed class Event
    {
        public long Number;
        public DateTime Utc;
        public CapturedProtocol Protocol;
        public bool FromServer;
        public byte[] Bytes;
        public bool Greeting;
        public int Instance = -1;
        public bool WindowClosed;
        public string Hex;

        /// <summary>For a game server segment: the packets it completed, decoded ahead of the session to judge sends by their replies.</summary>
        public List<byte[]> Packets;
    }

    private sealed class Importer : IDisposable
    {
        private static readonly CaptureConnection Bncs = new("text capture", "bncs");
        private static readonly CaptureConnection Mcp = new("text capture", "mcp");

        private readonly CaptureSession _session;
        private readonly Dictionary<int, (long Line, DateTime Utc)> _lastSeen;
        private readonly LinkedList<Event> _queue = new();
        private readonly List<Event> _unresolved = [];
        private readonly Dictionary<int, D2gsServerStream> _ahead = [];
        private LinkedListNode<Event> _decodedUpTo;
        private DateTime _lastUtc;
        private readonly Dictionary<int, CapturedClient> _clients = [];
        private readonly Dictionary<CaptureConnection, CapturedClient> _byConnection = [];
        private readonly SentAttribution _attribution = new();
        private readonly TextCaptureImportResult _result = new();
        private readonly Action<string> _report;
        private Event _openSegment;
        private (DateTime Utc, long Number, uint Id, uint Type)? _interact;
        private int _nextInstance;
        private long _number;
        private bool _realmOpen;
        private CapturedClient _lastFed;

        public Importer(string root, Dictionary<int, (long, DateTime)> lastSeen, Action<string> report)
        {
            _lastSeen = lastSeen;
            _report = report;
            _session = new CaptureSession(new CaptureTraceWriter(root)) { Report = report };
            _session.Recorded = (id, kind, packet, at) =>
            {
                if (kind == TraceRecordKind.D2gsReceived && _byConnection.TryGetValue(id, out var client))
                {
                    client.Live.Received(packet, at);
                }
            };
        }

        public void Line(string line)
        {
            _number++;
            _result.Lines++;
            var match = LinePattern.Match(line);
            if (!match.Success)
            {
                return;
            }

            var utc = Time(match.Groups[1].Value);
            _lastUtc = utc;
            var message = match.Groups[3].Value;
            if (_interact is { } interact)
            {
                _interact = null;
                var bytes = message.StartsWith("0x13, ", StringComparison.Ordinal) ? PrintedBytes(message) : null;
                if (bytes != null)
                {
                    Sent(interact.Utc, CapturedProtocol.D2gs, bytes);
                    _result.UsedLines++;
                    return;
                }

                Rebuilt("EntityInteract");
                Sent(interact.Utc, CapturedProtocol.D2gs, [0x13, .. BitConverter.GetBytes(interact.Type), .. BitConverter.GetBytes(interact.Id)]);
            }

            if (Classify(utc, message))
            {
                _result.UsedLines++;
            }

            Drain(final: false);
        }

        private bool Classify(DateTime utc, string message)
        {
            if (message.StartsWith("Instance ", StringComparison.Ordinal))
            {
                var instance = InstancePattern.Match(message);
                if (instance.Success)
                {
                    Hint(int.Parse(instance.Groups[1].Value, CultureInfo.InvariantCulture), instance.Groups[2].Value);
                    return true;
                }

                return false;
            }

            if (After(message, "D2GS Initial packet received: ") is { } greeting)
            {
                CloseWindow();
                Enqueue(new Event { Utc = utc, Protocol = CapturedProtocol.D2gs, FromServer = true, Bytes = Convert.FromHexString(greeting), Greeting = true, Instance = _nextInstance++ });
                return true;
            }

            if (After(message, "D2GS Full packet received: ") is { } segment)
            {
                CloseWindow();
                _openSegment = new Event { Utc = utc, Protocol = CapturedProtocol.D2gs, FromServer = true, Bytes = Convert.FromHexString(segment), Hex = segment };
                Enqueue(_openSegment);
                return true;
            }

            if (message.StartsWith("Outgoing packet ", StringComparison.Ordinal) || message.StartsWith("Send unknown D2GS packet", StringComparison.Ordinal))
            {
                var data = AfterAny(message, " with data: ", " with data ");
                var bytes = data == null ? null : PrintedBytes(data);
                if (bytes == null)
                {
                    _result.UnusableSentLines++;
                    return false;
                }

                Sent(utc, CapturedProtocol.D2gs, bytes);
                return true;
            }

            if (RunPattern.Match(message) is { Success: true } run)
            {
                Rebuilt("Run");
                var x = ushort.Parse(run.Groups[1].Value, CultureInfo.InvariantCulture);
                var y = ushort.Parse(run.Groups[2].Value, CultureInfo.InvariantCulture);
                Sent(utc, CapturedProtocol.D2gs, [0x03, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y)]);
                return true;
            }

            if (message.StartsWith("BuyItem -> ", StringComparison.Ordinal) && After(message, " with data ") is { } buy && PrintedBytes(buy) is { } buyBytes)
            {
                Sent(utc, CapturedProtocol.D2gs, buyBytes);
                return true;
            }

            if (SellPattern.Match(message) is { Success: true } sell)
            {
                Rebuilt("SellItem (merchant id unknown, left 0)");
                Sent(utc, CapturedProtocol.D2gs, [0x33, 0, 0, 0, 0, .. BitConverter.GetBytes(uint.Parse(sell.Groups[1].Value, CultureInfo.InvariantCulture)), 0, 0, 0, 0, 0, 0, 0, 0]);
                return true;
            }

            if (InteractPattern.Match(message) is { Success: true } interactLine)
            {
                _interact = (utc, _number, uint.Parse(interactLine.Groups[1].Value, CultureInfo.InvariantCulture), uint.Parse(interactLine.Groups[2].Value, CultureInfo.InvariantCulture));
                return true;
            }

            if (After(message, "BNCS Full packet received: ") is { } bncsReceived)
            {
                CloseWindow();
                Realm(utc, CapturedProtocol.Bncs, true, Convert.FromHexString(bncsReceived));
                return true;
            }

            if (After(message, "MCP Initial packet received: ") is { } mcpGreeting)
            {
                CloseWindow();
                Realm(utc, CapturedProtocol.Mcp, true, Convert.FromHexString(mcpGreeting));
                return true;
            }

            if (After(message, "MCP Full packet received: ") is { } mcpReceived)
            {
                CloseWindow();
                Realm(utc, CapturedProtocol.Mcp, true, Convert.FromHexString(mcpReceived));
                return true;
            }

            foreach (var (prefix, protocol) in new[] { ("Send BNCS packet", CapturedProtocol.Bncs), ("Send unknown BNCS packet", CapturedProtocol.Bncs), ("Send MCP packet", CapturedProtocol.Mcp), ("Send unknown MCP packet", CapturedProtocol.Mcp) })
            {
                if (message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var bytes = After(message, " with data ") is { } data ? PrintedBytes(data) : null;
                    if (bytes == null)
                    {
                        _result.UnusableSentLines++;
                        return false;
                    }

                    CloseWindow();
                    Realm(utc, protocol, false, bytes);
                    return true;
                }
            }

            return false;
        }

        private void Rebuilt(string what) => _result.Rebuilt[what] = _result.Rebuilt.GetValueOrDefault(what) + 1;

        private void Sent(DateTime utc, CapturedProtocol protocol, byte[] bytes)
        {
            CloseWindow();
            Enqueue(new Event { Utc = utc, Protocol = protocol, FromServer = false, Bytes = bytes });
        }

        private void Realm(DateTime utc, CapturedProtocol protocol, bool fromServer, byte[] bytes)
            => Enqueue(new Event { Utc = utc, Protocol = protocol, FromServer = fromServer, Bytes = bytes });

        private void Enqueue(Event e)
        {
            e.Number = _number;
            _queue.AddLast(e);
        }

        private void CloseWindow()
        {
            if (_openSegment != null)
            {
                _openSegment.WindowClosed = true;
                if (_openSegment.Instance < 0)
                {
                    _unresolved.Add(_openSegment);
                }

                _openSegment = null;
            }
        }

        /// <summary>
        /// An "Instance N" line right after a raw segment names its connection. A segment no frame completed in
        /// gets no such line; its bytes reappear inside the frame printed once the frame is whole.
        /// </summary>
        private void Hint(int instance, string hex)
        {
            if (_openSegment is { Instance: < 0 } open)
            {
                open.Instance = instance;
                _result.SegmentsNamed++;
            }

            for (var i = _unresolved.Count - 1; i >= 0; i--)
            {
                var waiting = _unresolved[i];
                if (waiting.Instance >= 0)
                {
                    _unresolved.RemoveAt(i);
                }
                else if (waiting.Hex.Length >= ContainedMinimumBytes * 2 && Contains(hex, waiting.Hex))
                {
                    waiting.Instance = instance;
                    _result.SegmentsFoundInFrame++;
                    _unresolved.RemoveAt(i);
                }
            }
        }

        private static bool Contains(string frame, string segment)
        {
            for (var at = frame.IndexOf(segment, StringComparison.Ordinal); at >= 0; at = frame.IndexOf(segment, at + 1, StringComparison.Ordinal))
            {
                if (at % 2 == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void Drain(bool final)
        {
            while (_queue.First is { } node)
            {
                var head = node.Value;
                if (head.Protocol == CapturedProtocol.D2gs && head.FromServer && head.Instance < 0)
                {
                    if (!final && (!head.WindowClosed || _number - head.Number < GiveUpLines))
                    {
                        return;
                    }

                    head.Instance = Guess(head.Utc);
                    _result.SegmentsGuessed++;
                }

                DecodeAhead();
                if (head.Protocol == CapturedProtocol.D2gs && !head.FromServer && !final
                    && (_lastUtc < head.Utc + ReplyWindow || _decodedUpTo?.Next is { } undecoded && undecoded.Value.Utc <= head.Utc + ReplyWindow))
                {
                    return;
                }

                if (_decodedUpTo == node)
                {
                    _decodedUpTo = null;
                }

                var next = node.Next;
                _queue.RemoveFirst();
                Process(head, next);
            }
        }

        /// <summary>Decodes the game server segments whose connection is known, in queue order, stopping at the first one still unknown.</summary>
        private void DecodeAhead()
        {
            for (var node = _decodedUpTo == null ? _queue.First : _decodedUpTo.Next; node != null; node = node.Next)
            {
                var e = node.Value;
                if (e.Protocol == CapturedProtocol.D2gs && e.FromServer)
                {
                    if (e.Instance < 0)
                    {
                        return;
                    }

                    if (!_ahead.TryGetValue(e.Instance, out var decoder))
                    {
                        decoder = new D2gsServerStream(e.Greeting);
                        _ahead[e.Instance] = decoder;
                    }

                    e.Packets = [];
                    decoder.Append(e.Bytes, e.Packets);
                }

                _decodedUpTo = node;
            }
        }

        /// <summary>What the clients received in the reply window after a send, in capture order.</summary>
        private List<(CapturedClient Client, byte[] Packet)> Following(Event sent, LinkedListNode<Event> next)
        {
            var following = new List<(CapturedClient, byte[])>();
            for (var node = next; node != null && node.Value.Utc <= sent.Utc + ReplyWindow; node = node.Next)
            {
                var e = node.Value;
                if (e.Packets != null && _clients.TryGetValue(e.Instance, out var client))
                {
                    following.AddRange(e.Packets.Select(p => (client, p)));
                }
            }

            return following;
        }

        /// <summary>The connection waiting longest for the rest of a frame, else the one fed last.</summary>
        private int Guess(DateTime utc)
        {
            var waiting = _clients.Values.Where(c => _session.PendingServerBytes(c.Id) > 0).OrderByDescending(c => c == _lastFed).FirstOrDefault();
            return (waiting ?? _lastFed ?? _clients.Values.LastOrDefault())?.Instance ?? 0;
        }

        private void Process(Event e, LinkedListNode<Event> next)
        {
            CloseEnded(e.Utc);
            switch (e.Protocol)
            {
                case CapturedProtocol.D2gs when e.FromServer:
                    var client = Client(e.Instance, e.Utc, fromStart: e.Greeting);
                    _lastFed = client;
                    _session.Data(client.Id, CapturedProtocol.D2gs, true, e.Bytes, e.Utc);
                    break;
                case CapturedProtocol.D2gs:
                    var open = _clients.Values.Where(c => c.OpenedUtc <= e.Utc).ToList();
                    var chosen = _attribution.Choose(open, e.Bytes, e.Utc, Following(e, next));
                    if (chosen == null)
                    {
                        break;
                    }

                    _session.Data(chosen.Id, CapturedProtocol.D2gs, false, e.Bytes, e.Utc, withoutEvidence: _attribution.LastWithoutEvidence);
                    foreach (var packet in SentAttribution.Split(e.Bytes))
                    {
                        chosen.Live.Sent(packet, e.Utc);
                    }

                    break;
                default:
                    if (!_realmOpen)
                    {
                        _realmOpen = true;
                        _session.Open(Bncs, CapturedProtocol.Bncs, e.Utc, fromStart: false, label: "capture-bncs");
                        _session.Open(Mcp, CapturedProtocol.Mcp, e.Utc, fromStart: false, label: "capture-mcp");
                    }

                    _session.Data(e.Protocol == CapturedProtocol.Bncs ? Bncs : Mcp, e.Protocol, e.FromServer, e.Bytes, e.Utc);
                    break;
            }
        }

        private CapturedClient Client(int instance, DateTime utc, bool fromStart)
        {
            if (_clients.TryGetValue(instance, out var client))
            {
                return client;
            }

            var last = _lastSeen.TryGetValue(instance, out var seen) ? seen.Utc : utc;
            var id = new CaptureConnection("text capture", $"d2gs instance {instance}");
            client = new CapturedClient(instance, id, utc, last);
            _clients[instance] = client;
            _byConnection[id] = client;
            _session.Open(id, CapturedProtocol.D2gs, utc, fromStart);
            _result.GameConnections++;
            return client;
        }

        private void CloseEnded(DateTime utc)
        {
            foreach (var client in _clients.Values.Where(c => c.LastUtc + CloseGrace < utc).ToList())
            {
                CloseClient(client, client.LastUtc + CloseGrace);
            }
        }

        private void CloseClient(CapturedClient client, DateTime utc)
        {
            _session.Close(client.Id, utc);
            _clients.Remove(client.Instance);
            _byConnection.Remove(client.Id);
            client.Live.Dispose();
        }

        public TextCaptureImportResult Finish()
        {
            if (_interact is { } interact)
            {
                _interact = null;
                Rebuilt("EntityInteract");
                Sent(interact.Utc, CapturedProtocol.D2gs, [0x13, .. BitConverter.GetBytes(interact.Type), .. BitConverter.GetBytes(interact.Id)]);
            }

            CloseWindow();
            Drain(final: true);
            foreach (var client in _clients.Values.ToList())
            {
                CloseClient(client, client.LastUtc);
            }

            _session.Dispose();
            _result.SentAttribution = _attribution.Methods;
            _result.SentInconsistent = _attribution.Inconsistent;
            _result.Stats = _session.Stats;
            return _result;
        }

        public void Dispose()
        {
            foreach (var client in _clients.Values)
            {
                client.Live.Dispose();
            }

            _session.Dispose();
        }

        private static string After(string message, string prefix)
            => message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : AfterInside(message, prefix);

        private static string AfterInside(string message, string marker)
        {
            if (!marker.StartsWith(' '))
            {
                return null;
            }

            var at = message.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? null : message[(at + marker.Length)..];
        }

        private static string AfterAny(string message, params string[] markers)
        {
            foreach (var marker in markers)
            {
                if (AfterInside(message, marker) is { } rest)
                {
                    return rest;
                }
            }

            return null;
        }
    }
}
