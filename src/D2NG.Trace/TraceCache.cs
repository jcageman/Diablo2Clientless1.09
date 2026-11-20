using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace D2NG.Trace;

/// <summary>
/// The query tool's caches on disk, under %LOCALAPPDATA%\D2NG\Trace\cache: what reading a trace file tells
/// (<see cref="TraceFileSummary"/>) and what replaying a client derives (<see cref="Query.DerivedCache"/>). Every entry
/// is keyed on what it was computed from - the files' lengths and write times - and every cache file on the build that
/// wrote it, so an entry is used only where computing afresh would give the same; deleting the folder changes no answer.
/// </summary>
public static class TraceCache
{
    private static string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D2NG", "Trace", "cache");

    /// <summary>On in the Trace tool unless <c>--no-cache</c>; off by default, so a library user or a test reads only the files.</summary>
    public static bool Enabled { get; set; }

    public static string Root
    {
        get => _root;
        set => _root = value;
    }

    /// <summary>The builds of the tool, the trace library and the game code: a cache written by any other build is not read.</summary>
    public static string BuildStamp { get; } = string.Join("|", new[] { typeof(TraceCache).Assembly, typeof(Core.Game).Assembly, System.Reflection.Assembly.GetEntryAssembly() }
        .Where(a => a != null).Select(a => a.ManifestModule.ModuleVersionId.ToString("N")));

    /// <summary>
    /// A file's length as an open handle reports it, -1 when it cannot be opened. A directory listing, and a FileInfo, may
    /// still give the length a file had when its writer last flushed the directory entry, so a file being written would
    /// match a cache entry of an earlier length.
    /// </summary>
    public static long LengthNow(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            return stream.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>A short stable name for a key, for file names.</summary>
    public static string Hash(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..24];

    /// <summary>Writes a cache file whole or not at all: a reader never sees half of one, and two processes writing one leave one of them.</summary>
    public static void WriteAtomically(string path, Action<BinaryWriter> write)
    {
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            long length;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(BuildStamp);
                write(writer);
                writer.Flush();
                length = stream.Length;
            }

            File.Move(temporary, path, overwrite: true);
            Wrote(length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Reads a cache file written by this build; null when there is none, it is another build's, or anything about it
    /// fails to read - a cut or corrupt file is a miss, never an error.
    /// </summary>
    public static T Read<T>(string path, Func<BinaryReader, T> read)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            return reader.ReadString() == Magic && reader.ReadString() == BuildStamp ? read(reader) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A count read from a cache file, refused when the bytes left could not hold that many items of at least this size.</summary>
    public static int ReadCount(this BinaryReader reader, int bytesEach)
    {
        var count = reader.ReadInt32();
        var left = reader.BaseStream.Length - reader.BaseStream.Position;
        if (count < 0 || (long)count * Math.Max(1, bytesEach) > left)
        {
            throw new InvalidDataException($"a cache file claims {count} items with {left} bytes left");
        }

        return count;
    }

    private const string Magic = "d2trace-cache-1";

    private const string TemporarySuffix = ".tmp";

    /// <summary>The whole cache stays under this; past it the least recently written entries go.</summary>
    public static long CapBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Bytes written since the cache was last pruned; past an eighth of the cap it is pruned again. Starts past it, so a process's first write prunes.</summary>
    private static long _writtenSincePrune = long.MaxValue / 4;

    private static int _pruning;

    private static void Wrote(long bytes)
    {
        if (System.Threading.Interlocked.Add(ref _writtenSincePrune, bytes) >= CapBytes / 8)
        {
            Prune();
        }
    }

    /// <summary>
    /// Keeps the cache under <see cref="CapBytes"/>: each game folder of derived entries, and each file of the other buckets,
    /// is one unit, and the least recently written units go until the rest fits. A temporary file older than an hour is a
    /// write that never finished and goes too. A unit that cannot be deleted is passed over. Runs at the first write of a
    /// process and after every eighth of the cap written since.
    /// </summary>
    public static void Prune()
    {
        if (System.Threading.Interlocked.Exchange(ref _pruning, 1) == 1)
        {
            return;
        }

        try
        {
            System.Threading.Interlocked.Exchange(ref _writtenSincePrune, 0);
            var root = new DirectoryInfo(Root);
            if (!root.Exists)
            {
                return;
            }

            var units = new List<(FileSystemInfo Entry, long Bytes, DateTime Written)>();
            var stale = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var bucket in Safe(root.GetDirectories))
            {
                if (string.Equals(bucket.Name, "derived", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var game in Safe(bucket.GetDirectories))
                    {
                        var files = Safe(() => game.GetFiles());
                        var live = files.Where(f => !IsStaleTemporary(f, stale)).ToList();
                        foreach (var file in files.Except(live))
                        {
                            TryDelete(file.FullName);
                        }

                        units.Add((game, live.Sum(f => f.Length), live.Count == 0 ? DateTime.MinValue : live.Max(f => f.LastWriteTimeUtc)));
                    }

                    continue;
                }

                foreach (var file in Safe(() => bucket.GetFiles("*", SearchOption.AllDirectories)))
                {
                    if (IsStaleTemporary(file, stale))
                    {
                        TryDelete(file.FullName);
                    }
                    else
                    {
                        units.Add((file, file.Length, file.LastWriteTimeUtc));
                    }
                }
            }

            var total = 0L;
            foreach (var unit in units.OrderByDescending(u => u.Written))
            {
                total += unit.Bytes;
                if (total > CapBytes)
                {
                    TryDelete(unit.Entry);
                }
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _pruning, 0);
        }
    }

    private static bool IsStaleTemporary(FileInfo file, DateTime stale)
        => file.Name.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase) && file.LastWriteTimeUtc < stale;

