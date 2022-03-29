using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers
{
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
            if (!await MovementHelpers.MoveToLocation(game, pathingService, mapApiService, warp.Location, movementMode))
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
                game.RequestUpdate(game.Me.Id);
                var isValidPoint = await pathingService.IsNavigatablePointInArea(game.MapId, Difficulty.Normal, area, game.Me.Location);
                return isValidPoint;
            }, TimeSpan.FromSeconds(3.5)))
            {
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
                var clientArea = await mapApiService.GetAreaFromLocation(game.MapId, Difficulty.Normal, game.Me.Location, game.Me.Area) ?? game.Me.Area;
                if (!clientArea.HasValue)
                {
                    return false;
                }
                var path = await pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, clientArea.Value, game.Me.Location, location, movementMode);
                if (path.Count != 0 && !await TakePathOfLocations(game, path, movementMode, token))
                {
                    Log.Warning($"Walking to location failed at {game.Me.Location}");
                    return false;
                }

                await game.MoveToAsync(location);
            }

            return true;
        }

        public static async Task<bool> MoveToWorldObject(Game game,
                                                         IPathingService pathingService,
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
                var path = await pathingService.GetPathToLocation(game, worldObject.Location, movementMode);
                if (path.Count != 0 && !await TakePathOfLocations(game, path, movementMode, token))
                {
                    Log.Warning($"Walking to enemy to attack failed at {game.Me.Location}");
                    return false;
                }

                await game.MoveToAsync(worldObject);
            }

            return true;
        }

        public static async Task<bool> TakePathOfLocations(Game game, List<Point> points, MovementMode movementMode, CancellationToken? token = null)
        {
            if (movementMode == MovementMode.Walking)
            {
                return await WalkPathOfLocations(game, points, token);
            }
            else
            {
                return await TeleportViaPath(game, points, token);
            }
        }
        private static async Task<bool> WalkPathOfLocations(Game game, List<Point> points, CancellationToken? token)
        {
            if (points.Count == 0)
            {
                Log.Warning($"Walk path of length 0 found, something went wrong while client at location: {game.Me.Location}");
                return true;
            }

            var tries = 0;
            var maxTries = 15;
            var previousBackupPoint = -1;

            for (int i = 0; i < points.Count; ++i)
            {
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
                    var bestIndex = i;
                    var j = i - 1;
                    while (j > 0)
                    {
                        var newDistance = game.Me.Location.Distance(points[j]);
                        if (newDistance < bestDistance)
                        {
                            bestDistance = newDistance;
                            bestIndex = j;
                        }

                        j--;
                    }
                    Log.Information($"Backing up to point {j} from {i}");

                    if (previousBackupPoint == bestIndex && tries < maxTries)
                    {
                        if (!game.IsInTown() && game.Me.Class == CharacterClass.Barbarian && game.Me.HasSkill(Skill.Whirlwind))
                        {
                            Log.Debug($"Seems stuck, whirlwinding to point {point}");
                            game.UseRightHandSkillOnLocation(Skill.Whirlwind, point);
                            Thread.Sleep((int)(game.Me.Location.Distance(point) * 80 + 400));
                        }
                    }

                    previousBackupPoint = bestIndex;
                    i = bestIndex;
                }
                Log.Debug($"Running to point {point}");
                await game.MoveToAsync(point);
            }

            return true;
        }

        private static async Task<bool> TeleportViaPath(Game game, List<Point> path, CancellationToken? token)
        {
            if (path.Count == 0)
            {
                Log.Warning($"Teleport of length 0 found, something went wrong while client at location: {game.Me.Location}");
                return false;
            }

            foreach (var point in path)
            {
                if (token.HasValue && token.Value.IsCancellationRequested)
                {
                    break;
                }
                if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    if (token.HasValue && token.Value.IsCancellationRequested)
                    {
                        return true;
                    }
                    if (!await game.TeleportToLocationAsync(point))
                    {
                        Log.Debug($"Teleport to {point} failing retrying at location: {game.Me.Location}");
                        return false;
                    }
                    return true;
                }, TimeSpan.FromSeconds(4)))
                {
                    if (token.HasValue && token.Value.IsCancellationRequested)
                    {
                        return true;
                    }
                    Log.Warning($"Teleport failed at location: {game.Me.Location}");
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
}
