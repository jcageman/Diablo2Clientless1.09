using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.CS
{
    public class CSBot : IBotInstance
    {
        /*
         * Notes
            top seal
            easy
            "x": 7815,
            "y": 5155
            Good kill location (7778, 5186)

            slalom
            "x": 7773,
            "y": 5155
            Good kill location (7777, 5222)

            right seal
            "x": 7915,
            "y": 5275
            Good kill locations
            (7924, 5275)  --> seals below each other
            (7917, 5290) --> one left down, one right up
            
            left seal
            "x": 7655,
            "y": 5275
            Good kill locations
            (7681, 5296) split seal 
            (7675, 5315) long road

         * 
         * */

        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        ITownManagementService _townManagementService;
        private readonly CsConfiguration _csconfig;

        public CSBot(
            IOptions<BotConfiguration> config,
            IOptions<CsConfiguration> csconfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            ITownManagementService townManagementService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _csconfig = csconfig.Value;
            _townManagementService = townManagementService;
        }

        public string GetName()
        {
            return "cs";
        }

        public async Task Run()
        {
            _csconfig.Validate();
            var clients = new List<Tuple<AccountCharacter,Client>>();
            foreach (var account in _csconfig.Accounts)
            {
                var client = new Client();
                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                _externalMessagingClient.RegisterClient(client);
                clients.Add(Tuple.Create(account,client));
            }

            int gameCount = 1;

            while (true)
            {
                var firstClient = clients.First();

                if(!await LeaveGameAndRejoinMCPWithRetry(firstClient.Item2, firstClient.Item1))
                {
                    continue;
                }

                var result = await RealmConnectHelpers.CreateGameWithRetry(gameCount, firstClient.Item2, _config, firstClient.Item1);
                if(!result.Item1)
                {
                    gameCount = result.Item2;
                    continue;
                }

                await GameLoop(clients, gameCount++);
            }

        }

        private async Task<bool> LeaveGameAndRejoinMCPWithRetry(Client client, AccountCharacter cowAccount)
        {
            if (!client.Chat.IsConnected())
            {
                if (!await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, cowAccount, 10))
                {
                    return false;
                }
            }

            if (client.Game.IsInGame())
            {
                Log.Information($"Leaving game with {client.LoggedInUserName()}");
                client.Game.LeaveGame();
            }

            if (!client.RejoinMCP())
            {
                Log.Warning($"Disconnecting client {cowAccount.Username} since reconnecting to MCP failed, reconnecting to realm");
                return await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, cowAccount, 10);
            }

            return true;
        }

        public async Task GameLoop(List<Tuple<AccountCharacter, Client>> clients, int gameCount)
        {
            var townTasks = clients.Select(async c =>
            {
                Log.Information("In game");
                var client = c.Item2;
                client.Game.RequestUpdate(client.Game.Me.Id);
                if (!GeneralHelpers.TryWithTimeout(
                    (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
                    TimeSpan.FromSeconds(10)))
                {
                    return false;
                }

                var townManagementOptions = new TownManagementOptions()
                {
                    Act = Act.Act4
                };

                if (!await _townManagementService.PerformTownTasks(c.Item2, townManagementOptions))
                {
                    return false;
                }

                if(c.Item1.Character.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase))
                {
                    return await TeleportToCs(c.Item2);
                }

                return true;
            }
            ).ToList();
            var townResults = await Task.WhenAll(townTasks);
            if (townResults.Any(r => !r))
            {
                Log.Warning($"One or more characters failed there town task");
                return;
            }
        }

        private async Task<bool> TeleportToCs(Client client)
        {
            Log.Information($"Taking waypoint to {Waypoint.RiverOfFlame}");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.RiverOfFlame))
            {
                Log.Warning($"Teleporting failed at location {client.Game.Me.Location}");
                return false;
            }

            Log.Information($"Teleporting to {Area.ChaosSanctuary}");
            var pathToChaos = await _pathingService.GetPathToObjectWithOffset(client.Game.MapId, Difficulty.Normal, Area.RiverOfFlame, client.Game.Me.Location, EntityCode.WaypointAct4Levels, -6, -319, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToChaos, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {Area.ChaosSanctuary} warp failed at location {client.Game.Me.Location}");
                return false;
            }

            var goalLocation = client.Game.Me.Location.Add(0, -30);
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(goalLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await KillLeftSeal(client))
            {
                return false;
            }

            if (!await KillTopSeal(client))
            {
                return false;
            }

            if (!await KillRightSeal(client))
            {
                return false;
            }

            return await KillDiablo(client);
        }

        private async Task<bool> KillDiablo(Client client)
        {
            var pathToSeal = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToSeal, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.RightSeal1} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!_townManagementService.CreateTownPortalToTown(client))
            {
                return false;
            }

            var stopWatch = new Stopwatch();

            bool bFoundBosses = false;
            var bosses = new List<WorldObject>();
            do
            {
                if (!bFoundBosses || stopWatch.Elapsed > TimeSpan.FromSeconds(1))
                {
                    bosses = client.Game.GetNPCsByCode(NPCCode.Diablo)
                    .Where(npc => npc.State != EntityState.Dead && npc.State != EntityState.Dieing && npc.Location.Distance(client.Game.Me.Location) <= 20)
                    .ToList();
                    if (stopWatch.IsRunning)
                    {
                        stopWatch.Restart();
                    }

                }

                if (!bosses.Any())
                {
                    continue;
                }

                if (!bFoundBosses)
                {
                    bFoundBosses = true;
                    stopWatch.Start();
                }

                var nearest = bosses.OrderBy(npc => npc.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                if (nearest.Location.Distance(client.Game.Me.Location) > 10)
                {
                    if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                    {
                        return await client.Game.TeleportToLocationAsync(nearest.Location);
                    }, TimeSpan.FromSeconds(2)))
                    {
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(0.1));
                if (nearest.LifePercentage > 40)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }
                else
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                }

            } while (!bFoundBosses || bosses.Any());

            return true;
        }

        private async Task<bool> KillRightSeal(Client client)
        {
            Log.Information($"Teleporting to {EntityCode.RightSeal1}");
            var pathToSeal = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.RightSeal1, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToSeal, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.RightSeal1} failed at location {client.Game.Me.Location}");
                return false;
            }

            var seal1 = client.Game.GetEntityByCode(EntityCode.RightSeal1).First();
            var seal2 = client.Game.GetEntityByCode(EntityCode.RightSeal2).First();
            Log.Information($"{EntityCode.RightSeal1} at location {seal1.Location}");
            Log.Information($"{EntityCode.RightSeal2} at location {seal2.Location}");
            /*
            right seal
            "x": 7915,
            "y": 5275
            Good kill locations
            (7924, 5275)-- > seals below each other
           (7917, 5290)-- > one left down, one right up
            */
            var killLocation = seal1.Location.X < seal2.Location.X ? seal1.Location.Add(30, -10) : seal1.Location.Add(12, -38);
            Log.Information($"Kill at location {killLocation}");

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(killLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!_townManagementService.CreateTownPortalToTown(client))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(seal1.Location);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                client.Game.InteractWithEntity(seal1);
                return seal1.State == EntityState.Enabled;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(seal2.Location);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                client.Game.InteractWithEntity(seal2);
                return seal2.State == EntityState.Enabled;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(killLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            var stopWatch = new Stopwatch();

            bool bFoundBosses = false;
            var bosses = new List<WorldObject>();
            do
            {
                if (!stopWatch.IsRunning || stopWatch.Elapsed > TimeSpan.FromSeconds(1))
                {
                    bosses = client.Game.GetNPCsByCode(NPCCode.VenomLord)
                    .Where(npc => npc.State != EntityState.Dead && npc.State != EntityState.Dieing && npc.Location.Distance(killLocation) <= 20)
                    .ToList();
                    if (stopWatch.IsRunning)
                    {
                        stopWatch.Restart();
                    }

                }

                if (!bosses.Any())
                {
                    continue;
                }

                if (!bFoundBosses)
                {
                    bFoundBosses = true;
                    stopWatch.Start();
                }

                var nearest = bosses.OrderBy(npc => npc.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                if (nearest.Location.Distance(client.Game.Me.Location) > 10)
                {
                    if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                    {
                        return await client.Game.TeleportToLocationAsync(nearest.Location);
                    }, TimeSpan.FromSeconds(2)))
                    {
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(0.2));
                client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);

            } while (!bFoundBosses || bosses.Any());

            return true;
        }

        private async Task<bool> KillTopSeal(Client client)
        {
            Log.Information($"Teleporting to {EntityCode.TopSeal}");
            var pathToTopSeal = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.TopSeal, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTopSeal, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.TopSeal} failed at location {client.Game.Me.Location}");
                return false;
            }

            var topSeal = client.Game.GetEntityByCode(EntityCode.TopSeal).First();
            Log.Information($"TopSeal at location {topSeal.Location}");
            var toLeftOfSealIsValid = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, topSeal.Location.Add(-20, 0));
            var killLocation = toLeftOfSealIsValid ? topSeal.Location.Add(-37, 31) : topSeal.Location.Add(0, 70);
            Log.Information($"Kill at location {killLocation} with {toLeftOfSealIsValid}");
            var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {pathToKillingLocation} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!_townManagementService.CreateTownPortalToTown(client))
            {
                return false;
            }

            var pathToTopSeal2 = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.TopSeal, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTopSeal2, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {pathToKillingLocation} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                client.Game.InteractWithEntity(topSeal);
                await Task.Delay(100);
                return client.Game.GetEntityByCode(EntityCode.TopSeal).First().State == EntityState.Enabled;
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning($"Opening {EntityCode.TopSeal} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {pathToKillingLocation} failed at location {client.Game.Me.Location}");
                return false;
            }

            var stopWatch = new Stopwatch();

            bool bFoundBosses = false;
            var bosses = new List<WorldObject>();
            do
            {
                if (!stopWatch.IsRunning || stopWatch.Elapsed > TimeSpan.FromSeconds(1))
                {
                    bosses = client.Game.GetNPCsByCode(NPCCode.OblivionKnight)
                    .Where(npc => npc.State != EntityState.Dead && npc.State != EntityState.Dieing && npc.Location.Distance(killLocation) <= 25)
                    .ToList();
                    bosses.AddRange(client.Game.GetNPCsByCode(NPCCode.DoomKnight)
                    .Where(npc => npc.State != EntityState.Dead && npc.State != EntityState.Dieing && npc.Location.Distance(killLocation) <= 25)
                    .ToList());
                    if (stopWatch.IsRunning)
                    {
                        stopWatch.Restart();
                    }
                }

                if (!bosses.Any())
                {
                    continue;
                }

                if (!bFoundBosses)
                {
                    bFoundBosses = true;
                    stopWatch.Start();
                }

                var nearest = bosses.OrderBy(npc => npc.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                if (nearest.Location.Distance(client.Game.Me.Location) > 10)
                {
                    Log.Information($"Teleporting to {nearest.Location}");
                    if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                    {
                        return await client.Game.TeleportToLocationAsync(nearest.Location);
                    }, TimeSpan.FromSeconds(2)))
                    {
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(0.2));
                client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);

            } while (!bFoundBosses || bosses.Any());

            return true;
        }

        private async Task<bool> KillLeftSeal(Client client)
        {
            Log.Information($"Teleporting to {EntityCode.LeftSeal1}");
            var pathToLeftSeal = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.LeftSeal1, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToLeftSeal, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.LeftSeal1} failed at location {client.Game.Me.Location}");
                return false;
            }

            var leftSeal1 = client.Game.GetEntityByCode(EntityCode.LeftSeal1).First();
            var leftSeal2 = client.Game.GetEntityByCode(EntityCode.LeftSeal2).First();
            Log.Information($"LeftSeal1 at location {leftSeal1.Location}");
            Log.Information($"LeftSeal2 at location {leftSeal2.Location}");
            var leftSealKillLocation = leftSeal1.Location.Y > leftSeal2.Location.Y ? leftSeal1.Location.Add(26, -21) : leftSeal1.Location.Add(20, 40);
            Log.Information($"Kill at location {leftSealKillLocation}");

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSealKillLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!_townManagementService.CreateTownPortalToTown(client))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSeal1.Location);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                client.Game.InteractWithEntity(leftSeal1);
                return leftSeal1.State == EntityState.Enabled;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSeal2.Location);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                client.Game.InteractWithEntity(leftSeal2);
                return leftSeal2.State == EntityState.Enabled;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSealKillLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            var stopWatch = new Stopwatch();

            bool bFoundStormCaster = false;
            var stormCasters = new List<WorldObject>();
            do
            {
                if (!stopWatch.IsRunning || stopWatch.Elapsed > TimeSpan.FromSeconds(0.5))
                {
                    stormCasters = client.Game.GetNPCsByCode(NPCCode.StormCaster)
                    .Where(npc => npc.State != EntityState.Dead && npc.State != EntityState.Dieing && npc.Location.Distance(leftSealKillLocation) <= 25)
                    .ToList();
                    if (stopWatch.IsRunning)
                    {
                        stopWatch.Restart();
                    }

                }

                if (!stormCasters.Any())
                {
                    continue;
                }

                if (!bFoundStormCaster)
                {
                    bFoundStormCaster = true;
                    stopWatch.Start();
                }

                var nearest = stormCasters.OrderBy(npc => npc.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                if (nearest.Location.Distance(client.Game.Me.Location) > 10)
                {
                    if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                    {
                        return await client.Game.TeleportToLocationAsync(nearest.Location);
                    }, TimeSpan.FromSeconds(2)))
                    {
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(0.1));
                client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);

            } while (!bFoundStormCaster || stormCasters.Any());

            return true;
        }

        void HandleEventMessage(Client client, EventNotifyPacket eventNotifyPacket)
        {
            if (eventNotifyPacket.PlayerRelationType == PlayerRelationType.InvitesYouToParty)
            {
                var relevantPlayer = client.Game.Players.Where(p => p.Id == eventNotifyPacket.EntityId).FirstOrDefault();
                client.Game.AcceptInvite(relevantPlayer);
            }
        }
    }
}
