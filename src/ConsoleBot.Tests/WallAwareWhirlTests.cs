using ConsoleBot.Attack;
using D2NG.Navigation.Services.MapApi;

namespace ConsoleBot.Tests;

public class WallAwareWhirlTests
{
    // A Travincal temple platform: the barbarian at (4818,1190) below its wall, the council on top.
    private static readonly string[] TempleRows =
    [
        "..######........................######...",
        "..######........................#........",
        "..#.####........................#.O......",
        "..#..###............m...........#........",
        "..#...##..........m..m.m........#........",
        "..#....######....#....#....######........",
        "..#....##########################........",
        "..#.....##########################.......",
        "..#.....##########################.......",
        "..#......................................",
        "..#.................@.............O......",
    ];

    private static AreaMap Temple() => new()
    {
        LevelOrigin = new Point(4798, 1180),
        Map = TempleRows.Select(row => row.Select(c => c == '#' ? 1 : 0).ToArray()).ToArray(),
    };

    private static readonly Point Barbarian = new(4818, 1190);

    [Fact]
    public void ALaneOverThePlatformWallIsBlocked()
    {
        Assert.False(WallAwareWhirl.IsLaneClear(Temple(), Barbarian, new Point(4820, 1183)));
    }

    [Fact]
    public void ALaneAlongTheFloorIsClear()
    {
        Assert.True(WallAwareWhirl.IsLaneClear(Temple(), Barbarian, new Point(4830, 1190)));
        Assert.True(WallAwareWhirl.IsLaneClear(Temple(), Barbarian, new Point(4806, 1190)));
    }

    [Fact]
    public void ALaneIntoTheSideWallIsBlocked()
    {
        Assert.False(WallAwareWhirl.IsLaneClear(Temple(), Barbarian, new Point(4799, 1190)));
    }

    [Fact]
    public void ALaneOffTheMapIsBlocked()
    {
        Assert.False(WallAwareWhirl.IsLaneClear(Temple(), Barbarian, new Point(4818, 1195)));
    }

    [Fact]
    public void FromOpenGroundTheSlackDoesNotCarryALaneThroughTheThickWall()
    {
        Assert.False(WallAwareWhirl.IsLaneClear(Temple(), new Point(4818, 1189), new Point(4818, 1184), WallAwareWhirl.EdgeSlack));
    }

    [Fact]
    public void NoMoreThanTwoBlockedCellsAreSkipped()
    {
        var map = new AreaMap
        {
            LevelOrigin = new Point(100, 100),
            Map = Enumerable.Range(0, 20).Select(row => Enumerable.Repeat(row is >= 5 and <= 8 ? 1 : 0, 20).ToArray()).ToArray(),
        };

        Assert.False(WallAwareWhirl.IsLaneClear(map, new Point(110, 105), new Point(110, 108), WallAwareWhirl.EdgeSlack));
        Assert.True(WallAwareWhirl.IsLaneClear(map, new Point(110, 105), new Point(110, 107), WallAwareWhirl.EdgeSlack));
    }

    [Fact]
    public void AMonsterStandingInTheWallStillGetsAFlankSpot()
    {
        var map = WallAlongTheTop();
        var inTheWall = new Point(110, 108);

        var flank = WallAwareWhirl.FlankSpot(map, new Point(113, 115), inTheWall, [inTheWall]);

        Assert.NotNull(flank);
        Assert.Equal(109, flank!.Start.Y);
        Assert.Equal(1, flank.Covered);
        Assert.True(flank.Start.Distance(flank.End) >= WhirlwindPlanner.MinimumSpin);
    }

