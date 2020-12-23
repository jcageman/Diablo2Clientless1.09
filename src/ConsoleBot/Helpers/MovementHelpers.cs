using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers
{
    public static class MovementHelpers
    {
        public static async Task<bool> TakePathOfLocations(Game game, List<Point> points, MovementMode movementMode, CancellationToken? token = null)
        {
            if(movementMode == MovementMode.Walking)
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

            var previousBackupPoint = -1;

            for (int i = 0; i < points.Count; ++i)
            {
                if (token.HasValue && token.Value.IsCancellationRequested)
                {
                    return true;
                }

                var point = points[i];
                if (game.Me.Location.Distance(point) > 20)
                {
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

                    if (previousBackupPoint == bestIndex)
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
                if(token.HasValue && token.Value.IsCancellationRequested)
                {
                    break;
                }
                if (!await GeneralHelpers.TryWithTimeout(async (retryCount) => {
                    if (token.HasValue && token.Value.IsCancellationRequested)
                    {
                        return true;
                    }
                    return await game.TeleportToLocationAsync(point);
                }, TimeSpan.FromSeconds(4)))
                {
                    if (token.HasValue && token.Value.IsCancellationRequested)
                    {
                        return true;
                    }
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
