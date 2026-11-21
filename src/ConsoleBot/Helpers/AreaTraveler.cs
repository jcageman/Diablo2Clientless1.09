using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.Tracing;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

/// <summary>
/// Moves a character from area to area along the map's route, crossing each border by path and warp.
/// Lifted out of the rush bot so the leveling party walks the same verified crossing logic; a walking
/// party needs the steps apart, to fight between finding the crossing and taking it.
/// </summary>
public sealed class AreaTraveler(IPathingService pathingService, IMapApiService mapApiService)
{
    private const int AreaHopAttempts = 3;

    /// <summary>The map api returns a different layout per difficulty for one map id; the bots path on Normal's.</summary>
    public const Difficulty MapDifficulty = Difficulty.Normal;

    public async Task<IReadOnlyList<Area>> Route(Client client, Area destination)
        => await pathingService.GetAreaRoute(client.Game.MapId, MapDifficulty, await CurrentArea(client), destination);

    /// <summary>
    /// The area the character stands in by its position, not by the area last reported: a Hell party stood at the Stony
    /// Fields waypoint while the client still said Rogue Encampment, and every walk out of "town" failed for minutes.
    /// </summary>
    public async Task<Area> CurrentArea(Client client)
    {
        var at = client.Game.Me.Location;
        var area = await pathingService.ResolveArea(client.Game.MapId, MapDifficulty, client.Game.Area, client.Game.Act, at);
        if (at == null || await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, at))
        {
            return area;
        }

