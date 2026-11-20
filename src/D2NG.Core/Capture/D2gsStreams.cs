using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Helpers;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;

namespace D2NG.Core.Capture;

/// <summary>What one gap in a server stream cost: frames known lost and compressed bytes skipped, a missed segment's included.</summary>
/// <param name="PacketIndex">Where in the packets of the call the packets after the gap start.</param>
public readonly record struct StreamGap(int PacketIndex, int Frames, long Bytes);

/// <summary>
/// The server side of one game server connection as a byte stream: the A7 01 greeting, then compressed
/// frames (a one or two byte length, Huffman coded content holding whole packets). Yields the packets the
/// bot's connection would hand its handlers, one by one.
/// </summary>
public sealed class D2gsServerStream
{
    private const int UnsyncedMaxHeld = 64 * 1024;
    private const int FramesToBelieve = 3;
    private const int CandidatesBetweenClockChecks = 8;

    /// <summary>Candidates every call checks before the time budget counts, so a slow first call (code still being compiled) still makes progress.</summary>
    private const int CandidatesBeforeBudget = 64;

    /// <summary>
    /// Ids the server sends only while a client joins: in 189 bot and 24 real-client game traces of November 2026 none
    /// came later than a second after the connection's first packet. A frame holding one cannot be found mid-game,
    /// so a resync that decodes one has read garbage. Experience as a dword (0x1C) also came only early there, but
    /// a high level character earns it mid-game, so it is not one of them.
    /// </summary>
    private static readonly HashSet<byte> JoinOnly = [0x01, 0x02, 0x0B, 0x5E, 0x5F, 0x7B, 0x7E, 0x94];

    private readonly List<byte[]> _scratch = [];
    private readonly List<int> _unfinished = [];
    private readonly List<(int End, DateTime At)> _chunks = [];
    private int _scanFrom;

    /// <summary>Where the current search began - the bytes after a hole, the end of a bad frame - while it is still buffered, else -1, as for a capture that began mid-stream.</summary>
    private int _searchOrigin;
    private byte[] _buffer = new byte[8192];
    private int _length;
    private bool _greetingChecked;
    private bool _synced;
    private int _gapFrames;
    private long _gapBytes;
    private bool _gapOpen;

    /// <param name="fromStart">Whether the capture saw the connection open, so the stream starts at its first byte.</param>
    public D2gsServerStream(bool fromStart)
    {
        _synced = fromStart;
        _greetingChecked = !fromStart;

        // A capture that began mid-stream starts at an arbitrary byte, so a frame found right there is no more trusted
        // than one found further on; after a missed segment the bytes start at a segment boundary, which mostly is a frame's.
        _searchOrigin = -1;
    }

    /// <summary>Frames whose content did not split into whole packets; the packets before the bad one are kept, as the bot's connection keeps them.</summary>
    public int BadFrames { get; private set; }

    /// <summary>Times the start of a frame had to be searched for: a capture that began mid-stream, or after lost bytes.</summary>
    public int Resyncs { get; private set; }

    /// <summary>Frames found by a search away from where it began, whose packets were dropped as possibly garbage.</summary>
    public int DroppedFirstFrames { get; private set; }

    /// <summary>Bytes thrown away while searching for a frame start.</summary>
    public long SkippedBytes { get; private set; }

    /// <summary>The first byte of the packet each bad frame stopped at, by count.</summary>
    public Dictionary<byte, int> UnsizedPackets { get; } = [];

    public int Pending => _length;

    /// <summary>A gap the stream has not found its way past yet: what it has cost so far; null when there is none.</summary>
    public StreamGap? OpenGap => _gapOpen ? new StreamGap(0, Math.Max(1, _gapFrames), _gapBytes) : null;

    public bool Synced => _synced;

    /// <summary>
    /// How long one <see cref="Append"/> may search for a frame start before it stops and resumes with the next
    /// bytes: the search runs on the capture thread, and garbage costs tens of microseconds an offset.
    /// </summary>
    public TimeSpan ResyncBudget { get; init; } = TimeSpan.FromMilliseconds(2);

    private bool _unbounded;

    /// <summary>
    /// The connection closed, so what is buffered is all there will be: a search for a frame start that the time budget
    /// cut short runs on to the end of it, and the packets it finds are added as <see cref="Append(ReadOnlySpan{byte}, List{byte[]}, List{StreamGap}, DateTime, List{DateTime})"/> adds them.
    /// </summary>
    public int Finish(List<byte[]> packets, List<StreamGap> gapsBefore, DateTime atUtc, List<DateTime> times)
    {
        _unbounded = true;
        try
        {
            return Append(ReadOnlySpan<byte>.Empty, packets, gapsBefore, atUtc, times);
        }
        finally
        {
            _unbounded = false;
        }
    }

