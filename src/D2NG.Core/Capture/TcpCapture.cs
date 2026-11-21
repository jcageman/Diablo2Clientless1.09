using System;
using System.Collections.Generic;

namespace D2NG.Core.Capture;

/// <summary>
/// Captured TCP segments of the game's three protocols into a <see cref="CaptureSession"/>: each direction put back
/// in order with its bytes' own capture times, the session told while a direction holds bytes behind a hole, and
/// connections opened on a SYN and closed on a reset or both FINs. It runs inside the capture callback, so nothing
/// it does throws there: a failure is counted and reported, and the next segment is taken as usual.
/// </summary>
public sealed class TcpCapture : IDisposable
{
    private const int FailuresReported = 5;

    /// <summary>How much capture time a connection may run past a segment held behind a hole before the hole is given up.</summary>
    public static readonly TimeSpan DefaultHoleWait = TimeSpan.FromSeconds(3);

    private readonly CaptureSession _session;
    private readonly TimeSpan _holeWait;
    private readonly Dictionary<CaptureConnection, Streams> _streams = [];

    public TcpCapture(CaptureSession session, TimeSpan? holeWait = null)
    {
        _session = session;
        _holeWait = holeWait ?? DefaultHoleWait;
    }

    public CaptureSession Session => _session;

    /// <summary>Segments whose handling threw; the capture went on after each.</summary>
    public int Failures { get; private set; }

    public void Accept(CaptureConnection id, CapturedProtocol protocol, bool fromServer, uint sequence, bool syn, bool ack, bool fin, bool reset, ReadOnlySpan<byte> payload, DateTime atUtc)
    {
        try
        {
            AcceptSegment(id, protocol, fromServer, sequence, syn, ack, fin, reset, payload, atUtc);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failures++;
            if (Failures <= FailuresReported)
            {
                _session.Report?.Invoke($"{id}: a segment could not be traced ({e.GetType().Name}: {e.Message}); the capture goes on{(Failures == FailuresReported ? ", later failures are only counted" : "")}");
            }
        }
    }

    private void AcceptSegment(CaptureConnection id, CapturedProtocol protocol, bool fromServer, uint sequence, bool syn, bool ack, bool fin, bool reset, ReadOnlySpan<byte> payload, DateTime atUtc)
    {
        if (syn && !ack && !fromServer)
        {
            Finish(id, atUtc);
            _session.Open(id, protocol, atUtc, fromStart: true);
            _streams[id] = new Streams(protocol);
        }

        if (!_streams.TryGetValue(id, out var streams))
        {
            if (reset || payload.Length == 0)
            {
                return;
            }

            streams = new Streams(protocol);
            _streams[id] = streams;
        }

        (fromServer ? streams.ToClient : streams.ToServer).Accept(
            sequence,
            syn,
            payload,
            atUtc,
            (bytes, at) => _session.Data(id, protocol, fromServer, bytes, at),
            at => _session.Lost(id, fromServer, at, (fromServer ? streams.ToClient : streams.ToServer).LastHoleBytes));
        streams.ToServer.GiveUpOlderThan(atUtc, _holeWait, (bytes, at) => _session.Data(id, protocol, false, bytes, at), at => _session.Lost(id, false, at, streams.ToServer.LastHoleBytes));
        streams.ToClient.GiveUpOlderThan(atUtc, _holeWait, (bytes, at) => _session.Data(id, protocol, true, bytes, at), at => _session.Lost(id, true, at, streams.ToClient.LastHoleBytes));
        _session.HoldBefore(id, Earliest(streams.ToServer.EarliestHeld, streams.ToClient.EarliestHeld));

        if (reset)
        {
            Finish(id, atUtc);
            return;
        }

        if (fin)
        {
            if (fromServer)
            {
                streams.ServerFin = true;
            }
            else
            {
                streams.ClientFin = true;
            }

            if (streams.ClientFin && streams.ServerFin)
            {
                Finish(id, atUtc);
            }
        }
    }

    public void Dispose()
    {
        foreach (var id in new List<CaptureConnection>(_streams.Keys))
        {
            try
            {
                Finish(id, DateTime.UtcNow);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Failures++;
            }
        }

        _session.Dispose();
    }

    private void Finish(CaptureConnection id, DateTime atUtc)
    {
        if (!_streams.Remove(id, out var streams))
        {
            return;
        }

        streams.ToServer.Finish((bytes, at) => _session.Data(id, streams.Protocol, false, bytes, at), at => _session.Lost(id, false, at, streams.ToServer.LastHoleBytes));
        streams.ToClient.Finish((bytes, at) => _session.Data(id, streams.Protocol, true, bytes, at), at => _session.Lost(id, true, at, streams.ToClient.LastHoleBytes));
        _session.Close(id, atUtc);
    }

    private static DateTime? Earliest(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : a < b ? a : b;

    private sealed class Streams(CapturedProtocol protocol)
    {
        public CapturedProtocol Protocol { get; } = protocol;
        public TcpReassembler ToServer { get; } = new();
        public TcpReassembler ToClient { get; } = new();
        public bool ClientFin { get; set; }
        public bool ServerFin { get; set; }
    }
}
