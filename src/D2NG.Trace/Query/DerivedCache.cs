using D2NG.Core;
using D2NG.Core.D2GS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace D2NG.Trace.Query;

/// <summary>
/// An observer whose state after a whole replay can be written and read back exactly, so a later command can skip the
/// replay (<see cref="DerivedCache"/>). <see cref="CacheKey"/> names the observer and every setting that changes what it
/// derives.
/// </summary>
public interface ICachedObserver : IReplayObserver
{
    string CacheKey { get; }

    void Save(BinaryWriter w);

    void Load(BinaryReader r);
}

/// <summary>
/// What replaying one client of one game to its end derived, per observer, under %LOCALAPPDATA%\D2NG\Trace\cache\derived.
/// An entry holds the fingerprint of everything the replay read - the game's start, each of the client's files with its
/// length (as this process reads it) and write time and the rules it is replayed under, the rules override, and the
/// game data files - and is used only when the fingerprint is the same; the build is checked by <see cref="TraceCache"/>.
/// A replay is skipped only when every observer it feeds is cached; otherwise it runs and the entries are written. The
/// whole cache is kept under its cap by <see cref="TraceCache.Prune"/>.
/// </summary>
public static class DerivedCache
{
    public static bool TryLoad(GameTrace game, string label, IReadOnlyList<IReplayObserver> observers) => TryLoad(game, label, observers, out _);

    /// <summary>
    /// Fills the observers from the cache; false on a miss. Any entry that is missing, another fingerprint's, cut short,
    /// corrupt or fails to load is a miss. <paramref name="spoiled"/> says a miss came after some observer was already
    /// loaded in part, so they must be made afresh before replaying into them.
    /// </summary>
    public static bool TryLoad(GameTrace game, string label, IReadOnlyList<IReplayObserver> observers, out bool spoiled)
    {
        spoiled = false;
        if (!TraceCache.Enabled || observers.Count == 0 || !observers.All(o => o is ICachedObserver))
        {
            return false;
        }

        var fingerprint = Fingerprint(game, label);
        if (fingerprint == null)
        {
            return false;
        }

        var loaded = new List<byte[]>();
        foreach (ICachedObserver observer in observers)
        {
            var bytes = TraceCache.Read(Entry(game, label, observer), reader =>
            {
                if (reader.ReadString() != fingerprint)
                {
                    return null;
                }

                var payload = reader.ReadBytes(reader.ReadCount(1));
                return reader.ReadBytes(ChecksumLength).AsSpan().SequenceEqual(Checksum(payload)) ? payload : null;
            });
            if (bytes == null)
            {
                return false;
            }

            loaded.Add(bytes);
        }

        for (var i = 0; i < observers.Count; i++)
        {
            spoiled = true;
            try
            {
                using var reader = new BinaryReader(new MemoryStream(loaded[i]), Encoding.UTF8);
                ((ICachedObserver)observers[i]).Load(reader);
            }
            catch (Exception)
            {
                return false;
            }
        }

        spoiled = false;
        return true;
    }

    public static void Store(GameTrace game, string label, IReadOnlyList<IReplayObserver> observers)
    {
        if (!TraceCache.Enabled || !observers.All(o => o is ICachedObserver) || Fingerprint(game, label) is not { } fingerprint)
        {
            return;
        }

        foreach (ICachedObserver observer in observers)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
            {
                observer.Save(writer);
            }

            var bytes = memory.ToArray();
            TraceCache.WriteAtomically(Entry(game, label, observer), writer =>
            {
                writer.Write(fingerprint);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                writer.Write(Checksum(bytes));
            });
        }
    }

    private static byte[] Checksum(byte[] payload) => SHA256.HashData(payload)[..ChecksumLength];

    private const int ChecksumLength = 16;

    private static string Entry(GameTrace game, string label, ICachedObserver observer)
        => Path.Combine(TraceCache.Root, "derived", TraceCache.Hash(game.Folder + "|" + GameQuery.Identity(game)), $"{TraceCache.Hash(label)}-{TraceCache.Hash(observer.CacheKey)}.bin");

    /// <summary>Everything a client's replay read, as text; null when a file cannot be looked at.</summary>
    internal static string Fingerprint(GameTrace game, string label)
    {
        try
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"start {game.StartUtc.Ticks}; rules {ReplayRules.OverrideText ?? "recorded"}");
            foreach (var file in game.FilesOf(label))
            {
                var summary = TraceFileSummary.Of(file.Path);
                text.Append(CultureInfo.InvariantCulture, $"; {Path.GetFullPath(file.Path)} {summary.Length} {summary.WrittenUtc.Ticks} {ReplayRules.For(file.Header, file.Path).Version}");
            }

            foreach (var name in new[] { GameDataLocation.ItemDataFile, GameDataLocation.ItemPropertiesFile, GameDataLocation.MonsterResistsFile, GameDataLocation.ShrinesFile, GameDataLocation.ItemAffixesFile, GameDataLocation.SkillsFile, GameDataLocation.ItemPricesFile })
            {
                if (GameDataLocation.TryResolve(name, out var path))
                {
                    var info = new FileInfo(path);
                    text.Append(CultureInfo.InvariantCulture, $"; {path} {info.Length} {info.LastWriteTimeUtc.Ticks}");
                }
                else
                {
                    text.Append(CultureInfo.InvariantCulture, $"; {name} missing");
                }
            }

            return text.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>Writing and reading the values observers keep.</summary>