    private static T[] Safe<T>(Func<T[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDelete(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
            }
            else
            {
                entry.Delete();
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly ConcurrentDictionary<string, SummaryBucket> Buckets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cached summary of a file whose length and write time are these, or null.</summary>
    internal static TraceFileSummary Summary(string path, long length, DateTime writtenUtc)
        => Enabled && Bucket(path).Get(Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path), length, writtenUtc, path) is { } found ? found : null;

    internal static void Remember(TraceFileSummary summary, byte[] header)
    {
        if (Enabled)
        {
            Bucket(summary.Path).Put(Path.GetFileName(Path.GetDirectoryName(summary.Path)) + "/" + Path.GetFileName(summary.Path), summary, header);
        }
    }

    /// <summary>Writes what was learnt since the cache files were read. Called once a command has run.</summary>
    public static void Flush()
    {
        if (!Enabled)
        {
            return;
        }

        foreach (var bucket in Buckets.Values)
        {
            bucket.Save();
        }

        foreach (var bucket in Ranges.Values)
        {
            bucket.Save();
        }
    }

    /// <summary>Forgets what this process loaded, as if it had just started; for tests.</summary>
    public static void Reset()
    {
        Buckets.Clear();
        Ranges.Clear();
        System.Threading.Interlocked.Exchange(ref _writtenSincePrune, long.MaxValue / 4);
    }

    private static readonly ConcurrentDictionary<string, RangeBucket> Ranges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first and last line stamps of a sidecar whose length and write time are these, or null.</summary>
    internal static (long First, long Last)? SidecarRange(string path, long length, DateTime writtenUtc)
        => Enabled ? RangesOf(path).Get(Path.GetFileName(path), length, writtenUtc.Ticks) : null;

    internal static void RememberSidecarRange(string path, long length, DateTime writtenUtc, (long First, long Last) range)
    {
        if (Enabled)
        {
            RangesOf(path).Put(Path.GetFileName(path), length, writtenUtc.Ticks, range);
        }
    }

    private static RangeBucket RangesOf(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        return Ranges.GetOrAdd(folder, f => new RangeBucket(Path.Combine(Root, "sidecars", Hash(f) + ".bin"), f));
    }

    /// <summary>One cache file per folder of sidecars (a date folder): each sidecar's length, write time and line stamp range.</summary>
    private sealed class RangeBucket(string file, string folder)
    {
        private readonly object _gate = new();
        private Dictionary<string, (long Length, long Written, long First, long Last)> _entries;
        private bool _dirty;

        private void Load() => _entries ??= ReadDisk() ?? new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, (long Length, long Written, long First, long Last)> ReadDisk()
            => TraceCache.Read(file, reader =>
            {
                if (!string.Equals(reader.ReadString(), folder, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var count = reader.ReadCount(33);
                var entries = new Dictionary<string, (long, long, long, long)>(count, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                {
                    entries[reader.ReadString()] = (reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
                }

                return entries;
            });

        public (long, long)? Get(string name, long length, long written)
        {
            lock (_gate)
            {
                Load();
                return _entries.TryGetValue(name, out var e) && e.Length == length && e.Written == written ? (e.First, e.Last) : null;
            }
        }

        public void Put(string name, long length, long written, (long First, long Last) range)
        {
            lock (_gate)
            {
                Load();
                _entries[name] = (length, written, range.First, range.Last);
                _dirty = true;
            }
        }

        public void Save()
        {
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                foreach (var (name, e) in ReadDisk() ?? [])
                {
                    _entries.TryAdd(name, e);
                }

                WriteAtomically(file, writer =>
                {
                    writer.Write(folder);
                    writer.Write(_entries.Count);
                    foreach (var (name, e) in _entries)
                    {
                        writer.Write(name);
                        writer.Write(e.Length);
                        writer.Write(e.Written);
                        writer.Write(e.First);
                        writer.Write(e.Last);
                    }
                });
                _dirty = false;
            }
        }
    }

    /// <summary>One cache file per folder of game folders (a date folder): the summaries of its trace files.</summary>
    private static SummaryBucket Bucket(string path)
    {
        var games = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "";
        return Buckets.GetOrAdd(games, folder => new SummaryBucket(Path.Combine(Root, "files", Hash(folder) + ".bin"), folder));
    }

    private sealed class SummaryBucket(string file, string folder)
    {
        private readonly object _gate = new();
        private Dictionary<string, (long Length, long Written, byte[] Header, string ServerGame, long EndTicks, KeyValuePair<byte, long>[] Records, bool OpensShop, bool AsksVendor)> _entries;
        private bool _dirty;

        private void Load() => _entries ??= ReadDisk() ?? new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, (long Length, long Written, byte[] Header, string ServerGame, long EndTicks, KeyValuePair<byte, long>[] Records, bool OpensShop, bool AsksVendor)> ReadDisk()
            => TraceCache.Read(file, reader =>
            {
                if (!string.Equals(reader.ReadString(), folder, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var count = reader.ReadCount(32);
                var entries = new Dictionary<string, (long, long, byte[], string, long, KeyValuePair<byte, long>[], bool, bool)>(count, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                {
                    var name = reader.ReadString();
                    var length = reader.ReadInt64();
                    var written = reader.ReadInt64();
                    var header = reader.ReadBytes(reader.ReadCount(1));
                    var serverGame = reader.ReadBoolean() ? reader.ReadString() : null;
                    var end = reader.ReadInt64();
                    var records = new KeyValuePair<byte, long>[reader.ReadCount(9)];
                    for (var r = 0; r < records.Length; r++)
                    {
                        records[r] = new(reader.ReadByte(), reader.ReadInt64());
                    }

                    entries[name] = (length, written, header, serverGame, end, records, reader.ReadBoolean(), reader.ReadBoolean());
                }

                return entries;
            });

        public TraceFileSummary Get(string name, long length, DateTime writtenUtc, string path)
        {
            lock (_gate)
            {
                Load();
                if (!_entries.TryGetValue(name, out var entry) || entry.Length != length || entry.Written != writtenUtc.Ticks)
                {
                    return null;
                }

                TraceHeader header;
                try
                {
                    using var reader = new TraceReader(new MemoryStream(entry.Header, writable: false));
                    header = reader.Header;
                }
                catch (Exception)
                {
                    return null;
                }

                return new TraceFileSummary
                {
                    Path = path,
                    Length = entry.Length,
                    WrittenUtc = writtenUtc,
                    Header = header,
                    ServerGame = entry.ServerGame,
                    EndUtc = new DateTime(entry.EndTicks, DateTimeKind.Utc),
                    Records = entry.Records.ToDictionary(r => (Core.Tracing.TraceRecordKind)r.Key, r => r.Value),
                    Evidence = new BuildEvidence(entry.OpensShop, entry.AsksVendor),
                };
            }
        }

        public void Put(string name, TraceFileSummary summary, byte[] header)
        {
            lock (_gate)
            {
                Load();
                _entries[name] = (summary.Length, summary.WrittenUtc.Ticks, header, summary.ServerGame, summary.EndUtc.Ticks,
                    [.. summary.Records.OrderBy(r => r.Key).Select(r => new KeyValuePair<byte, long>((byte)r.Key, r.Value))], summary.Evidence.OpensShop, summary.Evidence.AsksVendor);
                _dirty = true;
            }
        }

        public void Save()
        {
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                foreach (var (name, e) in ReadDisk() ?? [])
                {
                    _entries.TryAdd(name, e);
                }

                WriteAtomically(file, writer =>
                {
                    writer.Write(folder);
                    writer.Write(_entries.Count);
                    foreach (var (name, e) in _entries)
                    {
                        writer.Write(name);
                        writer.Write(e.Length);
                        writer.Write(e.Written);
                        writer.Write(e.Header.Length);
                        writer.Write(e.Header);
                        writer.Write(e.ServerGame != null);
                        if (e.ServerGame != null)
                        {
                            writer.Write(e.ServerGame);
                        }

                        writer.Write(e.EndTicks);
                        writer.Write(e.Records.Length);
                        foreach (var (kind, records) in e.Records)
                        {
                            writer.Write(kind);
                            writer.Write(records);
                        }

                        writer.Write(e.OpensShop);
                        writer.Write(e.AsksVendor);
                    }
                });
                _dirty = false;
            }
        }
    }
}
