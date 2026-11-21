using ConsoleBot.Attack;
using ConsoleBot.Bots.Types.Cows;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;

namespace ConsoleBot.Tests;

/// <summary>
/// Recorded moments the amazon left the cow level as surrounded, replayed through the kiting search the
/// way <c>CowBot.StepBack</c> builds it: the cows within 30 as the bot placed them (simulated motion) and
/// the level's collision grid.
/// </summary>
public class SurroundedMomentTests
{
    [Fact]
    public void Two_cows_just_inside_three_on_either_side_leave_the_open_ground_between_them()
    {
        // Full life, one hit taken: cows at 2.8 north-east and 3 south, open ground east-south-east.
        var (here, search, seen) = Moment("cow-amazon-gap-to-the-east.json");

        Assert.True(Kiting.InDanger(seen));
        var step = Kiting.ChooseStep(search) ?? Kiting.WayOut(search);
        Assert.NotNull(step);
        Assert.True(step.Spot.X > here.X, $"stepped to {step.Spot}, not east");
        Assert.True(step.Nearest > Kiting.NearestDistance(here, search.Cows) + 1);
        Assert.False(Kiting.Surrounded(seen, Kiting.HasWayOut(search)));
    }

    [Theory]
    [InlineData("cow-amazon-landing-pack.json")]
    [InlineData("cow-amazon-dense-pack.json")]
    [InlineData("cow-amazon-ring.json")]
    public void A_pack_closed_around_it_is_still_left(string fixture)
    {
        // Landed in a pack whose only gap gains 0.4; eight and nine cows within five, adjacent ones among them.
        var (_, search, seen) = Moment(fixture);

        Assert.Null(Kiting.ChooseStep(search));
        Assert.Null(Kiting.WayOut(search));
        Assert.True(Kiting.Surrounded(seen, Kiting.HasWayOut(search)));
    }

    private static (Point Here, StepSearch Search, List<CloseCow> Seen) Moment(string fixture)
    {
        var moment = MomentFixture.Load(fixture);
        var map = moment.Map();
        var here = moment.Self;
        var cows = moment.Monsters("simulated")
            .Where(m => !MonsterTracking.IsDead(m.State) && m.Location.Distance(here) <= 30)
            .ToList();
        var search = new StepSearch
        {
            From = here,
            Cows = cows.Select(cow => cow.Location).ToList(),
            LightningEnchanted = cows.Where(KeptAway).Select(cow => cow.Location).ToList(),
            Walkable = point => Cell(map, point) is int cell && AreaMapExtensions.IsMovable(cell),
            InSight = (from, to) => InSight(map, from, to),
        };
        var seen = cows
            .Where(cow => cow.Location.Distance(here) <= Kiting.LightningEnchantedDistance && InSight(map, here, cow.Location))
            .Select(cow => new CloseCow(cow.Location.Distance(here), cow.IsBoss || cow.IsChampion || cow.MonsterEnchantments.Count > 0, KeptAway(cow)))
            .ToList();
        return (here, search, seen);
    }

    private static bool KeptAway(WorldObject cow)
        => cow.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)
            || CowKing.IsCowKing(cow.NPCCode, cow.MonsterEnchantments, cow.IsBossMinion);

    private static int? Cell(AreaMap map, Point point)
    {
        var row = point.Y - map.LevelOrigin.Y;
        var column = point.X - map.LevelOrigin.X;
        return row < 0 || row >= map.Map.Length || column < 0 || column >= map.Map[row].Length ? null : map.Map[row][column];
    }

    private static bool InSight(AreaMap map, Point from, Point to)
        => AttackService.GetPointsOnLine(from.X, from.Y, to.X, to.Y)
            .All(point => Cell(map, point) is int cell && (AreaMapExtensions.IsMovable(cell) || cell == 1));
}
