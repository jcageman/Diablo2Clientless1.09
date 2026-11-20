using D2NG.Core.D2GS.Items;
using D2NG.Core.ItemData;
using System;
using System.Text;
using Xunit;

namespace D2NG.Core.Tests.ItemData;

public class ItemRequirementsTests
{
    private static void Put(byte[] raw, int at, int value) => BitConverter.GetBytes(value).CopyTo(raw, at);

    private static byte[] Records(int count, int size)
    {
        var raw = new byte[4 + count * size];
        BitConverter.GetBytes(count).CopyTo(raw, 0);
        return raw;
    }

    [Fact]
    public void AUniqueDecodesAtThe109Offsets()
    {
        var raw = Records(2, UniqueItemReader.UniqueItemSize);
        var s = 4 + UniqueItemReader.UniqueItemSize;
        Encoding.ASCII.GetBytes("rin ").CopyTo(raw, s);
        Encoding.ASCII.GetBytes("Pre The Stone of Jordan").CopyTo(raw, s + 6);
        Put(raw, s + 44, 39);
        Put(raw, s + 48, 0);
        Put(raw, s + 52, 5);
        Put(raw, s + 56, 5000);

        var unique = UniqueItemReader.DecodeUniqueItems(raw, "uniqueitems.bin")[1];

        Assert.Equal((1, "rin", "Pre The Stone of Jordan"), (unique.Index, unique.Code, unique.Name));
        Assert.Equal((39, 0, 5, 5000), (unique.Level, unique.LevelRequirement, unique.CostMultiply, unique.CostAdd));
        Assert.True(unique.Pre);
    }

    [Fact]
    public void ASetRecordHoldsItsItemsBySlot()
    {
        var raw = Records(1, UniqueItemReader.SetSize);
        Encoding.ASCII.GetBytes("Civerb's Vestments").CopyTo(raw, 4);
        Encoding.ASCII.GetBytes("lrg ").CopyTo(raw, 4 + 140);
        Encoding.ASCII.GetBytes("Civerb's Ward").CopyTo(raw, 4 + 165);
        Put(raw, 4 + 152, 9);
        Encoding.ASCII.GetBytes("amu ").CopyTo(raw, 4 + 200);
        Put(raw, 4 + 212, 11);
        Encoding.ASCII.GetBytes("    ").CopyTo(raw, 4 + 260);

        // A two-piece bonus of 10% life steal at the partial bonuses' first slot, as Sigon's carries.
        Put(raw, 4 + 1172, 1);
        Put(raw, 4 + 1172 + 8, 10);
        Put(raw, 4 + 1172 + 12, 10);

        var (items, sets) = UniqueItemReader.DecodeSetItems(raw, "setitems.bin", ["ac", "lifesteal"]);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, sets[0].ItemCount);
        Assert.Equal(("lifesteal", 10), (sets[0].Partial[2][0].Property, sets[0].Partial[2][0].Max));
        Assert.Equal((0, "Civerb's Vestments", "lrg", "Civerb's Ward", 9), (items[0].SetIndex, items[0].SetName, items[0].Code, items[0].Name, items[0].LevelRequirement));
        Assert.Equal(("amu", 11), (items[1].Code, items[1].LevelRequirement));
    }

    private static ItemRequirements Requirements()
    {
        var table = new ItemAffixTable();
        table.BaseItems.Add(new BaseItemEntry { Code = "ful", LevelRequirement = 0, RequiredStrength = 80 });
        table.BaseItems.Add(new BaseItemEntry { Code = "xpk", LevelRequirement = 22, RequiredStrength = 50 });
        table.UniqueItems.Add(new UniqueItemEntry { Index = 0, Code = "ful", LevelRequirement = 28 });
        table.UniqueItems.Add(new UniqueItemEntry { Index = 1, Code = "ful", Name = "Pre Goldskin", LevelRequirement = 0 });
        table.UniqueItems.Add(new UniqueItemEntry { Index = 2, Code = "xpk", LevelRequirement = 15 });
        table.SetItems.Add(new SetItemEntry { SetIndex = 3, Code = "ful", LevelRequirement = 20 });
        return new ItemRequirements(table);
    }

    [Theory]
    [InlineData(QualityType.Normal, "xpk", 0u, 0u, 22)]
    [InlineData(QualityType.Unique, "ful", 0u, 0u, 28)]
    [InlineData(QualityType.Unique, "ful", 1u, 0u, 0)]
    [InlineData(QualityType.Unique, "xpk", 2u, 0u, 22)]
    [InlineData(QualityType.Set, "ful", 0u, 3u, 20)]
    public void TheRequirementIsTheHigherOfBaseAndItem(QualityType quality, string code, uint unique, uint set, int expected)
    {
        var item = new Item { Quality = quality, Type = code, UniqueCode = unique, SetCode = set };

        Assert.Equal(expected, Requirements().RequiredLevel(item));
    }

    [Theory]
    [InlineData(QualityType.Magical)]
    [InlineData(QualityType.Rare)]
    public void AffixRequirementsAreUnknown(QualityType quality)
    {
        Assert.Null(Requirements().RequiredLevel(new Item { Quality = quality, Type = "ful" }));
    }

    [Fact]
    public void StrengthComesFromTheBase()
    {
        Assert.Equal(80, Requirements().RequiredStrength(new Item { Quality = QualityType.Unique, Type = "ful" }));
    }
}
