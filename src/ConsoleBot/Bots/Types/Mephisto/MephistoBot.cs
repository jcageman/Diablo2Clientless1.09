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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Mephisto
{
    public class MephistoBot : SingleClientBotBase, IBotInstance
    {
        private readonly IPathingService _pathingService;
        private readonly ITownManagementService _townManagementService;

        public MephistoBot(
            IOptions<BotConfiguration> config,
            IOptions<MephistoConfiguration> mephconfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMuleService muleService,
            ITownManagementService townManagementService) : base(config.Value, mephconfig.Value, externalMessagingClient, muleService)
        {
            _pathingService = pathingService;
            _townManagementService = townManagementService;
        }

        public string GetName()
        {
            return "mephisto";
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
            if (!GeneralHelpers.TryWithTimeout(
                (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
                TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            if (client.Game.Me.Class != CharacterClass.Sorceress)
            {
                throw new NotSupportedException("Only sorceress is supported on Mephisto");
            }

            /*
            while (client.Game.Players.Count < 2)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
            */

            var townManagementOptions = new TownManagementOptions()
            {
                Act = Act.Act3,
                ResurrectMerc = false
            };

            var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
            if (townTaskResult.ShouldMule)
            {
                NeedsMule = true;
                return true;
            }

            Log.Information("Taking DuranceOfHateLevel2 Waypoint");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.DuranceOfHateLevel2))
            {
                Log.Information("Taking DuranceOfHateLevel2 waypoint failed");
                return false;
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
            }

            var path2 = await _pathingService.GetPathFromWaypointToArea(client.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel2, Waypoint.DuranceOfHateLevel2, Area.DuranceOfHateLevel3, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, path2, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to DuranceOfHateLevel3 warp failed at location {client.Game.Me.Location}");
                return false;
            }

            var warp = client.Game.GetNearestWarp();
            if (warp == null || warp.Location.Distance(client.Game.Me.Location) > 20)
            {
                Log.Warning($"Warp not close enough at location {warp?.Location} while at location {client.Game.Me.Location}");
                return false;
            }

            Log.Information($"Taking warp to Durance 3");
            if (!GeneralHelpers.TryWithTimeout((_) => client.Game.TakeWarp(warp) && client.Game.Area == Area.DuranceOfHateLevel3,
                TimeSpan.FromSeconds(2)))
            {
                Log.Warning($"Taking warp failed at location {client.Game.Me.Location} to warp at location {warp.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
                var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel3, client.Game.Me.Location);
                return isValidPoint;
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Checking whether moved to area failed");
                return false;
            }

            Log.Information($"Teleporting to Mephisto");
            var path3 = await _pathingService.GetPathToLocation(client.Game, new Point(17566, 8070), MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, path3, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to Mephisto failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) => client.Game.GetNPCsByCode(NPCCode.Mephisto).Count > 0, TimeSpan.FromSeconds(2)))
            {
                Log.Warning($"Finding Mephisto failed while at location {client.Game.Me.Location}");
                return false;
            }

            var mephisto = client.Game.GetNPCsByCode(NPCCode.Mephisto).Single();
            Log.Information($"Killing Mephisto");
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    if (!client.Game.IsInGame())
                    {
                        return true;
                    }

                    if (mephisto.Location.Distance(client.Game.Me.Location) < 30 && (!client.Game.ClientCharacter.IsExpansion || mephisto.LifePercentage > 50))
                    {
                        client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                    }

                    Thread.Sleep(200);
                    if (retryCount % 5 == 0)
                    {
                        client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, mephisto);
                    }

                    return mephisto.LifePercentage < 30;
                },
                TimeSpan.FromSeconds(50)))
            {
                Log.Warning($"Killing Mephisto failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, mephisto);

                if (!client.Game.IsInGame())
                {
                    return true;
                }

                return GeneralHelpers.TryWithTimeout((_) => mephisto.State == EntityState.Dead,
                    TimeSpan.FromSeconds(0.7));
            }, TimeSpan.FromSeconds(50)))
            {
                Log.Warning($"Killing Mephisto failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!PickupNearbyItems(client))
            {
                Log.Warning($"Failed to pickup items at location {client.Game.Me.Location}");
                return false;
            }

            return true;
        }

        private bool PickupNearbyItems(Client client)
        {
            var pickupItems = client.Game.Items.Values.Where(i => i.Ground && Pickit.Pickit.ShouldPickupItem(client.Game, i, true)).OrderBy(n => n.Location.Distance(client.Game.Me.Location));
            Log.Information($"Killed Mephisto, picking up {pickupItems.Count()} items ");
            foreach (var item in pickupItems)
            {
                if (item.Location.Distance(client.Game.Me.Location) > 30)
                {
                    Log.Warning($"Skipped {item} since it's at location {item.Location}, while player at {client.Game.Me.Location}");
                    continue;
                }

                if (!client.Game.IsInGame())
                {
                    return false;
                }

                InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                if (client.Game.Inventory.FindFreeSpace(item) == null)
                {
                    Log.Warning($"Skipped {item.GetFullDescription()} since inventory is full");
                    continue;
                }

                if (!GeneralHelpers.TryWithTimeout((retryCount =>
                {
                    if (client.Game.Me.Location.Distance(item.Location) >= 5)
                    {
                        client.Game.TeleportToLocation(item.Location);
                        return false;
                    }
                    else
                    {
                        client.Game.PickupItem(item);
                        Thread.Sleep(50);
                        if (client.Game.Inventory.FindItemById(item.Id) == null && !item.IsGold)
                        {
                            return false;
                        }
                    }

                    return true;
                }), TimeSpan.FromSeconds(3)))
                {
                    Log.Warning($"Picking up item {item.GetFullDescription()} at location {item.Location} from location {client.Game.Me.Location} failed");
                }
            }

            return true;
        }
    }
}
