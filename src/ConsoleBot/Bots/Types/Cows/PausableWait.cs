using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// A retry loop like GeneralHelpers.TryWithTimeout whose budget does not run while <c>paused</c> holds, before or after
/// a try. The party waits on a member that is gambling, about 80s, and a wait that counted it ran out: the 70s wait for
/// the portal character's catacombs portal expired during her gamble and gave the whole game up.
/// </summary>
public static class PausableWait
{
    private static readonly TimeSpan BetweenTries = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Paused time allowed on top of the budget, three gambles' worth: a gamble that never ends - listings refreshed with
    /// nothing worth buying and gold that stays up - would otherwise hold every wait on it, and the party in town, forever.
    /// </summary>
    public static readonly TimeSpan MostPaused = TimeSpan.FromMinutes(4);

    public static async Task<bool> Try(Func<int, Task<bool>> action, TimeSpan budget, Func<bool> paused)
    {
        var counted = TimeSpan.Zero;
        var wall = Stopwatch.StartNew();
        for (var retryCount = 0; counted < budget && wall.Elapsed < budget + MostPaused; retryCount++)
        {
            var lap = Stopwatch.StartNew();
            var pausedBefore = paused();
            if (await action(retryCount))
            {
                return true;
            }

            await Task.Delay(BetweenTries);
            if (!pausedBefore && !paused())
            {
                counted += lap.Elapsed;
            }
        }

        return false;
    }
}
