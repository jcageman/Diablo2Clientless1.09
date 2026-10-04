using D2NG.Core.ItemData;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.ItemData;

public class ItemAffixTableTests
{
    private static ItemAffixTable Table(params AffixEntry[] affixes) => new()
    {
        ItemTypes =
        [
            new ItemTypeEntry { Code = "armo" },
            new ItemTypeEntry { Code = "helm", Equivalent1 = "armo" },
            new ItemTypeEntry { Code = "circ", Equivalent1 = "helm" },
            new ItemTypeEntry { Code = "misc" },
            new ItemTypeEntry { Code = "ring", Equivalent1 = "misc" }
        ],
        Affixes = [.. affixes]
    };

    private static AffixEntry Affix(string name, params string[] itemTypes) => new()
    {
        Kind = AffixKind.Suffix,
        Name = name,
        Spawnable = true,
        Rare = true,
        Frequency = 1,
        ItemTypes = [.. itemTypes],
        Mods = [new PropertyRange { Property = "hp", Min = 1, Max = 10 }]
    };

    private static readonly BaseItemEntry Circlet = new() { Code = "ci0", Type = "circ" };
    private static readonly BaseItemEntry Ring = new() { Code = "rin", Type = "ring" };

    private static string[] Names(ItemAffixTable table, BaseItemEntry item, bool expansion, AffixQuality quality)
        => table.EligibleAffixes(item, expansion, quality).Select(a => a.Name).ToArray();

    [Fact]
    public void AnAffixRollsOnEveryTypeBelowTheOneItNames()
    {
        var table = Table(Affix("of Life", "armo"));

        Assert.Equal(["of Life"], Names(table, Circlet, true, AffixQuality.Magic));
        Assert.Empty(Names(table, Ring, true, AffixQuality.Magic));
    }

    [Fact]
    public void AnExclusionBeatsAnInclusion()
    {
        var affix = Affix("of Life", "armo");
        affix.ExcludedItemTypes = ["helm"];

        Assert.Empty(Names(Table(affix), Circlet, true, AffixQuality.Magic));
    }

    [Fact]
    public void ClassicGamesNeverRollExpansionAffixes()
    {
        var affix = Affix("of the Apprentice", "ring");
        affix.Version = 100;
        var table = Table(affix);

        Assert.Empty(Names(table, Ring, false, AffixQuality.Magic));
        Assert.Equal(["of the Apprentice"], Names(table, Ring, true, AffixQuality.Magic));
    }

    [Fact]
    public void RareItemsOnlyDrawRareFlaggedAffixes()
    {
        var magicOnly = Affix("of Fortune", "ring");
        magicOnly.Rare = false;
        var table = Table(magicOnly, Affix("of Chance", "ring"));

        Assert.Equal(["of Fortune", "of Chance"], Names(table, Ring, false, AffixQuality.Magic));
        Assert.Equal(["of Chance"], Names(table, Ring, false, AffixQuality.Rare));
    }

    [Fact]
    public void UnspawnableAndZeroFrequencyAffixesNeverRoll()
    {
        var unspawnable = Affix("Classic", "ring");
        unspawnable.Spawnable = false;
        var never = Affix("of Nothing", "ring");
        never.Frequency = 0;

        Assert.Empty(Names(Table(unspawnable, never), Ring, true, AffixQuality.Magic));
    }

    [Fact]
    public void ASavedTableLoadsBackTheSame()
    {
        var path = Path.GetTempFileName();
        try
        {
            Table(Affix("of Life", "armo")).Save(path);

            var loaded = ItemAffixTable.Load(path);

            Assert.Equal(["of Life"], Names(loaded, Circlet, true, AffixQuality.Rare));
            Assert.Equal("hp", loaded.Affixes[0].Mods[0].Property);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