    /// <summary>Appends bytes and adds every packet now complete to <paramref name="packets"/>; returns how many packets went missing in bad frames (counted once per bad frame).</summary>
    public int Append(ReadOnlySpan<byte> bytes, List<byte[]> packets) => Append(bytes, packets, null, default, null);

    /// <param name="gapsBefore">Gets, for each gap the stream found its way past - a bad frame, or missed bytes (<see cref="Lost"/>) - where the packets after it start and what it cost.</param>
    /// <param name="atUtc">When the bytes were captured.</param>
    /// <param name="times">Gets, for each packet, the capture time of the bytes that completed its frame: a frame that spans segments is stamped with its last.</param>
    public int Append(ReadOnlySpan<byte> bytes, List<byte[]> packets, List<StreamGap> gapsBefore, DateTime atUtc, List<DateTime> times)
    {
        if (_length + bytes.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + bytes.Length));
        }

        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        _chunks.Add((_length, atUtc));
        var lost = 0;
        var offset = 0;
        if (!_greetingChecked)
        {
            if (_length < 2)
            {
                return 0;
            }

            _greetingChecked = true;
            if (_buffer[0] == 0xA7 && _buffer[1] == 0x01)
            {
                offset = 2;
            }
        }

        var deadline = _unbounded ? long.MaxValue : Stopwatch.GetTimestamp() + (long)(ResyncBudget.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (!_synced)
            {
                var start = FindFrameStart(offset, deadline, out var resumeAt);
                if (start < 0)
                {
                    Skip(resumeAt - offset);
                    offset = resumeAt;
                    if (_length - offset > UnsyncedMaxHeld)
                    {
                        Skip(_length - offset - UnsyncedMaxHeld);
                        offset = _length - UnsyncedMaxHeld;
                    }

                    break;
                }

                Skip(start - offset);
                offset = start;
                if (start != _searchOrigin && TryFrame(_buffer.AsSpan(start, _length - start), out var firstHeader, out var firstSize))
                {
                    // Found away from where the search began, the first frame may be the tail of a real frame that
                    // happens to read as one ending exactly where the next real frame starts: 6 of 1,000 resyncs of
                    // real traffic believed such a frame even with three in a row required. Its packets are dropped.
                    Skip(firstHeader + firstSize);
                    offset += firstHeader + firstSize;
                    DroppedFirstFrames++;
                    _gapFrames++;
                }

                RestartSearch(0);
                _synced = true;
                Resyncs++;
                if (_gapOpen)
                {
                    gapsBefore?.Add(new StreamGap(packets.Count, Math.Max(1, _gapFrames), _gapBytes));
                    _gapOpen = false;
                }
            }

            if (!TryFrame(_buffer.AsSpan(offset, _length - offset), out var header, out var size))
            {
                break;
            }

            var content = _buffer.AsSpan(offset + header, size).ToArray();
            offset += header + size;
            if (size == 0)
            {
                continue;
            }

            var before = packets.Count;
            var split = Split(content, packets, out var stoppedAt);
            if (times != null)
            {
                var completedAt = CapturedAt(offset);
                for (var i = before; i < packets.Count; i++)
                {
                    times.Add(completedAt);
                }
            }

            if (!split)
            {
                // A frame that does not split is either one with a packet the size table lacks, whose end is still
                // right, or a header read out of step; the search that follows finds the next frame either way.
                BadFrames++;
                lost++;
                StartGap(1);
                UnsizedPackets[stoppedAt] = UnsizedPackets.GetValueOrDefault(stoppedAt) + 1;
                _synced = false;
                RestartSearch(offset);
            }
        }

        Compact(offset);
        _chunks.RemoveAll(chunk => chunk.End <= offset);
        for (var i = 0; i < _chunks.Count; i++)
        {
            _chunks[i] = (_chunks[i].End - offset, _chunks[i].At);
        }

        if (!_synced)
        {
            ShiftSearch(offset);
        }

        return lost;
    }

    private void StartGap(int frames)
    {
        _gapOpen = true;
        _gapFrames = frames;
        _gapBytes = 0;
    }

    private void Skip(long bytes)
    {
        SkippedBytes += bytes;
        if (_gapOpen)
        {
            _gapBytes += bytes;
        }
    }

    /// <summary>When the buffered byte before <paramref name="end"/> was captured.</summary>
    private DateTime CapturedAt(int end)
    {
        foreach (var (chunkEnd, at) in _chunks)
        {
            if (chunkEnd >= end)
            {
                return at;
            }
        }

        return _chunks.Count > 0 ? _chunks[^1].At : default;
    }

    /// <summary>
    /// Bytes went missing (<paramref name="missedBytes"/> of them where known): whatever is buffered belongs to a frame
    /// that can no longer be completed. The gap is reported with the packets after it, once the stream finds a frame.
    /// </summary>
    public void Lost(long missedBytes = 0)
    {
        if (!_gapOpen)
        {
            StartGap(0);
        }

        _gapBytes += missedBytes;
        if (_length > 0)
        {
            _gapFrames++;
        }

        Skip(_length);
        _length = 0;
        _chunks.Clear();
        _synced = false;
        _greetingChecked = true;
        RestartSearch(0);
    }

    /// <summary>The frame at the start of <paramref name="data"/>: header length and content length, false while incomplete.</summary>
    public static bool TryFrame(ReadOnlySpan<byte> data, out int header, out int size)
    {
        header = 0;
        size = 0;
        if (data.Length < 1)
        {
            return false;
        }

        if (data[0] >= 0xF0)
        {
            if (data.Length < 2)
            {
                return false;
            }

            header = 2;
            size = ((data[0] & 0xF) << 8) + data[1] - 2;
        }
        else
        {
            header = 1;
            size = data[0] - 1;
        }

        if (size < 0)
        {
            size = 0;
            return true;
        }

        return data.Length >= header + size;
    }

    /// <summary>Decompresses a frame's content and splits it the way the bot's connection does; false when a packet could not be sized or the sizes overran.</summary>
    public static bool Split(byte[] content, List<byte[]> packets, out byte stoppedAt)
    {
        stoppedAt = 0;
        byte[] output;
        try
        {
            Huffman.Decompress(content, out output);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }

        var index = 0;
        while (index < output.Length)
        {
            int size;
            try
            {
                size = GameServerConnection.GetPacketSize(new ArraySegment<byte>(output, index, output.Length - index));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                stoppedAt = output[index];
                return false;
            }

            if (size <= 0 || index + size > output.Length)
            {
                stoppedAt = output[index];
                return false;
            }

            packets.Add(output.AsSpan(index, size).ToArray());
            index += size;
        }

        return output.Length > 0;
    }

    /// <summary>
    /// A frame start is only believed when the frame and the two after it decode into whole packets of known ids
    /// that can come mid-game: compressed bytes read from a random offset now and then decode into a frame that
    /// splits, and two in a row did 7 times in 1,000 resyncs of real traffic. A candidate whose frames have not
    /// all arrived does not stop the search - a later one may already show three - but the next call resumes at
    /// the first of them. Returns -1 with <paramref name="resumeAt"/> where the next call has to go on, every
    /// offset before it ruled out: that first unfinished candidate, or where the time budget ran out.
    /// </summary>
    private int FindFrameStart(int from, long deadline, out int resumeAt)
    {
        _scanFrom = Math.Max(_scanFrom, from);
        for (var i = 0; i < _unfinished.Count;)
        {
            switch (Believe(_unfinished[i]))
            {
                case Candidate.Valid:
                    resumeAt = _unfinished[i];
                    return resumeAt;
                case Candidate.Invalid:
                    _unfinished.RemoveAt(i);
                    break;
                default:
                    i++;
                    break;
            }
        }

        var checkedThisCall = 0;
        for (; _scanFrom < _length; _scanFrom++)
        {
            if (++checkedThisCall > CandidatesBeforeBudget && checkedThisCall % CandidatesBetweenClockChecks == 0 && Stopwatch.GetTimestamp() > deadline)
            {
                break;
            }

            var check = Believe(_scanFrom);
            if (check == Candidate.Valid)
            {
                resumeAt = _scanFrom;
                return resumeAt;
            }

            if (check == Candidate.Incomplete)
            {
                _unfinished.Add(_scanFrom);
            }
        }

        resumeAt = _unfinished.Count > 0 ? Math.Min(_unfinished[0], _scanFrom) : _scanFrom;
        return -1;
    }

    /// <summary>Whether the frame at <paramref name="start"/> and the two after it all decode, or one of them has not fully arrived.</summary>
    private Candidate Believe(int start)
    {
        var candidate = start;
        for (var believed = 0; believed < FramesToBelieve; believed++)
        {
            var check = Check(candidate, out var next);
            if (check != Candidate.Valid)
            {
                return check;
            }

            candidate = next;
        }

        return Candidate.Valid;
    }

    /// <summary>Drops the first <paramref name="consumed"/> buffered bytes, and every search position before them.</summary>
    private void ShiftSearch(int consumed)
    {
        _scanFrom = Math.Max(0, _scanFrom - consumed);
        _searchOrigin = _searchOrigin >= consumed ? _searchOrigin - consumed : -1;
        _unfinished.RemoveAll(u => u < consumed);
        for (var i = 0; i < _unfinished.Count; i++)
        {
            _unfinished[i] -= consumed;
        }
    }

    private void RestartSearch(int at)
    {
        _searchOrigin = at;
        _scanFrom = at;
        _unfinished.Clear();
    }

    private enum Candidate
    {
        Invalid,
        Incomplete,
        Valid,
    }

    private Candidate Check(int start, out int next)
    {
        next = start;
        var rest = _buffer.AsSpan(start, _length - start);
        if (!TryFrame(rest, out var header, out var size))
        {
            return Candidate.Incomplete;
        }

        if (size == 0)
        {
            return Candidate.Invalid;
        }

        next = start + header + size;
        _scratch.Clear();
        // One byte packets (0x00-0x06: loading, load done, exit) are left out: any run of those bytes splits,
        // so garbage made of them looks like a frame, and a real one only comes while joining or leaving.
        return Split(rest.Slice(header, size).ToArray(), _scratch, out _)
            && _scratch.TrueForAll(p => p.Length > 1 && !JoinOnly.Contains(p[0]) && Enum.IsDefined(typeof(D2GS.Packet.InComingPacket), p[0]))
            ? Candidate.Valid
            : Candidate.Invalid;
    }

    private void Compact(int consumed)
    {
        if (consumed <= 0)
        {
            return;
        }

        Array.Copy(_buffer, consumed, _buffer, 0, _length - consumed);
        _length -= consumed;
    }
}

