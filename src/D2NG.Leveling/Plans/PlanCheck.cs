using D2NG.Core.D2GS.Players;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace D2NG.Leveling.Plans;

public static class PlanCheck
{
    private const int TopLevel = 99;

    /// <summary>Skills every class has, which a combat stage may use without a point in them.</summary>
    private static readonly HashSet<Skill> CommonSkills = [Skill.Attack, Skill.ThrowStuff, Skill.Kick];

    /// <summary>Everything wrong with the plan; empty when it can be spent from level 1.</summary>
    public static IReadOnlyList<string> Problems(BuildPlan plan, SkillCatalog catalog)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Name))
        {
            problems.Add("The plan has no name");
        }

        CheckStats(plan, problems);
        CheckSkills(plan, catalog, problems);
        if (problems.Count > 0)
        {
            return problems;
        }

        var rows = PlanSimulation.Run(plan, catalog, TopLevel);
        var final = rows[^1].Skills;
        foreach (var step in plan.Skills.Where(step => !step.Save && final.GetValueOrDefault(step.Skill!.Value) < step.Level))
        {
            problems.Add($"{step} is not reached by level {TopLevel}");
        }

        CheckCombat(plan, rows, problems);
        return problems;
    }

    private static void CheckStats(BuildPlan plan, List<string> problems)
    {
        var start = ClassStats.Start(plan.Class);
        if (plan.Stats.Count(target => target.Rest) != 1)
        {
            problems.Add("Exactly one stat must take the rest");
        }

        foreach (var group in plan.Stats.GroupBy(target => target.Stat).Where(group => group.Count() > 1))
        {
            problems.Add($"{group.Key} is listed more than once");
        }

        foreach (var target in plan.Stats)
        {
            if (!start.ContainsKey(target.Stat))
            {
                problems.Add($"{target.Stat} is not a stat points go into");
                continue;
            }

            if (target.Base < start[target.Stat])
            {
                problems.Add($"{target.Stat} base {target.Base} is below the class's starting {start[target.Stat]}");
            }

            if (target.Cap < (target.Base ?? start[target.Stat]))
            {
                problems.Add($"{target.Stat} cap {target.Cap} is below its base");
            }
        }

        var basePoints = plan.Stats.Where(target => target.Base != null && start.ContainsKey(target.Stat))
            .Sum(target => Math.Max(0, target.Base!.Value - start[target.Stat]));
        var available = 5 * (TopLevel - 1) + QuestReward.Classic.Sum(reward => reward.StatPoints);
        if (basePoints > available)
        {
            problems.Add($"The bases need {basePoints} stat points, more than the {available} a character ever gets");
        }
    }

    private static void CheckSkills(BuildPlan plan, SkillCatalog catalog, List<string> problems)
    {
        foreach (var step in plan.Skills)
        {
            if (step.Save)
            {
                if (step.UntilLevel is not (>= 2 and <= TopLevel))
                {
                    problems.Add($"A save step needs a level between 2 and {TopLevel}");
                }

                continue;
            }

            if (step.Skill is not { } skill)
            {
                problems.Add("A step names neither a skill nor a save");
                continue;
            }

            if (!catalog.TryGet(skill, out var entry) || entry.Class != plan.Class)
            {
                problems.Add($"{skill} is not a {plan.Class} skill");
                continue;
            }

            if (step.Level is < 1 or > SkillCatalog.MaxHardPoints)
            {
                problems.Add($"{skill} {step.Level}: a skill takes 1 to {SkillCatalog.MaxHardPoints} hard points");
            }
        }
    }

    private static void CheckCombat(BuildPlan plan, IReadOnlyList<LevelRow> rows, List<string> problems)
    {
        if (plan.Combat.Count == 0 || plan.Combat[0].FromLevel != 1)
        {
            problems.Add("The combat stages must start at level 1");
        }

        for (var i = 1; i < plan.Combat.Count; i++)
        {
            if (plan.Combat[i].FromLevel <= plan.Combat[i - 1].FromLevel)
            {
                problems.Add($"Combat stage from level {plan.Combat[i].FromLevel} is out of order");
            }
        }

        var fromStartingGear = ClassStats.StartingItemSkills(plan.Class);
        foreach (var stage in plan.Combat.Where(stage => stage.FromLevel is >= 1 and <= TopLevel))
        {
            var skills = rows[stage.FromLevel - 1].Skills;
            foreach (var skill in new[] { stage.Attack, stage.Secondary, stage.Aura }.OfType<Skill>())
            {
                if (!CommonSkills.Contains(skill) && !fromStartingGear.Contains(skill) && skills.GetValueOrDefault(skill) == 0)
                {
                    problems.Add($"Combat stage from level {stage.FromLevel} uses {skill}, which has no point yet");
                }
            }
        }
    }
}

public static class PlanConformance
{
    /// <summary>
    /// Points a character holds that its plan never gives. Quest rewards arrive at different levels
    /// for every character, so this does not ask for the plan's exact state, only that nothing was
    /// spent outside it.
    /// </summary>
    public static IReadOnlyList<string> Deviations(BuildPlan plan, SkillCatalog catalog,
        IReadOnlyDictionary<Skill, int> hardSkills, IReadOnlyDictionary<Attribute, int> hardStats)
    {
        var deviations = new List<string>();
        var allowed = new Dictionary<Skill, int>();
        foreach (var step in plan.Skills.Where(step => step.Skill != null))
        {
            Allow(step.Skill!.Value, step.Level, catalog, allowed);
        }

        foreach (var (skill, points) in hardSkills.Where(pair => pair.Value > 0))
        {
            if (points > allowed.GetValueOrDefault(skill))
            {
                deviations.Add($"{skill} has {points} points, the plan gives at most {allowed.GetValueOrDefault(skill)}");
            }
        }

        var start = ClassStats.Start(plan.Class);
        foreach (var (stat, startValue) in start)
        {
            var target = plan.Stats.FirstOrDefault(target => target.Stat == stat);
            if (target?.Rest == true)
            {
                continue;
            }

            var most = Math.Max(target?.Cap ?? startValue, target?.Base ?? startValue);
            if (hardStats.GetValueOrDefault(stat) > most)
            {
                deviations.Add($"{stat} is {hardStats.GetValueOrDefault(stat)}, the plan allows at most {most}");
            }
        }

        return deviations;
    }

    private static void Allow(Skill skill, int level, SkillCatalog catalog, Dictionary<Skill, int> allowed)
    {
        allowed[skill] = Math.Max(allowed.GetValueOrDefault(skill), level);
        foreach (var prerequisite in catalog.Prerequisites(skill))
        {
            Allow(prerequisite, 1, catalog, allowed);
        }
    }
}
