using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace D2NG.Trace;

/// <summary>
/// A trace file as this process first saw it. A game still being recorded grows while a command reads it, and two passes
/// over one file - a command replays a client more than once, <c>--compare-rules</c> twice - would score different
/// data; so every read of a file in the process stops at the length it had when the process first opened it.
/// </summary>
public static class Snapshot
{
    private static readonly ConcurrentDictionary<string, long> Lengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The length a file is read to in this process: its length when first asked.</summary>
    public static long LengthOf(string path, long lengthNow) => Lengths.GetOrAdd(System.IO.Path.GetFullPath(path), lengthNow);

    /// <summary>Opens a file for reading, cut at <see cref="LengthOf"/>.</summary>
    public static Stream Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        var length = LengthOf(path, stream.Length);
        return stream.Length > length ? new Capped(stream, length) : stream;
    }

    /// <summary>Forgets every length, so the next open of a file sees it as it is now; for tests that rewrite a file in place.</summary>
    public static void Forget(string path) => Lengths.TryRemove(System.IO.Path.GetFullPath(path), out _);

    private sealed class Capped(Stream inner, long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = Math.Min(value, length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var left = length - inner.Position;
            return left <= 0 ? 0 : inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => inner.Position + offset,
                _ => length + offset,
            };
            return inner.Seek(Math.Min(target, length), SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// What one pass over a trace file's record headers tells: its header, game server id, end, records per kind and the
/// <see cref="BuildEvidence"/> an unstamped header needs. Read once per file per process.
/// </summary>
public sealed class TraceFileSummary
{
    private const int LogonSearch = 40;

    public required string Path { get; init; }

    /// <summary>The bytes read: the file's length when the process first opened it.</summary>
    public required long Length { get; init; }

    public required DateTime WrittenUtc { get; init; }

    public required TraceHeader Header { get; init; }

    /// <summary>The header's game server id, or for older files "#token" from the logon packet; null when neither is there.</summary>
    public string ServerGame { get; init; }

    public required DateTime EndUtc { get; init; }

    public required IReadOnlyDictionary<TraceRecordKind, long> Records { get; init; }

    public BuildEvidence Evidence { get; init; }

    private static readonly ConcurrentDictionary<string, TraceFileSummary> Known = new(StringComparer.OrdinalIgnoreCase);

    public static TraceFileSummary Of(string path) => Of(new FileInfo(path));

    /// <param name="info">The file as a directory listing found it.</param>
    public static TraceFileSummary Of(FileInfo info)
    {
        var path = info.FullName;
        if (Known.TryGetValue(path, out var known))
        {
            return known;
        }

        var lengthNow = TraceCache.Enabled && info.Exists ? TraceCache.LengthNow(path) : -1;
        if (lengthNow >= 0 && TraceCache.Summary(path, lengthNow, info.LastWriteTimeUtc) is { } cached && Snapshot.LengthOf(path, cached.Length) == cached.Length)
        {
            return Known.GetOrAdd(path, cached);
        }

        var summary = Read(path, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
        if (lengthNow >= 0 && summary.Length == lengthNow && ReadHeaderBytes(path, summary.Header.Length) is { } header)
        {
            TraceCache.Remember(summary, header);
        }

        return Known.GetOrAdd(path, summary);
    }

    private static byte[] ReadHeaderBytes(string path, int length)
    {
        try
        {
            var bytes = new byte[length];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.ReadAtLeast(bytes, length, throwOnEndOfStream: false) == length ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets the summary of a file, for tests that rewrite one in place.</summary>
    public static void Forget(string path)
    {
        Known.TryRemove(System.IO.Path.GetFullPath(path), out _);
        Snapshot.Forget(path);
    }

    private static TraceFileSummary Read(string path, DateTime written)
    {
        using var _ = Profile.Time("open: read a trace file's header and end");
        using var reader = new TraceReader(path);
        var header = reader.Header;
        var counts = new Dictionary<TraceRecordKind, long>();
        var searched = header.ServerGame == null ? LogonSearch : 0;
        var seen = 0;
        ushort? token = null;
        var opensShop = false;
        var asksVendor = false;
        var last = reader.Visit((kind, payload, _) =>
        {
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (seen < searched)
            {
                seen++;
                if (kind == TraceRecordKind.D2gsSent && payload.Length >= 7 && payload[0] == (byte)Core.D2GS.Packet.OutGoingPacket.GameLogon)
                {
                    token = BinaryPrimitives.ReadUInt16LittleEndian(payload[5..]);
                    seen = searched;
                }
            }

            if (kind == TraceRecordKind.ClientAction && payload.Length > 0 && payload[0] == (byte)ClientAction.OpenShop)
            {
                opensShop = true;
            }
            else if (kind == TraceRecordKind.D2gsSent && payload.Length >= 5 && payload[0] == (byte)Core.D2GS.Packet.OutGoingPacket.EntityAction
                && BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]) is 1 or 2 or 4)
            {
                asksVendor = true;
            }
        });

        return new TraceFileSummary
        {
            Path = path,
            Length = reader.StreamLength,
            WrittenUtc = written,
            Header = header,
            ServerGame = header.ServerGame ?? (token is { } found ? "#" + found.ToString(CultureInfo.InvariantCulture) : null),
            EndUtc = header.StartUtc + last,
            Records = counts,
            Evidence = new BuildEvidence(opensShop, asksVendor),
        };
    }
}
