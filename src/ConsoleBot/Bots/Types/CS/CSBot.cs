using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
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
using System.Threading;
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
        private readonly ITownManagementService _townManagementService;
        private readonly IAttackService _attackService;
        private readonly CsConfiguration _csconfig;

        public CSBot(
            IOptions<BotConfiguration> config,
            IOptions<CsConfiguration> csconfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            ITownManagementService townManagementService,
            IAttackService attackService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _csconfig = csconfig.Value;
            _townManagementService = townManagementService;
            _attackService = attackService;
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
                var accountAndClient = Tuple.Create(account, client);
                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                if(GetIsLeadClient(accountAndClient))
                {
                    client.OnReceivedPacketEvent(InComingPacket.PlayerInGame, (packet) => NewPlayerJoinGame(client, new PlayerInGamePacket(packet)));
                }
                _externalMessagingClient.RegisterClient(client);
                clients.Add(accountAndClient);
            }

            int gameCount = 1;

            while (true)
            {;
                var leaveTasks = clients.Select(async (c, i) =>
                {
                    return await LeaveGameAndRejoinMCPWithRetry(c.Item2, c.Item1);
                }).ToList();
                var leaveResults = await Task.WhenAll(leaveTasks);
                if (leaveResults.Any(r => !r))
                {
                    Log.Warning($"One or more characters failed to leave and rejoin");
                    continue;
                }

                var leadClient = clients.First(c => GetIsLeadClient(c));
                var result = await RealmConnectHelpers.CreateGameWithRetry(gameCount, leadClient.Item2, _config, leadClient.Item1);
                gameCount = result.Item2;
                if (!result.Item1)
                {
                    continue;
                }

                await GameLoop(clients, gameCount);
                gameCount++;
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
            var leadClient = clients.Single(c => GetIsLeadClient(c));
            var csManager = new CSManager(new List<Client>{ leadClient.Item2 });
            uint currentTeleportId = 0;
            var nextGameCancellation = new CancellationTokenSource();
            var gameTasks = clients.Select(async (c,i) =>
            {
                bool isLeadClient = leadClient == c;
                if (!isLeadClient)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * i));
                    if (!await RealmConnectHelpers.JoinGameWithRetry(gameCount, c.Item2, _config, c.Item1))
                    {
                        return false;
                    }
                }

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

                Func<uint> getTeleportId = () => currentTeleportId;

                if (c.Item1.Character.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase))
                {
                    var result = await TaxiCs(c.Item2, csManager, t => currentTeleportId = t);
                    nextGameCancellation.Cancel();
                    return result;
                }
                else
                {
                    var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
                    var pathToTpLocation = await _pathingService.GetPathToLocation(client.Game, new Point(5042, 5036), movementMode);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTpLocation, movementMode))
                    {
                        Log.Warning($"Client {client.Game.Me.Name} {movementMode} to portal area failed at {client.Game.Me.Location}");
                        return false;
                    }

                    await BaseCsBot(csManager, getTeleportId, nextGameCancellation, client, GetKillActionForClass(client));
                }

                return true;
            }
            ).ToList();
            var firstCompletedResult = await Task.WhenAny(gameTasks);
            if (!await firstCompletedResult)
            {
                Log.Warning($"One or more characters failed there town task");
                nextGameCancellation.Cancel();
                await Task.WhenAll(gameTasks);
                return;
            }

            var townResults = await Task.WhenAll(gameTasks);
            if (townResults.Any(r => !r))
            {
                Log.Warning($"One or more characters failed there town task");
                nextGameCancellation.Cancel();
                return;
            }
            
        }

        private async Task<bool> BaseCsBot(CSManager csManager, Func<uint> getTeleportId,
                                           CancellationTokenSource nextGameCancellation, Client client,
                                           Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action)
        {
            var localTeleportId = 0U;
            while (!nextGameCancellation.IsCancellationRequested && client.Game.IsInGame())
            {
                await Task.Delay(100);
                if (localTeleportId != getTeleportId() && !client.Game.IsInTown())
                {
                    Log.Information($"Client {client.Game.Me.Name} Taking town portal to town");
                    if (!await _townManagementService.TakeTownPortalToTown(client))
                    {
                        continue;
                    }
                }

                if (client.Game.IsInTown() && getTeleportId() != 0 && getTeleportId() != localTeleportId)
                {
                    Log.Information($"Client {client.Game.Me.Name} taking town portal to chaos");
                    var teleportPlayer = client.Game.Players.First(p => p.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase));
                    if (!await _townManagementService.TakeTownPortalToArea(client, teleportPlayer, Area.ChaosSanctuary))
                    {
                        continue;
                    }

                    localTeleportId = getTeleportId();
                }

                if(!client.Game.IsInTown())
                {
                    var anyPlayersWithoutShouts = ClassHelpers.AnyPlayerIsMissingShouts(client);
                    if (anyPlayersWithoutShouts && client.Game.Me.Class == CharacterClass.Barbarian)
                    {
                        await ClassHelpers.CastAllShouts(client);
                    }
                    else if (ClassHelpers.IsMissingShouts(client.Game.Me))
                    {
                        Log.Information($"Client {client.Game.Me.Name} waiting for bo");
                        await WaitForBo(client);
                    }
                    else
                    {
                        await Task.Delay(100);
                        if(!await KillBosses(client, csManager, nextGameCancellation, client.Game.Me.Location, action, localTeleportId, getTeleportId))
                        {
                            return false;
                        }
                    }
                }
            }

            if(!nextGameCancellation.IsCancellationRequested && !client.Game.IsInGame())
            {
                return false;
            }

            return true;
        }

        private bool GetIsLeadClient(Tuple<AccountCharacter, Client> c)
        {
            return c.Item1.Character.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase);
        }

        private async Task<bool> TaxiCs(Client client, CSManager csManager, Action<uint> setTeleportId)
        {
            Log.Information($"Client {client.Game.Me.Name} Taking waypoint to {Waypoint.RiverOfFlame}");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.RiverOfFlame))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting failed at location {client.Game.Me.Location}");
                return false;
            }

            Log.Information($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary}");
            var pathToChaos = await _pathingService.GetPathToObjectWithOffset(client.Game.MapId, Difficulty.Normal, Area.RiverOfFlame, client.Game.Me.Location, EntityCode.WaypointAct4Levels, -6, -319, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToChaos, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary} warp failed at location {client.Game.Me.Location}");
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

            var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!_townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            setTeleportId.Invoke(myPortal.Id);
            csManager.ResetAliveMonsters();
            if (!await WaitForBo(client))
            {
                return false;
            }

            csManager.ResetAliveMonsters();

            setTeleportId.Invoke(0);

            if (!await KillLeftSeal(client, csManager, setTeleportId))
            {
                return false;
            }

            csManager.ResetAliveMonsters();
            setTeleportId.Invoke(0);

            if (!await KillTopSeal(client, csManager, setTeleportId))
            {
                return false;
            }

            csManager.ResetAliveMonsters();
            setTeleportId.Invoke(0);

            if (!await KillRightSeal(client, csManager, setTeleportId))
            {
                return false;
            }

            csManager.ResetAliveMonsters();
            setTeleportId.Invoke(0);

            return await KillDiablo(client, csManager, setTeleportId);
        }

        private static async Task<bool> WaitForBo(Client client)
        {
            var initialLocation = client.Game.Me.Location;
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            while (stopWatch.Elapsed < TimeSpan.FromSeconds(10) && ClassHelpers.AnyPlayerIsMissingShouts(client))
            {
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                await client.Game.MoveToAsync(initialLocation.Add((short)new Random().Next(-5, 5), (short)new Random().Next(-5, 5)));
            }

            if (stopWatch.Elapsed >= TimeSpan.FromSeconds(10))
            {
                Log.Warning($"Client {client.Game.Me.Name} Failed waiting for bo at area {client.Game.Area} at location {client.Game.Me.Location}");
                return false;
            }

            client.Game.UseHealthPotion();

            return true;
        }

        private async Task<bool> KillDiablo(Client client, CSManager csManager, Action<uint> setTeleportId)
        {
            var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!_townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            setTeleportId.Invoke(myPortal.Id);
            var action = GetSorceressKillAction(client);
            if (!await KillBosses(client, csManager, null, client.Game.Me.Location, action, 0, () => 0))
            {
                return false;
            }
            return true;
        }

        private async Task<bool> KillRightSeal(Client client, CSManager csManager, Action<uint> setTeleportId)
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
            var killLocation = seal1.Location.X < seal2.Location.X ? seal1.Location.Add(30, -10) : seal1.Location.Add(12, -38);

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(killLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!_townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            setTeleportId.Invoke(myPortal.Id);

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

            var action = GetSorceressKillAction(client);
            if(!await KillBosses(client, csManager, null, killLocation, action, 0, () => 0))
            {
                return false;
            }

            return true;
        }

        private async Task<bool> KillTopSeal(Client client, CSManager csManager, Action<uint> setTeleportId)
        {
            Log.Information($"Teleporting to {EntityCode.TopSeal}");
            var pathToTopSeal = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.TopSeal, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTopSeal, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.TopSeal} failed at location {client.Game.Me.Location}");
                return false;
            }

            var topSeal = client.Game.GetEntityByCode(EntityCode.TopSeal).First();
            var toLeftOfSealIsValid = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, topSeal.Location.Add(-20, 0));
            var killLocation = toLeftOfSealIsValid ? topSeal.Location.Add(-37, 31) : topSeal.Location.Add(0, 70);
            var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {pathToKillingLocation} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!_townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            setTeleportId.Invoke(myPortal.Id);

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

            var action = GetSorceressKillAction(client);
            if (!await KillBosses(client, csManager, null, killLocation, action, 0, () => 0))
            {
                return false;
            }

            return true;
        }

        private async Task<bool> KillLeftSeal(Client client, CSManager csManager, Action<uint> setTeleportId)
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
            var leftSealKillLocation = leftSeal1.Location.Y > leftSeal2.Location.Y ? leftSeal1.Location.Add(26, -21) : leftSeal1.Location.Add(20, 40);
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSealKillLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!_townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            setTeleportId.Invoke(myPortal.Id);

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

            var action = GetSorceressKillAction(client);
            if (!await KillBosses(client, csManager, null, leftSealKillLocation, action, 0, () => 0))
            {
                return false;
            }

            return true;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetKillActionForClass(Client client)
        {
            return client.Game.Me.Class switch
            {
                CharacterClass.Barbarian => GetBarbarianKillAction(client),
                CharacterClass.Sorceress => GetSorceressKillAction(client),
                CharacterClass.Paladin => GetPaladinKillAction(client),
                CharacterClass.Necromancer => GetNecromancerKillAction(client),
                _ => new Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task>((csManager, enemies, bosses) =>
{
    return Task.CompletedTask;
}),
            };
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetBarbarianKillAction(Client client)
        {
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = (async (csManager, enemies, bosses) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(0.1));
                if(client.Game.Me.Effects.Contains(EntityEffect.Ironmaiden))
                {
                    var nearbyPlayer = client.Game.Players.Where(p => p.Id != client.Game.Me.Id).OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                    if(nearbyPlayer != null && nearbyPlayer.Location.Distance(client.Game.Me.Location) < 40)
                    {
                        var pathNearest = await _pathingService.GetPathToLocation(client.Game, nearbyPlayer.Location, MovementMode.Walking);
                        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                        {
                            Log.Warning($"Walking to Player failed at {client.Game.Me.Location}");
                        }
                    }

                    await PickupItemsFromPickupList(client, csManager, 20);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 20);

                    return;
                }

                var nearest = bosses.FirstOrDefault();
                if(nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 10);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 10);
                }

                if (nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    return;
                }

                if (client.Game.Me.Location.Distance(nearest.Location) > 5)
                {
                    var closeTo = client.Game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 6);
                    if (client.Game.Me.Location.Distance(closeTo) > 3)
                    {
                        closeTo = nearest.Location;
                    }

                    var pathNearest = await _pathingService.GetPathToLocation(client.Game, nearest.Location, MovementMode.Walking);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                    {
                        Log.Warning($"Walking to Boss failed at {client.Game.Me.Location}");
                    }
                }

                var wwDirection = client.Game.Me.Location.GetPointPastPointInSameDirection(nearest.Location, 6);
                if (client.Game.Me.Location.Equals(nearest.Location) || !await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, client.Game.Area, wwDirection))
                {
                    var bestPathCount = int.MaxValue;
                    foreach (var (p1, p2) in new List<(short, short)> { (-6, 0), (6, 0), (0, -6), (0, 6) })
                    {
                        var move = nearest.Location.Add(p1, p2);
                        if(!await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, client.Game.Area, move))
                        {
                            continue;
                        }

                        var path = await _pathingService.GetPathToLocation(client.Game, move, MovementMode.Walking);
                        if (path.Count > 0 && path.Count < bestPathCount)
                        {
                            bestPathCount = path.Count;
                            wwDirection = move;
                        }
                    }
                }

                var wwDistance = client.Game.Me.Location.Distance(wwDirection);
                client.Game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                await Task.Delay(TimeSpan.FromSeconds(wwDistance * 0.005 + 0.2));
            });
            return action;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetPaladinKillAction(Client client)
        {
            var auraTimer = new Stopwatch();
            auraTimer.Start();
            var bhTimer = new Stopwatch();
            bhTimer.Start();
            var flipFlop = true;
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = async (csManager, enemies, bosses) =>
            {
                if (!client.Game.Me.Effects.Contains(EntityEffect.Holyshield))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
                    await Task.Delay(100);
                }

                if (auraTimer.Elapsed > TimeSpan.FromSeconds(4))
                {
                    client.Game.ChangeSkill(flipFlop ? Skill.Concentration : Skill.Fanaticism, Hand.Right);
                    auraTimer.Restart();
                    flipFlop = !flipFlop;
                }

                var nearest = bosses.FirstOrDefault();
                if (nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 10);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 10);
                }

                if (nearest == null && enemies.Any())
                {
                    if (bhTimer.Elapsed > TimeSpan.FromSeconds(0.3))
                    {
                        client.Game.ShiftHoldLeftHandSkillOnLocation(Skill.BlessedHammer, client.Game.Me.Location);
                        bhTimer.Restart();
                    }
                    return;
                }

                if (nearest == null)
                {
                    return;
                }

                if (nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                    return;
                }

                if (nearest.Location.Distance(client.Game.Me.Location) > 15)
                {
                    var goalLocation = client.Game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 3);
                    client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
                    var pathNearest = await _pathingService.GetPathToLocation(client.Game, goalLocation, MovementMode.Walking);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                    {
                        Log.Warning($"Walking to Nearest failed at {client.Game.Me.Location}");
                    }
                    client.Game.ChangeSkill(flipFlop ? Skill.Concentration : Skill.Fanaticism, Hand.Right);
                }

                if (bhTimer.Elapsed > TimeSpan.FromSeconds(0.3))
                {
                    client.Game.ShiftHoldLeftHandSkillOnLocation(Skill.BlessedHammer, client.Game.Me.Location);
                    bhTimer.Restart();
                }
                
                await Task.Delay(100);
            };
            return action;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetSorceressKillAction(Client client)
        {
            var moveTimer = new Stopwatch();
            moveTimer.Start();
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = (async (csManager, enemies, bosses) =>
            {
                if (!client.Game.Me.Effects.Contains(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                }

                var nearest = bosses.FirstOrDefault();
                if(nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 30);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 30);
                    nearest = enemies.FirstOrDefault();
                }

                if(nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                    return;
                }

                var distanceToNearest = nearest.Location.Distance(client.Game.Me.Location);

                if (distanceToNearest < 5)
                {
                    await _attackService.TeleportToNearbySafeSpot(client, enemies.Select(e => e.Location).ToList(), nearest.Location, 10);
                }
                else if(moveTimer.Elapsed > TimeSpan.FromSeconds(3))
                {
                    var nearbyPlayer = client.Game.Players.Where(p => p.Id != client.Game.Me.Id).OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                    var nearbyPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).OrderBy(t => t.Location.Distance(client.Game.Me.Location)).First();
                    var nearbyLocation = nearbyPlayer != null ? nearbyPlayer.Location : nearbyPortal.Location;
                    await _attackService.TeleportToNearbySafeSpot(client, enemies.Select(e => e.Location).ToList(), nearbyLocation, 10);
                    moveTimer.Restart();
                }

                if (client.Game.Me.HasSkill(Skill.FrostNova) && distanceToNearest < 10 && client.Game.WorldObjects.TryGetValue((nearest.Id, EntityType.NPC), out var monster) && !monster.Effects.Contains(EntityEffect.Cold))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.FrostNova, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                }

                client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                await Task.Delay(TimeSpan.FromSeconds(0.1));
            });
            return action;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetNecromancerKillAction(Client client)
        {
            var curseSkill = client.Game.Players.Any(c => c.Class == CharacterClass.Paladin) ? Skill.LifeTap : Skill.AmplifyDamage;
            var curseTimer = new Stopwatch();
            curseTimer.Start();
            var ceTimer = new Stopwatch();
            ceTimer.Start();
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = async (csManager, enemies, bosses) =>
            {
                if (!client.Game.Me.Effects.Contains(EntityEffect.Bonearmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BoneArmor, client.Game.Me.Location);
                    await Task.Delay(100);
                }

                var nearest = bosses.FirstOrDefault();
                if (nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 10);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 10);
                }

                if(nearest == null)
                {
                    await Task.Delay(100);
                    return;
                }

                if (nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                    return;
                }

                if (curseTimer.Elapsed > TimeSpan.FromSeconds(4))
                {
                    if(client.Game.Me.HasSkill(curseSkill))
                    {
                        client.Game.UseRightHandSkillOnLocation(curseSkill, nearest.Location);
                    }
                    else if(client.Game.Me.HasSkill(Skill.IronMaiden))
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.IronMaiden, nearest.Location);
                    }
                    
                    curseTimer.Restart();
                }

                if (ceTimer.Elapsed > TimeSpan.FromSeconds(0.3))
                {
                    csManager.CastCorpseExplosion(client);
                    ceTimer.Restart();
                }

                await Task.Delay(100);
            };
            return action;
        }

        private async Task<bool> KillBosses(Client client,
                                CSManager csManager,
                                CancellationTokenSource nextGameCancellation,
                                Point killLocation,
                                Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action,
                                uint oldTeleportId,
                                Func<uint> getTeleportId)
        {
            var stopWatch = new Stopwatch();
            var stepTimeWatch = new Stopwatch();
            stepTimeWatch.Start();
            bool bFoundBosses = false;
            var enemies = new List<AliveMonster>();
            var bosses = new List<AliveMonster>();
            do
            {
                if(stepTimeWatch.Elapsed > TimeSpan.FromSeconds(60))
                {
                    return false;
                }

                if (!stopWatch.IsRunning || stopWatch.Elapsed > TimeSpan.FromSeconds(0.5))
                {
                    enemies = csManager.GetNearbyAliveMonsters(killLocation, 35.0);
                    bosses = enemies.Where(e => e.MonsterEnchantments.Any() || e.NPCCode == NPCCode.Diablo).ToList();
                    if (stopWatch.IsRunning)
                    {
                        stopWatch.Restart();
                    }

                }

                if (bosses.Any())
                {
                    if (!bFoundBosses)
                    {
                        bFoundBosses = true;
                        stopWatch.Start();
                    }
                }

                await action.Invoke(csManager, enemies, bosses);

            } while ((nextGameCancellation == null || !nextGameCancellation.IsCancellationRequested) && client.Game.IsInGame() && (!bFoundBosses || bosses.Any()) && oldTeleportId == getTeleportId());

            if(client.Game.IsInGame())
            {
                await PickupItemsFromPickupList(client, csManager, 15);
            }
            
            return client.Game.IsInGame();
        }

        void NewPlayerJoinGame(Client client, PlayerInGamePacket playerInGamePacket)
        {
            var relevantPlayer = client.Game.Players.Where(p => p.Id == playerInGamePacket.Id).FirstOrDefault();
            client.Game.InvitePlayer(relevantPlayer);
        }

        void HandleEventMessage(Client client, EventNotifyPacket eventNotifyPacket)
        {
            if (eventNotifyPacket.PlayerRelationType == PlayerRelationType.InvitesYouToParty)
            {
                var relevantPlayer = client.Game.Players.Where(p => p.Id == eventNotifyPacket.EntityId).FirstOrDefault();
                client.Game.AcceptInvite(relevantPlayer);
            }
        }

        private async Task MoveToLocation(Client client, Point location, CancellationToken? token = null)
        {
            var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
            var distance = client.Game.Me.Location.Distance(location);
            if (distance > 15)
            {
                var path = await _pathingService.GetPathToLocation(client.Game, location, movementMode);
                if (token.HasValue && token.Value.IsCancellationRequested)
                {
                    return;
                }
                await MovementHelpers.TakePathOfLocations(client.Game, path.ToList(), movementMode, token);
            }
            else
            {
                if (movementMode == MovementMode.Teleport)
                {
                    client.Game.TeleportToLocation(location);
                }
                else
                {
                    client.Game.MoveTo(location);
                }
            }
        }

        private async Task PickupNearbyRejuvenationsIfNeeded(Client client, CSManager csManager, int distance)
        {
            var totalRejuvanationPotions = client.Game.Inventory.Items.Count(i => i.Name == ItemName.RejuvenationPotion || i.Name == ItemName.FullRejuvenationPotion);
            var pickitList = csManager.GetRejuvenationPotionPickupList(client, distance, (int)5 - totalRejuvanationPotions);
            foreach (var item in pickitList)
            {
                Log.Information($"Client {client.Game.Me.Name} picking up {item.Name}");
                await MoveToLocation(client, item.Location);
                if (item.Ground)
                {
                    if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        client.Game.MoveTo(item.Location);
                        client.Game.PickupItem(item);
                        return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                        {
                            await Task.Delay(50);
                            return client.Game.Belt.FindItemById(item.Id) != null;
                        }, TimeSpan.FromSeconds(0.2));
                    }, TimeSpan.FromSeconds(3)))
                    {
                        csManager.PutRejuvenationOnPickitList(client, item);
                    }
                }
            }
        }

        private async Task PickupItemsFromPickupList(Client client, CSManager csManager, double distance)
        {
            var pickitList = csManager.GetPickitList(client, distance);
            foreach (var item in pickitList)
            {
                if (item.Ground)
                {
                    Log.Information($"Client {client.Game.Me.Name} picking up {item.Amount} {item.Name}");
                    await MoveToLocation(client, item.Location);
                    if (client.Game.Inventory.FindFreeSpace(item) != null && await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        client.Game.MoveTo(item.Location);
                        client.Game.PickupItem(item);
                        return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                        {
                            await Task.Delay(50);
                            if (!item.IsGold && client.Game.Inventory.FindItemById(item.Id) == null)
                            {
                                return false;
                            }

                            return true;
                        }, TimeSpan.FromSeconds(0.2));
                    }, TimeSpan.FromSeconds(3)))
                    {
                        InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                    }
                    else
                    {
                        csManager.PutItemOnPickitList(client, item);
                    }
                }
            }
        }
    }
}
