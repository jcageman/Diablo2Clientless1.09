using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS.Players;

namespace ConsoleBot.Tests;

/// <summary>On 1.09 an aura lingers about 8 s after the paladin switches away, so none may be off for longer than 6 s.</summary>
public class AuraTurnTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    private static double LongestAway(IReadOnlyList<Skill> turns, Skill aura)
    {
        var longest = 0.0;
        var lastOn = Start;
        for (var t = Start; t < Start.AddMinutes(2); t = t.AddMilliseconds(100))
        {
            if (LevelingCombat.TurnOf(turns, t) == aura)
            {
                longest = Math.Max(longest, (t - lastOn).TotalSeconds);
                lastOn = t;
            }
        }

        return longest;
    }

    [Fact]
    public void TwoAurasTakeSixSecondTurns()
    {
        List<Skill> turns = [Skill.Fanaticism, Skill.Concentration];

        Assert.All(turns, aura => Assert.InRange(LongestAway(turns, aura), 5.9, 6.2));
    }

    [Fact]
    public void ThreeAurasAreEachBackWithinSixSeconds()
    {
        List<Skill> turns = [Skill.Fanaticism, Skill.Concentration, Skill.Conviction];

        Assert.All(turns, aura => Assert.InRange(LongestAway(turns, aura), 5.9, 6.2));
    }
}
