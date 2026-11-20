using D2NG.Core.Tracing;
using System;
using System.Globalization;
using System.IO;

namespace D2NG.Core.Capture;

/// <summary>
/// Writes trace files for traffic someone else's client produced, stamped with when the capture saw each
/// packet rather than when it is written. Same layout and folders as <see cref="TraceRecorder"/>:
/// &lt;root&gt;/&lt;yyyyMMdd&gt;/&lt;game&gt;/&lt;client&gt;.d2trace, the game folder claimed by the server's game id,
/// and &lt;root&gt;/&lt;yyyyMMdd&gt;/realm/&lt;client&gt;-&lt;HHmmss&gt;.d2trace; the date is the capture's.
/// </summary>
public sealed class CaptureTraceWriter
{
    public CaptureTraceWriter(string root, TraceSource source = TraceSource.Sniffer)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Path.GetFullPath(root);
        Source = source;
    }

    public string Root { get; }

    public TraceSource Source { get; }

    public CaptureTraceFile OpenGame(DateTime startUtc, string gameName, string clientLabel, string serverGame)
    {
        var directory = Path.Combine(DateFolder(startUtc), TraceRecorder.Safe(gameName));
        if (serverGame != null)
        {
            directory = TraceChannel.GameDirectory(directory, serverGame);
        }

        return new CaptureTraceFile(this, directory, TraceRecorder.Safe(clientLabel), startUtc, clientLabel, gameName, serverGame);
    }

    public CaptureTraceFile OpenRealm(DateTime startUtc, string clientLabel)
    {
        var suffix = startUtc.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture);
        return new CaptureTraceFile(this, Path.Combine(DateFolder(startUtc), "realm"), $"{TraceRecorder.Safe(clientLabel)}-{suffix}", startUtc, clientLabel, "", null);
    }

    private string DateFolder(DateTime utc) => Path.Combine(Root, utc.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
}

/// <summary>One trace file written synchronously; a record earlier than the one before it is stamped with the earlier one's time, so the file stays in time order.</summary>
public sealed class CaptureTraceFile : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly FileStream _stream;
    private readonly byte[] _header = new byte[TraceFormat.RecordHeaderSize];
    private long _lastTicks;
    private DateTime _lastFlush = DateTime.UtcNow;

    internal CaptureTraceFile(CaptureTraceWriter writer, string directory, string fileName, DateTime startUtc, string clientLabel, string gameName, string serverGame)
    {
        Directory.CreateDirectory(directory);
        StartUtc = startUtc;
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? fileName : $"{fileName}-{attempt}";
            var path = System.IO.Path.Combine(directory, name + TraceFormat.Extension);
            try
            {
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
                Path = path;
                break;
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        try
        {
            _stream.Write(TraceFormat.Header(writer.Source, startUtc, clientLabel, gameName, serverGame));
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string Path { get; }

    public DateTime StartUtc { get; }

    public long Records { get; private set; }

    public bool Closed { get; private set; }

    /// <summary>Why writing stopped (the disk filled, the file went away); the file is closed and takes no more records.</summary>
    public string Failure { get; private set; }

    /// <summary>Records stamped earlier than the one before them, written with the earlier one's time.</summary>
    public long Clamped { get; private set; }

    public void Write(TraceRecordKind kind, ReadOnlySpan<byte> payload, DateTime atUtc)
    {
        if (Closed || payload.Length > TraceFormat.MaxPayload)
        {
            return;
        }

        var ticks = (atUtc - StartUtc).Ticks;
        if (ticks < _lastTicks)
        {
            Clamped++;
            ticks = _lastTicks;
        }

        _lastTicks = ticks;
        TraceFormat.WriteRecordHeader(_header, kind, ticks, payload.Length);
        try
        {
            _stream.Write(_header);
            _stream.Write(payload);
            Records++;
            var now = DateTime.UtcNow;
            if (now - _lastFlush >= FlushInterval)
            {
                _stream.Flush();
                _lastFlush = now;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Failure = e.Message;
            Dispose();
        }
    }

    public void WriteGap(uint dropped, DateTime atUtc)
    {
        Span<byte> payload = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, dropped);
        Write(TraceRecordKind.Gap, payload, atUtc);
    }

    public void Dispose()
    {
        if (Closed)
        {
            return;
        }

        Closed = true;
        try
        {
            _stream.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failure ??= e.Message;
        }
    }
}
