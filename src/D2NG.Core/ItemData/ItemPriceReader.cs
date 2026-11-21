using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace D2NG.Core.ItemData;

/// <summary>
/// Reads the price columns out of patch_d2.mpq's compiled tables. The offsets are 1.09's, found by
/// matching the d2exp txt columns against the bin records; the bins win where the two disagree
/// (npc.bin pays up to 35000 in Hell where npc.txt says 25000, and itemstatcost.bin prices
/// enhanced damage at half the txt's multiplier). Record sizes are checked by
/// <see cref="ItemAffixReader.RecordCount"/>, so another version fails instead of misreading.
/// </summary>
public static class ItemPriceReader
{
    internal const int StatSize = 36;
    internal const int SkillSize = 360;
    internal const int UniqueSize = 228;
    internal const int NpcSize = 136;
    internal const int BookSize = 68;
    internal const int SetSize = 1332;

    private const string ExcelDirectory = @"data\global\excel\";

    public static (ItemPriceTable Table, IReadOnlyList<string> Notes) Read(string gameDirectory)
    {
        var notes = new List<string>();
        var table = new ItemPriceTable();

        byte[] Load(string name, int recordSize)
        {
            var file = ExcelDirectory + name;
            var (archive, raw) = ItemAffixReader.ReadRawTable(gameDirectory, file);
            var count = ItemAffixReader.RecordCount(raw, file, recordSize);
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

        table.ItemTypes = DecodeItemTypes(Load("itemtypes.bin", ItemAffixReader.ItemTypeSize), "itemtypes.bin");
        var typeCodes = table.ItemTypes.Select(t => t.Code).ToList();

        table.Bases.AddRange(DecodeBases(Load("weapons.bin", ItemAffixReader.BaseItemSize), "weapons.bin", BaseItemKind.Weapon, typeCodes));
        table.Bases.AddRange(DecodeBases(Load("armor.bin", ItemAffixReader.BaseItemSize), "armor.bin", BaseItemKind.Armor, typeCodes));
        table.Bases.AddRange(DecodeBases(Load("misc.bin", ItemAffixReader.BaseItemSize), "misc.bin", BaseItemKind.Misc, typeCodes));
        table.Prefixes = DecodeAffixes(Load("magicprefix.bin", ItemAffixReader.AffixSize), "magicprefix.bin");
        table.Suffixes = DecodeAffixes(Load("magicsuffix.bin", ItemAffixReader.AffixSize), "magicsuffix.bin");
        table.Stats = DecodeStats(Load("itemstatcost.bin", StatSize), "itemstatcost.bin");
        table.Skills = DecodeSkills(Load("skills.bin", SkillSize), "skills.bin");
        table.Uniques = DecodeUniques(Load("uniqueitems.bin", UniqueSize), "uniqueitems.bin");
        table.SetItems = DecodeSetItems(Load("setitems.bin", SetSize), "setitems.bin");
        table.Vendors = DecodeVendors(Load("npc.bin", NpcSize), "npc.bin");
        table.Books = DecodeBooks(Load("books.bin", BookSize), "books.bin");
        return (table, notes);
    }

    internal static List<ItemTypePrice> DecodeItemTypes(byte[] raw, string file)
    {
        var types = ItemAffixReader.DecodeItemTypes(raw, file);
        return types.Select(type =>
        {
            var start = 4 + type.Index * ItemAffixReader.ItemTypeSize;
            return new ItemTypePrice
            {
                Code = type.Code,
                Equivalent1 = type.Equivalent1,
                Equivalent2 = type.Equivalent2,
                Repair = raw[start + 0x06] != 0,
                Quiver = BitConverter.ToUInt16(raw, start + 0x10) != 0,
                Throwable = raw[start + 0x14] != 0,
                StaffMods = type.StaffMods,
                Class = type.Class
            };
        }).ToList();
    }

    internal static List<BasePrice> DecodeBases(byte[] raw, string file, BaseItemKind kind, IReadOnlyList<string> typeCodes)
    {
        var count = ItemAffixReader.RecordCount(raw, file, ItemAffixReader.BaseItemSize);
        var bases = new List<BasePrice>();
        for (var i = 0; i < count; i++)
        {
            var start = 4 + i * ItemAffixReader.BaseItemSize;
            var type = raw[start + 0x1B3];
            bases.Add(new BasePrice
            {
                Code = ItemAffixReader.ReadString(raw, start + 0x144, 4, file, i).Trim(),
                Kind = kind,
                Type = type is 0 or 0xFF || type >= typeCodes.Count ? null : typeCodes[type],
                MaxDefense = BitConverter.ToInt32(raw, start + 0x168),
                Cost = BitConverter.ToInt32(raw, start + 0x178),
                MaxStack = BitConverter.ToInt32(raw, start + 0x180),
                Durability = raw[start + 0x1A7],
                NoDurability = raw[start + 0x1A8] != 0,
                Stackable = raw[start + 0x1BF] != 0
            });
        }

        return bases;
    }

    internal static List<AffixPrice> DecodeAffixes(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, ItemAffixReader.AffixSize);
        return Enumerable.Range(0, count).Select(i =>
        {
            var start = 4 + i * ItemAffixReader.AffixSize;
            return new AffixPrice
            {
                Index = i,
                Name = ItemAffixReader.ReadString(raw, start, 32, file, i),
                Multiply = BitConverter.ToInt32(raw, start + 0x7C),
                Add = BitConverter.ToInt32(raw, start + 0x80)
            };
        }).ToList();
    }

