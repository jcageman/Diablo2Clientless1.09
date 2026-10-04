using ConsoleBot.Attack;

namespace ConsoleBot.Tests;

public class WhirlwindPlannerTests
{
    private static int Swept(Point from, Point to, params Point[] monsters)
    {
        double dx = (double)to.X - from.X;
        double dy = (double)to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var count = 0;
        foreach (var monster in monsters)
        {
            var mx = (double)monster.X - from.X;
            var my = (double)monster.Y - from.Y;
            var along = (mx * dx + my * dy) / length;
            if (along < 0 || along > length)
            {
                continue;
            }

            if (Math.Sqrt(Math.Max(0, mx * mx + my * my - along * along)) <= WhirlwindPlanner.SweepRadius)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void AimsThroughAllOfAPackInALine()
    {
        Point[] pack = [new(110, 100), new(112, 100), new(114, 100)];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack).Target;

        Assert.NotNull(aim);
        Assert.Equal(3, Swept(new Point(100, 100), aim, pack));
    }

    [Fact]
    public void CarriesPastTheMonsterItAimsAt()
    {
        var aim = WhirlwindPlanner.Aim(new Point(100, 100), [new Point(120, 100)]).Target;

        Assert.NotNull(aim);
        Assert.True(aim.X > 120, $"expected to travel past x=120, stopped at {aim}");
    }

    [Fact]
    public void PrefersTheDenseArmOfAnLShapedPack()
    {
        Point[] pack =
        [
            new(110, 100), new(112, 100), new(114, 100), new(116, 100),
            new(100, 140),
        ];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack).Target;

        Assert.NotNull(aim);
        Assert.Equal(4, Swept(new Point(100, 100), aim, pack));
    }

    [Fact]
    public void CrossesAPackItIsStandingInTheMiddleOf()
    {
        Point[] pack = [new(90, 100), new(95, 100), new(105, 100), new(110, 100)];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack).Target;

        Assert.NotNull(aim);
        Assert.Equal(2, Swept(new Point(100, 100), aim, pack));
        Assert.NotEqual(new Point(100, 100), aim);
    }

    [Fact]
    public void NeverAimsAtAPackBehindIt()
    {
        Point[] pack = [new(80, 100), new(70, 100)];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack).Target;

        Assert.NotNull(aim);
        Assert.True(aim.X < 100, $"expected to turn towards the pack, aimed at {aim}");
        Assert.True(Swept(new Point(100, 100), aim, pack) >= 1);
    }

    [Fact]
    public void TakesTheShorterLineWhenTwoSweepTheSameCount()
    {
        Point[] pack = [new(105, 100), new(110, 100), new(60, 100), new(55, 100)];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack).Target;

        Assert.NotNull(aim);
        Assert.True(aim.X > 100, $"expected the near pair to the east, aimed at {aim}");
    }

    [Fact]
    public void ReturnsNothingWhenThereIsNoPack()
    {
        var aim = WhirlwindPlanner.Aim(new Point(100, 100), []);

        Assert.Null(aim.Target);
        Assert.Equal(0, aim.Covered);
    }

    [Fact]
    public void SurvivesAMonsterStandingOnTheCharacter()
    {
        Point[] pack = [new(100, 100), new(120, 100)];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack).Target;

        Assert.NotNull(aim);
        Assert.True(aim.X >= 100 + WhirlwindPlanner.MinimumSpin,
            $"a monster underfoot must not collapse the spin, got {aim}");
    }

    [Fact]
    public void ReportsTheCountOnTheChosenLineNotThePackSize()
    {
        Point[] pack =
        [
            new(110, 100), new(112, 100), new(114, 100), new(116, 100),
            new(100, 140),
        ];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack);

        Assert.Equal(4, aim.Covered);
    }

    [Fact]
    public void ReportsOneForALineThatCoversASingleMonster()
    {
        var aim = WhirlwindPlanner.Aim(new Point(100, 100), [new Point(120, 100)]);

        Assert.Equal(1, aim.Covered);
    }

    [Fact]
    public void StopsJustPastTheNearestMonsterOnTheLineNotTheFurthest()
    {
        Point[] pack = [new(115, 100), new(125, 100), new(135, 100)];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack);

        Assert.Equal(115 + WhirlwindPlanner.Overshoot, aim.Target.X);
    }

    [Fact]
    public void SpinLengthDoesNotGrowWithPackDepth()
    {
        var near = WhirlwindPlanner.Aim(new Point(100, 100), [new Point(115, 100)]);
        Point[] deep = [new(115, 100), new(125, 100), new(140, 100)];

        var through = WhirlwindPlanner.Aim(new Point(100, 100), deep);

        Assert.Equal(near.Target.X, through.Target.X);
    }

    [Fact]
    public void CountsOnlyMonstersInsideTheSpinNotBeyondIt()
    {
        Point[] pack = [new(115, 100), new(118, 100), new(140, 100)];

        var aim = WhirlwindPlanner.Aim(new Point(100, 100), pack);

        Assert.Equal(2, aim.Covered);
    }
}
