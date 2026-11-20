using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace D2NG.Core.Tracing;

/// <summary>
/// One trace file. Producers append records to the active buffer under a lock held only for the copy;
/// the recorder's writer swaps in the spare buffer and writes the full one out, so a producer never
/// waits on the disk. A full buffer drops the record and leaves a gap record behind.
/// </summary>
public sealed class TraceChannel
{
    private readonly object _gate = new();
    private readonly TraceRecorder _recorder;
    private readonly long _startTimestamp;
    private readonly double _ticksPerTimestamp;
    private readonly long _digestInterval;
    private readonly int _bufferBytes;
    private byte[] _active;
    private byte[] _spare;
    private int _length;
    private uint _pendingDropped;
    private bool _wakeRequested;
    private volatile bool _closing;
    private long _lastDigest;
    private FileStream _file;
    private bool _failed;
    private bool _flushed;

    public const string ServerGameFile = "server-game.txt";

    internal TraceChannel(TraceRecorder recorder, string directory, string fileName, string clientLabel, string gameName, int bufferBytes, TimeSpan digestInterval, string serverGame = null)
    {
        ServerGame = serverGame;
        _recorder = recorder;
        Directory = directory;
        FileName = fileName;
        ClientLabel = clientLabel;
        GameName = gameName;
        StartUtc = DateTime.UtcNow;
        _startTimestamp = Stopwatch.GetTimestamp();
        _ticksPerTimestamp = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        _digestInterval = (long)(digestInterval.TotalSeconds * Stopwatch.Frequency);
        _lastDigest = _startTimestamp;
        _bufferBytes = bufferBytes;
    }

    /// <summary>Where the file goes; for a game with a server id, settled when the file is opened.</summary>
    public string Directory { get; private set; }

    public string ServerGame { get; }

    public string FileName { get; }

    public string ClientLabel { get; }

    public string GameName { get; }

    public DateTime StartUtc { get; }

    /// <summary>Null until the writer has opened the file, which it does only once there is data.</summary>
    public string Path { get; private set; }

    public long DroppedRecords { get; private set; }

    public long RecordsWritten { get; private set; }

    internal bool Closing => _closing;

    public void Write(TraceRecordKind kind, ReadOnlySpan<byte> payload)
    {
        var wake = false;
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            if (_active == null)
            {
                _active = new byte[_bufferBytes];
                _spare = new byte[_bufferBytes];
            }

            var elapsed = (long)((Stopwatch.GetTimestamp() - _startTimestamp) * _ticksPerTimestamp);
            var needed = TraceFormat.RecordHeaderSize + payload.Length;
            var gapNeeded = _pendingDropped > 0 ? TraceFormat.RecordHeaderSize + 4 : 0;
            if (payload.Length > TraceFormat.MaxPayload || _length + gapNeeded + needed > _active.Length)
            {
                _pendingDropped++;
                DroppedRecords++;
                return;
            }

            if (gapNeeded > 0)
            {
                AppendGap(elapsed);
            }

            var destination = _active.AsSpan(_length);
            TraceFormat.WriteRecordHeader(destination, kind, elapsed, payload.Length);
            payload.CopyTo(destination[TraceFormat.RecordHeaderSize..]);
            _length += needed;
            RecordsWritten++;
            if (!_wakeRequested && _length > _active.Length / 2)
            {
                _wakeRequested = true;
                wake = true;
            }
        }