/// <summary>How the length of a sent packet the size table does not know was decided.</summary>
public enum UnsizedCut : byte
{
    /// <summary>It took the rest of the segment: no later offset began a run of known packets that ends with the segment.</summary>
    RestOfSegment,

    /// <summary>It ends where the rest of the segment parses as known, plausible packets.</summary>
    BeforeKnownPackets,
}

/// <summary>
/// The client side of a game server connection: uncompressed packets back to back. Sizes are the 1.09
/// client's, checked against every packet the bot sends. A packet whose size is known always takes exactly
/// that size. One whose size is not known - an id missing from the table, or a chat message whose strings
/// do not end - ends where the rest of the segment parses as known packets, else takes the rest of the
/// segment, which is right whenever the client sent it in a segment of its own - the common case.
/// </summary>
public sealed class D2gsClientStream
{
    private byte[] _pending = [];

    /// <summary>Packets whose length had to be decided because their size is not known.</summary>
    public Dictionary<byte, int> Unsized { get; } = [];

    /// <summary>The same packets by id and how their length was decided.</summary>
    public Dictionary<(byte Id, UnsizedCut How), int> UnsizedHow { get; } = [];

    public int Pending => _pending.Length;

    public void Append(ReadOnlySpan<byte> bytes, List<byte[]> packets)
    {
        var data = _pending.Length == 0 ? bytes.ToArray() : [.. _pending, .. bytes];
        _pending = [];
        var index = 0;
        while (index < data.Length)
        {
            var size = Size(data.AsSpan(index));
            if (size == Incomplete)
            {
                _pending = data[index..];
                return;
            }

            if (size == Unknown)
            {
                size = UnknownLength(data, index, out var how);
                Unsized[data[index]] = Unsized.GetValueOrDefault(data[index]) + 1;
                UnsizedHow[(data[index], how)] = UnsizedHow.GetValueOrDefault((data[index], how)) + 1;
            }
            else if (index + size > data.Length)
            {
                _pending = data[index..];
                return;
            }

            packets.Add(data.AsSpan(index, size).ToArray());
            index += size;
        }
    }