    [Fact]
    public void TheDirectionAWhirlFailedAlongIsRefusedForAFewSecondsAndOnlyWithinThirtyDegrees()
    {
        var failed = new FailedWhirls();
        var at = new DateTime(2026, 11, 15, 12, 0, 0);
        failed.Record(Barbarian, new Point(4828, 1190), at);

        Assert.True(failed.Refuses(Barbarian, new Point(4830, 1190), at.AddSeconds(2)));
        Assert.True(failed.Refuses(Barbarian, new Point(4828, 1194), at.AddSeconds(2)));
        Assert.True(failed.Refuses(new Point(4819, 1190), new Point(4830, 1190), at.AddSeconds(2)));
        Assert.False(failed.Refuses(Barbarian, new Point(4826, 1196), at.AddSeconds(2)));
        Assert.False(failed.Refuses(Barbarian, new Point(4808, 1190), at.AddSeconds(2)));
        Assert.False(failed.Refuses(Barbarian, Barbarian, at.AddSeconds(2)));
        Assert.False(failed.Refuses(Barbarian, new Point(4830, 1190), at.AddSeconds(3)));
    }

    [Fact]
    public void MovingOnClearsAFailedWhirl()
    {
        var failed = new FailedWhirls();
        var at = new DateTime(2026, 11, 15, 12, 0, 0);
        failed.Record(Barbarian, new Point(4828, 1190), at);

        Assert.False(failed.Refuses(new Point(4821, 1190), new Point(4831, 1190), at.AddSeconds(1)));
    }

    [Fact]
    public void ThePathedWalkRanksTheFlankSpots()
    {
        var near = new FlankCandidate(new Point(1, 1), new Point(9, 1), 1);
        var far = new FlankCandidate(new Point(2, 2), new Point(10, 2), 2);

        Assert.Same(far, WallAwareWhirl.BestFlank([(near, 12), (far, 5)]));
        Assert.Same(far, WallAwareWhirl.BestFlank([(near, 5), (far, 6.5)]));
        Assert.Same(near, WallAwareWhirl.BestFlank([(near, 5), (far, 8)]));
    }

    private const double MinimumWhirl = 5;

    private static (Point Target, int Covered) ClippedAim(AreaMap map, Point from, List<Point> pack)
        => WhirlwindPlanner.Aim(from, pack, end => WallAwareWhirl.ClipLane(map, from, end), MinimumWhirl);

    [Fact]
    public void ThePlannerFindsNoWhirlIntoTheWall()
    {
        var council = new List<Point> { new(4818, 1183), new(4816, 1184), new(4819, 1184), new(4821, 1184) };

        Assert.Null(ClippedAim(Temple(), Barbarian, council).Target);
        Assert.NotNull(WhirlwindPlanner.Aim(Barbarian, council).Target);
    }

    [Fact]
    public void ThePlannerKeepsAWhirlAlongTheFloor()
    {
        var map = Temple();
        var pack = new List<Point> { new(4824, 1190), new(4826, 1189) };

        var aim = ClippedAim(map, Barbarian, pack);

        Assert.NotNull(aim.Target);
        Assert.True(WallAwareWhirl.IsLaneClear(map, Barbarian, aim.Target));
    }

    [Fact]
    public void APackWithItsBackToTheWallIsWhirledFromTheOpenSide()
    {
        var map = Temple();
        var onThePlatform = new Point(4819, 1180);
        var pack = new List<Point> { new(4819, 1184), new(4818, 1183) };

        var aim = ClippedAim(map, onThePlatform, pack);

        Assert.NotNull(aim.Target);
        Assert.True(aim.Covered >= 1);
        Assert.True(WallAwareWhirl.IsLaneClear(map, onThePlatform, aim.Target));
        Assert.True(aim.Target.Y <= 1185);
    }

    [Fact]
    public void AMonsterBesideHimWithAWallAtItsBackIsWhirledAlongTheWall()
    {
        var map = new AreaMap
        {
            LevelOrigin = new Point(100, 100),
            Map = Enumerable.Range(0, 30).Select(row => Enumerable.Repeat(row <= 8 ? 1 : 0, 30).ToArray()).ToArray(),
        };
        var me = new Point(110, 110);
        var monster = new Point(110, 109);

        var aim = ClippedAim(map, me, [monster]);

        Assert.NotNull(aim.Target);
        Assert.Equal(1, aim.Covered);
        Assert.True(me.Distance(aim.Target) >= WhirlwindPlanner.MinimumSpin);
        Assert.True(WallAwareWhirl.IsLaneClear(map, me, aim.Target));
    }

