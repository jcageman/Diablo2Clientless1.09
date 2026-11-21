using D2NG.Core.D2GS.Act;
using MpqLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace D2NG.Core.LevelData;

/// <summary>
/// Reads the area monster levels out of a Diablo 2 install's compiled levels table. Its record index is the area id.
/// </summary>
public static class LevelsReader
{
    public const string LevelsPath = @"data\global\excel\levels.bin";

    public static readonly string[] ArchivePriority = ["patch_d2.mpq", "d2exp.mpq", "d2data.mpq"];

    /// <summary>
    /// Byte offsets of MonLvl1-3 and MonLvl1Ex-3Ex, keyed by record size; 712 is 1.09. Found by matching the Normal
    /// levels of nineteen act 1 areas, and checked: every classic Nightmare and Hell level is Normal plus 25 and 50.
    /// </summary>
    private static readonly Dictionary<int, (int Classic, int Expansion)> OffsetsByRecordSize = new() { [712] = (12, 15) };

    /// <summary>Areas whose Normal level is known, to refuse a layout the offsets do not fit.</summary>
    private static readonly (Area Area, int Normal)[] Checks = [(Area.BloodMoor, 1), (Area.CatacombsLevel3, 12), (Area.JailLevel1, 10)];

    public static (AreaLevelTable Table, IReadOnlyList<string> Notes) Read(string gameDirectory)
    {
        var notes = new List<string>();
        var (archive, raw) = ReadRawTable(gameDirectory, notes);
        var recordCount = raw.Length >= 4 ? BitConverter.ToInt32(raw, 0) : 0;
        if (recordCount <= 0 || (raw.Length - 4) % recordCount != 0)
        {
            throw new InvalidDataException($"{LevelsPath} in {archive} declares {recordCount} records, which does not divide {raw.Length - 4} bytes evenly.");
        }

        var recordSize = (raw.Length - 4) / recordCount;
        if (!OffsetsByRecordSize.TryGetValue(recordSize, out var offsets))
        {
            throw new InvalidDataException(
                $"{LevelsPath} in {archive} has {recordSize} byte records; the level offsets are only known for {string.Join(", ", OffsetsByRecordSize.Keys)}.");
        }

        var entries = new List<AreaLevels>();
        for (var i = 1; i < recordCount; i++)
        {
            var start = 4 + i * recordSize;
            int[] Row(int offset) => [raw[start + offset], raw[start + offset + 1], raw[start + offset + 2]];
            var classic = Row(offsets.Classic);
            var expansion = Row(offsets.Expansion);
            if (classic[0] == 0 && expansion[0] == 0)
            {
                continue;
            }

            entries.Add(new AreaLevels { Area = (Area)i, Name = ((Area)i).ToString(), Classic = classic, Expansion = expansion });
        }

        foreach (var (area, normal) in Checks)
        {
            var found = entries.Find(entry => entry.Area == area);
            if (found?.Classic[0] != normal)
            {
                throw new InvalidDataException(
                    $"{area} reads Normal level {found?.Classic[0]} in {archive}, not {normal}: the level offsets are wrong for this version.");
            }
        }

        notes.Add($"{recordCount} levels, {recordSize} byte records, {entries.Count} with monsters");
        var source = new AreaLevelSource
        {
            GameDirectory = gameDirectory,
            Archive = archive,
            File = LevelsPath,
            Sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            RecordCount = recordCount,
            RecordSize = recordSize,
            ExtractedUtc = DateTime.UtcNow
        };
        return (AreaLevelTable.FromEntries(entries, source), notes);
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
            if (!mpq.FileExists(LevelsPath))
            {
                notes.Add($"{archive} has no {LevelsPath}, skipped");
                continue;
            }

            using var stream = mpq.OpenFile(LevelsPath);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (archive, buffer.ToArray());
        }

        throw new FileNotFoundException($"None of {string.Join(", ", ArchivePriority)} in '{gameDirectory}' contains {LevelsPath}.");
    }
}
