using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Mule;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Mule;

/// <summary>
/// Steps every client that visits a mule game takes, shared by the mule run and the cube bot.
/// </summary>
public static class MuleClientSteps
{
    /// <summary>
    /// Waits until the client knows who and where it is in the game it just entered.
    /// </summary>
    public static async Task<bool> WaitForInitialize(Client client, ILogger logger)
    {
        var timer = new Stopwatch();
        timer.Start();
        while (client.Game.Me == null && timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(100);
        }

        if (client.Game.Me == null)
        {
            logger.LogError("{ClientName} failed to initialize Me", client.Game.Me?.Name);
            return false;
        }

        client.Game.RequestUpdate(client.Game.Me.Id);
        if (!GeneralHelpers.TryWithTimeout(
            (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
            TimeSpan.FromSeconds(5)))
        {
            logger.LogError("{ClientName} failed to initialize current location", client.Game.Me?.Name);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stores what a mule character holds after a visit, so the mule run and the cube bot can plan
    /// without logging on. Does nothing without a mule database.
    /// </summary>
    public static async Task RecordCharacter(IMuleRepository repository, string accountName, string characterName, Game game, ILogger logger)
    {
        if (repository == null)
        {
            return;
        }

        try
        {
            var snapshot = MuleCharacterSnapshot.Take(game, i => D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i));
            await repository.UpdateCharacter(accountName, characterName, snapshot);
            logger.LogInformation("Recorded {Account}-{Character}: {Items} items, {FreeCells} free cells, widest fit per height {Profile}",
                accountName, characterName, snapshot.Items.Count, snapshot.FreeCells, string.Join("/", snapshot.FitProfile.Skip(1)));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Mule database unavailable, not recording {Account}-{Character}", accountName, characterName);
        }
    }
}
