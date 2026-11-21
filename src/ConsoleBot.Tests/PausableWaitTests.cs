using ConsoleBot.Bots.Types.Cows;
using System;
using System.Threading.Tasks;

namespace ConsoleBot.Tests;

public class PausableWaitTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_try_that_succeeds_ends_the_wait_at_once()
    {
        var tries = 0;
        Assert.True(await PausableWait.Try(_ => Task.FromResult(++tries == 1), Budget, () => false));
        Assert.Equal(1, tries);
    }

    [Fact]
    public async Task Without_a_pause_the_wait_gives_up_after_its_budget()
    {
        var tries = 0;
        Assert.False(await PausableWait.Try(_ => Task.FromResult(++tries < 0), Budget, () => false));
        Assert.InRange(tries, 1, 8);
    }

    [Fact]
    public async Task Time_while_paused_does_not_use_up_the_budget()
    {
        // Paused for the first twenty tries, each 20ms or more apart: four times the budget, none of it counted.
        var tries = 0;
        Assert.False(await PausableWait.Try(_ => Task.FromResult(++tries < 0), Budget, () => tries <= 20));
        Assert.True(tries > 21, $"gave up after {tries} tries");
    }

    [Fact]
    public async Task A_try_that_succeeds_during_a_pause_still_ends_the_wait()
    {
        var tries = 0;
        Assert.True(await PausableWait.Try(_ => Task.FromResult(++tries == 15), Budget, () => true));
        Assert.Equal(15, tries);
    }
}
