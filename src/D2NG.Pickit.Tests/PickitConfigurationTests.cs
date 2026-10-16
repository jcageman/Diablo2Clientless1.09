using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using D2NG.Pickit;

namespace D2NG.Pickit.Tests;

public class PickitConfigurationTests
{
    [Fact]
    public void DefaultGambleConfiguration_MatchesCurrentRules()
    {
        var configuration = CreateCurrentConfiguration();

        Assert.True(Pickit.ShouldGamble(89, CreateItem(ItemName.Boots, identified: false), configuration));
        Assert.True(Pickit.ShouldGamble(90, CreateItem(ItemName.Amulet, identified: false), configuration));
        Assert.False(Pickit.ShouldGamble(89, CreateItem(ItemName.Amulet, identified: false), configuration));
        Assert.False(Pickit.ShouldGamble(90, CreateItem(ItemName.Boots, identified: false), configuration));
    }

    [Fact]
    public void IdentifiedItemsAreNeverGambled()
    {
        var configuration = CreateCurrentConfiguration();

        Assert.False(Pickit.ShouldGamble(90, CreateItem(ItemName.Amulet, identified: true), configuration));
    }

    [Fact]
    public void CustomGambleConfigurationUsesOnlyLevelAndName()
    {
        var configuration = new PickitConfiguration
        {
            Gamble = new GambleConfiguration
            {
                Rules = [
                    new() { ItemNames = [ItemName.Ring, ItemName.Amulet], MinimumCharacterLevel = 50 },
                    new() { ItemNames = [ItemName.Belt], MinimumCharacterLevel = 0 }
                ]
            },
            NipDirectory = string.Empty
        };

        Assert.True(Pickit.ShouldGamble(50, CreateItem(ItemName.Ring, identified: false), configuration));
        Assert.True(Pickit.ShouldGamble(50, CreateItem(ItemName.Amulet, identified: false), configuration));
        Assert.True(Pickit.ShouldGamble(49, CreateItem(ItemName.Belt, identified: false), configuration));
        Assert.False(Pickit.ShouldGamble(50, CreateItem(ItemName.Belt, identified: false), configuration));
    }

    [Fact]
    public void QuietNip_KeepsRoutineLootOutOfTheChat()
    {
        var rules = QuietRules("""
            [name] == ring && [quality] == unique
            [type] == gem
            [name] == solrune || [name] == nefrune
            [keptonlyby] == craftbases
            """);

        Assert.False(Quiet(CreateItem(ItemName.Ring, QualityType.Unique, ClassificationType.Ring), rules));
        Assert.False(Quiet(CreateItem(ItemName.PerfectAmethyst, QualityType.Normal, ClassificationType.Gem), rules));
        Assert.False(Quiet(CreateItem(ItemName.SolRune, QualityType.Normal, ClassificationType.Rune), rules));
        Assert.True(Quiet(CreateItem(ItemName.Ring, QualityType.Rare, ClassificationType.Ring), rules));
    }

    [Fact]
    public void QuietNip_HidesCraftStock_ButNotTheSameBaseWithStats()
    {
        var rules = QuietRules("[keptonlyby] == craftbases");
        var plain = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Magical);
        plain.Level = 70;
        // The same crafting base, but with stats Amulets.nip keeps it for.
        var caster = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Magical);
        caster.Level = 70;
        TestItemFactory.AddStat(caster, StatType.SorceressSkills, 2);
        TestItemFactory.AddStat(caster, StatType.FasterCastRate, 10);
        foreach (var resistance in new[] { StatType.FireResistance, StatType.ColdResistance, StatType.LightningResistance, StatType.PoisonResistance })
        {
            TestItemFactory.AddStat(caster, resistance, 40);
        }

        Assert.False(Quiet(plain, rules));
        Assert.True(Quiet(caster, rules));
    }

    [Fact]
    public void NoQuietNip_ReportsEveryKeptItem()
    {
        var configuration = CreateCurrentConfiguration();

        Assert.Empty(Pickit.LoadQuietRules(configuration));
    }

    [Fact]
    public void MissingQuietNip_StopsStartup()
    {
        var configuration = CreateCurrentConfiguration();
        configuration.QuietNip = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.nip");

        Assert.Throws<FileNotFoundException>(() => Pickit.LoadQuietRules(configuration));
    }

    private static bool Quiet(Item item, IReadOnlyList<Nip.NipRule> rules)
    {
        return Pickit.SendItemToKeepToExternalClient(item, rules);
    }

    private static IReadOnlyList<Nip.NipRule> QuietRules(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"quiet-{Guid.NewGuid():N}.nip");
        File.WriteAllText(path, text);
        try
        {
            var configuration = CreateCurrentConfiguration();
            configuration.QuietNip = path;
            return Pickit.LoadQuietRules(configuration);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Item CreateItem(ItemName name, QualityType quality = QualityType.Normal, ClassificationType classification = ClassificationType.Amulet, bool identified = false)
    {
        return new Item
        {
            Name = name,
            Quality = quality,
            Classification = classification,
            IsIdentified = identified,
            Properties = []
        };
    }

    private static PickitConfiguration CreateCurrentConfiguration()
    {
        return new PickitConfiguration
        {
            NipDirectory = Path.Combine(AppContext.BaseDirectory, "Nips", "Expansion"),
            Gamble = new GambleConfiguration
            {
                Rules =
                [
                    new() { ItemNames = [ItemName.Amulet], MinimumCharacterLevel = 90 },
                    new() { ItemNames = [ItemName.Boots, ItemName.HeavyBoots], MinimumCharacterLevel = 0 }
                ]
            }
        };
    }
}
