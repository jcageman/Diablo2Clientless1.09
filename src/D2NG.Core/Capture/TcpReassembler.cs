using System;
using System.Collections.Generic;

namespace D2NG.Core.Capture;

/// <summary>
/// One direction of a TCP connection put back in order by sequence number: retransmitted bytes are
/// dropped, a segment that arrives early waits for the one before it, and when too much waits behind a
/// hole the hole is given up on and reported as lost. Every byte keeps the capture time of the segment
/// that first carried it.
/// </summary>
public sealed class TcpReassembler
{
    private readonly int _maxHeldSegments;
    private readonly SortedList<uint, (byte[] Data, DateTime At)> _early = new(new SequenceOrder());
    private uint _next;
    private bool _started;

    public TcpReassembler(int maxHeldSegments = 64)
    {
        _maxHeldSegments = maxHeldSegments;
    }

    /// <summary>Whether the stream is known from its first byte: the SYN was seen.</summary>
    public bool FromStart { get; private set; }

    public long RetransmittedBytes { get; private set; }

    public int Holes { get; private set; }

    /// <summary>Bytes the last hole given up was missing.</summary>
    public long LastHoleBytes { get; private set; }

    /// <summary>The capture time of the earliest byte waiting behind a hole; null when nothing waits.</summary>
    public DateTime? EarliestHeld
    {
        get
        {
            DateTime? earliest = null;
            foreach (var (_, at) in _early.Values)
            {
                if (earliest == null || at < earliest)
                {
                    earliest = at;
                }
            }

            return earliest;
        }
    }

    public void Accept(uint sequence, bool syn, ReadOnlySpan<byte> payload, Action<byte[]> deliver, Action lost)
        => Accept(sequence, syn, payload, default, (bytes, _) => deliver(bytes), _ => lost());

    /// <summary>
    /// Takes one segment captured at <paramref name="at"/>. <paramref name="deliver"/> is called with each run of
    /// bytes now in order and the time to stamp it with, <paramref name="lost"/> before bytes that follow a hole.
    /// Bytes that waited behind a hole the network filled are stamped with the segment that filled it, the moment
    /// the receiver's TCP could hand them on; bytes released by giving a hole up keep their own time, since the
    /// receiver had them all along and only the capture missed a segment.
    /// </summary>
    public void Accept(uint sequence, bool syn, ReadOnlySpan<byte> payload, DateTime at, Action<byte[], DateTime> deliver, Action<DateTime> lost)
    {
        if (syn)
        {
            _next = sequence + 1;
            _started = true;
            FromStart = true;
            _early.Clear();
            sequence++;
        }

        if (payload.Length == 0)
        {
            return;
        }

        if (!_started)
        {
            _next = sequence;
            _started = true;
        }

        var offset = (int)(_next - sequence);
        if (offset > 0)
        {
            if (offset >= payload.Length)
            {
                RetransmittedBytes += payload.Length;
                return;
            }

            RetransmittedBytes += offset;
            payload = payload[offset..];
            sequence = _next;
        }

        if (sequence != _next)
        {
            Hold(sequence, payload, at);
            if (_early.Count > _maxHeldSegments)
            {
                GiveUpHole(deliver, lost);
            }

            return;
        }

        deliver(payload.ToArray(), at);
        _next = sequence + (uint)payload.Length;
        DrainEarly(deliver, at);
    }

    /// <summary>
    /// Gives up every hole whose first held segment was captured more than <paramref name="wait"/> before
    /// <paramref name="newest"/>, the newest segment of the connection in either direction. A hole in what the client
    /// sent never fills when the capture alone missed the segment: the server acknowledged it, so the client never
    /// sends it again, and an idle client sends nothing that would push the hole out by count.
    /// </summary>
    public void GiveUpOlderThan(DateTime newest, TimeSpan wait, Action<byte[], DateTime> deliver, Action<DateTime> lost)
    {
        while (_early.Count > 0 && newest - _early.Values[0].At > wait)
        {
            GiveUpHole(deliver, lost);
        }
    }

    /// <summary>The connection ended: whatever still waits behind a hole is delivered after reporting it lost.</summary>
    public void Finish(Action<byte[]> deliver, Action lost) => Finish((bytes, _) => deliver(bytes), _ => lost());

    public void Finish(Action<byte[], DateTime> deliver, Action<DateTime> lost)
    {
        while (_early.Count > 0)
        {
            GiveUpHole(deliver, lost);
        }
    }

    private void GiveUpHole(Action<byte[], DateTime> deliver, Action<DateTime> lost)
    {
        Holes++;
        LastHoleBytes = _early.Keys[0] - _next;
        lost(_early.Values[0].At);
        _next = _early.Keys[0];
        DrainEarly(deliver, null);
    }

    /// <summary>
    /// Keeps the bytes of an early segment that no held segment carries yet, as pieces of their own: a longer
    /// retransmit at a held sequence adds its tail, and one that overlaps held pieces fills only what is between them.
    /// </summary>
    private void Hold(uint sequence, ReadOnlySpan<byte> payload, DateTime at)
    {
        var start = sequence;
        var end = sequence + (uint)payload.Length;
        var cursor = start;
        var pieces = new List<(uint Sequence, int From, int Length)>();
        foreach (var (heldSequence, (data, _)) in _early)
        {
            var heldEnd = heldSequence + (uint)data.Length;
            if (Before(heldEnd, cursor) || heldEnd == cursor)
            {
                continue;
            }

            if (!Before(heldSequence, end))
            {
                break;
            }

            if (Before(cursor, heldSequence))
            {
                pieces.Add((cursor, (int)(cursor - start), (int)(heldSequence - cursor)));
            }

            cursor = heldEnd;
            if (!Before(cursor, end))
            {
                break;
            }
        }

        if (Before(cursor, end))
        {
            pieces.Add((cursor, (int)(cursor - start), (int)(end - cursor)));
        }

        foreach (var (pieceSequence, from, length) in pieces)
        {
            _early.Add(pieceSequence, (payload.Slice(from, length).ToArray(), at));
        }
    }

    /// <param name="releasedAt">The time of the segment that filled the hole, or null when the hole was given up.</param>
    private void DrainEarly(Action<byte[], DateTime> deliver, DateTime? releasedAt)
    {
        while (_early.Count > 0)
        {
            var sequence = _early.Keys[0];
            var offset = (int)(_next - sequence);
            if (offset < 0)
            {
                return;
            }

            var (payload, at) = _early.Values[0];
            _early.RemoveAt(0);
            if (offset >= payload.Length)
            {
                RetransmittedBytes += payload.Length;
                continue;
            }

            RetransmittedBytes += offset;
            deliver(payload[offset..], releasedAt is { } released && released > at ? released : at);
            _next = sequence + (uint)payload.Length;
        }
    }

    private static bool Before(uint x, uint y) => (int)(x - y) < 0;

    /// <summary>Sequence numbers wrap: order by the signed distance between them.</summary>
    private sealed class SequenceOrder : IComparer<uint>
    {
        public int Compare(uint x, uint y) => ((int)(x - y)).CompareTo(0);
    }
}
