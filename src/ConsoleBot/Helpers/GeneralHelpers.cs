using D2NG.Core;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

public static class GeneralHelpers
{
    public static bool TryWithTimeout(Func<int, bool> action, TimeSpan timeout)
    {
        bool success = false;
        TimeSpan elapsed = TimeSpan.Zero;
        int retryCount = 0;
        while ((!success) && (elapsed < timeout))
        {
            Stopwatch sw = new();
            sw.Start();
            success = action(retryCount);
            if(!success)
            {
                Thread.Sleep(20);
            }
            sw.Stop();
            elapsed += sw.Elapsed;
            retryCount++;
        }

        return success;
    }

    public static async Task<bool> TryWithTimeout(Func<int, Task<bool>> action, TimeSpan timeout)
    {
        bool success = false;
        TimeSpan elapsed = TimeSpan.Zero;
        int retryCount = 0;
        while ((!success) && (elapsed < timeout))
        {
            Stopwatch sw = new();
            sw.Start();
            success = await action(retryCount);
            if (!success)
            {
                await Task.Delay(20);
            }
            sw.Stop();
            elapsed += sw.Elapsed;
            retryCount++;
        }

        return success;
    }

    public static async Task<bool> PickupCorpseIfExists(Client client, IPathingService pathingService)
    {
        var corpseId = client.Game.Me.CorpseId;
        if (corpseId != null)
        {
            var pickupSucceeded = false;
            var corpse = client.Game.Players.FirstOrDefault(p => p.Id == corpseId);
            Log.Information("Found corpse {CorpseId} for {ClientName}, trying to pickup", corpse.Id, client.LoggedInUserName());
            pickupSucceeded = await TryWithTimeout(async (retryCount) =>
            {
                if (client.Game.Me.Location.Distance(corpse.Location) > 5)
                {
                    Log.Information("Getting walking path from {FromLocation} to {ToLocation} with distance {Distance}", client.Game.Me.Location, corpse.Location, client.Game.Me.Location.Distance(corpse.Location));
                    var walkingPath = await pathingService.GetPathToLocation(client.Game, corpse.Location, MovementMode.Walking);
                    await MovementHelpers.TakePathOfLocations(client.Game, walkingPath, MovementMode.Walking);
                    return false;
                }

                return client.Game.PickupBody(corpse);
            }, TimeSpan.FromSeconds(5));

            var message = pickupSucceeded ? "succeeded" : "failed";
            Log.Information("Pickup of corpse {Result}", message);
            return pickupSucceeded;
        }

        return true;
    }
}
