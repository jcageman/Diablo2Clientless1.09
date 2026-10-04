using MpqLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace D2NG.Core.MonsterData;

/// <summary>
/// Reads monster resistances out of a Diablo 2 install.
///
/// The readable <c>monstats.txt</c> in d2exp.mpq is the wrong source twice over: patch_d2.mpq ships
/// a compiled <c>monstats.bin</c> that overrides it (they disagree on Hell physical resistance for
/// 502 of 575 monsters), and the txt carries separator rows that produce no record, so its line
/// numbers stop matching class ids around row 417. The bin has no separator rows, and its record
/// index is the class id the server sends.
/// </summary>
public static class MonstatsReader
{
    public const string MonstatsPath = @"data\global\excel\monstats.bin";

    /// <summary>Archives in the order the game resolves them. First hit wins.</summary>
    public static readonly string[] ArchivePriority = ["patch_d2.mpq", "d2exp.mpq", "d2data.mpq"];

    /// <summary>
    /// Offset of the 18 signed resistance bytes, keyed by record size, since the record layout is
    /// version specific. 864 is 1.09. An unknown size is a hard failure rather than a guess,
    /// because a wrong offset decodes into plausible-looking nonsense.
    /// </summary>
    private static readonly Dictionary<int, int> ResistOffsetByRecordSize = new() { [864] = 729 };

    public static (MonsterResistTable Table, IReadOnlyList<string> Notes) Read(string gameDirectory)
    {
        var notes = new List<string>();
        var (archive, raw) = ReadRawTable(gameDirectory, notes);
        var table = Decode(raw, archive, gameDirectory, notes);
        return (table, notes);
    }

    internal static MonsterResistTable Decode(byte[] raw, string archive, string gameDirectory, List<string> notes)
    {
        if (raw.Length < 4)
        {
            throw new InvalidDataException($"{MonstatsPath} in {archive} is {raw.Length} bytes, too short to hold a record count.");
        }

        var recordCount = BitConverter.ToInt32(raw, 0);
        if (recordCount <= 0 || (raw.Length - 4) % recordCount != 0)
        {
            throw new InvalidDataException(
                $"{MonstatsPath} in {archive} declares {recordCount} records but has {raw.Length - 4} bytes of records, which does not divide evenly.");
        }

        var recordSize = (raw.Length - 4) / recordCount;
        if (!ResistOffsetByRecordSize.TryGetValue(recordSize, out var resistOffset))
        {
            throw new InvalidDataException(
                $"{MonstatsPath} in {archive} has an unrecognised record size of {recordSize} bytes. "
                + "The resistance offset is only known for "
                + string.Join(", ", ResistOffsetByRecordSize.Keys)
                + ". Locate the offset for this version before trusting the output.");
        }

        var entries = new List<MonsterResistEntry>(recordCount);
        for (var i = 0; i < recordCount; i++)
        {
            entries.Add(ReadRecord(raw, 4 + i * recordSize, resistOffset, i, archive));
        }

        var source = new MonsterResistSource
        {
            GameDirectory = gameDirectory,
            Archive = archive,
            File = MonstatsPath,
            Sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            RecordCount = recordCount,
            RecordSize = recordSize,
            ResistOffset = resistOffset,
            ExtractedUtc = DateTime.UtcNow
        };

        notes.Add($"{recordCount} monsters, {recordSize} byte records, resistances at +{resistOffset}");
        return MonsterResistTable.FromEntries(entries, source);
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
            if (!mpq.FileExists(MonstatsPath))
            {
                notes.Add($"{archive} has no {MonstatsPath}, skipped");
                continue;
            }

            using var stream = mpq.OpenFile(MonstatsPath);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (archive, buffer.ToArray());
        }

        throw new FileNotFoundException(
            $"None of {string.Join(", ", ArchivePriority)} in '{gameDirectory}' contains {MonstatsPath}.");
    }

    private static MonsterResistEntry ReadRecord(byte[] raw, int start, int resistOffset, int classId, string archive)
    {
        return new MonsterResistEntry
        {
            ClassId = classId,
            Class = ReadClassName(raw, start, classId, archive),
            Physical = ReadResists(raw, start + resistOffset + 0 * 3, classId, archive),
            Magic = ReadResists(raw, start + resistOffset + 1 * 3, classId, archive),
            Fire = ReadResists(raw, start + resistOffset + 2 * 3, classId, archive),
            Lightning = ReadResists(raw, start + resistOffset + 3 * 3, classId, archive),
            Cold = ReadResists(raw, start + resistOffset + 4 * 3, classId, archive),
            Poison = ReadResists(raw, start + resistOffset + 5 * 3, classId, archive)
        };
    }

    /// <summary>
    /// The Class string sits at the front of the record. Reading it is not just for diagnostics: a
    /// record that does not start with printable ASCII means the layout moved, which is the cheapest
    /// way to catch a version the offset table does not really cover.
    /// </summary>
    private static string ReadClassName(byte[] raw, int start, int classId, string archive)
    {
        var length = 0;
        while (length < 32 && raw[start + length] != 0)
        {
            length++;
        }

        // A wholly blank record is a placeholder row, of which 1.09 has a handful. Only a name
        // field with no terminator at all means the layout moved.
        if (length == 32)
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

    private static int[] ReadResists(byte[] raw, int start, int classId, string archive)
    {
        var values = new int[3];
        for (var i = 0; i < values.Length; i++)
        {
            // Signed: Andariel is fire -50 on every difficulty.
            values[i] = (sbyte)raw[start + i];
            if (values[i] < -100 || values[i] > 200)
            {
                throw new InvalidDataException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Record {0} in {1} decodes a resistance of {2}, outside the plausible -100..200, so the resistance offset is wrong for this version.",
                        classId,
                        archive,
                        values[i]));
            }
        }

        return values;
    }
}