    internal static List<StatPrice> DecodeStats(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, StatSize);
        return Enumerable.Range(0, count).Select(i =>
        {
            var start = 4 + i * StatSize;
            return new StatPrice
            {
                Id = i,
                Multiply = BitConverter.ToInt32(raw, start + 0x08),
                Add = BitConverter.ToInt32(raw, start + 0x0C),
                Encode = raw[start + 0x20]
            };
        }).ToList();
    }

    internal static List<SkillPrice> DecodeSkills(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, SkillSize);
        return Enumerable.Range(0, count).Select(i =>
        {
            var start = 4 + i * SkillSize;
            return new SkillPrice
            {
                Id = i,
                CostMultiply = BitConverter.ToInt32(raw, start + 0x20),
                CostAdd = BitConverter.ToInt32(raw, start + 0x24)
            };
        }).ToList();
    }

    /// <summary>The record index is the unique id an item packet carries.</summary>
    internal static List<UniquePrice> DecodeUniques(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, UniqueSize);
        return Enumerable.Range(0, count).Select(i =>
        {
            var start = 4 + i * UniqueSize;
            return new UniquePrice
            {
                Index = i,
                Name = ItemAffixReader.ReadString(raw, start + 0x06, 32, file, i),
                CostMultiply = BitConverter.ToInt32(raw, start + 0x34),
                CostAdd = BitConverter.ToInt32(raw, start + 0x38)
            };
        }).ToList();
    }

    /// <summary>The set's six items are 60 byte blocks from 0x8C, each starting with its base code.</summary>
    internal static List<SetItemPrice> DecodeSetItems(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, SetSize);
        var items = new List<SetItemPrice>();
        for (var set = 0; set < count; set++)
        {
            for (var slot = 0; slot < 6; slot++)
            {
                var start = 4 + set * SetSize + 0x8C + slot * 60;
                var code = ItemAffixReader.ReadString(raw, start, 4, file, set).Trim();
                if (code.Length > 0)
                {
                    items.Add(new SetItemPrice
                    {
                        Set = set,
                        Code = code,
                        CostMultiply = BitConverter.ToInt32(raw, start + 0x10),
                        CostAdd = BitConverter.ToInt32(raw, start + 0x14)
                    });
                }
            }
        }

        return items;
    }

    internal static List<BookPrice> DecodeBooks(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, BookSize);
        return Enumerable.Range(0, count).Select(i =>
        {
            var start = 4 + i * BookSize;
            return new BookPrice
            {
                BookCode = ItemAffixReader.ReadString(raw, start + 0x1C, 4, file, i).Trim(),
                CostPerCharge = BitConverter.ToInt32(raw, start + 0x14)
            };
        }).Where(book => book.BookCode.Length > 0).ToList();
    }

    /// <summary>
    /// npc.bin keeps its three multipliers in the opposite order to npc.txt: what the vendor charges
    /// first, then what it pays, then what it charges to repair.
    /// </summary>
    internal static List<VendorPrice> DecodeVendors(byte[] raw, string file)
    {
        var count = ItemAffixReader.RecordCount(raw, file, NpcSize);
        return Enumerable.Range(0, count).Select(i =>
        {
            var start = 4 + i * NpcSize;
            return new VendorPrice
            {
                Name = ItemAffixReader.ReadString(raw, start, 64, file, i),
                SellMultiply = BitConverter.ToInt32(raw, start + 0x40),
                BuyMultiply = BitConverter.ToInt32(raw, start + 0x44),
                RepairMultiply = BitConverter.ToInt32(raw, start + 0x48),
                MaxBuy = [BitConverter.ToInt32(raw, start + 0x7C), BitConverter.ToInt32(raw, start + 0x80), BitConverter.ToInt32(raw, start + 0x84)]
            };
        }).ToList();
    }
}
