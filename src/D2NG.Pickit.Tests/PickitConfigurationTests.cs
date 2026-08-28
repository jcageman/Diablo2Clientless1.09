using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
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
            NipDirectory = string.Empty,
            ExternalItemBlacklist = []
        };

        Assert.True(Pickit.ShouldGamble(50, CreateItem(ItemName.Ring, identified: false), configuration));
        Assert.True(Pickit.ShouldGamble(50, CreateItem(ItemName.Amulet, identified: false), configuration));
        Assert.True(Pickit.ShouldGamble(49, CreateItem(ItemName.Belt, identified: false), configuration));
        Assert.False(Pickit.ShouldGamble(50, CreateItem(ItemName.Belt, identified: false), configuration));
    }

    [Fact]
    public void DefaultExternalBlacklistMatchesCurrentRules()
    {
        var configuration = CreateCurrentConfiguration();

        Assert.False(Pickit.SendItemToKeepToExternalClient(CreateItem(ItemName.Ring, QualityType.Unique), configuration));
        Assert.False(Pickit.SendItemToKeepToExternalClient(CreateItem(ItemName.PerfectAmethyst, QualityType.Normal, ClassificationType.Gem), configuration));
        Assert.False(Pickit.SendItemToKeepToExternalClient(CreateItem(ItemName.SolRune, QualityType.Normal), configuration));
        Assert.False(Pickit.SendItemToKeepToExternalClient(CreateItem(ItemName.HeavyGloves, QualityType.Magical), configuration));
        Assert.True(Pickit.SendItemToKeepToExternalClient(CreateItem(ItemName.Ring, QualityType.Rare), configuration));
    }

    [Fact]
    public void CustomExternalBlacklistIsAnAllowByDefaultRuleSet()
    {
        var configuration = new PickitConfiguration
        {
            ExternalItemBlacklist =
            [
                new ExternalItemBlacklistRule { ItemNames = [ItemName.Amulet] }
            ],
            NipDirectory = string.Empty,
            Gamble = new GambleConfiguration { Rules = [] }
        };

        Assert.False(Pickit.SendItemToKeepToExternalClient(CreateItem(ItemName.Amulet, QualityType.Rare), configuration));
        Assert.True(Pickit.SendItemToKeepToExternalClient(CreateItem(ItemName.Ring, QualityType.Unique), configuration));
    }

    private static Item CreateItem(ItemName name, QualityType quality = QualityType.Normal, ClassificationType classification = ClassificationType.Amulet, bool identified = false)
    {
        return new Item
        {
            Name = name,
            Quality = quality,
            Classification = classification,
            IsIdentified = identified
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
            },
            ExternalItemBlacklist =
            [
                new() { ItemNames = [ItemName.Ring], Quality = QualityType.Unique },
                new() { Classification = ClassificationType.Gem },
                new() { ItemNames = [ItemName.SolRune, ItemName.NefRune] },
                new()
                {
                    ItemNames = [ItemName.HeavyGloves, ItemName.SharkskinGloves, ItemName.VampireboneGloves],
                    Quality = QualityType.Magical
                }
            ]
        };
    }
}