        // On a waypoint tile, which no map counts as walkable, the reported area stays: two members stood on Travincal's
        // waypoint "in Kurast Docks" and walked for the Spider Forest. The ground a step away says which level it is.
        var areas = Enum.GetValues<Area>().Where(candidate => candidate != Area.None && candidate.MapToAct() == client.Game.Act).ToList();
        foreach (var step in new[] { 3, 6 })
        {
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) })
            {
                var near = new Point((ushort)(at.X + dx * step), (ushort)(at.Y + dy * step));
                foreach (var candidate in areas)
                {
                    if (await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, candidate, near))
                    {
                        return candidate;
                    }
                }
            }
        }

        return area;
    }

    public async Task<bool> TraverseTo(Client client, Area destination)
    {
        var route = await Route(client, destination);
        if (route.Count == 0)
        {
            Log.Error("No route from {From} to {Destination}", client.Game.Area, destination);
            return false;
        }

        foreach (var target in route.Skip(1))
        {
            var entered = false;
            for (var attempt = 0; attempt < AreaHopAttempts && !entered; attempt++)
            {
                // Chickened out of the game, the client kept "teleporting" from where it left for minutes.
                if (!client.Game.IsInGame())
                {
                    return false;
                }

                var mode = MovementHelpers.PreferredMovement(client.Game);
                var from = await CurrentArea(client);
                var path = await PathIntoAdjacent(client, from, target, attempt);
                if (path == null)
                {
                    TraceEvents.Step(client.Game, $"cross {from}->{target}", StepPhase.End, attempt + 1, StepOutcome.Failed, "no path");
                    await Task.Delay(1000);
                    continue;
                }

                // Teleport tops out around 30 units and the planner strides right at that ceiling, so a
                // hop that will not land leaves the character parked repeating the same failed path.
                var moved = await MovementHelpers.TakePathOfLocations(client.Game, path, mode);
                if (!moved && mode == MovementMode.Teleport)
                {
                    Log.Information("Teleport path stalled at {Location} on the way to {Target}; walking it instead", client.Game.Me.Location, target);
                    await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking);
                }

                entered = await FinishCrossing(client, from, target);
                TraceEvents.Step(client.Game, $"cross {from}->{target}", StepPhase.End, attempt + 1, entered ? StepOutcome.Ok : StepOutcome.Failed, () => entered ? null : $"not in {target} at {client.Game.Me?.Location}");
            }

            if (!entered)
            {
                Log.Error("{Name} stopped in {Area} while trying to enter {Target}", client.Game.Me.Name, client.Game.Area, target);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The path from where the character stands to the crossing into <paramref name="target"/>, or null
    /// when none could be found this attempt. A later attempt steps aside first: the crossing search
    /// ranks candidates by distance, so retrying from the same spot returns the same unreachable one.
    /// </summary>
    public async Task<List<Point>> PathIntoAdjacent(Client client, Area from, Area target, int attempt)
    {
        // Straight off a waypoint the cached location still holds the old one, and pathing snaps the
        // destination but never the source.
        client.Game.RequestUpdate(client.Game.Me.Id);
        await Task.Delay(500);
        if (!await SyncPosition(client, from))
        {
            // A waypoint tile is unwalkable in the map grid, so every path from it comes back empty.
            await NudgeOntoWalkableGround(client, from);
        }

        if (attempt > 0)
        {
            await StepAside(client, from);
        }

        try
        {
            var path = await pathingService.GetPathToAdjacentArea(
                client.Game.MapId, MapDifficulty, from, client.Game.Me.Location, target, MovementHelpers.PreferredMovement(client.Game));
            Log.Information("CROSSING {From}->{Target} attempt {Attempt}: at {Location}, path {Count} points {First}..{Last}",
                from, target, attempt + 1, client.Game.Me.Location, path.Count,
                path.Count > 0 ? path[0].ToString() : "none", path.Count > 0 ? path[^1].ToString() : "none");
            return path;
        }
        catch (InvalidOperationException exception)
        {
            Log.Warning("No path from {From} into {Target} on attempt {Attempt} at {Location}: {Reason}",
                from, target, attempt + 1, client.Game.Me.Location, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// After walking to a crossing: takes the warp when one is in reach (caves, stairs), and confirms the
    /// character stands in <paramref name="target"/> - outdoor borders need no warp at all.
    /// </summary>
    public async Task<bool> FinishCrossing(Client client, Area from, Area target)
    {
        client.Game.RequestUpdate(client.Game.Me.Id);
        await Task.Delay(400);
        var warp = client.Game.GetNearestWarp();
        Log.Information("CROSSING {From}->{Target} landed at {Location}, nearest warp {Warp} at {Distance:0.0}",
            from, target, client.Game.Me.Location, warp?.Location.ToString() ?? "none",
            warp == null ? -1 : warp.Location.Distance(client.Game.Me.Location));
        if (warp != null && warp.Location.Distance(client.Game.Me.Location) < 25)
        {
            await MovementHelpers.TakeWarp(client.Game, pathingService, mapApiService, MovementHelpers.PreferredMovement(client.Game), warp, target);
        }

        return await ConfirmInArea(client, target);
    }

    /// <summary>
    /// Waits until the position the client believes it is at is one the map agrees is walkable. Position
    /// is stale for a moment after every area change, and pathing from a stale one fails.
    /// </summary>
    public async Task<bool> SyncPosition(Client client, Area area)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, client.Game.Me.Location))
            {
                return true;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(250);
        }

        Log.Warning("{Name} is at {Location} in {Area}, which the map does not consider walkable", client.Game.Me.Name, client.Game.Me.Location, area);
        return false;
    }

    /// <summary>
    /// Moves a few units to ground the map considers walkable, using direct movement rather than the
    /// pathing service, which cannot produce a path out of an unwalkable source in the first place.
    /// </summary>
    public async Task<bool> NudgeOntoWalkableGround(Client client, Area area)
    {
        var from = client.Game.Me.Location;
        foreach (var (dx, dy) in new[] { (0, 18), (18, 0), (0, -18), (-18, 0), (13, 13), (-13, 13), (13, -13), (-13, -13) })
        {
            var candidate = new Point((ushort)(from.X + dx), (ushort)(from.Y + dy));
            if (!await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, candidate))
            {
                continue;
            }

            // Not MoveTo: that reports success without sending anything under its no-op range, which is
            // the distance this step lives in.
            await client.Game.MoveToAsync(candidate);
            await Task.Delay(400);
            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(300);
            if (await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, client.Game.Me.Location))
            {
                Log.Information("Stepped off unwalkable ground at {From} onto {To}", from, client.Game.Me.Location);
                return true;
            }
        }

        Log.Warning("Could not step off unwalkable ground at {From} in {Area}", from, area);
        return false;
    }

    public async Task StepAside(Client client, Area area)
    {
        var from = client.Game.Me.Location;
        var mode = MovementHelpers.PreferredMovement(client.Game);
        foreach (var (dx, dy) in new[] { (0, -40), (40, 0), (0, 40), (-40, 0), (30, -30), (-30, 30) })
        {
            var candidate = new Point((ushort)(from.X + dx), (ushort)(from.Y + dy));
            if (!await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, candidate))
            {
                continue;
            }

            var path = await pathingService.GetPathToLocation(client.Game, candidate, mode);
            if (path.Count > 0 && await MovementHelpers.TakePathOfLocations(client.Game, path, mode))
            {
                Log.Debug("Stepped aside from {From} to {To} before retrying", from, client.Game.Me.Location);
                return;
            }
        }
    }

    /// <param name="attempts">
    /// How many times to re-ask before giving up. Confirming arrival after an interact is worth eight
    /// tries; asking whether we are somewhere before setting off is worth one.
    /// </param>
    public async Task<bool> ConfirmInArea(Client client, Area area, int attempts = 8)
    {
        // The server sends the new position by itself within a tenth of a second of a portal, warp or
        // waypoint, so look before asking.
        if (await GeneralHelpers.TryWithTimeout(async _ =>
            {
                if (await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, client.Game.Me.Location))
                {
                    return true;
                }

                await Task.Delay(50);
                return false;
            }, TimeSpan.FromMilliseconds(300)))
        {
            return true;
        }

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(500);
            if (await pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, client.Game.Me.Location))
            {
                return true;
            }
        }

        return false;
    }
}
