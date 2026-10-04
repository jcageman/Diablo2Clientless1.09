using D2NG.Core.D2GS.Enums;
using MpqLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace D2NG.Core.ItemData;

/// <summary>
/// Reads the item affix tables out of a Diablo 2 install.
///
/// Only the compiled .bin files in patch_d2.mpq are trusted. The readable .txt files in d2exp.mpq
/// are an older revision: itemtypes.txt has 92 rows against the bin's 104, armor.txt is 74 bases
/// short, and affix ranges and levels differ ("Sturdy" is 20-30% at level 4 in the txt and 10-20%
/// at level 1 in the bin). The layouts below are 1.09's and differ from the 1.10 structs D2MOO
/// documents: item type references are single bytes, and cubemain keeps its item strings verbatim.
/// Any other record size fails rather than decoding into plausible-looking nonsense.
/// </summary>
public static class ItemAffixReader
{
    public static readonly string[] ArchivePriority = ["patch_d2.mpq", "d2exp.mpq", "d2data.mpq"];

    private const string ExcelDirectory = @"data\global\excel\";

    internal const int PropertySize = 16;
    internal const int ItemTypeSize = 232;
    internal const int AffixSize = 132;
    internal const int BaseItemSize = 564;
    internal const int CubeRecipeSize = 344;

    public static (ItemAffixTable Table, IReadOnlyList<string> Notes) Read(string gameDirectory)
    {
        var notes = new List<string>();
        var table = new ItemAffixTable();

        byte[] Load(string name, int recordSize)
        {
            var file = ExcelDirectory + name;
            var (archive, raw) = ReadRawTable(gameDirectory, file);
            var count = RecordCount(raw, file, recordSize);
            table.Sources.Add(new ItemAffixSourceFile
            {
                File = file,
                Archive = archive,
                Sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
                RecordCount = count,
                RecordSize = recordSize,
                ExtractedUtc = DateTime.UtcNow
            });
            notes.Add($"{file}: {count} records from {archive}");
            return raw;
        }

        var properties = DecodeProperties(Load("properties.bin", PropertySize), "properties.bin");
        table.ItemTypes = DecodeItemTypes(Load("itemtypes.bin", ItemTypeSize), "itemtypes.bin");
        var typeCodes = table.ItemTypes.Select(t => t.Code).ToList();

        table.BaseItems.AddRange(DecodeBaseItems(Load("weapons.bin", BaseItemSize), "weapons.bin", BaseItemKind.Weapon, typeCodes));
        table.BaseItems.AddRange(DecodeBaseItems(Load("armor.bin", BaseItemSize), "armor.bin", BaseItemKind.Armor, typeCodes));
        table.BaseItems.AddRange(DecodeBaseItems(Load("misc.bin", BaseItemSize), "misc.bin", BaseItemKind.Misc, typeCodes));

        table.Affixes.AddRange(DecodeAffixes(Load("magicprefix.bin", AffixSize), "magicprefix.bin", AffixKind.Prefix, properties, typeCodes));
        table.Affixes.AddRange(DecodeAffixes(Load("magicsuffix.bin", AffixSize), "magicsuffix.bin", AffixKind.Suffix, properties, typeCodes));
        table.Affixes.AddRange(DecodeAffixes(Load("automagic.bin", AffixSize), "automagic.bin", AffixKind.AutoMagic, properties, typeCodes));

        table.CubeRecipes = DecodeCubeRecipes(Load("cubemain.bin", CubeRecipeSize), "cubemain.bin", properties);

        return (table, notes);
    }

    /// <summary>1.09's properties.bin is nothing but the property codes, NUL padded.</summary>
    internal static List<string> DecodeProperties(byte[] raw, string file)
    {
        var count = RecordCount(raw, file, PropertySize);
        return Enumerable.Range(0, count)
            .Select(i => ReadString(raw, 4 + i * PropertySize, PropertySize, file, i))
            .ToList();
    }

