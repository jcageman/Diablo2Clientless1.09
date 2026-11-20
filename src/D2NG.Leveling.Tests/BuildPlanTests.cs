using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using D2NG.Leveling.Plans;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace D2NG.Leveling.Tests;

public class BuildPlanTests
{
    private static readonly SkillCatalog Catalog = SkillCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "skills.json"));

    private static BuildPlan Sorceress(string skills, string combat = """[{ "fromLevel": 1, "attack": "Attack" }]""") => BuildPlan.Parse($$"""
        {
          "name": "test",
          "class": "Sorceress",
          "stats": [{ "stat": "Strength", "base": 30, "cap": 60 }, { "stat": "Vitality", "rest": true }],
          "skills": {{skills}},
          "combat": {{combat}}
        }
        """);

    private static Dictionary<Skill, int> Points(params (Skill Skill, int Points)[] points)
        => points.ToDictionary(point => point.Skill, point => point.Points);

    [Fact]
    public void AMissingPrerequisiteTakesThePointFirst()
    {
        var plan = Sorceress("""[{ "skill": "Blizzard", "level": 1 }]""");

        Assert.Equal(Skill.FrostNova, SkillAllocator.Next(plan, Catalog, 24, Points()));
        Assert.Equal(Skill.IceBolt, SkillAllocator.Next(plan, Catalog, 24, Points((Skill.FrostNova, 1))));
        Assert.Equal(Skill.Blizzard, SkillAllocator.Next(plan, Catalog, 24,
            Points((Skill.FrostNova, 1), (Skill.IceBolt, 1), (Skill.IceBlast, 1), (Skill.GlacialSpike, 1))));
    }

    [Fact]
    public void ASkillTakesOnePointPerLevelAboveItsRequirement()
    {
        var plan = Sorceress("""[{ "skill": "StaticField", "level": 20 }]""");

        Assert.Null(SkillAllocator.Next(plan, Catalog, 5, Points()));
        Assert.Equal(Skill.StaticField, SkillAllocator.Next(plan, Catalog, 6, Points()));
        Assert.Null(SkillAllocator.Next(plan, Catalog, 6, Points((Skill.StaticField, 1))));
        Assert.Equal(Skill.StaticField, SkillAllocator.Next(plan, Catalog, 7, Points((Skill.StaticField, 1))));
    }

    [Fact]
    public void AStepNotOpenYetKeepsThePoints()
    {
        // Cold Mastery opens at 30: until then the plan keeps the points rather than spend them on Fire Bolt.
        var plan = Sorceress("""[{ "skill": "ColdMastery", "level": 20 }, { "skill": "FireBolt", "level": 20 }]""");

        Assert.Null(SkillAllocator.Next(plan, Catalog, 10, Points()));
    }

    [Fact]
    public void ASkillNotOpenYetKeepsThePointsAndItsPrerequisitesGetOneEach()
    {
        var plan = Sorceress("""[{ "skill": "FrozenOrb", "level": 20 }, { "skill": "Warmth", "level": 20 }]""");

        Assert.NotNull(SkillAllocator.Next(plan, Catalog, 20, Points()));
        var chain = new List<Skill>();
        var open = new Queue<Skill>(Catalog.Prerequisites(Skill.FrozenOrb));
        while (open.TryDequeue(out var needed))
        {
            if (!chain.Contains(needed))
            {
                chain.Add(needed);
                foreach (var deeper in Catalog.Prerequisites(needed))
                {
                    open.Enqueue(deeper);
                }
            }
        }

        Assert.Null(SkillAllocator.Next(plan, Catalog, 20, Points([.. chain.Select(skill => (skill, 1))])));
    }

    [Fact]
    public void AnOpenSkillFullForItsLevelPassesThePointOn()
    {
        var plan = Sorceress("""[{ "skill": "StaticField", "level": 20 }, { "skill": "Telekinesis", "level": 20 }]""");

        Assert.Equal(Skill.Telekinesis, SkillAllocator.Next(plan, Catalog, 6, Points((Skill.StaticField, 1))));
    }

    [Fact]
    public void ASaveStepKeepsThePointsUntilItsLevel()
    {
        var plan = Sorceress("""[{ "skill": "FireBolt", "level": 2 }, { "save": true, "untilLevel": 30 }, { "skill": "Warmth", "level": 20 }]""");

        Assert.Equal(Skill.FireBolt, SkillAllocator.Next(plan, Catalog, 10, Points()));
        Assert.Null(SkillAllocator.Next(plan, Catalog, 10, Points((Skill.FireBolt, 2))));
        Assert.Equal(Skill.Warmth, SkillAllocator.Next(plan, Catalog, 30, Points((Skill.FireBolt, 2))));
    }

    [Fact]
    public void SavedPointsGoIntoSeveralSkillsOnceTheyOpen()
    {
        var plan = Sorceress("""
            [{ "skill": "FireBolt", "level": 3 }, { "save": true, "untilLevel": 30 },
             { "skill": "FrozenOrb", "level": 20 }, { "skill": "ColdMastery", "level": 20 }]
            """);

        var level30 = PlanSimulation.Run(plan, Catalog, 30)[^1];

        Assert.Equal(1, level30.Skills[Skill.FrozenOrb]);
        Assert.Equal(1, level30.Skills[Skill.ColdMastery]);
        Assert.True(level30.UnspentSkillPoints > 0);
    }

    [Fact]
    public void StatsFillTheBaseThenGearWithinTheCapThenTheRest()
    {
        var plan = Sorceress("[]");
        var start = new Dictionary<Attribute, int>(ClassStats.Start(CharacterClass.Sorceress));

        Assert.Equal(Attribute.Strength, StatAllocator.Next(plan, start));
        start[Attribute.Strength] = 30;
        Assert.Equal(Attribute.Vitality, StatAllocator.Next(plan, start));
        Assert.Equal(Attribute.Strength, StatAllocator.Next(plan, start, new Dictionary<Attribute, int> { [Attribute.Strength] = 45 }));
        start[Attribute.Strength] = 60;
        Assert.Equal(Attribute.Vitality, StatAllocator.Next(plan, start, new Dictionary<Attribute, int> { [Attribute.Strength] = 90 }));
    }

    [Fact]
    public void TheCheckRefusesAnotherClassesSkill()
    {
        var plan = Sorceress("""[{ "skill": "Whirlwind", "level": 1 }]""");

        Assert.Contains(PlanCheck.Problems(plan, Catalog), problem => problem.Contains("not a Sorceress skill", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCheckRefusesAnAttackWithoutAPointYet()
    {
        var plan = Sorceress("""[{ "skill": "FrozenOrb", "level": 1 }]""",
            """[{ "fromLevel": 1, "attack": "Attack" }, { "fromLevel": 20, "attack": "FrozenOrb" }]""");

        Assert.Contains(PlanCheck.Problems(plan, Catalog), problem => problem.Contains("FrozenOrb, which has no point yet", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSorceressCastsFireBoltFromHerStartingStaff()
    {
        var plan = Sorceress("[]", """[{ "fromLevel": 1, "attack": "FireBolt" }]""");

        Assert.Empty(PlanCheck.Problems(plan, Catalog));
    }

    [Fact]
    public void ACharacterOnThePlanHasNoDeviations()
    {
        var plan = Sorceress("""[{ "skill": "Blizzard", "level": 5 }]""");
        var skills = Points((Skill.Blizzard, 3), (Skill.FrostNova, 1), (Skill.IceBolt, 1), (Skill.IceBlast, 1), (Skill.GlacialSpike, 1));
        var stats = new Dictionary<Attribute, int>(ClassStats.Start(CharacterClass.Sorceress)) { [Attribute.Vitality] = 200 };

        Assert.Empty(PlanConformance.Deviations(plan, Catalog, skills, stats));
    }

    [Fact]
    public void PointsOutsideThePlanAreDeviations()
    {
        var plan = Sorceress("""[{ "skill": "Blizzard", "level": 5 }]""");
        var stats = new Dictionary<Attribute, int>(ClassStats.Start(CharacterClass.Sorceress)) { [Attribute.Energy] = 80 };

        var deviations = PlanConformance.Deviations(plan, Catalog, Points((Skill.FireBolt, 1), (Skill.IceBlast, 2)), stats);

        Assert.Equal(3, deviations.Count);
    }
}
