using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Tracing;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

public static class MovementHelpers
{
    public static async Task<bool> TakeWarp(
        Game game,
        IPathingService pathingService,
        IMapApiService mapApiService,
        MovementMode movementMode,
        WarpData warp,
        Area area)
    {
        Log.Information($"Moving to warp at {warp.Location} from {game.Me.Location}");
        if (!await MoveToLocation(game, pathingService, mapApiService, warp.Location, movementMode))
        {
            return false;
        }

        Log.Information($"Taking warp to {area}");
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (warp.Location.Distance(game.Me.Location) > 5)
            {
                await game.MoveToAsync(warp.Location);
                return false;
            }

            if (!game.TakeWarp(warp))
            {
                return false;
            }

            // Give the answer time to arrive. Reading the position in the same breath as asking for it
            // judged arrival on the stale one, and every retry then re-sent the warp interact.
            game.RequestUpdate(game.Me.Id);
            await Task.Delay(400);
            var isValidPoint = await pathingService.IsNavigatablePointInArea(game.MapId, Difficulty.Normal, area, game.Me.Location);
            return isValidPoint;
        }, TimeSpan.FromSeconds(10)))
        {
            TraceEvents.Move(game, warp.Location, warp.Location, MoveKind.Warp, MoveOutcome.NotNavigable);
            Log.Error("Checking whether moved to area failed");
            return false;
        }
        return true;
    }
    public static async Task<bool> MoveToLocation(Game game,
                                                  IPathingService pathingService,
                                                  IMapApiService mapApiService,
                                                  Point location,
                                                  MovementMode movementMode,
                                                  CancellationToken? token = null)
    {
        if (game.Me.Location.Distance(location) < 10)
        {
            return await game.MoveToAsync(location);
        }
        else
        {
            // game.Area is resolved from the tile being stood in, so asking the map api which area this
            // position is in would only repeat work: it used to be necessary because the area the client
            // reported came from the last map reveal packet, neighbouring levels included.
            var path = await pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, game.Area, game.Me.Location, location, movementMode);
            if (path.Count == 0)
            {
                TraceEvents.Move(game, game.Me.Location, location, MoveKind.PathRequest, MoveOutcome.EmptyPath, "direct move");
            }

            if (path.Count != 0 && !await TakePathOfLocations(game, path, movementMode, token)
                && !(movementMode == MovementMode.Walking && await WalkAroundObjects(game, pathingService, location, token)))
            {
                Log.Warning($"Walking to location failed at {game.Me.Location}");
                // A failed walk returns at once without moving; callers that retry in a loop would spin on it.
                await Task.Delay(200);
                return false;
            }

            await game.MoveToAsync(location);
        }

        return true;
    }

    private const double BlockingObjectReach = 15;

    /// <summary>
    /// A second walk after one got stuck, around the objects seen near the character that the map does not have:
    /// a sewer hall's random baskets stood across the path the map drew, and the server held the paladin against
    /// them for eight minutes. Each object closes its own cell and the four beside it.
    /// </summary>
    private static async Task<bool> WalkAroundObjects(Game game, IPathingService pathingService, Point location, CancellationToken? token)
    {
        var at = game.Me.Location;
        var objects = game.WorldObjects.Values
            .Where(entity => entity.Type == EntityType.Object && entity.Location != null && entity.Location.Distance(at) <= BlockingObjectReach
                && entity.Code is not (EntityCode.TownPortal or EntityCode.RedTownPortal)
                // Doors are passages, not obstacles: Travincal's open gates read as blockers and every walk round them
                // came back "no way around" while the party tried to reach the Compelling Orb.
                && !D2NG.Navigation.Extensions.AreaMapExtensions.DoorObjectCodes.Contains((int)entity.Code))
            .Select(entity => entity.Location).ToList();
        if (objects.Count == 0)
        {
            return false;
        }

        var blocked = objects.SelectMany(cell => new[]
        {
            cell, new Point((ushort)(cell.X - 1), cell.Y), new Point((ushort)(cell.X + 1), cell.Y),
            new Point(cell.X, (ushort)(cell.Y - 1)), new Point(cell.X, (ushort)(cell.Y + 1)),
        }).Where(cell => cell.Distance(at) >= 1).ToList();
        var path = await pathingService.GetWalkingPathAround(game.MapId, Difficulty.Normal, game.Area, at, location, blocked);
        if (path.Count == 0)
        {
            // Close-set braziers left no gap once each closed its neighbours too: Travincal's walks to the Compelling Orb
            // all came back "no way around". The objects' own cells alone, then.
            path = await pathingService.GetWalkingPathAround(game.MapId, Difficulty.Normal, game.Area, at, location,
                objects.Where(cell => cell.Distance(at) >= 1).ToList());
        }

        Log.Information("Stuck at {At}: walking around {Count} objects the map does not show, {Outcome}", at, objects.Count,
            path.Count == 0 ? "no way around" : $"a path of {path.Count} points");
        return path.Count > 0 && await TakePathOfLocations(game, path, MovementMode.Walking, token);
    }

    public static async Task<bool> MoveToWorldObject(Game game,
                                                     IPathingService pathingService,
                                                     IMapApiService mapApiService,
                                                     WorldObject worldObject,
                                                     MovementMode movementMode,
                                                     CancellationToken? token = null)
    {
        if (game.Me.Location.Distance(worldObject.Location) < 10)
        {
            await game.MoveToAsync(worldObject);
        }
        else
        {
            var path = await pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, game.Area, game.Me.Location, worldObject.Location, movementMode);
            if (path.Count != 0 && !await TakePathOfLocations(game, path, movementMode, token))
            {
                Log.Warning($"Walking to enemy to attack failed at {game.Me.Location}");
                return false;
            }

            await game.MoveToAsync(worldObject);
        }

        return true;
    }

    /// <summary>
    /// How this character should move right now. Teleport costs mana, and a sorceress that has run
    /// out cannot teleport at all - the move fails silently, so she cannot reach a merchant to buy
    /// mana potions, and cannot recover. One of ours spent whole sessions like that: no mana potions
    /// drunk, seventy six failed moves, and not a single attack. Walking is slow; being stuck is
    /// permanent.
    /// </summary>
    public static MovementMode PreferredMovement(Game game)
    {
        return game.Me.HasSkill(Skill.Teleport) && game.Me.Mana > TeleportManaFloor
            ? MovementMode.Teleport
            : MovementMode.Walking;
    }

    /// <summary>Teleport costs about twenty four mana, so below this it will simply not fire.</summary>
    private const int TeleportManaFloor = 30;

    public static async Task<bool> TakePathOfLocations(Game game, List<Point> points, MovementMode movementMode, CancellationToken? token = null)
    {
        var from = game.Me.Location;
        var arrived = movementMode == MovementMode.Walking
            ? await WalkPathOfLocations(game, points, token)
            : await TeleportViaPath(game, points, token);
        TraceEvents.Move(game, from, points.Count > 0 ? points[^1] : null, movementMode == MovementMode.Walking ? MoveKind.Walk : MoveKind.Teleport, () => MoveOutcomeOf(game, points, movementMode, arrived, token));
        return arrived;
    }

    private static MoveOutcome MoveOutcomeOf(Game game, List<Point> points, MovementMode movementMode, bool arrived, CancellationToken? token)
        => points.Count == 0 ? MoveOutcome.EmptyPath
            : token?.IsCancellationRequested == true ? MoveOutcome.Cancelled
            : !game.IsInGame() ? MoveOutcome.LeftGame
            : arrived ? MoveOutcome.Arrived
            : movementMode == MovementMode.Walking ? MoveOutcome.Stuck
            : MoveOutcome.Timeout;
    private static async Task<bool> WalkPathOfLocations(Game game, List<Point> points, CancellationToken? token)
    {
        if (points.Count == 0)
        {
            Log.Debug($"Walk path of length 0 found, something went wrong while client at location: {game.Me.Location}");
            return true;
        }

        var tries = 0;
        var maxTries = 15;
        var previousBackupPoint = -1;

        for (int i = 0; i < points.Count; ++i)
        {
            if(tries >= maxTries)
            {
                Log.Warning($"failed to walk to end location, something went wrong while client at location: {game.Me.Location}");
                return false;
            }

            if (token.HasValue && token.Value.IsCancellationRequested)
            {
                return true;
            }

            var point = points[i];
            if(!game.IsInGame())
            {
                return false;
            }

            if (game.Me.Location.Distance(point) > 20)
            {
                tries++;
                var bestDistance = game.Me.Location.Distance(point);
                int? bestIndex = null;
                var j = i - 1;
                while (j > 0)
                {
                    var newDistance = game.Me.Location.Distance(points[j]);
                    if (newDistance < bestDistance && newDistance < 20)
                    {
                        bestDistance = newDistance;
                        bestIndex = j;
                    }

                    j--;
                }

                if(bestIndex == null)
                {
                    Log.Warning($"failed to find back-up point, something went wrong while client at location: {game.Me.Location}");
                    // Fails before moving at all: callers that retry at once logged it nine times in 80 ms.
                    await Task.Delay(200);
                    return false;
                }
                Log.Information($"Backing up to point {bestIndex} from {i}");
                // The repeat guard answers a move to the same point at once: without a pause the fifteen tries ran out in a
                // millisecond and the walk failed without having moved.
                await Task.Delay(250);

                if (previousBackupPoint == bestIndex && tries < maxTries)
                {
                    if (!game.IsInTown() && game.Me.Class == CharacterClass.Barbarian && game.Me.HasSkill(Skill.Whirlwind))
                    {
                        Log.Debug($"Seems stuck, whirlwinding to point {point}");
                        game.UseRightHandSkillOnLocation(Skill.Whirlwind, point);
                        await Task.Delay((int)(game.Me.Location.Distance(point) * 80 + 400));
                    }
                }

                previousBackupPoint = bestIndex.Value;
                i = bestIndex.Value;
            }
            await game.MoveToAsync(point);
        }

        return true;
    }

    private const double FinalHopMinDistance = 3;

    /// <summary>How far short of a path's last point a retried final hop aims; still within reach of what stands there.</summary>
    private const double FinalHopShortOf = 3;

    private static async Task<bool> TeleportViaPath(Game game, List<Point> path, CancellationToken? token)
    {
        if (path.Count == 0)
        {
            Log.Warning($"Teleport of length 0 found, something went wrong while client at location: {game.Me.Location}");
            return false;
        }

        for (var i = 0; i < path.Count; i++)
        {
            var point = path[i];
            // Teleporting skips a point within ten, which is right on the way but left the destination
            // itself up to ten short: returning to a kill spot 8.6 away spent the full four-second retry
            // standing still, every time.
            var last = i == path.Count - 1;
            var minDistance = last ? FinalHopMinDistance : 10;
            if (token.HasValue && token.Value.IsCancellationRequested)
            {
                break;
            }
            var retryTeleportTask = GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (token.HasValue && token.Value.IsCancellationRequested)
                {
                    return true;
                }

                // A path's last point is often an object's own spot, and the server may find no room to put the
                // character there: the hop onto the Tristram portal went unanswered for four seconds and the cow game
                // was given up. Every other try aims a few units short of it, on the caster's side.
                var aim = last && retryCount % 2 == 1 && game.Me.Location.Distance(point) > FinalHopShortOf
                    ? game.Me.Location.GetPointBeforePointInSameDirection(point, FinalHopShortOf)
                    : point;
                if (!await game.TeleportToLocationAsync(aim, minDistance))
                {
                    Log.Debug($"Teleport to {point} failing retrying at location: {game.Me.Location}");
                    return false;
                }
                return true;
            }, TimeSpan.FromSeconds(4));

            await Task.WhenAll(retryTeleportTask, Task.Delay(280));

            if (!await retryTeleportTask)
            {
                if (token.HasValue && token.Value.IsCancellationRequested)
                {
                    return true;
                }
                Log.Warning($"Teleport to {point} failed at location: {game.Me.Location}");
                return false;
            }

            if (!game.IsInGame())
            {
                return false;
            }
        }

        return true;
    }
}
