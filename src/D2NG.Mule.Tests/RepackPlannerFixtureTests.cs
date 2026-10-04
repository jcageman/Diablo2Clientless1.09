using D2NG.Mule.Packing;

namespace D2NG.Mule.Tests;

public class RepackPlannerFixtureTests
{
    /// <summary>
    /// A stash recorded off a real full mule. Nothing here can be rearranged, which is the case
    /// the planner has to survive rather than the case it is for.
    /// </summary>
    private static List<StashItem> FullMuleStash()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures",
            "repack-mule.json");
        return MuleFixture.Load(path).StashItems();
    }

    [Fact]
    public void AFullStashThatCannotBeRepackedYieldsNoPlanRatherThanThrowing()
    {
        var stash = FullMuleStash();

        var plan = RepackPlanner.Plan(stash, MuleGrids.StashSize, new OccupancyGrid(10, 4), [new Shape(2, 3)]);

        Assert.Null(plan);
    }
}
