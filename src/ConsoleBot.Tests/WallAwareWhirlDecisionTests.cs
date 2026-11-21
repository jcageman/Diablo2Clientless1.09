using ConsoleBot.Attack;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Services.MapApi;

namespace ConsoleBot.Tests;

public class WallAwareWhirlDecisionTests
{
    /// <summary>The point the barbarian kept whirling at from the Travincal moment, across the temple's edge.</summary>
    private static readonly Point AskedAcrossTheWall = new(4820, 1178);

    private static (AreaMap Map, Point Me, List<Foe> Foes) Wall(string model = "legacy")
    {
        var moment = MomentFixture.Load("travincal-whirl-wall.json");
        var foes = moment.Monsters(model).Select(m => new Foe(m.Id, m.NPCCode, m.Location, m.State, m.LifePercentage)).ToList();
        return (moment.Map(), moment.Self, foes);
    }

    private static readonly DateTime FailedAt = new(2026, 11, 15, 12, 0, 0);

    private static Decision Decide(AreaMap map, Point me, IReadOnlyList<Foe> foes, Point asked, FailedWhirls? failed = null, IReadOnlySet<uint>? givenUp = null)
        => WallAwareWhirlDecision.Decide(map, me, foes, asked, (a, b) => GridPath.Length(map, a, b),
            failed == null ? null : end => failed.Refuses(me, end, FailedAt.AddSeconds(1)), givenUp);

    private static FailedWhirls FailedToward(Point from, Point toward)
    {
        var failed = new FailedWhirls();
        failed.Record(from, toward, FailedAt);
        return failed;
    }

    [Fact]
    public void TheWhirlThatWentNowhereIsNotWhatTheWallAwarePlannerChooses()
    {
        var (map, me, foes) = Wall();

        Assert.False(WallAwareWhirl.IsLaneClear(map, me, AskedAcrossTheWall, WallAwareWhirl.EdgeSlack));
        var decision = Decide(map, me, foes, AskedAcrossTheWall);

        Assert.NotEqual(WhirlChoice.Same, decision.Choice);
        Assert.Contains(decision.Choice, new[] { WhirlChoice.Blocked, WhirlChoice.WalkAlongWall, WhirlChoice.Approach });
        Assert.True(decision.Target == null || WallAwareWhirl.IsLaneClear(map, decision.WalkTo ?? me, decision.Target, WallAwareWhirl.EdgeSlack));
    }

    [Fact]
    public void TheCouncilAcrossTheWallIsNotOnHisSideByAnyShortPath()
    {
        var (map, me, foes) = Wall();
        var council = foes.Where(f => f.State == EntityState.Alive && f.Code is NPCCode.CouncilMember1 or NPCCode.CouncilMember2).ToList();

        Assert.NotEmpty(council);
        Assert.All(council, f => Assert.False(GridPath.Length(map, me, f.At) is { } path && WallAwareWhirl.IsShortDetour(me.Distance(f.At), path)));
    }

    private static AreaMap Open(int size = 60, Func<int, int, int>? value = null)
        => new()
        {
            LevelOrigin = new Point(1000, 1000),
            Map = [.. Enumerable.Range(0, size).Select(y => Enumerable.Range(0, size).Select(x => value?.Invoke(x, y) ?? 0).ToArray())],
            AdjacentLevels = [],
            Npcs = [],
            Objects = [],
        };

    private static Foe Member(uint id, ushort x, ushort y) => new(id, NPCCode.CouncilMember1, new Point(x, y), EntityState.Alive, 100);

    [Fact]
    public void OnOpenGroundTheSameWhirlIsSameAndAnotherPointIsADifferentWhirl()
    {
        var map = Open();
        var me = new Point(1020, 1030);
        List<Foe> foes = [Member(1, 1025, 1030)];

        var plan = Decide(map, me, foes, null);
        Assert.Equal(WhirlChoice.DifferentWhirl, plan.Choice);
        Assert.Equal([1u], plan.AimAt);
        Assert.Equal(WhirlChoice.Same, Decide(map, me, foes, plan.Target).Choice);
        Assert.Equal(WhirlChoice.DifferentWhirl, Decide(map, me, foes, new Point(1020, 1040)).Choice);
        Assert.Equal(WhirlChoice.Approach, Decide(map, me, [Member(1, 1035, 1030)], null).Choice);
        Assert.Equal(WhirlChoice.NoTarget, Decide(map, me, [], null).Choice);
    }

    [Fact]
    public void TheBotsOwnFiltersDecideWhoIsAnEnemyAndAMemberGivenUpIsNotPicked()
    {
        var map = Open();
        var me = new Point(1020, 1030);
        var dying = Member(1, 1025, 1030) with { State = EntityState.Dieing };
        var lifeless = Member(2, 1024, 1030) with { Life = 0 };

        Assert.Equal(WhirlChoice.NoTarget, Decide(map, me, [dying], null).Choice);
        Assert.NotEqual(WhirlChoice.NoTarget, Decide(map, me, [lifeless], null).Choice);
        Assert.Equal(2u, Decide(map, me, [lifeless], null).Pick);
        var near = Member(3, 1023, 1030);
        var far = Member(4, 1027, 1036);
        Assert.Equal(3u, Decide(map, me, [near, far], null).Pick);
        Assert.Equal(4u, Decide(map, me, [near, far], null, givenUp: new HashSet<uint> { 3 }).Pick);
    }

    [Fact]
    public void AMonsterOnlyAlongTheDirectionAWhirlJustFailedIsWhirledAnotherWayOrApproachedAround()
    {
        var map = Open();
        var me = new Point(1020, 1030);
        List<Foe> foes = [Member(1, 1025, 1030)];

        var decision = Decide(map, me, foes, null, FailedToward(me, new Point(1031, 1030)));

        Assert.NotEqual(WhirlChoice.Same, decision.Choice);
        Assert.True(decision.Target == null || decision.WalkTo != null || !FailedWhirls.SameDirection(me, new Point(1031, 1030), me, decision.Target));
        Assert.Equal(WhirlChoice.DifferentWhirl, Decide(map, me, foes, null).Choice);
    }

    [Fact]
    public void AMonsterAtHisOwnPositionAfterAFailedWhirlGetsAWhirlInAnotherDirection()
    {
        var map = Open();
        var me = new Point(1020, 1030);
        var failedToward = new Point(1013, 1031);
        List<Foe> foes = [Member(1, 1020, 1030), Member(2, 1014, 1031)];

        var decision = Decide(map, me, foes, failedToward, FailedToward(me, failedToward));

        Assert.Equal(WhirlChoice.DifferentWhirl, decision.Choice);
        Assert.NotNull(decision.Target);
        Assert.False(FailedWhirls.SameDirection(me, failedToward, me, decision.Target));
    }

    [Fact]
    public void AMonsterBehindAFullWallIsBlockedAndAPathAroundIsMeasuredOnTheGrid()
    {
        var map = Open(value: (x, y) => x == 25 && y < 50 ? 7 : 0);
        var me = new Point(1020, 1030);
        List<Foe> foes = [Member(1, 1030, 1030)];

        Assert.Equal(WhirlChoice.Blocked, Decide(map, me, foes, new Point(1036, 1030)).Choice);
        var path = GridPath.Length(map, me, foes[0].At);
        Assert.NotNull(path);
        Assert.InRange(path.Value, 40, 50);
        Assert.Null(GridPath.Length(Open(value: (x, y) => x == 25 ? 7 : 0), me, foes[0].At));
    }
}
