using D2NG.Mule;
using D2NG.Mule.Models;
using D2NG.Mule.Packing;

namespace D2NG.Mule.Tests;

public class MuleSkipRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static MuleCharacterDb Seen(int freeCells, TimeSpan ago)
    {
        return new MuleCharacterDb { FreeCells = freeCells, SeenAt = Now - ago };
    }

    [Fact]
    public void UnknownCharacterIsVisited()
    {
        Assert.False(MuleSkipRule.ShouldSkip(null, Now));
    }

    [Fact]
    public void FullCharacterSeenYesterdayIsSkipped()
    {
        Assert.True(MuleSkipRule.ShouldSkip(Seen(0, TimeSpan.FromDays(1)), Now));
    }

    [Fact]
    public void FullCharacterSeenJustUnderAWeekAgoIsSkipped()
    {
        Assert.True(MuleSkipRule.ShouldSkip(Seen(0, TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1)), Now));
    }

    [Fact]
    public void FullCharacterSeenAWeekAgoIsVisitedAgain()
    {
        Assert.False(MuleSkipRule.ShouldSkip(Seen(0, TimeSpan.FromDays(7)), Now));
    }

    [Fact]
    public void CharacterWithOneFreeCellIsVisited()
    {
        Assert.False(MuleSkipRule.ShouldSkip(Seen(1, TimeSpan.FromDays(1)), Now));
    }

    [Fact]
    public void FreeCellsThatFitNothingWeCarrySkipTheCharacter()
    {
        // Only 1x1 and 1x2 spots are left; we carry a 2x3 armor and a 1x3 charm.
        var seen = Seen(6, TimeSpan.FromDays(1));
        seen.FitProfile = [0, 1, 1, 0, 0];

        Assert.True(MuleSkipRule.ShouldSkip(seen, Now, [new Shape(2, 3), new Shape(1, 3)]));
        Assert.False(MuleSkipRule.ShouldSkip(seen, Now, [new Shape(2, 3), new Shape(1, 1)]));
    }

    [Fact]
    public void RecordsWithoutAProfileAreVisited()
    {
        Assert.False(MuleSkipRule.ShouldSkip(Seen(6, TimeSpan.FromDays(1)), Now, [new Shape(2, 3)]));
    }

    [Fact]
    public void StaleProfileIsIgnored()
    {
        var seen = Seen(6, TimeSpan.FromDays(8));
        seen.FitProfile = [0, 0, 0, 0, 0];

        Assert.False(MuleSkipRule.ShouldSkip(seen, Now, [new Shape(1, 1)]));
    }

    [Fact]
    public void IdIsCaseInsensitive()
    {
        Assert.Equal(MuleCharacterDb.MakeId("Some-Account", "Mule"), MuleCharacterDb.MakeId("some-account", "mule"));
    }
}
