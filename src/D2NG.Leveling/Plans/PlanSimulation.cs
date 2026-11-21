using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace D2NG.Leveling.Plans;

public static class ClassStats
{
    /// <summary>Starting stats from charstats.txt.</summary>
    public static IReadOnlyDictionary<Attribute, int> Start(CharacterClass characterClass) => characterClass switch
    {
        CharacterClass.Amazon => Stats(20, 25, 20, 15),
        CharacterClass.Sorceress => Stats(10, 25, 10, 35),
        CharacterClass.Necromancer => Stats(15, 25, 15, 25),
        CharacterClass.Paladin => Stats(25, 20, 25, 15),
        CharacterClass.Barbarian => Stats(30, 20, 25, 10),
        CharacterClass.Druid => Stats(15, 20, 25, 20),
        CharacterClass.Assassin => Stats(20, 20, 20, 25),
        _ => throw new ArgumentOutOfRangeException(nameof(characterClass), characterClass, null)
    };

    /// <summary>Skills the class's starting gear grants: the sorceress's staff carries Fire Bolt.</summary>
    public static IReadOnlySet<Skill> StartingItemSkills(CharacterClass characterClass)
        => characterClass == CharacterClass.Sorceress ? new HashSet<Skill> { Skill.FireBolt } : new HashSet<Skill>();

    private static Dictionary<Attribute, int> Stats(int strength, int dexterity, int vitality, int energy) => new()
    {
        [Attribute.Strength] = strength,
        [Attribute.Dexterity] = dexterity,
        [Attribute.Vitality] = vitality,
        [Attribute.Energy] = energy
    };
}

/// <summary>
/// Where a simulated character collects the quest rewards: Den of Evil, Radament and Izual give skill
/// points, Lam Esen's Tome stat points, once per difficulty. The levels are a guess at a party that
/// clears as it goes; the live bot spends what the character really has.
/// </summary>
public sealed record QuestReward(int Level, int SkillPoints, int StatPoints)
{
    public static IReadOnlyList<QuestReward> Classic { get; } =
    [
        new(3, 1, 0), new(15, 1, 0), new(21, 0, 5), new(25, 2, 0),
        new(30, 1, 0), new(38, 1, 0), new(42, 0, 5), new(45, 2, 0),
        new(55, 1, 0), new(62, 1, 0), new(66, 0, 5), new(70, 2, 0)
    ];
}

public sealed record LevelRow(
    int Level,
    IReadOnlyList<Skill> SkillsSpent,
    IReadOnlyDictionary<Skill, int> Skills,
    IReadOnlyDictionary<Attribute, int> Stats,
    int UnspentSkillPoints,
    CombatStage? Combat);

public static class PlanSimulation
{
    public static IReadOnlyList<LevelRow> Run(BuildPlan plan, SkillCatalog catalog, int toLevel = 99, IReadOnlyList<QuestReward>? rewards = null)
    {
        rewards ??= QuestReward.Classic;
        var skills = new Dictionary<Skill, int>();
        var stats = new Dictionary<Attribute, int>(ClassStats.Start(plan.Class));
        var skillPoints = 0;
        var statPoints = 0;
        var rows = new List<LevelRow>();
        for (var level = 1; level <= toLevel; level++)
        {
            if (level > 1)
            {
                skillPoints++;
                statPoints += 5;
            }

            foreach (var reward in rewards.Where(reward => reward.Level == level))
            {
                skillPoints += reward.SkillPoints;
                statPoints += reward.StatPoints;
            }

            var spent = new List<Skill>();
            while (skillPoints > 0 && SkillAllocator.Next(plan, catalog, level, skills) is { } skill)
            {
                skills[skill] = skills.GetValueOrDefault(skill) + 1;
                spent.Add(skill);
                skillPoints--;
            }

            while (statPoints > 0 && StatAllocator.Next(plan, stats) is { } stat)
            {
                stats[stat]++;
                statPoints--;
            }

            rows.Add(new LevelRow(level, spent, new Dictionary<Skill, int>(skills), new Dictionary<Attribute, int>(stats),
                skillPoints, plan.Combat.LastOrDefault(stage => stage.FromLevel <= level)));
        }

        return rows;
    }
}
