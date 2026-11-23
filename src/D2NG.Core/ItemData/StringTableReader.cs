using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace D2NG.Core.ItemData;

/// <summary>
/// Reads the game's string tables, which turn the keys the excel bins use (a unique's row name, a base's code)
/// into the names the game shows: uniqueitems.bin calls Bonesnap "Bonesob" and the ring base "ring".
/// </summary>
public static class StringTableReader
{
    /// <summary>Later tables override earlier ones, as the game loads them.</summary>
    public static readonly string[] Tables =
    [
        @"data\local\lng\eng\string.tbl",
        @"data\local\lng\eng\expansionstring.tbl",
        @"data\local\lng\eng\patchstring.tbl",
    ];

    private const int HeaderSize = 21;
    private const int NodeSize = 17;

    public static Dictionary<string, string> Read(string gameDirectory, List<string> notes)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in Tables)
        {
            try
            {
                var (archive, raw) = ItemAffixReader.ReadRawTable(gameDirectory, table);
                var count = Decode(raw, strings);
                notes.Add($"{table}: {count} strings from {archive}");
            }
            catch (FileNotFoundException)
            {
                notes.Add($"{table}: not found, its names stay as keys");
            }
        }

        return strings;
    }

    /// <summary>
    /// A 21 byte header (crc, element count, hash table size, version, data start, max tries, file size), the element
    /// indices, then the hash nodes: used flag, index, hash, key offset, value offset and value length.
    /// </summary>
    internal static int Decode(byte[] raw, Dictionary<string, string> into)
    {
        if (raw.Length < HeaderSize)
        {
            throw new InvalidDataException($"String table of {raw.Length} bytes is shorter than its header");
        }

        var elements = BitConverter.ToUInt16(raw, 2);
        var hashSize = BitConverter.ToInt32(raw, 4);
        var nodes = HeaderSize + elements * 2;
        if (nodes + hashSize * NodeSize > raw.Length)
        {
            throw new InvalidDataException($"String table hash nodes run past the end of the file ({hashSize} nodes)");
        }

        var count = 0;
        for (var i = 0; i < hashSize; i++)
        {
            var node = nodes + i * NodeSize;
            if (raw[node] == 0)
            {
                continue;
            }

            var key = ReadString(raw, BitConverter.ToInt32(raw, node + 7));
            var value = ReadString(raw, BitConverter.ToInt32(raw, node + 11));
            if (key.Length > 0)
            {
                into[key] = value;
                count++;
            }
        }

        return count;
    }

    private static string ReadString(byte[] raw, int offset)
    {
        if (offset <= 0 || offset >= raw.Length)
        {
            return "";
        }

        var end = Array.IndexOf(raw, (byte)0, offset);
        return Encoding.Latin1.GetString(raw, offset, (end < 0 ? raw.Length : end) - offset);
    }
}
