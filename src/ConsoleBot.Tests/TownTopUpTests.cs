using ConsoleBot.Bots.Types;
using ConsoleBot.TownManagement;
using D2NG.Core.D2GS.Act;

namespace ConsoleBot.Tests;

public class TownTopUpTests
{
    [Fact]
    public void A_top_up_heals_buys_and_repairs_and_does_nothing_else()
    {
        var options = new TownManagementOptions(new AccountConfig(), Act.Act1) { TopUpOnly = true };

        Assert.True(options.Runs(TownStep.Refresh));
        Assert.True(options.Runs(TownStep.Repair));
        Assert.True(options.Runs(TownStep.ResurrectMerc));
        Assert.False(options.Runs(TownStep.Identify));
        Assert.False(options.Runs(TownStep.SellRun));
        Assert.False(options.Runs(TownStep.Stash));
        Assert.False(options.Runs(TownStep.Gamble));
    }

    [Fact]
    public void A_full_visit_runs_every_step()
    {
        var options = new TownManagementOptions(new AccountConfig(), Act.Act1);

        Assert.All(Enum.GetValues<TownStep>(), step => Assert.True(options.Runs(step)));
    }
}
