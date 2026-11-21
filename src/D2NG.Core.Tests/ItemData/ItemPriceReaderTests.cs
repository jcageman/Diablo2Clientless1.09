using D2NG.Core.ItemData;
using System;
using System.Text;
using Xunit;

namespace D2NG.Core.Tests.ItemData;

/// <summary>
/// Pins the 1.09 offsets of the price columns, which were matched against the d2exp txt files and
/// then confirmed by real sales (ItemPriceTests).
/// </summary>
public class ItemPriceReaderTests
{
    private static byte[] Table(int recordSize, int count)
    {
        var raw = new byte[4 + recordSize * count];
        BitConverter.GetBytes(count).CopyTo(raw, 0);
        return raw;
    }

    private static void Put(byte[] raw, int at, string text) => Encoding.ASCII.GetBytes(text).CopyTo(raw, at);

    private static void Put(byte[] raw, int at, int value) => BitConverter.GetBytes(value).CopyTo(raw, at);

    [Fact]
    public void AVendorReadsWhatItPaysAfterWhatItCharges()
    {
        var raw = Table(ItemPriceReader.NpcSize, 1);
        Put(raw, 4, "jamella");
        Put(raw, 4 + 0x40, 1024);
        Put(raw, 4 + 0x44, 512);
        Put(raw, 4 + 0x48, 64);
        Put(raw, 4 + 0x7C, 20000);
        Put(raw, 4 + 0x80, 30000);
        Put(raw, 4 + 0x84, 35000);

        var vendor = Assert.Single(ItemPriceReader.DecodeVendors(raw, "npc.bin"));

        Assert.Equal("jamella", vendor.Name);
        Assert.Equal((1024, 512, 64), (vendor.SellMultiply, vendor.BuyMultiply, vendor.RepairMultiply));
        Assert.Equal([20000, 30000, 35000], vendor.MaxBuy);
    }

    [Fact]
    public void AStatReadsItsMultiplyAddAndEncode()
    {
        var raw = Table(ItemPriceReader.StatSize, 8);
        var start = 4 + 7 * ItemPriceReader.StatSize;
        Put(raw, start + 0x04, 1024);
        Put(raw, start + 0x08, 20);
        Put(raw, start + 0x0C, 56);
        raw[start + 0x20] = 1;

        var stat = ItemPriceReader.DecodeStats(raw, "itemstatcost.bin")[7];

        Assert.Equal((7, 20, 56, 1), (stat.Id, stat.Multiply, stat.Add, stat.Encode));
    }

    [Fact]
    public void ASetRowListsEachItemByCode()
    {
        var raw = Table(ItemPriceReader.SetSize, 1);
        Put(raw, 4, "Tancred's Battlegear");
        Put(raw, 4 + 0x8C, "mpi ");
        Put(raw, 4 + 0x8C + 60, "amu ");
        Put(raw, 4 + 0x8C + 60 + 0x10, 5);
        Put(raw, 4 + 0x8C + 60 + 0x14, 2500);
        Put(raw, 4 + 0x8C + 120, "    ");

        var items = ItemPriceReader.DecodeSetItems(raw, "setitems.bin");

        Assert.Equal(["mpi", "amu"], items.ConvertAll(i => i.Code));
        Assert.Equal((0, 5, 2500), (items[1].Set, items[1].CostMultiply, items[1].CostAdd));
    }

    [Fact]
    public void ABaseReadsItsCostAndStack()
    {
        var raw = Table(ItemAffixReader.BaseItemSize, 1);
        Put(raw, 4, "Javelin");
        Put(raw, 4 + 0x144, "jav ");
        Put(raw, 4 + 0x168, 0);
        Put(raw, 4 + 0x178, 12);
        Put(raw, 4 + 0x180, 500);
        raw[4 + 0x1A7] = 2;
        raw[4 + 0x1BF] = 1;
        raw[4 + 0x1B3] = 1;

        var item = Assert.Single(ItemPriceReader.DecodeBases(raw, "weapons.bin", BaseItemKind.Weapon, ["", "jave"]));

        Assert.Equal(("jav", "jave", 12, 500, 2), (item.Code, item.Type, item.Cost, item.MaxStack, item.Durability));
        Assert.True(item.Stackable);
    }
}
