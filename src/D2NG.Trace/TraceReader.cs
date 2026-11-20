using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace D2NG.Trace;

public readonly record struct TraceRecord(TraceRecordKind Kind, TimeSpan Elapsed, byte[] Payload)
{
    public uint DroppedCount => Kind == TraceRecordKind.Gap && Payload.Length >= 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(Payload)
        : 0;

    /// <summary>A sniffer's gap: compressed bytes it skipped, a missed segment's included; null in a bot's trace, whose gaps count dropped records.</summary>
    public uint? SkippedBytes => Kind == TraceRecordKind.Gap && Payload.Length >= 8
        ? BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(4))
        : null;

    /// <summary>What a gap lost, in words.</summary>
    public string GapText => SkippedBytes is { } bytes ? $"{DroppedCount} frames dropped, {bytes} bytes skipped" : $"{DroppedCount} records dropped";
}

/// <summary>A file that is not a readable d2trace: empty, a different format, or a header that contradicts itself.</summary>
public sealed class TraceFormatException(string message) : IOException(message);

public sealed class TraceHeader
{
    public ushort Version { get; init; }
    public TraceSource Source { get; init; }
    public DateTime StartUtc { get; init; }
    public string ClientLabel { get; init; }
    public string GameName { get; init; }

    /// <summary>The game server's id for the game ("address#token"), or null in files written before it was recorded.</summary>
    public string ServerGame { get; init; }
    public int Length { get; init; }

    /// <summary>
    /// The client rules the recorder interpreted packets under, <see cref="TraceFormat.RulesNotApplicable"/> for a real client;
    /// null in files written before it was recorded, whose rules are unknown (<see cref="ReplayRules.Resolve"/> estimates them).
    /// </summary>
    public int? RulesVersion { get; init; }

    /// <summary>The recorder's build ("1.0.0+&lt;sha&gt;"), or null in files written before it was recorded.</summary>
    public string Build { get; init; }

    public DateTime? BuildUtc { get; init; }
}

/// <summary>
/// Reads a .d2trace file front to back. A record cut short at the end - the process died mid-write -
/// ends the enumeration and sets <see cref="Truncated"/>; gap records are yielded like any other.
/// </summary>
public sealed class TraceReader : IDisposable
{
    private readonly Stream _stream;

    public TraceReader(string path)
        : this(Snapshot.Open(path))
    {
        Path = path;
    }

    /// <summary>The file read, or null for a stream.</summary>
    public string Path { get; }

    /// <summary>How many bytes the reader reads in all; -1 for a stream that cannot say.</summary>
    public long StreamLength => _stream.CanSeek ? _stream.Length : -1;

