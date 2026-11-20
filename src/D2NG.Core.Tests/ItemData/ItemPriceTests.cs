using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.ItemData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace D2NG.Core.Tests.ItemData;

/// <summary>
/// Every sale here was made on the realm by a bot and logged with the gold the vendor paid
/// ("Sold item" lines). The item is parsed back from the packet it arrived in, so the test covers
/// the parser's stats as much as the formula. item-prices.json is the generated table cut down to
/// the rows these items use.
/// </summary>
public class ItemPriceTests
{
    private static readonly string DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly ItemPriceTable Table = ItemPriceTable.Load(Path.Combine(DataDirectory, "item-prices.json"));

    private sealed record Sale(string Vendor, string Difficulty, int Gold, string Item, string Packet, List<string> Socketed);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly List<Sale> Sales = File.ReadAllLines(Path.Combine(DataDirectory, "item-sales.jsonl"))
        .Where(line => line.Length > 0)
        .Select(line => JsonSerializer.Deserialize<Sale>(line, JsonOptions))
        .ToList();

    public static TheoryData<int, string> CapturedSales()
    {
        var data = new TheoryData<int, string>();
        for (var i = 0; i < Sales.Count; i++)
        {
            data.Add(i, Sales[i].Item);
        }

        return data;
    }

    private static Item Parse(string hex) => new ParseItemPacket(new D2gsPacket(Convert.FromHexString(hex))).Item;

    private static int? Price(Sale sale)
    {
        var item = Parse(sale.Packet);
        foreach (var socketed in sale.Socketed.Select(Parse))
        {
            item.SocketedItems[socketed.Id] = socketed;
        }

        return ItemPrice.SellValue(item, Table, Enum.Parse<Difficulty>(sale.Difficulty), sale.Vendor);
    }

    [Theory]
    [MemberData(nameof(CapturedSales))]
    public void ReproducesWhatTheVendorPaid(int index, string item)
    {
        var sale = Sales[index];
        Assert.True(sale.Gold == Price(sale), $"{item}: paid {sale.Gold}, computed {Price(sale)}");
    }

    [Fact]
    public void TheVendorPaysHalfAnItemsWorth()
    {
        var potion = Sales.First(s => s.Item.StartsWith("hp5 ", StringComparison.Ordinal));
        Assert.Equal(250, potion.Gold);
        Assert.Equal(250, Price(potion));
    }

    [Fact]
    public void HellSalesStopAtTheVendorsCap()
    {
        Assert.Contains(Sales, s => s.Gold == 35000);
        Assert.All(Sales.Where(s => s.Gold == 35000), s => Assert.Equal(35000, Price(s)));
    }

    [Fact]
    public void AnUnknownBaseHasNoPrice()
    {
        var item = Parse(Sales[0].Packet);
        item.Type = "zzz";
        Assert.Null(ItemPrice.SellValue(item, Table, Difficulty.Hell));
    }
}
