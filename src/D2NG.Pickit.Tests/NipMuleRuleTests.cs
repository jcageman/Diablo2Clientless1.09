using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using D2NG.Pickit.Nip;

namespace D2NG.Pickit.Tests;

/// <summary>
/// Mule rules: nip lines read by expression only, with '[keptonlyby]' naming the one keep file.
/// </summary>
public class NipMuleRuleTests
{
    private static IReadOnlyList<NipRule> Parse(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mule-{Guid.NewGuid():N}.nip");
        File.WriteAllText(path, text);
        try
        {
            return NipParser.ParseFile(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool Matches(IReadOnlyList<NipRule> rules, Item item, params string[] keepSources)
    {
        var context = new NipEvaluationContext(item) { KeepRuleSources = () => keepSources };
        return rules.Any(r => r.MatchesExpression(context));
    }

    [Theory]
    [InlineData(ClassificationType.Ring, ItemName.Ring, QualityType.Rare, true)]
    [InlineData(ClassificationType.Ring, ItemName.Ring, QualityType.Unique, false)]
    [InlineData(ClassificationType.Gem, ItemName.PerfectRuby, QualityType.Normal, false)]
    [InlineData(ClassificationType.Amulet, ItemName.Amulet, QualityType.Unique, true)]
    public void GeneralRule_KeepsOutGemsAndUniqueRings(ClassificationType classification, ItemName name, QualityType quality, bool expected)
    {
        // The type pre-filter would read '[quality] == unique' as required; mule rules skip it.
        var rules = Parse("[type] != gem && ([name] != ring || [quality] != unique)");

        Assert.Equal(expected, Matches(rules, TestItemFactory.Create(classification, name, quality)));
    }

    [Fact]
    public void PropertyOnlyLine_IsAComplete_MuleRule()
    {
        var rules = Parse("[name] == nefrune\n");

        Assert.True(Matches(rules, TestItemFactory.Create(ClassificationType.Rune, ItemName.NefRune, QualityType.Normal)));
        Assert.False(Matches(rules, TestItemFactory.Create(ClassificationType.Rune, ItemName.SolRune, QualityType.Normal)));
    }

    [Fact]
    public void KeptOnlyBy_NamesTheSingleKeepFile()
    {
        var rules = Parse("[keptonlyby] == craftbases");
        var amulet = TestItemFactory.Create(ClassificationType.Amulet, ItemName.Amulet, QualityType.Magical);

        Assert.True(Matches(rules, amulet, Path.Combine("nips", "CraftBases.nip")));
        Assert.False(Matches(rules, amulet, "CraftBases.nip", "Amulets.nip"));
        // An empty text would compare equal to any word, so nothing kept must not match.
        Assert.False(Matches(rules, amulet));
    }
}
