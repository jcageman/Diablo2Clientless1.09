using MpqLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace D2NG.Core.ObjectData;

/// <summary>
/// Reads the shrine rows out of a Diablo 2 install's objects table.
///
/// Like monstats, the compiled <c>objects.bin</c> in patch_d2.mpq is what the game loads, and its
/// record index is the object class id the server sends. The readable objects.txt has an Expansion
/// separator row that shifts every id after 410, so it is not used.
/// </summary>
public static class ObjectsReader
{
    public const string ObjectsPath = @"data\global\excel\objects.bin";

    /// <summary>Archives in the order the game resolves them. First hit wins.</summary>
    public static readonly string[] ArchivePriority = ["patch_d2.mpq", "d2exp.mpq", "d2data.mpq"];

    /// <summary>The Name column is the first field of the record, NUL terminated.</summary>
    private const int NameLength = 64;

    /// <summary>
    /// Offset of the Parm0 dword, keyed by record size, since the record layout is version specific.
    /// 464 is 1.09. Parm0 is what the shrine init function switches on: 1 health, 2 mana, 3 random.
    /// An unknown size is a hard failure rather than a guess.
    /// </summary>
    private static readonly Dictionary<int, int> ParmOffsetByRecordSize = new() { [464] = 392 };

    public static (ShrineTable Table, IReadOnlyList<string> Notes) Read(string gameDirectory)
    {
        var notes = new List<string>();
        var (archive, raw) = ReadRawTable(gameDirectory, notes);
        var table = Decode(raw, archive, gameDirectory, notes);
        return (table, notes);
    }

    internal static ShrineTable Decode(byte[] raw, string archive, string gameDirectory, List<string> notes)
    {
        if (raw.Length < 4)
        {
            throw new InvalidDataException($"{ObjectsPath} in {archive} is {raw.Length} bytes, too short to hold a record count.");
        }

        var recordCount = BitConverter.ToInt32(raw, 0);
        if (recordCount <= 0 || (raw.Length - 4) % recordCount != 0)
        {
            throw new InvalidDataException(
                $"{ObjectsPath} in {archive} declares {recordCount} records but has {raw.Length - 4} bytes of records, which does not divide evenly.");
        }

        var recordSize = (raw.Length - 4) / recordCount;
        if (!ParmOffsetByRecordSize.TryGetValue(recordSize, out var parmOffset))
        {
            throw new InvalidDataException(
                $"{ObjectsPath} in {archive} has an unrecognised record size of {recordSize} bytes. "
                + "The Parm0 offset is only known for "
                + string.Join(", ", ParmOffsetByRecordSize.Keys)
                + ". Locate the offset for this version before trusting the output.");
        }

        var entries = new List<ShrineEntry>();
        for (var i = 0; i < recordCount; i++)
        {
            var start = 4 + i * recordSize;
            var name = ReadName(raw, start, i, archive);
            if (!name.Contains("shrine", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parm0 = BitConverter.ToInt32(raw, start + parmOffset);
            if (parm0 < (int)ShrineKind.Health || parm0 > (int)ShrineKind.Random)
            {
                throw new InvalidDataException(
                    $"Record {i} ({name}) in {archive} has Parm0 {parm0}, outside the 1..3 a shrine can have, so the Parm0 offset is wrong for this version.");
            }

            entries.Add(new ShrineEntry { ClassId = i, Name = name, Kind = (ShrineKind)parm0 });
        }

        var source = new ShrineSource
        {
            GameDirectory = gameDirectory,
            Archive = archive,
            File = ObjectsPath,
            Sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            RecordCount = recordCount,
            RecordSize = recordSize,
            ParmOffset = parmOffset,
            ExtractedUtc = DateTime.UtcNow
        };

        notes.Add($"{recordCount} objects, {recordSize} byte records, Parm0 at +{parmOffset}, {entries.Count} shrines");
        return ShrineTable.FromEntries(entries, source);
    }

    private static (string Archive, byte[] Raw) ReadRawTable(string gameDirectory, List<string> notes)
    {
        foreach (var archive in ArchivePriority)
        {
            var path = Path.Combine(gameDirectory, archive);
            if (!File.Exists(path))
            {
                notes.Add($"{archive} not present, skipped");
                continue;
            }

            using var mpq = new MpqArchive(path);
            if (!mpq.FileExists(ObjectsPath))
            {
                notes.Add($"{archive} has no {ObjectsPath}, skipped");
                continue;
            }

            using var stream = mpq.OpenFile(ObjectsPath);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (archive, buffer.ToArray());
        }

        throw new FileNotFoundException(
            $"None of {string.Join(", ", ArchivePriority)} in '{gameDirectory}' contains {ObjectsPath}.");
    }

    /// <summary>
    /// The Name string sits at the front of the record. A record that does not start with
    /// NUL-terminated printable ASCII means the layout moved, which is the cheapest way to catch a
    /// version the offset table does not really cover. Blank names are placeholder rows and are fine.
    /// </summary>
    private static string ReadName(byte[] raw, int start, int classId, string archive)
    {
        var length = 0;
        while (length < NameLength && raw[start + length] != 0)
        {
            length++;
        }

        if (length == NameLength)
        {
            throw new InvalidDataException(
                $"Record {classId} in {archive} does not begin with a NUL-terminated name, so the record layout is not the one this reader knows.");
        }

        for (var i = 0; i < length; i++)
        {
            if (raw[start + i] < 0x20 || raw[start + i] > 0x7e)
            {
                throw new InvalidDataException(
                    $"Record {classId} in {archive} has a non-printable byte in its name field, so the record layout is not the one this reader knows.");
            }
        }

        return Encoding.ASCII.GetString(raw, start, length);
    }
}
