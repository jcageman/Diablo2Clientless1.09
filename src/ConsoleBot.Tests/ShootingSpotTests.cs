using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS;

namespace ConsoleBot.Tests;

/// <summary>
/// A hall running east (y 20-26) that turns sharply north (x 34-40); everything else is wall. The pack stands just round
/// the bend, six north of the inner corner at (34, 20).
/// </summary>
public class ShootingSpotTests
{
    private static int? Cell(Point point)
        => point.X > 60 || point.Y > 60 ? null
            : (point.Y is >= 20 and <= 26 && point.X <= 40) || (point.X is >= 34 and <= 40 && point.Y <= 26) ? 0 : 3;

    private static readonly Point Target = new(37, 14);

    private static readonly List<Point> Pack = [Target, new Point(36, 12), new Point(38, 13)];

    [Fact]
    public void ShootsFromTheWideSideOfTheBendNotFromBesideThePack()
    {
        var spot = LevelingCombat.ShootingSpot(new Point(12, 23), Target, Pack, 18, Cell);

        Assert.NotNull(spot);
        Assert.True(spot.Distance(Target) >= 10, $"{spot} is {spot.Distance(Target):0.0} from the target");
        Assert.True(spot.Y >= 24, $"{spot} hugs the inner corner");
        Assert.All(Pack, monster => Assert.True(monster.Distance(spot) >= 8));
    }

    [Fact]
    public void NoneWhenEverySpotThatSeesTheTargetIsAFarWalk()
    {
        Assert.Null(LevelingCombat.ShootingSpot(new Point(0, 23), Target, Pack, 18, Cell));
    }
}
