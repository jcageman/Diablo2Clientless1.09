using System.IO;
using System.Linq;
using D2NG.Core.D2GS.Enums;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit.Tests;

/// <summary>
/// Item codes can start with a digit - elite weapons are 7xx, exceptional ones 9xx - so
/// '[itemtype] == 7*' has to read as a code prefix, not as the number 7 times something.
/// </summary>
public class NipItemCodeTests
{
    private static NipRule ParseSingleRule(string line)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, line);
            return NipParser.ParseFile(path).Single();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Item Weapon(string code)
    {
        var item = TestItemFactory.Create(ClassificationType.Polearm, ItemName.GiantThresher, QualityType.Normal, sockets: 6);
        item.Type = code;
        return item;
    }

    [Theory]
    [InlineData("7gt", true)]
    [InlineData("9gt", false)]
    public void DigitLedCodePrefix(string code, bool expected)
    {
        var rule = ParseSingleRule("[itemtype] == 7* # [sockets] == 6");

        Assert.Equal(expected, rule.MatchesKeep(new NipEvaluationContext(Weapon(code))));
    }

    [Fact]
    public void DigitLedFullCode()
    {
        var rule = ParseSingleRule("[itemtype] == 7gt # [sockets] == 6");

        Assert.True(rule.MatchesKeep(new NipEvaluationContext(Weapon("7gt"))));
    }

    [Fact]
    public void NumberTimesStat_StillMultiplies()
    {
        var item = Weapon("7gt");
        TestItemFactory.AddStat(item, StatType.MinimumLifeStolenPerHit, 5);
        var rule = ParseSingleRule("[itemtype] == 7* # 2*[lifeleech] >= 10 && 2 * [lifeleech] < 11");

        Assert.True(rule.MatchesKeep(new NipEvaluationContext(item)));
    }
}
