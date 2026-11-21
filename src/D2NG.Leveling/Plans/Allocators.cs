using D2NG.Core.D2GS.Players;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace D2NG.Leveling.Plans;

public static class SkillAllocator
{
    /// <summary>
    /// The skill the next point goes to, or null to keep it. A step whose skill is not open yet keeps the
    /// points, as a save step does; one that is open but full for this level is passed over. Missing
    /// prerequisites get their single point first.
    /// </summary>
    public static Skill? Next(BuildPlan plan, SkillCatalog catalog, int characterLevel, IReadOnlyDictionary<Skill, int> hardPoints)
    {
        foreach (var step in plan.Skills)
        {
            if (step.Save)
            {
                if (characterLevel < step.UntilLevel)
                {
                    return null;
                }

                continue;
            }

            if (step.Skill is not { } skill || Points(hardPoints, skill) >= step.Level)
            {
                continue;
            }

            if (Spendable(skill, catalog, characterLevel, hardPoints) is { } spend)
            {
                return spend;
            }

            // A skill not open yet keeps the points for itself: later steps get none, its prerequisites only the one
            // point each they need, so that once it opens the saved points go into it and its neighbours together.
            // A whirlwind barbarian otherwise put five points into Find Item, its last skill, while it waited for 30.
            if (catalog.MaxHardPointsAt(skill, characterLevel) == 0)
            {
                return null;
            }
        }

        return null;
    }

    private static Skill? Spendable(Skill skill, SkillCatalog catalog, int characterLevel, IReadOnlyDictionary<Skill, int> hardPoints)
    {
        var missingPrerequisite = false;
        foreach (var prerequisite in catalog.Prerequisites(skill))
        {
            if (Points(hardPoints, prerequisite) > 0)
            {
                continue;
            }

            if (Spendable(prerequisite, catalog, characterLevel, hardPoints) is { } spend)
            {
                return spend;
            }

            missingPrerequisite = true;
        }

        return !missingPrerequisite && Points(hardPoints, skill) < catalog.MaxHardPointsAt(skill, characterLevel) ? skill : null;
    }

    private static int Points(IReadOnlyDictionary<Skill, int> hardPoints, Skill skill)
        => hardPoints.GetValueOrDefault(skill);
}

public static class StatAllocator
{
    /// <param name="hardStats">Base stats without items, as the plan counts them from the class's start.</param>
    /// <param name="wanted">What the gear the character wants to wear asks for, per stat.</param>
    public static Attribute? Next(BuildPlan plan, IReadOnlyDictionary<Attribute, int> hardStats, IReadOnlyDictionary<Attribute, int>? wanted = null)
    {
        foreach (var target in plan.Stats)
        {
            if (target.Base is { } floor && hardStats.GetValueOrDefault(target.Stat) < floor)
            {
                return target.Stat;
            }
        }

        if (wanted != null)
        {
            foreach (var target in plan.Stats)
            {
                if (target.Cap is { } cap && wanted.TryGetValue(target.Stat, out var need)
                    && hardStats.GetValueOrDefault(target.Stat) < Math.Min(need, cap))
                {
                    return target.Stat;
                }
            }
        }

        return plan.Stats.FirstOrDefault(target => target.Rest)?.Stat;
    }
}