        if (wake)
        {
            _recorder.Wake();
        }
    }

    /// <summary>
    /// Whether a digest is due, checked on the game server's receive thread after a packet's handlers
    /// ran; only that thread reads and moves the digest clock.
    /// </summary>
    public bool DigestDue()
        => Stopwatch.GetTimestamp() - _lastDigest >= _digestInterval;

    public void WriteDigest(TraceDigest digest)
    {
        if (digest == null)
        {
            return;
        }

        _lastDigest = Stopwatch.GetTimestamp();
        Write(TraceRecordKind.Digest, digest.ToBytes());
    }

    public void PostponeDigest() => _lastDigest = Stopwatch.GetTimestamp();

    public void WriteClientAction(ClientAction action, uint argument)
    {
        Span<byte> payload = stackalloc byte[5];
        payload[0] = (byte)action;
        BinaryPrimitives.WriteUInt32LittleEndian(payload[1..], argument);
        Write(TraceRecordKind.ClientAction, payload);
    }

    public void Close()
    {
        _closing = true;
        _recorder.Wake();
    }

    /// <summary>
    /// A mark naming the same id whose last join is longer ago than this is an earlier game whose id the
    /// game server handed out again (the token is a small counter that restarts with the server).
    /// </summary>
    public static readonly TimeSpan SameGameWithin = TimeSpan.FromHours(3);

    private static readonly TimeSpan MarkReadRetry = TimeSpan.FromMilliseconds(25);

    /// <summary>The most one claim waits, over all its candidates, for marks other processes are still writing.</summary>
    public static readonly TimeSpan MarkWaitLimit = TimeSpan.FromMilliseconds(250);

    /// <summary>An empty mark not written to for this long was left by a process that died while claiming it.</summary>
    public static readonly TimeSpan AbandonedMark = TimeSpan.FromSeconds(2);

    private const int Candidates = 1000;

    internal static string GameDirectory(string baseDirectory, string serverGame) => GameDirectory(baseDirectory, serverGame, DateTime.UtcNow);

    /// <summary>
    /// The folder of the game the server knows by this id: the first of &lt;game&gt;, &lt;game&gt;~2, ... that
    /// this join claims, or whose mark names the same id (its first line) with a join within
    /// <see cref="SameGameWithin"/>. A mark without a join time, written by an older recorder, counts from
    /// the newest trace file in its folder. The mark is claimed with create-new, so of two bot processes
    /// joining at once one claims a folder and the other reads its mark - waiting while it is still being
    /// written, at most <see cref="MarkWaitLimit"/> per claim; an empty mark older than
    /// <see cref="AbandonedMark"/> is taken over. A folder holding traces but no mark is an earlier game.
    /// Nothing here may cost the trace: on any failure the file goes to a folder of its own, with a warning
    /// naming it.
    /// </summary>
    internal static string GameDirectory(string baseDirectory, string serverGame, DateTime nowUtc)
    {
        var waited = new Stopwatch();
        string reason;
        try
        {
            for (var number = 1; number < Candidates; number++)
            {
                var candidate = number == 1 ? baseDirectory : $"{baseDirectory}~{number}";
                var mark = System.IO.Path.Combine(candidate, ServerGameFile);
                System.IO.Directory.CreateDirectory(candidate);
                if (!File.Exists(mark) && System.IO.Directory.EnumerateFiles(candidate, "*" + TraceFormat.Extension).Any())
                {
                    continue;
                }

                try
                {
                    using (var claimed = new FileStream(mark, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        claimed.Write(System.Text.Encoding.UTF8.GetBytes(MarkText(serverGame, nowUtc)));
                    }

                    return candidate;
                }
                catch (IOException) when (File.Exists(mark))
                {
                }

                var (id, lastJoin) = ReadMark(mark, waited);
                if (id == "")
                {
                    continue;
                }

                if (id == null)
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(mark) > AbandonedMark)
                    {
                        WriteMark(mark, serverGame, nowUtc);
                        if (ReadMark(mark, waited).Id == serverGame)
                        {
                            return candidate;
                        }
                    }

                    continue;
                }

                lastJoin ??= LastWrite(candidate, mark);
                if (id == serverGame && nowUtc - lastJoin.Value <= SameGameWithin)
                {
                    WriteMark(mark, serverGame, nowUtc);
                    return candidate;
                }
            }

            reason = $"none of the {Candidates - 1} candidate folders is free";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reason = e.Message;
        }

        var own = $"{baseDirectory}~{Guid.NewGuid():N}"[..(baseDirectory.Length + 9)];
        System.IO.Directory.CreateDirectory(own);
        Serilog.Log.Warning("Could not settle the trace folder of {Game} under {Directory}: {Error}; tracing to {Fallback} instead", serverGame, baseDirectory, reason, own);
        return own;
    }

    /// <summary>When a mark without a join time was last joined: the newest trace file in its folder, else the mark itself.</summary>
    private static DateTime LastWrite(string folder, string mark)
    {
        var newest = System.IO.Directory.EnumerateFiles(folder, "*" + TraceFormat.Extension).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        return newest > DateTime.MinValue ? newest : File.GetLastWriteTimeUtc(mark);
    }

    private static string MarkText(string serverGame, DateTime nowUtc)
        => serverGame + "\n" + nowUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n";

    /// <summary>
    /// The id (first line) and last join (second line) of a mark. An empty or locked mark is being written
    /// by another process and is read again while the claim's wait, <paramref name="waited"/>, stays under
    /// <see cref="MarkWaitLimit"/>; an id of null means it stayed empty, an empty id that it stayed locked.
    /// </summary>
    internal static (string Id, DateTime? LastJoin) ReadMark(string mark, Stopwatch waited)
    {
        while (true)
        {
            try
            {
                using var stream = new FileStream(mark, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd().Split('\n');
                var id = lines[0].Trim();
                if (id.Length > 0)
                {
                    DateTime? lastJoin = lines.Length > 1 && DateTime.TryParse(lines[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : null;
                    return (id, lastJoin);
                }

                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(mark) > AbandonedMark)
                {
                    return (null, null);
                }
            }
            catch (IOException) when (waited.Elapsed < MarkWaitLimit)
            {
            }
            catch (IOException)
            {
                return ("", null);
            }

            if (waited.Elapsed >= MarkWaitLimit)
            {
                return (null, null);
            }

            waited.Start();
            System.Threading.Thread.Sleep(MarkReadRetry);
            waited.Stop();
        }
    }

    private static void WriteMark(string mark, string serverGame, DateTime nowUtc)
    {
        var temporary = mark + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(temporary, MarkText(serverGame, nowUtc));
            File.Move(temporary, mark, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void AppendGap(long elapsed)
    {
        var destination = _active.AsSpan(_length);
        TraceFormat.WriteRecordHeader(destination, TraceRecordKind.Gap, elapsed, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[TraceFormat.RecordHeaderSize..], _pendingDropped);
        _length += TraceFormat.RecordHeaderSize + 4;
        _pendingDropped = 0;
    }

    /// <summary>
    /// Only one drain may run at a time (the recorder's drain lock): the spare buffer is the drainer's
    /// alone between the swap and the next drain.
    /// </summary>
    /// <returns>Whether the channel is finished and can be forgotten.</returns>
    internal bool Drain()
    {
        bool closing;
        int length;
        lock (_gate)
        {
            closing = _closing;
            (_active, _spare) = (_spare, _active);
            length = _length;
            _length = 0;
            _wakeRequested = false;
            if (_pendingDropped > 0)
            {
                AppendGap((long)((Stopwatch.GetTimestamp() - _startTimestamp) * _ticksPerTimestamp));
            }
        }

        if (length > 0 && !_failed)
        {
            try
            {
                _file ??= Open();
                _file.Write(_spare, 0, length);
                _file.Flush();
                _flushed = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _failed = true;
                Serilog.Log.Warning("Tracing to {Directory} stopped: {Error}", Directory, e.Message);
                Abandon();
            }
        }

        if (closing && _length == 0)
        {
            _file?.Dispose();
            _file = null;
            lock (_gate)
            {
                _active = null;
                _spare = null;
            }

            return true;
        }

        return false;
    }

    /// <summary>Disposing flushes what the failed write left buffered, which fails again on a full disk.</summary>
    private void Abandon()
    {
        try
        {
            _file?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        _file = null;
        if (!_flushed && Path != null)
        {
            try
            {
                File.Delete(Path);
                Path = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private FileStream Open()
    {
        if (ServerGame != null)
        {
            Directory = GameDirectory(Directory, ServerGame);
        }

        System.IO.Directory.CreateDirectory(Directory);
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? FileName : $"{FileName}-{attempt}";
            var path = System.IO.Path.Combine(Directory, name + TraceFormat.Extension);
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
                var header = TraceFormat.Header(_recorder.Source, StartUtc, ClientLabel, GameName, ServerGame);
                stream.Write(header);
                Path = path;
                return stream;
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
    }
}
