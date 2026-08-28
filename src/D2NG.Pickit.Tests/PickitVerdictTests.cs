using System.Text.RegularExpressions;
using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class PickitVerdictTests
{
    [Fact]
    public void ExplainPickup_NamesTheRuleThatMatched()
    {
        var item = TestItemFactory.Create(ClassificationType.Rune, ItemName.JahRune, QualityType.Normal, identified: false);

        var verdict = Pickit.ExplainPickup(CharacterClass.Sorceress, shouldPickupGoldItems: true, item);

        Assert.True(verdict.Result);

        // Which file wins depends on rule order across the tree, so assert the shape, not the file.
        Assert.Matches(new Regex(@"^[A-Za-z]+\.nip:\d+$"), verdict.Reason);
    }

    [Fact]
    public void ExplainPickup_ReportsWhenNothingMatched()
    {
        var item = TestItemFactory.Create(ClassificationType.Rune, ItemName.TirRune, QualityType.Normal, identified: false);

        var verdict = Pickit.ExplainPickup(CharacterClass.Sorceress, shouldPickupGoldItems: true, item);

        Assert.False(verdict.Result);
        Assert.Equal("no matching rule", verdict.Reason);
    }

    [Fact]
    public void ExplainKeep_ReportsUnidentifiedItemsAreKeptWithoutARule()
    {
        var item = TestItemFactory.Create(ClassificationType.Ring, ItemName.Ring, QualityType.Rare, identified: false);

        var verdict = Pickit.ExplainKeep(CharacterClass.Sorceress, item);

        Assert.True(verdict.Result);
        Assert.Equal("unidentified", verdict.Reason);
    }

    // The audit must never disagree with the decision the bot actually acts on.
    [Theory]
    [InlineData(ClassificationType.Rune, ItemName.JahRune, QualityType.Normal, false)]
    [InlineData(ClassificationType.Rune, ItemName.TirRune, QualityType.Normal, false)]
    [InlineData(ClassificationType.Ring, ItemName.Ring, QualityType.Rare, true)]
    [InlineData(ClassificationType.Ring, ItemName.Ring, QualityType.Unique, false)]
    [InlineData(ClassificationType.Amulet, ItemName.Amulet, QualityType.Rare, true)]
    [InlineData(ClassificationType.Gem, ItemName.PerfectRuby, QualityType.Normal, false)]
    public void ExplainAgreesWithTheBooleanApi(ClassificationType classification, ItemName name, QualityType quality, bool identified)
    {
        var item = TestItemFactory.Create(classification, name, quality, identified);

        foreach (var characterClass in new[] { CharacterClass.Sorceress, CharacterClass.Paladin, CharacterClass.Barbarian })
        {
            foreach (var goldItems in new[] { true, false })
            {
                Assert.Equal(
                    Pickit.ShouldPickupItem(characterClass, goldItems, item),
                    Pickit.ExplainPickup(characterClass, goldItems, item).Result);
            }

            Assert.Equal(
                Pickit.ShouldKeepItem(characterClass, item),
                Pickit.ExplainKeep(characterClass, item).Result);
        }
    }
}