    public TraceReader(Stream stream)
    {
        _stream = stream;
        try
        {
            Header = ReadHeader(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public TraceHeader Header { get; }

    public bool Truncated { get; private set; }

    public int Gaps { get; private set; }

    public long DroppedRecords { get; private set; }

    public IEnumerable<TraceRecord> Records()
    {
        while (true)
        {
            var available = Fill(TraceFormat.RecordHeaderSize);
            if (available == 0)
            {
                yield break;
            }

            if (available < TraceFormat.RecordHeaderSize)
            {
                Consume(available);
                Truncated = true;
                yield break;
            }

            var header = _buffer.AsSpan(_position, TraceFormat.RecordHeaderSize);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
            var kind = (TraceRecordKind)header[2];
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(header[3..]);
            available = Fill(TraceFormat.RecordHeaderSize + length);
            if (available < TraceFormat.RecordHeaderSize + length)
            {
                Consume(available);
                Truncated = true;
                yield break;
            }

            var payload = length == 0 ? [] : _buffer.AsSpan(_position + TraceFormat.RecordHeaderSize, length).ToArray();
            Consume(TraceFormat.RecordHeaderSize + length);
            var record = new TraceRecord(kind, TimeSpan.FromTicks(ticks), payload);
            if (kind == TraceRecordKind.Gap)
            {
                Gaps++;
                DroppedRecords += record.DroppedCount;
            }

            yield return record;
        }
    }

    /// <summary>
    /// One pass from the read position: the elapsed time of the last whole record (zero for a file with none) and,
    /// among the first <paramref name="searched"/> records, the first that <paramref name="match"/> accepts.
    /// </summary>
    public TimeSpan Scan(int searched, Func<TraceRecordKind, ReadOnlySpan<byte>, bool> match, out TraceRecord? found)
    {
        TraceRecord? first = null;
        var seen = 0;
        var last = Visit((kind, payload, elapsed) =>
        {
            if (seen < searched)
            {
                seen++;
                if (match(kind, payload))
                {
                    first = new TraceRecord(kind, elapsed, payload.ToArray());
                    seen = searched;
                }
            }
        });
        found = first;
        return last;
    }

    public delegate void RecordVisitor(TraceRecordKind kind, ReadOnlySpan<byte> payload, TimeSpan elapsed);

    /// <summary>One pass from the read position over every whole record, without copying payloads; returns the elapsed time of the last (zero for none).</summary>
    public TimeSpan Visit(RecordVisitor visit)
    {
        var last = TimeSpan.Zero;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var filled = _filled - _position;
            Array.Copy(_buffer, _position, buffer, 0, filled);
            _position = _filled = 0;
            var at = 0;
            while (true)
            {
                if (filled - at < TraceFormat.RecordHeaderSize || filled - at < TraceFormat.RecordHeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at)))
                {
                    Array.Copy(buffer, at, buffer, 0, filled - at);
                    filled -= at;
                    at = 0;
                    var read = _stream.Read(buffer, filled, buffer.Length - filled);
                    if (read == 0)
                    {
                        return last;
                    }

                    filled += read;
                    continue;
                }

                var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at));
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(at + 3));
                last = TimeSpan.FromTicks(ticks);
                visit((TraceRecordKind)buffer[at + 2], buffer.AsSpan(at + TraceFormat.RecordHeaderSize, length), last);
                at += TraceFormat.RecordHeaderSize + length;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The elapsed time of the last whole record, read in large blocks and walking only the record headers; zero for a file with none.</summary>
    public TimeSpan LastElapsed()
    {
        var last = TimeSpan.Zero;
        while (true)
        {
            var available = Fill(TraceFormat.RecordHeaderSize);
            if (available < TraceFormat.RecordHeaderSize)
            {
                return last;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position));
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_position + 3));
            if (Skip(TraceFormat.RecordHeaderSize + length) < TraceFormat.RecordHeaderSize + length)
            {
                return last;
            }

            last = TimeSpan.FromTicks(ticks);
        }
    }

    private const int BlockSize = 1 << 16;

    private byte[] _buffer = new byte[BlockSize];
    private int _position;
    private int _filled;

    /// <summary>Buffers at least <paramref name="count"/> bytes from the read position, or what is left of the file; returns how many are buffered.</summary>
    private int Fill(int count)
    {
        if (_filled - _position >= count)
        {
            return _filled - _position;
        }

        if (_position > 0)
        {
            Array.Copy(_buffer, _position, _buffer, 0, _filled - _position);
            _filled -= _position;
            _position = 0;
        }

        if (count > _buffer.Length)
        {
            Array.Resize(ref _buffer, count);
        }

        while (_filled < count)
        {
            var read = _stream.Read(_buffer, _filled, _buffer.Length - _filled);
            if (read == 0)
            {
                break;
            }

            _filled += read;
        }

        return _filled - _position;
    }

    private void Consume(int count) => _position += count;

    /// <summary>Moves the read position on by <paramref name="count"/> bytes without buffering what is skipped; returns how many there were.</summary>
    private int Skip(int count)
    {
        var buffered = _filled - _position;
        if (buffered >= count)
        {
            _position += count;
            return count;
        }

        _position = _filled = 0;
        var skipped = buffered;
        if (_stream.CanSeek)
        {
            var left = _stream.Length - _stream.Position;
            var step = (int)Math.Min(count - skipped, left);
            _stream.Seek(step, SeekOrigin.Current);
            skipped += step;
            return skipped;
        }

        while (skipped < count)
        {
            var read = _stream.Read(_buffer, 0, Math.Min(_buffer.Length, count - skipped));
            if (read == 0)
            {
                break;
            }

            skipped += read;
        }

        return skipped;
    }

    private const int FixedHeaderSize = 8;

    private const int MinimumHeaderSize = 18 + 2 + 2;

    private static TraceHeader ReadHeader(Stream stream)
    {
        var fixedPart = new byte[FixedHeaderSize];
        if (stream.ReadAtLeast(fixedPart, fixedPart.Length, throwOnEndOfStream: false) < fixedPart.Length
            || !fixedPart.AsSpan(0, 4).SequenceEqual(TraceFormat.Magic))
        {
            throw new TraceFormatException("Not a d2trace file");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(4));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(6));
        if (length < MinimumHeaderSize)
        {
            throw new TraceFormatException($"d2trace header length {length} is shorter than the {MinimumHeaderSize} bytes every header holds");
        }

        var rest = new byte[length - FixedHeaderSize];
        if (stream.ReadAtLeast(rest, rest.Length, throwOnEndOfStream: false) < rest.Length)
        {
            throw new TraceFormatException("d2trace header is cut short");
        }

        var span = rest.AsSpan();
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(span[2..]);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw new TraceFormatException("d2trace header holds no valid start time");
        }

        var labelLength = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        var gameOffset = 12 + labelLength;
        if (gameOffset + 2 > span.Length)
        {
            throw new TraceFormatException($"d2trace client label of {labelLength} bytes runs past the header");
        }

        var label = Encoding.UTF8.GetString(span.Slice(12, labelLength));
        var gameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[gameOffset..]);
        if (gameOffset + 2 + gameLength > span.Length)
        {
            throw new TraceFormatException($"d2trace game name of {gameLength} bytes runs past the header");
        }

        var game = Encoding.UTF8.GetString(span.Slice(gameOffset + 2, gameLength));
        string serverGame = null;
        var serverOffset = gameOffset + 2 + gameLength;
        if (serverOffset + 2 <= span.Length)
        {
            var serverLength = BinaryPrimitives.ReadUInt16LittleEndian(span[serverOffset..]);
            if (serverLength > 0 && serverOffset + 2 + serverLength <= span.Length)
            {
                serverGame = Encoding.UTF8.GetString(span.Slice(serverOffset + 2, serverLength));
            }
        }

        var (rules, build, buildUtc) = TraceHeaderIdentity.Read(span, serverOffset);
        return new TraceHeader
        {
            Version = version,
            Source = (TraceSource)span[0],
            StartUtc = new DateTime(ticks, DateTimeKind.Utc),
            ClientLabel = label,
            GameName = game,
            ServerGame = serverGame,
            Length = length,
            RulesVersion = rules,
            Build = build,
            BuildUtc = buildUtc,
        };
    }

    public void Dispose() => _stream.Dispose();
}
