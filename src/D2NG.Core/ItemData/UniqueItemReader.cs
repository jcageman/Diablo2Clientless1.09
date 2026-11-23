using System;
using System.Collections.Generic;

namespace D2NG.Core.ItemData;

/// <summary>
/// Decodes uniqueitems.bin and setitems.bin at 1.09's layouts, found by matching the txt columns and
/// checked against the realm's armory: every unique id there names the record with its base code.
/// </summary>
public static class UniqueItemReader
{
    internal const int UniqueItemSize = 228;
    internal const int SetSize = 1332;

    private const int SetItemStart = 140;
    private const int SetItemSize = 60;
    private const int ItemsPerSet = 6;

    /// <summary>A unique's ten properties start at +68, four ints each, as for set items; Lidless Wall's 80-130 enhanced defense sits there.</summary>
    private const int UniquePropertyStart = 68;
    private const int UniquePropertyCount = 10;

    internal static List<UniqueItemEntry> DecodeUniqueItems(byte[] raw, string file, IReadOnlyList<string> properties = null)
    {
        var count = ItemAffixReader.RecordCount(raw, file, UniqueItemSize);
        var items = new List<UniqueItemEntry>();
        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * UniqueItemSize;
            items.Add(new UniqueItemEntry
            {
                Index = i,
                Code = ItemAffixReader.ReadString(raw, start, 4, file, i).Trim(),
                Name = ItemAffixReader.ReadString(raw, start + 6, 32, file, i),
                Level = BitConverter.ToInt32(raw, start + 44),
                LevelRequirement = BitConverter.ToInt32(raw, start + 48),
                CostMultiply = BitConverter.ToInt32(raw, start + 52),
                CostAdd = BitConverter.ToInt32(raw, start + 56),
                Properties = properties == null ? [] : Properties(raw, start + UniquePropertyStart, UniquePropertyCount, properties, file, i)
            });
        }

        return items;
    }

    /// <summary>
    /// Offsets inside a 1.09 set record, found by matching every property of setitems.txt: 443 of the 448 property
    /// lists agree, the other five are the patch's own revisions. Each item's properties sit in a block of seven - two
    /// of its own, then five it gains in the set - and the set's bonuses follow: four for two to five items worn, six
    /// for the full set. A property is four ints: its index in properties.bin, param, min and max.
    /// </summary>
    private const int ItemPropertyStart = 500;
    private const int ItemPropertyBlock = 112;
    private const int PartialStart = 1172;
    private const int FullStart = 1236;
    private const int PropertySize = 16;

    internal static (List<SetItemEntry> Items, List<SetEntry> Sets) DecodeSetItems(byte[] raw, string file, IReadOnlyList<string> properties)
    {
        var count = ItemAffixReader.RecordCount(raw, file, SetSize);
        var items = new List<SetItemEntry>();
        var sets = new List<SetEntry>();
        for (var set = 0; set < count; set++)
        {
            var start = 4 + set * SetSize;
            var setName = ItemAffixReader.ReadString(raw, start, 32, file, set);
            var entry = new SetEntry { Index = set, Name = setName, Full = Properties(raw, start + FullStart, 6, properties, file, set) };
            for (var worn = 2; worn <= 5; worn++)
            {
                var partial = Properties(raw, start + PartialStart + (worn - 2) * PropertySize, 1, properties, file, set);
                if (partial.Count > 0)
                {
                    entry.Partial[worn] = partial;
                }
            }

            sets.Add(entry);
            for (var slot = 0; slot < ItemsPerSet; slot++)
            {
                var item = start + SetItemStart + slot * SetItemSize;
                var code = ItemAffixReader.ReadString(raw, item, 4, file, set).Trim();
                if (code.Length == 0)
                {
                    continue;
                }

                items.Add(new SetItemEntry
                {
                    SetIndex = set,
                    SetName = setName,
                    Code = code,
                    Name = ItemAffixReader.ReadString(raw, item + 25, 32, file, set),
                    LevelRequirement = BitConverter.ToInt32(raw, item + 12),
                    CostMultiply = BitConverter.ToInt32(raw, item + 16),
                    CostAdd = BitConverter.ToInt32(raw, item + 20),
                    Properties = Properties(raw, start + ItemPropertyStart + slot * ItemPropertyBlock, 2, properties, file, set),
                    SetProperties = Properties(raw, start + ItemPropertyStart + slot * ItemPropertyBlock + 2 * PropertySize, 5, properties, file, set, out var worn),
                    SetPropertiesWorn = worn
                });
                entry.ItemCount++;
            }
        }

        return (items, sets);
    }

    private static List<PropertyRange> Properties(byte[] raw, int at, int count, IReadOnlyList<string> properties, string file, int record)
        => Properties(raw, at, count, properties, file, record, out _);

    /// <summary><paramref name="worn"/> holds, per property kept, the set items that must be worn for it: two for the first slot, then one more per slot.</summary>
    private static List<PropertyRange> Properties(byte[] raw, int at, int count, IReadOnlyList<string> properties, string file, int record, out List<int> worn)
    {
        worn = [];
        var ranges = new List<PropertyRange>();
        for (var i = 0; i < count; i++)
        {
            var offset = at + i * PropertySize;
            var property = BitConverter.ToInt32(raw, offset);
            var param = BitConverter.ToInt32(raw, offset + 4);
            var min = BitConverter.ToInt32(raw, offset + 8);
            var max = BitConverter.ToInt32(raw, offset + 12);
            if (property < 0 || property >= properties.Count || (param == 0 && min == 0 && max == 0))
            {
                continue;
            }

            ranges.Add(new PropertyRange { Property = properties[property], Param = param, Min = min, Max = max });
            worn.Add(i + 2);
        }

        return ranges;
    }
}