    private static AreaMap WallAlongTheTop() => new()
    {
        LevelOrigin = new Point(100, 100),
        Map = Enumerable.Range(0, 30).Select(row => Enumerable.Repeat(row <= 8 ? 1 : 0, 30).ToArray()).ToArray(),
    };

    [Fact]
    public void AMonsterAgainstTheWallIsWhirledFromTheNearerSideAlongTheWall()
    {
        var map = WallAlongTheTop();
        var monster = new Point(110, 109);
        var offCentreToTheEast = new Point(113, 115);

        var flank = WallAwareWhirl.FlankSpot(map, offCentreToTheEast, monster, [monster]);

        Assert.NotNull(flank);
        var (start, end, covered) = flank!;
        Assert.Equal(109, start.Y);
        Assert.InRange(start.X, 113, 115);
        Assert.Equal(109, end.Y);
        Assert.True(end.X < monster.X);
        Assert.True(start.Distance(end) >= WhirlwindPlanner.MinimumSpin);
        Assert.Equal(1, covered);
        Assert.True(WallAwareWhirl.IsLaneClear(map, start, end));
    }

    [Fact]
    public void FromTheWestTheWestSideIsTaken()
    {
        var monster = new Point(110, 109);

        var flank = WallAwareWhirl.FlankSpot(WallAlongTheTop(), new Point(106, 115), monster, [monster]);

        Assert.NotNull(flank);
        Assert.True(flank!.Start.X < monster.X);
        Assert.True(flank!.End.X > monster.X);
    }

    [Fact]
    public void AnAboutEqualWalkGoesToTheSideSweepingMore()
    {
        var monster = new Point(110, 109);
        var westOfIt = new Point(104, 109);

        var flank = WallAwareWhirl.FlankSpot(WallAlongTheTop(), new Point(110, 115), monster, [monster, westOfIt]);

        Assert.NotNull(flank);
        Assert.Equal(2, flank!.Covered);
        Assert.True(flank!.Start.X > monster.X);
    }

    // A corridor one cell wide running south from a wall at y 108, along x 110.
    private static AreaMap Corridor() => new()
    {
        LevelOrigin = new Point(100, 100),
        Map = Enumerable.Range(0, 30).Select(row => Enumerable.Range(0, 30).Select(column => column == 10 && row >= 9 ? 0 : 1).ToArray()).ToArray(),
    };

    [Fact]
    public void AnotherMemberInTheOpenIsWhirledBeforeWalkingToOneAgainstTheWall()
    {
        var me = new Point(110, 111);
        var againstTheWall = new Point(110, 109);
        var inTheOpen = new Point(110, 117);

        var plan = WallAwareWhirl.PlanWhirl(Corridor(), me, [againstTheWall, inTheOpen], MinimumWhirl);

        Assert.Null(plan.WalkTo);
        Assert.NotNull(plan.Target);
        Assert.True(plan.Target.Y > me.Y);
        Assert.Equal(1, plan.Covered);
    }

    [Fact]
    public void WithNoOpenWhirlAndNoRoomBesideItThereIsNoPlan()
    {
        var plan = WallAwareWhirl.PlanWhirl(Corridor(), new Point(110, 111), [new Point(110, 109)], MinimumWhirl);

        Assert.Null(plan.Target);
        Assert.Null(plan.WalkTo);
    }