public static class CacheIo
{
    public static void Write(this BinaryWriter w, TimeSpan value) => w.Write(value.Ticks);

    public static TimeSpan ReadTime(this BinaryReader r) => TimeSpan.FromTicks(r.ReadInt64());

    public static void Write(this BinaryWriter w, TimeSpan? value)
    {
        w.Write(value.HasValue);
        if (value is { } v)
        {
            w.Write(v.Ticks);
        }
    }

    public static TimeSpan? ReadNullableTime(this BinaryReader r) => r.ReadBoolean() ? TimeSpan.FromTicks(r.ReadInt64()) : null;

    public static void WriteText(this BinaryWriter w, string value)
    {
        w.Write(value != null);
        if (value != null)
        {
            w.Write(value);
        }
    }

    public static string ReadText(this BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    public static void Write(this BinaryWriter w, Point value)
    {
        w.Write(value != null);
        if (value != null)
        {
            w.Write(value.X);
            w.Write(value.Y);
        }
    }

    public static Point ReadPoint(this BinaryReader r) => r.ReadBoolean() ? new Point(r.ReadUInt16(), r.ReadUInt16()) : null;

    public static void Write(this BinaryWriter w, double? value)
    {
        w.Write(value.HasValue);
        if (value is { } v)
        {
            w.Write(v);
        }
    }

    public static double? ReadNullableDouble(this BinaryReader r) => r.ReadBoolean() ? r.ReadDouble() : null;

    public static void WriteList<T>(this BinaryWriter w, IReadOnlyCollection<T> items, Action<BinaryWriter, T> write)
    {
        w.Write(items.Count);
        foreach (var item in items)
        {
            write(w, item);
        }
    }

    public static void ReadList<T>(this BinaryReader r, ICollection<T> into, Func<BinaryReader, T> read)
    {
        into.Clear();
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            into.Add(read(r));
        }
    }

    public static void WriteTimes(this BinaryWriter w, IReadOnlyCollection<TimeSpan> times) => w.WriteList(times, (x, t) => x.Write(t));

    public static void ReadTimes(this BinaryReader r, ICollection<TimeSpan> into) => r.ReadList(into, x => x.ReadTime());

    public static void Write(this BinaryWriter w, Incident i)
    {
        w.Write(i.Offset);
        w.WriteText(i.Client);
        w.WriteText(i.Kind);
        w.WriteText(i.Detail);
        w.WriteText(i.Source);
        w.Write(i.End);
        w.WriteText(i.Subject);
        w.Write(i.Count.HasValue);
        if (i.Count is { } count)
        {
            w.Write(count);
        }

        w.WriteText(i.PerClient);
    }

    public static Incident ReadIncident(this BinaryReader r)
    {
        var offset = r.ReadTime();
        var client = r.ReadText();
        var kind = r.ReadText();
        var detail = r.ReadText();
        var source = r.ReadText();
        var end = r.ReadNullableTime();
        var subject = r.ReadText();
        int? count = r.ReadBoolean() ? r.ReadInt32() : null;
        return new Incident(offset, client, kind, detail, source) { End = end, Subject = subject, Count = count, PerClient = r.ReadText() };
    }
}
