using MpqLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MpqData;

/// <summary>
/// Prints the shape and the readable strings of any compiled excel table in the game archives.
///
/// Used to work out whether a table can answer a question before any code is written against it.
/// The layouts are version specific and undocumented, so the alternative is guessing an offset,
/// and a wrong offset decodes into plausible-looking nonsense rather than failing.
///
///   MpqData --gamedir "C:\Diablo II 1.09" --probe "data\global\excel\superuniques.bin"
/// </summary>
internal static class ProbeBin
{
    public static int Run(string gameDirectory, string binPath, string filter)
    {
        var found = false;
        foreach (var archive in D2NG.Core.MonsterData.MonstatsReader.ArchivePriority)
        {
            var path = Path.Combine(gameDirectory, archive);
            if (!File.Exists(path))
            {
                continue;
            }

            using var mpq = new MpqArchive(path);
            if (!mpq.FileExists(binPath))
            {
                Console.WriteLine($"{archive}: no {binPath}");
                continue;
            }

            using var stream = mpq.OpenFile(binPath);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var raw = buffer.ToArray();
            found = true;

            Console.WriteLine($"{archive}: {binPath} is {raw.Length} bytes");
            if (raw.Length < 4)
            {
                continue;
            }

            var recordCount = BitConverter.ToInt32(raw, 0);
            if (recordCount <= 0 || (raw.Length - 4) % recordCount != 0)
            {
                Console.WriteLine($"  declares {recordCount} records, which does not divide {raw.Length - 4} bytes evenly");
                continue;
            }

            var recordSize = (raw.Length - 4) / recordCount;
            Console.WriteLine($"  {recordCount} records of {recordSize} bytes");

            for (var i = 0; i < recordCount; i++)
            {
                var start = 4 + i * recordSize;
                var strings = ReadStrings(raw, start, recordSize);
                var line = string.Join(" | ", strings);
                if (filter != null && !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Console.WriteLine($"  [{i,3}] {line}");
                if (filter != null)
                {
                    // The interesting record, so show the small integers too: a base class id or a
                    // modifier id is what one table needs to point at another.
                    Console.WriteLine($"        words: {string.Join(" ", SmallWords(raw, start, recordSize))}");
                }
            }

            break;
        }

        if (!found)
        {
            Console.Error.WriteLine($"No archive in '{gameDirectory}' contains {binPath}.");
            return 1;
        }

        return 0;
    }

    /// <summary>Runs of printable ASCII, which is where the table keeps its names and keys.</summary>
    private static List<string> ReadStrings(byte[] raw, int start, int length)
    {
        var strings = new List<string>();
        var current = new StringBuilder();
        for (var i = start; i < start + length && i < raw.Length; i++)
        {
            var c = raw[i];
            if (c >= 0x20 && c < 0x7F)
            {
                current.Append((char)c);
                continue;
            }

            if (current.Length >= 3)
            {
                strings.Add(current.ToString());
            }

            current.Clear();
        }

        if (current.Length >= 3)
        {
            strings.Add(current.ToString());
        }

        return strings;
    }

    /// <summary>Every non-zero 16 bit value with its offset, so a cross reference can be spotted.</summary>
    private static IEnumerable<string> SmallWords(byte[] raw, int start, int length)
    {
        for (var i = 0; i + 1 < length; i += 2)
        {
            var value = BitConverter.ToUInt16(raw, start + i);
            if (value != 0 && value < 4096)
            {
                yield return $"@{i}={value}";
            }
        }
    }
}