    [Fact]
    public void WithNoOpenWhirlFromHereHeWalksBesideIt()
    {
        var map = WallAlongTheTop();
        var monster = new Point(110, 109);
        var nose = new Point(110, 110);
        map.Map[10] = Enumerable.Range(0, 30).Select(column => column == 10 ? 0 : 1).ToArray();
        map.Map[11] = Enumerable.Range(0, 30).Select(column => column == 10 ? 0 : 1).ToArray();

        var plan = WallAwareWhirl.PlanWhirl(map, nose, [monster], MinimumWhirl);

        Assert.NotNull(plan.WalkTo);
        Assert.Equal(109, plan.WalkTo.Y);
        Assert.NotNull(plan.Target);
        Assert.Equal(109, plan.Target.Y);
    }

    [Fact]
    public void ALaneIsClippedAtTheLastWalkableCell()
    {
        var clipped = WallAwareWhirl.ClipLane(Temple(), new Point(4819, 1180), new Point(4819, 1190));

        Assert.Equal((4819, 1185), (clipped.X, clipped.Y));
    }

    [Fact]
    public void AClearLaneIsNotClipped()
    {
        var clipped = WallAwareWhirl.ClipLane(Temple(), Barbarian, new Point(4830, 1190));

        Assert.Equal((4830, 1190), (clipped.X, clipped.Y));
    }

    [Fact]
    public void StandingOnTheWallsEdgeDoesNotCloseEveryLane()
    {
        Assert.True(WallAwareWhirl.IsLaneClear(Temple(), new Point(4815, 1186), new Point(4815, 1182)));
    }

    [Fact]
    public void AMonsterHalfInTheWallCanBeReached()
    {
        var map = Temple();
        var onThePlatform = new Point(4819, 1180);
        var inTheWall = new Point(4819, 1186);

        Assert.False(WallAwareWhirl.IsLaneClear(map, onThePlatform, inTheWall));
        Assert.True(WallAwareWhirl.IsLaneClear(map, onThePlatform, inTheWall, WallAwareWhirl.EdgeSlack));
    }

    [Fact]
    public void AWhirlThatNeverStartedNorMovedNorHurtWentNowhere()
    {
        Assert.True(WallAwareWhirl.WentNowhere(Barbarian, new Point(4819, 1189), 400, 400, started: false));
    }

    [Fact]
    public void AShortWhirlTheServerRanWentSomewhere()
    {
        Assert.False(WallAwareWhirl.WentNowhere(Barbarian, new Point(4819, 1189), 400, 400, started: true));
    }

    [Fact]
    public void AWhirlTheServerRanOnTheSpotWentNowhere()
    {
        Assert.True(WallAwareWhirl.WentNowhere(Barbarian, Barbarian, 400, 400, started: true));
    }

    [Fact]
    public void AWhirlThatHurtThePackWentSomewhere()
    {
        Assert.False(WallAwareWhirl.WentNowhere(Barbarian, Barbarian, 400, 380, started: false));
    }

