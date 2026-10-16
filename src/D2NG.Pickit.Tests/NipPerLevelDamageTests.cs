using System.IO;
using System.Linq;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit.Tests;

/// <summary>
/// Rules on max damage per character level (the Trump and Screaming prefixes) read the stat's PerLevel,
/// in eighths: Trump's +49 at level 99 arrives as 4.
/// </summary>
public class NipPerLevelDamageTests
{
    private const string Rules = """
        [type] == sword && [class] == elite && [quality] == rare && [flag] == ethereal # [itemmaxdamageperlevel] >= 4 && [itemreplenishdurability] > 0
        [type] == sword && [class] == elite && [quality] == rare # [itemmaxdamageperlevel] >= 4 && [ed] >= 81
        """;

    private static Item Sword(bool ethereal, int enhancedDamage, int repair) => new()
    {
        Name = ItemName.PhaseBlade,
        Type = "7cr",
        Classification = ClassificationType.Sword,
        Quality = QualityType.Rare,
        IsIdentified = true,
        Ethereal = ethereal,
        Properties = new()
        {
            [StatType.MaximumDamagePerLevel] = new() { Type = StatType.MaximumDamagePerLevel, PerLevel = 4 },
            [StatType.EnhancedMaximumDamage] = new() { Type = StatType.EnhancedMaximumDamage, Value = enhancedDamage },
            [StatType.RepairsDurability] = new() { Type = StatType.RepairsDurability, Value = repair }
        }
    };

    [Theory]
    [InlineData(true, 0, 3, true)]
    [InlineData(true, 0, 0, false)]
    [InlineData(false, 90, 0, true)]
    [InlineData(false, 60, 3, false)]
    public void KeepsEtherealWithRepairOrTopTierEnhancedDamage(bool ethereal, int enhancedDamage, int repair, bool kept)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Rules);
            var rules = NipParser.ParseFile(path);
            var context = new NipEvaluationContext(Sword(ethereal, enhancedDamage, repair));

            Assert.Equal(kept, rules.Any(rule => rule.MatchesKeep(context)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("crs", "normal")]
    [InlineData("9cr", "exceptional")]
    [InlineData("7cr", "elite")]
    [InlineData("8lw", "exceptional")]
    [InlineData("6lw", "elite")]
    [InlineData("bax", "normal")]
    [InlineData("pax", "normal")]
    [InlineData("am1", "normal")]
    [InlineData("oba", "exceptional")]
    [InlineData("amb", "elite")]
    [InlineData("pac", "elite")]
    [InlineData("zmb", "exceptional")]
    [InlineData("xit", "exceptional")]
    [InlineData("uh9", "elite")]
    [InlineData("ci3", "elite")]
    public void ClassTierReadsWeaponArmorAndClassItemCodes(string code, string tier)
    {
        var context = new NipEvaluationContext(new Item { Type = code, Properties = [] });
        context.TryResolve("class", out var value);
        Assert.Equal(tier, value.Text);
    }
}
