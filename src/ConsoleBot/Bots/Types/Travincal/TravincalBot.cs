using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Travincal
{
    public class TravincalBot : SingleClientBotBase, IBotInstance
    {
        private readonly IPathingService _pathingService;
        private readonly ITownManagementService _townManagementService;

        public TravincalBot(IOptions<BotConfiguration> config, IOptions<TravincalConfiguration> travconfig, IExternalMessagingClient externalMessagingClient, IPathingService pathingService,
            IMuleService muleService, ITownManagementService townManagementService)
        : base(config.Value, travconfig.Value, externalMessagingClient, muleService)
        {
            _pathingService = pathingService;
            _townManagementService = townManagementService;
        }

        public string GetName()
        {
            return "travincal";
        }

        public async Task Run()
        {
            var client = new Client();
            _externalMessagingClient.RegisterClient(client);
            await CreateGameLoop(client);
        }

        protected override async Task<bool> RunSingleGame(Client client)
        {
            Log.Information("In game");
            client.Game.RequestUpdate(client.Game.Me.Id);
            while (client.Game.Me.Location.X == 0 && client.Game.Me.Location.Y == 0)
            {
                Thread.Sleep(10);
            }

            if (client.Game.Me.Class != CharacterClass.Barbarian)
            {
                throw new NotSupportedException("Only barbarian is supported on travincal");
            }

            /*
             *
            while (client.Game.Players.Count < 2)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
             */

            var townManagementOptions = new TownManagementOptions()
            {
                Act = Act.Act4
            };

            await _townManagementService.PerformTownTasks(client, townManagementOptions);
            NeedsMule = client.Game.Inventory.Items.Any(i => i.IsIdentified && Pickit.Pickit.ShouldKeepItem(client.Game, i) && Pickit.Pickit.CanTouchInventoryItem(client.Game, i))
                            || client.Game.Cube.Items.Any(i => i.IsIdentified && Pickit.Pickit.ShouldKeepItem(client.Game, i));
            if (NeedsMule)
            {
                return true;
            }

            Log.Information("Taking travincal wp");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.Travincal))
            {
                Log.Information("Taking trav waypoint failed");
                return false;
            }

            Log.Information("Doing bo");
            if(!BarbBo(client.Game))
            {
                return false;
            }

            Log.Information("Walking to council members");
            
            var pathToCouncil = await _pathingService.GetPathToObjectWithOffset(client.Game, EntityCode.CompellingOrb, 23, 25, MovementMode.Walking);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToCouncil, MovementMode.Walking))
            {
                Log.Information($"Walking to councile members failed");
                return false;
            }

            Log.Information("Kill council members");
            if (!await KillCouncilMembers(client.Game))
            {
                Log.Information("Kill council members failed");
                return false;
            }

            Log.Information("Using find item");
            if (!await UseFindItemOnCouncilMembers(client.Game))
            {
                Log.Information("Finditem failed");
                return false;
            }

            Log.Information("Picking up left over items");
            if (!await PickupNearbyItems(client.Game, 300))
            {
                Log.Information("Pickup nearby items 1 failed");
            }

            if (!await PickupNearbyItems(client.Game, 300))
            {
                Log.Information("Pickup nearby items 2 failed");
            }

            Log.Information("Moving to town");
            if (!await _townManagementService.TakeTownPortalToTown(client))
            {
                Log.Information("Move to town failed");
                return false;
            }

            await _townManagementService.PerformTownTasks(client, townManagementOptions);

            Log.Information("Successfully finished game");
            return true;
        }

        private async Task<bool> PickupNearbyItems(Game game, double distance)
        {
            var pickupItems = game.Items.Where(i =>
            {
                return i.Ground && game.Me.Location.Distance(i.Location) < distance && Pickit.Pickit.ShouldPickupItem(game, i);
            }).OrderBy(n => game.Me.Location.Distance(n.Location));

            foreach (var item in pickupItems)
            {
                if (!game.IsInGame())
                {
                    return false;
                }

                InventoryHelpers.MoveInventoryItemsToCube(game);
                if (game.Inventory.FindFreeSpace(item) == null)
                {
                    continue;
                }

                await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    if (game.Me.Location.Distance(item.Location) >= 5)
                    {
                        var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, item.Location, MovementMode.Walking);
                        await MovementHelpers.TakePathOfLocations(game, pathNearest, MovementMode.Walking);
                        return false;
                    }

                    return true;
                }, TimeSpan.FromSeconds(3));

                if (game.Me.Location.Distance(item.Location) < 5)
                {
                    game.PickupItem(item);
                }
            }

            InventoryHelpers.MoveInventoryItemsToCube(game);
            return true;
        }

        private async Task<bool> UseFindItemOnCouncilMembers(Game game)
        {
            List<WorldObject> councilMembers = GetCouncilMembers(game);
            var nearestMembers = councilMembers.OrderBy(n => game.Me.Location.Distance(n.Location));

            foreach (var nearestMember in nearestMembers)
            {
                await PickupNearbyItems(game, 10);
                bool result = await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    if (!game.IsInGame())
                    {
                        return false;
                    }

                    if (nearestMember.Location.Distance(game.Me.Location) > 5)
                    {
                        var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearestMember.Location, MovementMode.Walking);
                        await MovementHelpers.TakePathOfLocations(game, pathNearest, MovementMode.Walking);
                    }

                    if (nearestMember.Location.Distance(game.Me.Location) <= 5)
                    {
                        if(game.UseFindItem(nearestMember))
                        {
                            return nearestMember.Effects.Contains(EntityEffect.CorpseNoDraw);
                        }
                    }

                    return false;

                }, TimeSpan.FromSeconds(5));

                if(!result)
                {
                    Log.Warning("Failed to do find item on corpse");
                }

                if (!game.IsInGame())
                {
                    return false;
                }
            }

            return true;
        }
        private async Task<bool> KillCouncilMembers(Game game)
        {
            var startTime = DateTime.Now;
            List<WorldObject> aliveMembers;
            do
            {
                List<WorldObject> councilMembers = GetCouncilMembers(game);
                aliveMembers = councilMembers
                    .Where(n => n.State != EntityState.Dead)
                    .OrderBy(n => game.Me.Location.Distance(n.Location))
                    .ToList();

                var nearest = aliveMembers.FirstOrDefault();
                if (nearest != null)
                {
                    if (!game.IsInGame())
                    {
                        return false;
                    }

                    if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMinutes(2))
                    {
                        Log.Information("Passed maximum elapsed time for killing council members");
                        return false;
                    }

                    if(game.Me.Location.Distance(nearest.Location) > 5)
                    {
                        var closeTo = game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 6);
                        if(game.Me.Location.Distance(closeTo) < 3)
                        {
                            closeTo = nearest.Location;
                        }

                        var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearest.Location, MovementMode.Walking);
                        if (!await MovementHelpers.TakePathOfLocations(game, pathNearest, MovementMode.Walking))
                        {
                            Log.Warning($"Walking to Council Member failed at {game.Me.Location}");
                        }
                    }

                    var wwDirection = game.Me.Location.GetPointPastPointInSameDirection(nearest.Location, 6);
                    if (game.Me.Location.Equals(nearest.Location))
                    {
                        var pathLeft = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearest.Location.Add(-6, 0), MovementMode.Walking);
                        var pathRight = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearest.Location.Add(6, 0), MovementMode.Walking);
                        if (pathLeft.Count < pathRight.Count)
                        {
                            Log.Debug($"same location, wwing to left");
                            wwDirection = new Point((ushort)(game.Me.Location.X - 6), game.Me.Location.Y);
                        }
                        else
                        {
                            Log.Debug($"same location, wwing to right");
                            wwDirection = new Point((ushort)(game.Me.Location.X + 6), game.Me.Location.Y);
                        }
                    }

                    //Log.Information($"player loc: {game.Me.Location}, nearest: {nearest.Location} ww destination: {wwDirection}  ");
                    game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                    await Task.Delay(TimeSpan.FromSeconds(0.3));

                }
            } while (aliveMembers.Any());

            return true;
        }

        private List<WorldObject> GetCouncilMembers(Game game)
        {
            var councilMembers = game.GetNPCsByCode(NPCCode.CouncilMember1);
            councilMembers.AddRange(game.GetNPCsByCode(NPCCode.CouncilMember2));
            return councilMembers;
        }

        private bool BarbBo(Game game)
        {
            if(!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                game.UseRightHandSkillOnLocation(Skill.BattleCommand, game.Me.Location);
                Thread.Sleep(200);
                return game.Me.Effects.Contains(EntityEffect.Battlecommand);
            }, TimeSpan.FromSeconds(3)))
            {
                Log.Warning("Battle command failed");
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                game.UseRightHandSkillOnLocation(Skill.BattleOrders, game.Me.Location);
                Thread.Sleep(200);
                return game.Me.Effects.Contains(EntityEffect.BattleOrders);
            }, TimeSpan.FromSeconds(3)))
            {
                Log.Warning("Battle orders failed");
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                game.UseRightHandSkillOnLocation(Skill.Shout, game.Me.Location);
                Thread.Sleep(200);
                return game.Me.Effects.Contains(EntityEffect.Shout);
            }, TimeSpan.FromSeconds(3)))
            {
                Log.Warning("Shout failed");
                return false;
            }

            return game.UseHealthPotion();
        }
    }
}