    [Fact]
    public void AWhirlThatMovedHimWentSomewhere()
    {
        Assert.False(WallAwareWhirl.WentNowhere(Barbarian, new Point(4824, 1190), 400, 400, started: false));
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static readonly DateTime Start = new(2026, 11, 15, 12, 0, 0);

    [Fact]
    public void SwitchingBetweenTheSameMembersIsNoProgress()
    {
        var progress = new KillProgress(Limit);
        Assert.False(progress.Stalled(1, 7, 100, Start));
        Assert.False(progress.Stalled(2, 8, 100, Start.AddSeconds(1)));

        for (var second = 2; second <= 15; second++)
        {
            Assert.False(progress.Stalled((uint)(second % 2 + 1), 7.5, 100, Start.AddSeconds(second)));
        }

        Assert.True(progress.Stalled(1, 7.5, 100, Start.AddSeconds(17)));
    }

    [Fact]
    public void GettingCloserThanEverIsProgress()
    {
        var progress = new KillProgress(Limit);
        progress.Stalled(1, 20, 100, Start);

        Assert.False(progress.Stalled(1, 12, 100, Start.AddSeconds(14)));
        Assert.False(progress.Stalled(1, 12, 100, Start.AddSeconds(28)));
        Assert.True(progress.Stalled(1, 12, 100, Start.AddSeconds(30)));
    }

    [Fact]
    public void WalkingAwayAndBackIsNoProgress()
    {
        var progress = new KillProgress(Limit);
        progress.Stalled(1, 7, 100, Start);
        progress.Stalled(1, 25, 100, Start.AddSeconds(5));

        Assert.True(progress.Stalled(1, 7, 100, Start.AddSeconds(16)));
    }

    [Fact]
    public void HurtingTheTargetIsProgress()
    {
        var progress = new KillProgress(Limit);
        progress.Stalled(1, 7, 100, Start);

        Assert.False(progress.Stalled(1, 7, 90, Start.AddSeconds(14)));
        Assert.False(progress.Stalled(1, 7, 90, Start.AddSeconds(28)));
    }

    [Fact]
    public void AfterGivingUpTheNextTargetGetsAFullLimit()
    {
        var progress = new KillProgress(Limit);
        progress.Stalled(1, 7, 100, Start);
        progress.Stalled(2, 8, 100, Start);
        Assert.True(progress.Stalled(1, 7, 100, Start.AddSeconds(16)));

        progress.GaveUp(Start.AddSeconds(16));

        Assert.False(progress.Stalled(2, 8, 100, Start.AddSeconds(20)));
        Assert.True(progress.Stalled(2, 8, 100, Start.AddSeconds(32)));
    }

    [Fact]
    public void AShortDetourIsHisSide()
    {
        Assert.True(WallAwareWhirl.IsShortDetour(10, 13));
        Assert.False(WallAwareWhirl.IsShortDetour(7, 40));
        Assert.False(WallAwareWhirl.IsShortDetour(7, 0));
    }

    [Fact]
    public void PathLengthStartsFromHim()
    {
        Assert.Equal(10, WallAwareWhirl.PathLength(new Point(0, 0), [new Point(3, 4), new Point(6, 8)]), 3);
    }

    private sealed record Member(string Name, double Distance, bool MySide);

    [Fact]
    public async Task AReachableMemberAtNineBeatsOneAcrossTheWallAtSix()
    {
        var across = new Member("across", 6, false);
        var reachable = new Member("reachable", 9, true);

        var (pick, skipped) = await WallAwareWhirl.ChooseMySideFirst([reachable, across], m => m.Distance, m => Task.FromResult(m.MySide));

        Assert.Same(reachable, pick);
        Assert.Same(across, Assert.Single(skipped));
    }

    [Fact]
    public async Task WithNoneOnHisSideTheNearestIsTaken()
    {
        var near = new Member("near", 6, false);
        var far = new Member("far", 9, false);

        var (pick, skipped) = await WallAwareWhirl.ChooseMySideFirst([far, near], m => m.Distance, m => Task.FromResult(m.MySide));

        Assert.Same(near, pick);
        Assert.Empty(skipped);
    }

    [Fact]
    public void AfterAManaPotionTheNextWaits()
    {
        var pacer = new ManaPotionPacer(30, TimeSpan.FromSeconds(1.5));
        var drank = new DateTime(2026, 11, 15, 12, 0, 0);
        pacer.Drank(drank);

        Assert.True(pacer.Waiting(20, drank.AddMilliseconds(700)));
        Assert.False(pacer.Waiting(20, drank.AddSeconds(1.6)));
    }

    [Fact]
    public void ManaClimbingPastTheFloorEndsTheWait()
    {
        var pacer = new ManaPotionPacer(30, TimeSpan.FromSeconds(1.5));
        var drank = new DateTime(2026, 11, 15, 12, 0, 0);
        pacer.Drank(drank);

        Assert.False(pacer.Waiting(60, drank.AddMilliseconds(500)));
        Assert.False(pacer.Waiting(20, drank.AddMilliseconds(800)));
    }

    [Fact]
    public void WithoutADrinkNothingWaits()
    {
        Assert.False(new ManaPotionPacer(30, TimeSpan.FromSeconds(1.5)).Waiting(10, DateTime.Now));
    }
}
