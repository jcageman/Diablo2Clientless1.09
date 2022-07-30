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
using D2NG.Navigation.Services.MapApi;
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
        private readonly IMapApiService _mapApiService;
        private readonly ITownManagementService _townManagementService;
        private readonly IAttackService _attackService;
        private readonly CsConfiguration _csconfig;

        public CSBot(
            IOptions<BotConfiguration> config,
            IOptions<CsConfiguration> csconfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMapApiService mapApiService,
            ITownManagementService townManagementService,
            IAttackService attackService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            this._mapApiService = mapApiService;
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
            var clients = new List<Tuple<AccountCharacter, Client>>();
            foreach (var account in _csconfig.Accounts)
            {
                var client = new Client();
                var accountAndClient = Tuple.Create(account, client);
                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                if (GetIsLeadClient(accountAndClient))
                {
                    client.OnReceivedPacketEvent(InComingPacket.PlayerInGame, (packet) => NewPlayerJoinGame(client, new PlayerInGamePacket(packet)));
                }
                _externalMessagingClient.RegisterClient(client);
                clients.Add(accountAndClient);
            }

            int gameCount = 1;

            try
            {
                while (true)
                {
                    ;
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

                    await Task.Delay(TimeSpan.FromSeconds(2));

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
            finally
            {
                foreach (var client in clients)
                {
                    client.Item2.Disconnect();
                }
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
                await client.Game.LeaveGame();
            }

            if (!await client.RejoinMCP())
            {
                Log.Warning($"Disconnecting client {cowAccount.Username} since reconnecting to MCP failed, reconnecting to realm");
                return await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, cowAccount, 10);
            }

            return true;
        }

        public async Task GameLoop(List<Tuple<AccountCharacter, Client>> clients, int gameCount)
        {
            var newState = new CsState();
            var leadClient = clients.Single(c => GetIsLeadClient(c));
            var csManager = new CSManager(clients.Select(c => c.Item2).ToList());
            var nextGameCancellation = new CancellationTokenSource();
            var gameTasks = clients.Select(async (c, i) =>
            {
                bool isLeadClient = leadClient == c;
                if (!isLeadClient)
                {
                    var numberOfSecondsToWait = (i > 3 ? 15 : 0);
                    await Task.Delay(TimeSpan.FromSeconds(numberOfSecondsToWait));
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
                    Act = Act.Act4,
                    ResurrectMerc = false
                };

                if (!await GeneralHelpers.TryWithTimeout(
                    async (_) =>
                    {
                        var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
                        if (!townTaskResult.Succes)
                        {
                            client.Game.RequestUpdate(client.Game.Me.Id);
                        }
                        return townTaskResult.Succes;
                    },
                    TimeSpan.FromSeconds(20)))
                {
                    return false;
                }

                if (c.Item1.Character.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase))
                {
                    try
                    {
                        var result = await TaxiCs(c.Item2, csManager, newState, nextGameCancellation);
                        return result;
                    }
                    finally
                    {
                        nextGameCancellation.Cancel();
                    }
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

                    var killingAction = GetKillActionForClass(client);
                    var ownState = new CsState();
                    while (!nextGameCancellation.IsCancellationRequested && client.Game.IsInGame())
                    {
                        await Task.Delay(100);
                        if (client.Game.IsInTown() && newState.TeleportId != null)
                        {
                            Log.Information($"Client {client.Game.Me.Name} taking town portal to chaos");
                            var teleportPlayer = client.Game.Players.FirstOrDefault(p => p.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase));
                            if (teleportPlayer == null || !await _townManagementService.TakeTownPortalToArea(client, teleportPlayer, Area.ChaosSanctuary))
                            {
                                continue;
                            }

                            ownState.TeleportId = newState.TeleportId;
                            Log.Information($"Client {client.Game.Me.Name} waiting for bo");
                            if (await WaitForBo(client, csManager, killingAction, nextGameCancellation))
                            {
                                break;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }

                    await BaseCsBot(csManager, ownState, newState, nextGameCancellation, client, killingAction);
                }

                return true;
            }
            ).ToList();

            try
            {
                var firstCompletedResult = await Task.WhenAny(gameTasks);
                if (!await firstCompletedResult)
                {
                    throw new Exception("Forced cancel due to failure");
                }
            }
            catch (Exception)
            {
                nextGameCancellation.Cancel();
            }

            var townResults = await Task.WhenAll(gameTasks);
            if (townResults.Any(r => !r))
            {
                Log.Warning($"One or more characters failed there game task");
                nextGameCancellation.Cancel();
                return;
            }

        }

        private async Task<bool> BaseCsBot(CSManager csManager, CsState ownState, CsState newState,
                                           CancellationTokenSource nextGameCancellation, Client client,
                                           Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action)
        {
            while (!nextGameCancellation.IsCancellationRequested && client.Game.IsInGame())
            {
                await Task.Delay(100);
                if (newState.TeleportHasChanged(ownState) && !client.Game.IsInTown())
                {
                    Log.Information($"Client {client.Game.Me.Name} Taking town portal to town");
                    if (!await _townManagementService.TakeTownPortalToTown(client))
                    {
                        continue;
                    }
                }

                if (client.Game.IsInTown() && newState.TeleportId != null && newState.TeleportHasChanged(ownState))
                {
                    Log.Information($"Client {client.Game.Me.Name} taking town portal to chaos");
                    var teleportPlayer = client.Game.Players.FirstOrDefault(p => p.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase));
                    if (teleportPlayer == null || !await _townManagementService.TakeTownPortalToArea(client, teleportPlayer, Area.ChaosSanctuary))
                    {
                        continue;
                    }

                    ownState.TeleportId = newState.TeleportId;
                }

                if (!client.Game.IsInTown())
                {
                    await Task.Delay(100);
                    if (!await KillBosses(client, csManager, nextGameCancellation, action, ownState, newState))
                    {
                        return false;
                    }
                }
            }

            if (!nextGameCancellation.IsCancellationRequested && !client.Game.IsInGame())
            {
                return false;
            }

            return true;
        }

        private bool GetIsLeadClient(Tuple<AccountCharacter, Client> c)
        {
            return c.Item1.Character.Equals(_csconfig.TeleportCharacterName, StringComparison.CurrentCultureIgnoreCase);
        }

        private async Task<bool> TaxiCs(Client client, CSManager csManager, CsState state, CancellationTokenSource nextGameCancellation)
        {
            Log.Information($"Client {client.Game.Me.Name} Taking waypoint to {Waypoint.RiverOfFlame}");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.RiverOfFlame))
            {
                Log.Warning($"Client {client.Game.Me.Name} Taking waypoint failed at location {client.Game.Me.Location}");
                return false;
            }

            Log.Information($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary}");
            var pathToChaos = await _pathingService.GetPathToObjectWithOffset(client.Game.MapId, Difficulty.Normal, Area.RiverOfFlame, client.Game.Me.Location, EntityCode.WaypointAct4Levels, -6, -319, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToChaos, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary} warp failed at location {client.Game.Me.Location}");
                return false;
            }

            var goalLocation = client.Game.Me.Location.Add(0, -20);
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(goalLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to location within {Area.ChaosSanctuary} failed at location {client.Game.Me.Location}");
                return false;
            }

            var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            state.TeleportId = myPortal.Id;
            state.KillLocation = client.Game.Me.Location;
            csManager.ResetMonsters();
            var action = GetSorceressKillAction(client);
            if (!await WaitForBo(client, csManager, action, nextGameCancellation))
            {
                return false;
            }

            if (nextGameCancellation.IsCancellationRequested)
            {
                return true;
            }

            csManager.ResetMonsters();

            state.TeleportId = null;

            if (!await KillLeftSeal(client, csManager, state))
            {
                return false;
            }

            if (nextGameCancellation.IsCancellationRequested)
            {
                return true;
            }

            csManager.ResetMonsters();
            state.TeleportId = null;

            if (!await KillTopSeal(client, csManager, state))
            {
                return false;
            }

            if (nextGameCancellation.IsCancellationRequested)
            {
                return true;
            }

            csManager.ResetMonsters();
            state.TeleportId = null;

            if (!await KillRightSeal(client, csManager, state))
            {
                return false;
            }

            if (nextGameCancellation.IsCancellationRequested)
            {
                return true;
            }

            csManager.ResetMonsters();
            state.TeleportId = null;

            return await KillDiablo(client, csManager, state);
        }

        private async Task<bool> WaitForBo(Client client, CSManager csManager, Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action, CancellationTokenSource nextGameCancellation)
        {
            var initialLocation = client.Game.Me.Location;
            var csState = new CsState();
            csState.KillLocation = initialLocation;
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            while (stopWatch.Elapsed < TimeSpan.FromSeconds(30) && ClassHelpers.AnyClientIsMissingShouts(csManager.Clients) && !nextGameCancellation.IsCancellationRequested)
            {
                var taskCancellation = new CancellationTokenSource();
                taskCancellation.CancelAfter(500);
                await client.Game.MoveToAsync(initialLocation.Add((short)new Random().Next(-5, 5), (short)new Random().Next(-5, 5)));

                await KillBosses(client, csManager, taskCancellation, action, csState, csState, 10);
                var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
                var pathToSeal = await _pathingService.GetPathToLocation(client.Game, initialLocation, movementMode);
                await MovementHelpers.TakePathOfLocations(client.Game, pathToSeal, movementMode);
            }

            if (stopWatch.Elapsed >= TimeSpan.FromSeconds(30))
            {
                Log.Warning($"Client {client.Game.Me.Name} Failed waiting for bo at area {client.Game.Area} at location {client.Game.Me.Location}");
                return false;
            }

            return true;
        }

        private async Task<bool> KillDiablo(Client client, CSManager csManager, CsState csState)
        {
            var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = client.Game.Me.Location;
            var action = GetSorceressKillAction(client);
            if (!await KillBosses(client, csManager, null, action, csState, csState))
            {
                return false;
            }
            return true;
        }

        private async Task<bool> KillRightSeal(Client client, CSManager csManager, CsState csState)
        {
            Log.Information($"Teleporting to {EntityCode.RightSeal1}");

            var seal1 = await GetSeal(client, EntityCode.RightSeal1);
            var seal2 = await GetSeal(client, EntityCode.RightSeal2);
            var killLocation = seal1.X < seal2.X ? seal1.Add(30, -10) : seal1.Add(12, -38);

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var pathToSeal = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
                return await MovementHelpers.TakePathOfLocations(client.Game, pathToSeal, MovementMode.Teleport);
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Teleporting to killing location of {EntityCode.RightSeal1} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = killLocation;

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(seal1);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var rightseal1Entity = client.Game.GetEntityByCode(EntityCode.RightSeal1).First();
                if (rightseal1Entity.State == EntityState.Activating || rightseal1Entity.State == EntityState.Activated)
                {
                    return true;
                }
                client.Game.InteractWithEntity(rightseal1Entity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(seal2);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var rightseal2Entity = client.Game.GetEntityByCode(EntityCode.RightSeal2).First();
                if (rightseal2Entity.State == EntityState.Activating || rightseal2Entity.State == EntityState.Activated)
                {
                    return true;
                }
                client.Game.InteractWithEntity(rightseal2Entity);
                await Task.Delay(100);
                return false;
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
            if (!await KillBosses(client, csManager, null, action, csState, csState))
            {
                return false;
            }

            return true;
        }

        private async Task<bool> KillTopSeal(Client client, CSManager csManager, CsState csState)
        {
            Log.Information($"Teleporting to {EntityCode.TopSeal}");

            Point topSeal = await GetSeal(client, EntityCode.TopSeal);
            var toLeftOfSealIsValid = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, topSeal.Add(-20, 0));
            var killLocation = toLeftOfSealIsValid ? topSeal.Add(-37, 31) : topSeal.Add(0, 70);
            var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to kill location {killLocation} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = killLocation;

            var pathToTopSeal2 = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.TopSeal, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTopSeal2, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.TopSeal} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var topSealEntity = client.Game.GetEntityByCode(EntityCode.TopSeal).First();
                if (topSealEntity.State == EntityState.Activating || topSealEntity.State == EntityState.Activated)
                {
                    return true;
                }
                client.Game.InteractWithEntity(topSealEntity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning($"Opening {EntityCode.TopSeal} failed at location {client.Game.Me.Location} with state {client.Game.GetEntityByCode(EntityCode.TopSeal).FirstOrDefault()?.State}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
                return await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport);
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Teleporting back to kill location failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await MoveKillingLocationIfFar(client, csManager, csState))
            {
                Log.Warning($"Bosses are far from usual location, moving location {client.Game.Me.Name}");
                return false;
            }

            Log.Information($"Killing top seal bosses {client.Game.Me.Name}");
            var action = GetSorceressKillAction(client);
            if (!await KillBosses(client, csManager, null, action, csState, csState))
            {
                return false;
            }

            return true;
        }

        private async Task<bool> MoveKillingLocationIfFar(Client client, CSManager csManager, CsState csState)
        {
            Log.Information($"Kill location {csState.KillLocation}");
            AliveMonster boss = null;
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                boss = csManager.GetNearbyAliveMonsters(csState.KillLocation, 80).FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                if (boss == null)
                {
                    await Task.Delay(100);
                }
                return boss != null;
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning($"Waiting for bosses to spawn failed at {client.Game.Me.Location}");
                return false;
            }

            if (client.Game.Me.Location.Distance(boss.Location) > 20)
            {
                Log.Information($"Bosses are far from usual location, moving location {client.Game.Me.Name}");
                var pathToBosses = await _pathingService.GetPathToLocation(client.Game, boss.Location, MovementMode.Teleport);
                if (await MovementHelpers.TakePathOfLocations(client.Game, pathToBosses, MovementMode.Teleport))
                {
                    if (!await _townManagementService.CreateTownPortal(client))
                    {
                        return false;
                    }

                    var newPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
                    csState.TeleportId = newPortal.Id;
                    csState.KillLocation = boss.Location;
                    return true;
                }

                return false;
            }

            return true;
        }

        private async Task<bool> KillLeftSeal(Client client, CSManager csManager, CsState csState)
        {
            Log.Information($"Teleporting to {EntityCode.LeftSeal1}");

            Point leftSeal1 = await GetSeal(client, EntityCode.LeftSeal1);
            Point leftSeal2 = await GetSeal(client, EntityCode.LeftSeal2);
            var leftSealKillLocation = leftSeal1.Y > leftSeal2.Y ? leftSeal1.Add(26, -21) : leftSeal1.Add(20, 40);

            var pathToLeftSeal = await _pathingService.GetPathToLocation(client.Game, leftSealKillLocation, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToLeftSeal, MovementMode.Teleport))
            {
                Log.Warning($"Teleporting to {EntityCode.LeftSeal1} failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            csState.TeleportId = myPortal.Id;
            csState.KillLocation = leftSealKillLocation;
            Log.Information($"Kill location {csState.KillLocation} with left seal kill {leftSealKillLocation}");
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSeal1);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var leftSeal1Entity = client.Game.GetEntityByCode(EntityCode.LeftSeal1).First();
                if (leftSeal1Entity.State == EntityState.Activating || leftSeal1Entity.State == EntityState.Activated)
                {
                    return true;
                }

                client.Game.InteractWithEntity(leftSeal1Entity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(leftSeal2);
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var leftSeal2Entity = client.Game.GetEntityByCode(EntityCode.LeftSeal2).First();
                if (leftSeal2Entity.State == EntityState.Activating || leftSeal2Entity.State == EntityState.Activated)
                {
                    return true;
                }

                client.Game.InteractWithEntity(leftSeal2Entity);
                await Task.Delay(100);
                return false;
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

            if (!await MoveKillingLocationIfFar(client, csManager, csState))
            {
                Log.Warning($"Bosses are far from usual location, moving location {client.Game.Me.Name}");
                return false;
            }

            var action = GetSorceressKillAction(client);
            if (!await KillBosses(client, csManager, null, action, csState, csState))
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
                CharacterClass.Amazon => GetAmazonKillAction(client),
                _ => new Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task>((csManager, enemies, bosses) =>
{
    return Task.CompletedTask;
}),
            };
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetAmazonKillAction(Client client)
        {
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = (async (csManager, enemies, bosses) =>
            {
                var nearest = bosses.FirstOrDefault();
                if (nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 10);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 10);
                    nearest = enemies.FirstOrDefault();
                }

                if (nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    return;
                }

                var nearbyPlayer = client.Game.Players
                .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Paladin || p.Class == CharacterClass.Barbarian))
                .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                if(nearbyPlayer != null)
                {
                    await _attackService.AssistPlayer(client, nearbyPlayer);
                }
            });
            return action;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetBarbarianKillAction(Client client)
        {
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = (async (csManager, enemies, bosses) =>
            {
                var anyPlayersWithoutShouts = ClassHelpers.AnyPlayerIsMissingShouts(client);
                if (anyPlayersWithoutShouts && client.Game.Me.Class == CharacterClass.Barbarian)
                {
                    await ClassHelpers.CastAllShouts(client);
                }

                if (client.Game.Me.Effects.ContainsKey(EntityEffect.Ironmaiden))
                {
                    var nearbyPlayer = client.Game.Players.Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Location.Distance(client.Game.Me.Location) > 5 && p.Class == CharacterClass.Paladin).OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                    if (nearbyPlayer != null)
                    {
                        if (nearbyPlayer.Location.Distance(client.Game.Me.Location) < 40 && nearbyPlayer.Location.Distance(client.Game.Me.Location) > 10)
                        {
                            var pathNearest = await _pathingService.GetPathToLocation(client.Game, nearbyPlayer.Location, MovementMode.Walking);
                            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                            {
                                Log.Warning($"Walking to Player failed at {client.Game.Me.Location}");
                            }
                        }
                        else
                        {
                            await client.Game.MoveToAsync(nearbyPlayer.Location.Add((short)new Random().Next(-10, 10), (short)new Random().Next(-10, 10)));
                        }
                    }

                    await PickupItemsFromPickupList(client, csManager, 20);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 20);

                    return;
                }

                var nearest = bosses.FirstOrDefault();
                if (nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 10);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 10);
                    nearest = enemies.FirstOrDefault();
                }

                if (nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    return;
                }

                if (client.Game.Me.Location.Distance(nearest.Location) > 10)
                {
                    var closeTo = client.Game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 6);
                    if (client.Game.Me.Location.Distance(closeTo) < 3)
                    {
                        closeTo = nearest.Location;
                    }

                    var cts = new CancellationTokenSource();
                    cts.CancelAfter(2000);
                    var pathNearest = await _pathingService.GetPathToLocation(client.Game, nearest.Location, MovementMode.Walking);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking, cts.Token))
                    {
                        Log.Warning($"Walking to Boss failed at {client.Game.Me.Location}");
                        return;
                    }
                }

                var wwDirection = client.Game.Me.Location.GetPointPastPointInSameDirection(nearest.Location, 6);
                if (client.Game.Me.Location.Equals(nearest.Location) || !await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, client.Game.Area, wwDirection))
                {
                    var bestPathCount = int.MaxValue;
                    foreach (var (p1, p2) in new List<(short, short)> { (-6, 0), (6, 0), (0, -6), (0, 6), (3, 3), (-3, -3), (-3, 3), (3, 3) })
                    {
                        var move = nearest.Location.Add(p1, p2);
                        if (client.Game.Me.Location.Distance(move) < 5)
                        {
                            continue;
                        }

                        if (!await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, client.Game.Area, move))
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

                client.Game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                await Task.Delay(TimeSpan.FromSeconds(0.3));
            });
            return action;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetPaladinKillAction(Client client)
        {
            Skill? auraSkill = null;
            if (client.Game.Me.HasSkill(Skill.Fanaticism))
            {
                auraSkill = Skill.Fanaticism;
            }
            else if (client.Game.Me.HasSkill(Skill.Concentration))
            {
                auraSkill = Skill.Concentration;
            }
            var bhTimer = new Stopwatch();
            bhTimer.Start();

            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = async (csManager, enemies, bosses) =>
            {
                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Holyshield) && client.Game.Me.HasSkill(Skill.HolyShield))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
                    await Task.Delay(100);
                }

                if (auraSkill != null && client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var currentSkill) && currentSkill != auraSkill)
                {
                    client.Game.ChangeSkill(auraSkill.Value, Hand.Right);
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

                if (nearest.Location.Distance(client.Game.Me.Location) > 15)
                {
                    var goalLocation = client.Game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 3);
                    client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
                    var cts = new CancellationTokenSource();
                    cts.CancelAfter(3000);
                    var pathNearest = await _pathingService.GetPathToLocation(client.Game, goalLocation, MovementMode.Walking);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking, cts.Token))
                    {
                        Log.Warning($"Walking to Nearest failed at {client.Game.Me.Location}");
                    }
                }

                if (bhTimer.Elapsed > TimeSpan.FromSeconds(0.3))
                {
                    client.Game.ShiftHoldLeftHandSkillOnLocation(Skill.BlessedHammer, client.Game.Me.Location);
                    bhTimer.Restart();
                }
            };
            return action;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetSorceressKillAction(Client client)
        {
            var moveTimer = new Stopwatch();
            moveTimer.Start();
            var orbTimer = new Stopwatch();
            orbTimer.Start();
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = (async (csManager, enemies, bosses) =>
            {
                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                }

                var nearest = bosses.FirstOrDefault();
                if (nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 30);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 30);
                    nearest = enemies.FirstOrDefault();
                }

                if (nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
                {
                    return;
                }

                var distanceToNearest = nearest.Location.Distance(client.Game.Me.Location);
                if (nearest.NPCCode == NPCCode.Diablo)
                {
                    if (distanceToNearest > 15)
                    {
                        await client.Game.TeleportToLocationAsync(nearest.Location);
                    }
                }
                else if (distanceToNearest < 5 || moveTimer.Elapsed > TimeSpan.FromSeconds(2))
                {
                    var nearbyPlayer = client.Game.Players
                    .Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Paladin)
                    .FirstOrDefault();
                    var nearbyPortal = client.Game.GetEntityByCode(EntityCode.TownPortal)
                    .OrderBy(t => t.Location.Distance(client.Game.Me.Location))
                    .FirstOrDefault();
                    var nearbyLocation = nearbyPlayer != null ? nearbyPlayer.Location : nearbyPortal?.Location;
                    if (nearbyLocation != null && await _attackService.MoveToNearbySafeSpot(client, enemies.Select(e => e.Location).ToList(), nearbyLocation, MovementMode.Teleport, 10))
                    {
                        moveTimer.Restart();
                    }
                    else if (nearbyLocation != null)
                    {
                        Log.Warning($"Back-up move teleporting to nearby player for {client.Game.Me.Name} on location {client.Game.Me.Location}");
                        await client.Game.TeleportToLocationAsync(nearbyLocation);
                    }
                }

                var monster = client.Game.WorldObjects.GetValueOrDefault((nearest.Id, EntityType.NPC));
                if (monster == null)
                {
                    return;
                }

                if (client.Game.Me.HasSkill(Skill.FrozenOrb)
                && orbTimer.Elapsed > TimeSpan.FromSeconds(1)
                && distanceToNearest < 20
                && (!monster.Effects.Contains(EntityEffect.Cold) || !ClassHelpers.CanStaticEntity(client, monster.LifePercentage)))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, monster.Location);
                    orbTimer.Restart();
                }
                else if (!client.Game.Me.HasSkill(Skill.FrozenOrb) && client.Game.Me.HasSkill(Skill.FrostNova) && distanceToNearest < 10 && !monster.Effects.Contains(EntityEffect.Cold))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.FrostNova, client.Game.Me.Location);
                    await Task.Delay(TimeSpan.FromSeconds(0.25));
                }
                else if (client.Game.Me.HasSkill(Skill.StaticField) && distanceToNearest < 20)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }
                await Task.Delay(TimeSpan.FromSeconds(0.1));
            });
            return action;
        }

        private Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> GetNecromancerKillAction(Client client)
        {
            var curseTimer = new Stopwatch();
            curseTimer.Start();
            var ceTimer = new Stopwatch();
            ceTimer.Start();
            Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action = async (csManager, enemies, bosses) =>
            {
                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Bonearmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BoneArmor, client.Game.Me.Location);
                    await Task.Delay(100);
                }

                if (client.Game.Me.Mana > 100
                    && client.Game.Me.HasSkill(Skill.Bloodgolem)
                    && !client.Game.Me.Summons.Exists(s => s.NPCCode == NPCCode.BloodGolem))
                {
                    Log.Information($"Summoning {NPCCode.BloodGolem}");
                    client.Game.UseRightHandSkillOnLocation(Skill.Bloodgolem, client.Game.Me.Location);
                    await Task.Delay(200);
                }

                var nearest = bosses.FirstOrDefault();
                if (nearest == null)
                {
                    await PickupItemsFromPickupList(client, csManager, 10);
                    await PickupNearbyRejuvenationsIfNeeded(client, csManager, 10);
                    nearest = enemies.FirstOrDefault();
                }

                if (nearest == null)
                {
                    await Task.Delay(100);
                    return;
                }

                if (curseTimer.Elapsed > TimeSpan.FromSeconds(1))
                {
                    foreach (var player in client.Game.Players
                        .Where(p => p.Class == CharacterClass.Barbarian && p.Location != null && p.Location.Distance(client.Game.Me.Location) < 30))
                    {
                        if (nearest.NPCCode == NPCCode.Diablo)
                        {
                            client.Game.UseRightHandSkillOnLocation(Skill.AmplifyDamage, player.Location);
                        }
                        else if (client.Game.Me.HasSkill(Skill.LifeTap))
                        {
                            client.Game.UseRightHandSkillOnLocation(Skill.LifeTap, player.Location);
                            await Task.Delay(200);
                        }
                    }
                    curseTimer.Restart();
                }


                var nearbyPlayer = client.Game.Players.Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Paladin).OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
                if (nearbyPlayer != null && nearbyPlayer.Location.Distance(client.Game.Me.Location) > 10)
                {
                    if (!await _attackService.MoveToNearbySafeSpot(client, enemies.Select(e => e.Location).ToList(), nearbyPlayer.Location, MovementMode.Walking, 15))
                    {
                        var toNearbyPaladinLocation = await _pathingService.GetPathToLocation(client.Game, nearbyPlayer.Location, MovementMode.Walking);
                        var cts = new CancellationTokenSource();
                        cts.CancelAfter(3000);
                        await MovementHelpers.TakePathOfLocations(client.Game, toNearbyPaladinLocation, MovementMode.Walking, cts.Token);
                    }
                }
                else if (ceTimer.Elapsed > TimeSpan.FromSeconds(0.3))
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
                                Func<CSManager, List<AliveMonster>, List<AliveMonster>, Task> action,
                                CsState ownState,
                                CsState newState,
                                double distance = 80.0)
        {
            var stopWatch = new Stopwatch();
            var stepTimeWatch = new Stopwatch();
            stepTimeWatch.Start();
            bool bFoundBosses = false;
            var enemies = new List<AliveMonster>();
            var bosses = new List<AliveMonster>();
            var deadBosses = false;
            do
            {
                await Task.Delay(100);
                if (stepTimeWatch.Elapsed > TimeSpan.FromSeconds(60))
                {
                    return false;
                }

                if (!stopWatch.IsRunning || stopWatch.Elapsed > TimeSpan.FromSeconds(0.5))
                {
                    enemies = csManager.GetNearbyAliveMonsters(newState.KillLocation, distance);
                    bosses = enemies.Where(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique)).ToList();
                    deadBosses = csManager.GetNearbyDeadMonsters(newState.KillLocation, distance).Any(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
                    if (stopWatch.IsRunning)
                    {
                        stopWatch.Restart();
                    }
                    if (bosses.Any())
                    {
                        if (!bFoundBosses)
                        {
                            bFoundBosses = true;
                            stopWatch.Start();
                        }
                    }
                }

                await action.Invoke(csManager, enemies, bosses);

            } while ((nextGameCancellation == null || !nextGameCancellation.IsCancellationRequested) && client.Game.IsInGame() && ((!bFoundBosses && !deadBosses) || bosses.Any()) && !ownState.TeleportHasChanged(newState));

            if (client.Game.IsInGame())
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
                    await client.Game.TeleportToLocationAsync(location);
                }
                else
                {
                    await client.Game.MoveToAsync(location);
                }
            }
        }

        private async Task<Point> GetSeal(Client client, EntityCode entityCode)
        {
            var map = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary);
            if (!map.Objects.TryGetValue((int)entityCode, out var objectPoints) || objectPoints.Count == 0)
            {
                Log.Error($"Did not find a {entityCode} in the mapdata");
                return null;
            }

            return objectPoints.First();
        }

        private async Task PickupNearbyRejuvenationsIfNeeded(Client client, CSManager csManager, int distance)
        {
            var totalRejuvanationPotions = client.Game.Inventory.Items.Count(i => i.Name == ItemName.RejuvenationPotion || i.Name == ItemName.FullRejuvenationPotion);
            var pickitList = csManager.GetRejuvenationPotionPickupList(client, distance, 5 - totalRejuvanationPotions);
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
            var maxPicks = 3;
            var picks = 0;
            var pickitList = new List<Item>();
            do
            {
                picks++;
                pickitList = pickitList = csManager.GetPickitList(client, distance);
                foreach (var item in pickitList)
                {
                    if (item.Ground)
                    {
                        Log.Information($"Client {client.Game.Me.Name} picking up {item.Amount} {item.Name}");
                        await MoveToLocation(client, item.Location);
                        if (client.Game.Inventory.FindFreeSpace(item) != null && await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                        {
                            await client.Game.MoveToAsync(item.Location);
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
            while (pickitList.Count != 0 && picks < maxPicks);
        }
    }
}