    /// <summary>
    /// The first later offset from which the rest parses as known, plausible packets ending exactly with the data
    /// is where an unknown packet ends; packet bodies are mostly small numbers, which read as sizable ids, so a
    /// split is only believed when what follows also makes sense (no zero coordinates, a real unit type).
    /// </summary>
    private static int UnknownLength(byte[] data, int index, out UnsizedCut how)
    {
        for (var end = index + 1; end < data.Length; end++)
        {
            if (ParsesToEnd(data, end))
            {
                how = UnsizedCut.BeforeKnownPackets;
                return end - index;
            }
        }

        how = UnsizedCut.RestOfSegment;
        return data.Length - index;
    }

    private static bool ParsesToEnd(byte[] data, int from)
    {
        while (from < data.Length)
        {
            var size = Size(data.AsSpan(from));
            if (size <= 0 || from + size > data.Length || !Plausible(data.AsSpan(from, size)))
            {
                return false;
            }

            from += size;
        }

        return true;
    }

    private static bool Plausible(ReadOnlySpan<byte> packet)
    {
        switch (packet[0])
        {
            case 0x01 or 0x03 or 0x05 or 0x08 or 0x0C or 0x0F or 0x5F:
                return BinaryPrimitives.ReadUInt16LittleEndian(packet[1..]) != 0 && BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]) != 0;
            case 0x02 or 0x04 or 0x06 or 0x07 or 0x09 or 0x0A or 0x0D or 0x0E or 0x10 or 0x11 or 0x13 or 0x2F or 0x30 or 0x4B:
                return BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]) <= 5;
            case 0x14 or 0x15:
                return true;
            default:
                return packet.Length == 1 || packet[1..].IndexOfAnyExcept((byte)0) >= 0;
        }
    }

    /// <summary>What is held back waiting for the rest of a packet goes out as it is: the connection ended or bytes were lost.</summary>
    public void Flush(List<byte[]> packets)
    {
        if (_pending.Length > 0)
        {
            packets.Add(_pending);
            _pending = [];
        }
    }

    public void Lost() => _pending = [];

    public const int Unknown = -1;
    public const int Incomplete = -2;

    private static readonly sbyte[] Sizes = BuildSizes();

    /// <summary>
    /// The size of the client packet at the start of <paramref name="data"/>, <see cref="Unknown"/>, or
    /// <see cref="Incomplete"/> for a chat packet not yet terminated. A chat packet's strings are text: a control
    /// byte before a string ends means the packet did not end where its strings say, so its size is not known.
    /// </summary>
    public static int Size(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return Incomplete;
        }

        var type = data[0];
        if (type is 0x14 or 0x15)
        {
            var end = 3;
            for (var strings = 0; strings < 3; strings++)
            {
                if (end > data.Length)
                {
                    return Incomplete;
                }

                var zero = data[end..].IndexOf((byte)0);
                var text = zero < 0 ? data[end..] : data.Slice(end, zero);
                if (text.IndexOfAnyInRange((byte)0x01, (byte)0x1F) >= 0)
                {
                    return Unknown;
                }

                if (zero < 0)
                {
                    return Incomplete;
                }

                end += zero + 1;
            }

            return end;
        }

        var size = Sizes[type];
        return size > 0 ? size : Unknown;
    }

    private static sbyte[] BuildSizes()
    {
        var sizes = new sbyte[256];
        void Set(int size, params int[] types)
        {
            foreach (var type in types)
            {
                sizes[type] = (sbyte)size;
            }
        }

        Set(1, 0x40, 0x41, 0x48, 0x60, 0x66, 0x68, 0x6B);
        Set(3, 0x1C, 0x3A, 0x3B, 0x3F, 0x4D, 0x58, 0x61);
        Set(5, 0x01, 0x03, 0x05, 0x08, 0x0C, 0x0F, 0x17, 0x19, 0x22, 0x24, 0x34, 0x37, 0x39, 0x3D, 0x3E, 0x4C, 0x5F, 0x62, 0x63);
        Set(6, 0x5D, 0x5E);
        Set(7, 0x4F);
        Set(9, 0x02, 0x04, 0x06, 0x07, 0x09, 0x0A, 0x0D, 0x0E, 0x10, 0x11, 0x13, 0x1A, 0x1B, 0x1D, 0x1E, 0x21, 0x23, 0x25, 0x27, 0x28, 0x29, 0x2A,
            0x2F, 0x30, 0x31, 0x36, 0x3C, 0x45, 0x49, 0x4B, 0x50, 0x51, 0x6A);
        Set(13, 0x16, 0x20, 0x26, 0x38, 0x46, 0x47);
        Set(17, 0x18, 0x1F, 0x32, 0x33, 0x35, 0x44, 0x59);
        Set(29, 0x65);
        return sizes;
    }
}