    internal static List<ItemTypeEntry> DecodeItemTypes(byte[] raw, string file)
    {
        var count = RecordCount(raw, file, ItemTypeSize);
        var codes = Enumerable.Range(0, count)
            .Select(i => ReadString(raw, 4 + i * ItemTypeSize, 4, file, i).Trim())
            .ToList();

        var types = new List<ItemTypeEntry>();
        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * ItemTypeSize;
            types.Add(new ItemTypeEntry
            {
                Index = i,
                Code = codes[i],
                Equivalent1 = TypeReference(raw[start + 0x04], codes, file, i),
                Equivalent2 = TypeReference(raw[start + 0x05], codes, file, i),
                AlwaysMagic = raw[start + 0x18] != 0,
                CanBeRare = raw[start + 0x19] != 0,
                AlwaysNormal = raw[start + 0x1A] != 0,
                Charm = raw[start + 0x1B] != 0,
                MaxSockets = Math.Max(raw[start + 0x1E], Math.Max(raw[start + 0x1F], raw[start + 0x20])),
                StaffMods = ClassReference(raw[start + 0x23]),
                Class = ClassReference(raw[start + 0x25])
            });
        }

        return types;
    }

    internal static List<BaseItemEntry> DecodeBaseItems(byte[] raw, string file, BaseItemKind kind, IReadOnlyList<string> typeCodes)
    {
        var count = RecordCount(raw, file, BaseItemSize);
        var items = new List<BaseItemEntry>();
        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * BaseItemSize;
            items.Add(new BaseItemEntry
            {
                Kind = kind,
                Name = ReadString(raw, start, 64, file, i),
                Code = ReadString(raw, start + 0x144, 4, file, i).Trim(),
                Version = BitConverter.ToUInt16(raw, start + 0x190),
                Level = raw[start + 0x197],
                Type = TypeReference(raw[start + 0x1B3], typeCodes, file, i),
                Type2 = TypeReference(raw[start + 0x1B4], typeCodes, file, i),
                LevelRequirement = raw[start + 0x1CC]
            });
        }

        return items;
    }

    internal static List<AffixEntry> DecodeAffixes(
        byte[] raw, string file, AffixKind kind, IReadOnlyList<string> properties, IReadOnlyList<string> typeCodes)
    {
        var count = RecordCount(raw, file, AffixSize);
        var affixes = new List<AffixEntry>();
        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * AffixSize;
            var affix = new AffixEntry
            {
                Kind = kind,
                Index = i,
                Name = ReadString(raw, start, 32, file, i),
                Version = BitConverter.ToUInt16(raw, start + 0x22),
                Spawnable = raw[start + 0x54] != 0,
                Level = BitConverter.ToInt32(raw, start + 0x58),
                Group = BitConverter.ToInt32(raw, start + 0x5C),
                MaxLevel = BitConverter.ToInt32(raw, start + 0x60),
                Rare = raw[start + 0x64] != 0,
                LevelRequirement = raw[start + 0x65],
                ClassSpecific = ClassReference(raw[start + 0x66]),
                Frequency = raw[start + 0x75]
            };

            for (var mod = 0; mod < 3; mod++)
            {
                var at = start + 0x24 + mod * 16;
                var property = BitConverter.ToInt32(raw, at);
                if (property < 0)
                {
                    continue;
                }

                affix.Mods.Add(new PropertyRange
                {
                    Property = PropertyReference(property, properties, file, i),
                    Param = BitConverter.ToInt32(raw, at + 4),
                    Min = BitConverter.ToInt32(raw, at + 8),
                    Max = BitConverter.ToInt32(raw, at + 12)
                });
            }

            for (var slot = 0; slot < 7; slot++)
            {
                if (TypeReference(raw[start + 0x69 + slot], typeCodes, file, i) is { } type)
                {
                    affix.ItemTypes.Add(type);
                }
            }

            for (var slot = 0; slot < 5; slot++)
            {
                if (TypeReference(raw[start + 0x70 + slot], typeCodes, file, i) is { } type)
                {
                    affix.ExcludedItemTypes.Add(type);
                }
            }

            affixes.Add(affix);
        }

        return affixes;
    }

    internal static List<CubeRecipeEntry> DecodeCubeRecipes(byte[] raw, string file, IReadOnlyList<string> properties)
    {
        var count = RecordCount(raw, file, CubeRecipeSize);
        var recipes = new List<CubeRecipeEntry>();
        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * CubeRecipeSize;
            var recipe = new CubeRecipeEntry
            {
                Index = i,
                Enabled = raw[start] != 0,
                Class = ClassReference(raw[start + 0x05]),
                Version = BitConverter.ToUInt16(raw, start + 0x08),
                Output = Unquote(ReadString(raw, start + 0xEC, 32, file, i)),
                PlayerLevelPercent = raw[start + 0x10E],
                ItemLevelPercent = raw[start + 0x10F]
            };

            for (var input = 0; input < raw[start + 0x06] && input < 7; input++)
            {
                recipe.Inputs.Add(Unquote(ReadString(raw, start + 0x0A + input * 32, 32, file, i)));
            }

            for (var mod = 0; mod < 5; mod++)
            {
                var at = start + 0x11C + mod * 12;
                var property = BitConverter.ToInt32(raw, at);
                if (property < 0)
                {
                    continue;
                }

                recipe.Mods.Add(new PropertyRange
                {
                    Property = PropertyReference(property, properties, file, i),
                    Param = BitConverter.ToUInt16(raw, at + 4),
                    Min = BitConverter.ToInt16(raw, at + 6),
                    Max = BitConverter.ToInt16(raw, at + 8),
                    Chance = BitConverter.ToUInt16(raw, at + 10)
                });
            }

            recipes.Add(recipe);
        }

        return recipes;
    }

    private static int RecordCount(byte[] raw, string file, int recordSize)
    {
        if (raw.Length < 4)
        {
            throw new InvalidDataException($"{file} is {raw.Length} bytes, too short to hold a record count.");
        }

        var count = BitConverter.ToInt32(raw, 0);
        if (count < 0 || 4 + (long)count * recordSize != raw.Length)
        {
            throw new InvalidDataException(
                $"{file} declares {count} records in {raw.Length - 4} bytes, which is not the {recordSize} byte records of the 1.09 layout this reader knows.");
        }

        return count;
    }

    /// <summary>
    /// Type references are row numbers into itemtypes. Row 0 is the blank "None" type and 0xFF fills
    /// the slots a table had no column for, so both mean none. Anything past the end means the
    /// offset is wrong for this version.
    /// </summary>
    private static string TypeReference(byte value, IReadOnlyList<string> typeCodes, string file, int record)
    {
        if (value == 0 || value == 0xFF)
        {
            return null;
        }

        if (value >= typeCodes.Count)
        {
            throw new InvalidDataException(
                $"Record {record} of {file} refers to item type {value}, past the {typeCodes.Count} rows of itemtypes, so the layout is not the one this reader knows.");
        }

        return string.IsNullOrEmpty(typeCodes[value]) ? null : typeCodes[value];
    }

    private static string PropertyReference(int value, IReadOnlyList<string> properties, string file, int record)
    {
        if (value >= properties.Count)
        {
            throw new InvalidDataException(
                $"Record {record} of {file} refers to property {value}, past the {properties.Count} rows of properties, so the layout is not the one this reader knows.");
        }

        return properties[value];
    }

    private static CharacterClass? ClassReference(byte value)
        => value <= (byte)CharacterClass.Assassin ? (CharacterClass)value : null;

    private static string Unquote(string value) => value.Trim('"');

    /// <summary>
    /// A NUL-terminated printable string at the given offset. A field that is not one means the
    /// layout moved, which is the cheapest check that a record size really is the version assumed.
    /// </summary>
    private static string ReadString(byte[] raw, int start, int length, string file, int record)
    {
        var end = 0;
        while (end < length && raw[start + end] != 0)
        {
            if (raw[start + end] < 0x20 || raw[start + end] > 0x7e)
            {
                throw new InvalidDataException(
                    $"Record {record} of {file} has a non-printable byte in a string field, so the layout is not the one this reader knows.");
            }

            end++;
        }

        return Encoding.ASCII.GetString(raw, start, end);
    }

    private static (string Archive, byte[] Raw) ReadRawTable(string gameDirectory, string file)
    {
        foreach (var archive in ArchivePriority)
        {
            var path = Path.Combine(gameDirectory, archive);
            if (!File.Exists(path))
            {
                continue;
            }

            using var mpq = new MpqArchive(path);
            if (!mpq.FileExists(file))
            {
                continue;
            }

            using var stream = mpq.OpenFile(file);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (archive, buffer.ToArray());
        }

        throw new FileNotFoundException($"None of {string.Join(", ", ArchivePriority)} in '{gameDirectory}' contains {file}.");
    }
}
