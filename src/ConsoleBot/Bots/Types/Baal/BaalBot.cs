using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace ConsoleBot.Bots.Types.Baal
{
    public class BaalBot : IBotInstance
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly ITownManagementService _townManagementService;
        private readonly IAttackService _attackService;
        private readonly IMapApiService _mapApiService;
        private readonly IMuleService _muleService;
        private readonly BaalConfiguration _baalConfig;
        private TaskCompletionSource<bool> NextGame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> BaalPortalOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConcurrentDictionary<string, ManualResetEvent> PlayersInGame = new ConcurrentDictionary<string, ManualResetEvent>();
        private uint? BoClientPlayerId;
        private uint? PortalClientPlayerId;
        private ConcurrentDictionary<string, bool> ShouldFollow = new ConcurrentDictionary<string, bool>();
        private ConcurrentDictionary<string, (Point, CancellationTokenSource)> FollowTasks = new ConcurrentDictionary<string, (Point, CancellationTokenSource)>();
        private HashSet<string> ClientsNeedingMule = new HashSet<string>();
        public BaalBot(IOptions<BotConfiguration> config, IOptions<BaalConfiguration> baalconfig,
            IExternalMessagingClient externalMessagingClient, IPathingService pathingService,
            ITownManagementService townManagementService,
            IAttackService attackService,
            IMapApiService mapApiService,
            IMuleService muleService
            )
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _townManagementService = townManagementService;
            _attackService = attackService;
            _mapApiService = mapApiService;
            _muleService = muleService;
            _baalConfig = baalconfig.Value;
        }

        public string GetName()
        {
            return "baal";
        }

        public async Task Run()
        {
            _baalConfig.Validate();
            var clients = new List<Client>();
            foreach (var account in _baalConfig.Accounts)
            {
                var client = new Client();
                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                _externalMessagingClient.RegisterClient(client);
                PlayersInGame.TryAdd(account.Character.ToLower(), new ManualResetEvent(false));
                ShouldFollow.TryAdd(account.Character.ToLower(), false);
                FollowTasks.TryAdd(account.Character.ToLower(), (null, new CancellationTokenSource()));
                client.OnReceivedPacketEvent(InComingPacket.EntityMove, async (packet) =>
                {
                    var entityMovePacket = new EntityMovePacket(packet);
                    if (entityMovePacket.UnitType == EntityType.Player && entityMovePacket.UnitId == PortalClientPlayerId && ShouldFollowLeadClient(client))
                    {
                        await FollowToLocation(client, entityMovePacket.MoveToLocation);
                    }
                });

                client.OnReceivedPacketEvent(InComingPacket.ReassignPlayer, async (packet) =>
                {
                    var reassignPlayerPacket = new ReassignPlayerPacket(packet);
                    if (reassignPlayerPacket.UnitType == EntityType.Player && reassignPlayerPacket.UnitId == PortalClientPlayerId && ShouldFollowLeadClient(client))
                    {
                        await FollowToLocation(client, reassignPlayerPacket.Location);
                    }
                });
                client.OnReceivedPacketEvent(InComingPacket.PartyAutomapInfo, async (packet) =>
                {
                    var partyAutomapInfoPacket = new PartyAutomapInfoPacket(packet);
                    if (partyAutomapInfoPacket.Id == PortalClientPlayerId && ShouldFollowLeadClient(client))
                    {
                        await FollowToLocation(client, partyAutomapInfoPacket.Location);
                    }
                });
                clients.Add(client);
            }

            var firstFiller = clients.First();
            firstFiller.OnReceivedPacketEvent(InComingPacket.PlayerInGame, (packet) => PlayerInGame(new PlayerInGamePacket(packet).Name));
            firstFiller.OnReceivedPacketEvent(InComingPacket.AssignPlayer, (packet) => PlayerInGame(new AssignPlayerPacket(packet).Name));
            firstFiller.OnReceivedPacketEvent(InComingPacket.TownPortalState, (packet) => TownPortalState(new TownPortalStatePacket(packet)));
            firstFiller.OnReceivedPacketEvent(InComingPacket.PlayerInGame, (packet) => NewPlayerJoinGame(firstFiller, new PlayerInGamePacket(packet)));
            firstFiller.OnReceivedPacketEvent(InComingPacket.ReceiveChat, (packet) =>
            {
                var chatPacket = new ChatPacket(packet);
                if (chatPacket.Message.Contains("next") || chatPacket.Message == "ng")
                {
                    NextGame.TrySetResult(true);
                }
            });

            int gameCount = 1;
            while (true)
            {
                foreach (var playerInGame in PlayersInGame)
                {
                    playerInGame.Value.Reset();
                }

                foreach (var key in ShouldFollow.Keys)
                {
                    ShouldFollow[key] = false;
                }

                foreach (var task in FollowTasks)
                {
                    task.Value.Item2.Cancel();
                }

                BaalPortalOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                NextGame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                BoClientPlayerId = null;
                PortalClientPlayerId = null;
                Log.Information($"Joining next game");

                try
                {
                    var leaveAndRejoinTasks = clients.Select(async (client, index) =>
                    {
                        var account = _baalConfig.Accounts[index];
                        return await LeaveGameAndRejoinMCPWithRetry(client, account);
                    }).ToList();
                    var rejoinResults = await Task.WhenAll(leaveAndRejoinTasks);
                    if (rejoinResults.Any(r => !r))
                    {
                        gameCount++;
                        continue;
                    }

                    foreach (var client in ClientsNeedingMule)
                    {
                        var foundClient = clients.Single(c => c.LoggedInUserName() == client);
                        await _externalMessagingClient.SendMessage($"{client}: needs mule, starting mule");
                        if (!await _muleService.MuleItemsForClient(foundClient))
                        {
                            await _externalMessagingClient.SendMessage($"{client}: failed mule");
                        }
                        else
                        {
                            await _externalMessagingClient.SendMessage($"{client}: finished mule");
                        }
                    }
                    ClientsNeedingMule.Clear();

                    var result = await RealmConnectHelpers.CreateGameWithRetry(gameCount, firstFiller, _config, _baalConfig.Accounts.First());
                    gameCount = result.Item2;
                    if (!result.Item1)
                    {
                        gameCount++;
                        Thread.Sleep(30000);
                        continue;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Failed one or more creates and joins, disconnecting clients {e}");
                    await LeaveGameAndDisconnectWithAllClients(clients);
                    gameCount++;
                    continue;
                }

                try
                {
                    var townTasks = new List<Task<bool>>();
                    bool anyTownFailures = false;
                    var rand = new Random();
                    for (int i = 0; i < clients.Count(); i++)
                    {
                        var account = _baalConfig.Accounts[i];
                        var client = clients[i];
                        Thread.Sleep(TimeSpan.FromSeconds(1 + i * 0.7));
                        if (firstFiller != client && !await RealmConnectHelpers.JoinGameWithRetry(gameCount, client, _config, account))
                        {
                            Log.Warning($"Client {client.LoggedInUserName()} failed to join game, retrying new game");
                            anyTownFailures = true;
                            break;
                        }

                        townTasks.Add(PrepareForBaalsTasks(client));
                    }

                    var townResults = await Task.WhenAll(townTasks);
                    if (anyTownFailures)
                    {
                        gameCount++;
                        continue;
                    }

                    if (townResults.Any(r => !r))
                    {
                        Log.Warning($"One or more characters failed there town task");
                        gameCount++;
                        continue;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Failed one or more town tasks with exception {e}");
                    continue;
                }

                if (!WaitHandle.WaitAll(PlayersInGame.Values.ToArray(), TimeSpan.FromSeconds(5)))
                {
                    Log.Information($"Not all players joined the game in time, retrying");
                    gameCount++;
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));

                foreach (var player in firstFiller.Game.Players)
                {
                    if (firstFiller.Game.Me.Id == player.Id)
                    {
                        continue;
                    }

                    firstFiller.Game.InvitePlayer(player);
                }

                var boClient = clients.Aggregate((agg, client) =>
                {
                    var boClient = client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0);
                    var boAgg = agg?.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) ?? 0;
                    if (boClient > 0 && boClient > boAgg)
                    {
                        return client;
                    }

                    return agg;
                });

                if (boClient == null)
                {
                    Log.Error($"Expected at least bo barb in game");
                    return;
                }

                BoClientPlayerId = boClient.Game.Me.Id;
                var portalClient = clients.First(c => _baalConfig.PortalCharacterName.Equals(c.Game.Me.Name, StringComparison.InvariantCultureIgnoreCase));
                PortalClientPlayerId = portalClient.Game.Me.Id;
                Log.Information($"Waiting for baal portal to open");
                await BaalPortalOpen.Task;
                Log.Information($"Baal portal open, moving to throne room");

                var killingClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && c.Game.Me.HasSkill(Skill.Nova)).ToList();
                Log.Information($"Selected {string.Join(",", killingClients.Select(c => c.Game.Me.Name))} for baal manager");
                var listeningClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && !killingClients.Contains(c)).ToList();
                listeningClients.Add(boClient);
                var baalManager = new BaalManager(killingClients, listeningClients, _mapApiService);
                await baalManager.Initialize();

                try
                {
                    var clientTasks = new List<Task<bool>>();
                    foreach (var client in clients)
                    {
                        clientTasks.Add(RunBaals(client, baalManager));
                    }

                    await Task.WhenAll(clientTasks);
                }
                catch (Exception e)
                {
                    Log.Error($"Failed one or more tasks with exception {e}");
                }

                Log.Information($"Going to next game");
                gameCount++;
            }
        }

        private async Task<bool> PrepareForBaalsTasks(Client client)
        {
            var timer = new Stopwatch();
            timer.Start();
            while (client.Game.Me == null && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(100);
            }

            if (client.Game.Me == null)
            {
                Log.Error($"{client.Game.Me.Name} failed to initialize Me");
                return false;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);
            if (!GeneralHelpers.TryWithTimeout(
                (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
                TimeSpan.FromSeconds(5)))
            {
                Log.Error($"{client.Game.Me.Name} failed to initialize current location");
                return false;
            }

            var townManagementOptions = new TownManagementOptions()
            {
                Act = Act.Act5,
            };

            var isPortalCharacter = _baalConfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.InvariantCultureIgnoreCase);
            if (isPortalCharacter)
            {
                var tomesOfTp = client.Game.Inventory.Items.Count(i => i.Name == ItemName.TomeOfTownPortal);
                if (tomesOfTp < 2)
                {
                    townManagementOptions.ItemsToBuy = new Dictionary<ItemName, int>()
                                   {
                                       { ItemName.TomeOfTownPortal, 2 - tomesOfTp }
                                   };
                }
            }

            var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
            if (townTaskResult.ShouldMule)
            {
                ClientsNeedingMule.Add(client.LoggedInUserName());
            }
            if (!townTaskResult.Succes)
            {
                return false;
            }

            if (isPortalCharacter)
            {
                if (!await CreateThroneRoomTp(client))
                {
                    return false;
                }
            }
            else
            {
                var tpLocation = new Point(5100, 5025);
                var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
                var pathBack = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.Harrogath, client.Game.Me.Location, tpLocation, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBack, movementMode))
                {
                    Log.Warning($"Client {client.Game.Me.Name} {movementMode} back failed at {client.Game.Me.Location}");
                    return false;
                }
            }

            return true;
        }

        private static async Task LeaveGameAndDisconnectWithAllClients(List<Client> clients)
        {
            foreach (var client in clients)
            {
                if (client.Game.IsInGame())
                {
                    await client.Game.LeaveGame();
                }
                await client.Disconnect();
            }
        }

        private async Task<bool> RunBaals(Client client, BaalManager baalManager)
        {
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                return await _townManagementService.TakeTownPortalToArea(client, client.Game.Players.First(p => p.Id == PortalClientPlayerId), Area.ThroneOfDestruction);
            }, TimeSpan.FromSeconds(30)))
            {
                Log.Warning($"Client {client.Game.Me.Name} stopped waiting throne run to start");
                NextGame.TrySetResult(true);
                return false;
            }

            if (client.Game.Me.Id == PortalClientPlayerId && !_townManagementService.CreateTownPortal(client))
            {
                NextGame.TrySetResult(true);
                return false;
            }

            if (!await SetupBo(client))
            {
                NextGame.TrySetResult(true);
                return false;
            }

            await GetTaskForWave(client, baalManager);

            if (client.Game.Me.Id == PortalClientPlayerId)
            {
                if (!await CreateBaalPortal(client, baalManager))
                {
                    NextGame.TrySetResult(true);
                    return false;
                }
                Log.Information($"Client {client.Game.Me.Name} created baal portal");
            }
            else
            {
                if (!await _townManagementService.TakeTownPortalToTown(client))
                {
                    NextGame.TrySetResult(true);
                    return false;
                }
            }

            await GetTaskForBaal(client, baalManager);
            return true;
        }

        private async Task<bool> CreateBaalPortal(Client client, BaalManager baalManager)
        {
            var baalPortal = client.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(100);
                baalPortal = client.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
                if (baalPortal == null)
                {
                    return false;
                }
                var pathBaalPortal = await _pathingService.GetPathToLocation(client.Game, baalPortal.Location, MovementMode.Teleport);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBaalPortal, MovementMode.Teleport))
                {
                    Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} to {EntityCode.BaalPortal} failed at {client.Game.Me.Location}");
                }

                return true;
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Client {client.Game.Me.Name} finding and moving to {EntityCode.BaalPortal} failed at {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await client.Game.MoveToAsync(baalPortal);

                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }

                client.Game.InteractWithEntity(baalPortal);
                return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(50);
                    return client.Game.Area == Area.TheWorldStoneChamber;
                }, TimeSpan.FromSeconds(0.5));
            }, TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }

                await Task.Delay(100);

                return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.TheWorldStoneChamber, client.Game.Me.Location);
            }, TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            var pathBaal = await _pathingService.GetPathToNPC(client.Game, NPCCode.Baal, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBaal, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} to {NPCCode.Baal} failed at {client.Game.Me.Location}");
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                var baal = baalManager.GetNearbyAliveMonsters(client, 200, 1).FirstOrDefault();
                if (baal == null)
                {
                    return false;
                }
                if (baal.Location.Distance(client.Game.Me.Location) > 10)
                {
                    pathBaal = await _pathingService.GetPathToLocation(client.Game, baal.Location, MovementMode.Teleport);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBaal, MovementMode.Teleport))
                    {
                        Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} to {NPCCode.Baal} failed at {client.Game.Me.Location}");
                        return false;
                    }
                }

                return true;
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} close to {NPCCode.Baal} failed at {client.Game.Me.Location}");
                return false;
            }

            if (!_townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            return true;
        }

        private async Task<bool> SetupBo(Client client)
        {
            if (client.Game.Me.Id == BoClientPlayerId)
            {
                await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                    if (retryCount % 5 == 0)
                    {
                        foreach (var player in client.Game.Players.Where(p => p.Location?.Distance(client.Game.Me.Location) < 10))
                        {
                            client.Game.RequestUpdate(player.Id);
                        }
                    }
                    return await ClassHelpers.CastAllShouts(client);
                }, TimeSpan.FromSeconds(15));
            }
            else
            {
                var random = new Random();
                await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    var boPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                    if (boPlayer != null)
                    {
                        var randomPointNear = boPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                        await client.Game.MoveToAsync(randomPointNear);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                    return !ClassHelpers.IsMissingShouts(client.Game.Me);
                }, TimeSpan.FromSeconds(15));
            }

            return true;
        }

        private async Task<bool> CreateThroneRoomTp(Client client)
        {
            var game = client.Game;
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.TheWorldStoneKeepLevel2))
            {
                Log.Information($"Taking {client.Game.Act} waypoint failed");
                return false;
            }

            var pathToWorldStone3 = await _pathingService.GetPathFromWaypointToArea(client.Game.MapId, Difficulty.Normal, Area.TheWorldStoneKeepLevel2, Waypoint.TheWorldStoneKeepLevel2, Area.TheWorldStoneKeepLevel3, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToWorldStone3, MovementMode.Teleport))
            {
                Log.Error($"Teleporting to {Area.TheWorldStoneKeepLevel3}  failed");
                return false;
            }

            var warp1 = client.Game.GetNearestWarp();
            if (warp1 == null || warp1.Location.Distance(client.Game.Me.Location) > 20)
            {
                Log.Warning($"Warp not close enough at location {warp1?.Location} while at location {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (warp1.Location.Distance(client.Game.Me.Location) > 5 && !await game.TeleportToLocationAsync(warp1.Location))
                {
                    Log.Debug($"Teleport to {warp1.Location} failing retrying at location: {game.Me.Location}");
                    return false;
                }

                return client.Game.TakeWarp(warp1) && client.Game.Area == Area.TheWorldStoneKeepLevel3;
            }, TimeSpan.FromSeconds(4)))
            {
                Log.Warning($"Teleport failed at location: {game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
                var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.TheWorldStoneKeepLevel3, client.Game.Me.Location);
                return isValidPoint;
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Checking whether moved to area failed");
                return false;
            }

            var pathToThroneRoom = await _pathingService.GetPathToArea(client.Game, Area.ThroneOfDestruction, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToThroneRoom, MovementMode.Teleport))
            {
                Log.Error($"Teleporting to {Area.ThroneOfDestruction}  failed");
                return false;
            }

            var warp2 = client.Game.GetNearestWarp();
            if (warp2 == null || warp2.Location.Distance(client.Game.Me.Location) > 20)
            {
                Log.Warning($"Warp not close enough at location {warp2?.Location} while at location {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (warp2.Location.Distance(client.Game.Me.Location) > 5 && !await game.TeleportToLocationAsync(warp2.Location))
                {
                    Log.Debug($"Teleport to {warp2.Location} failing retrying at location: {game.Me.Location}");
                    return false;
                }

                return client.Game.TakeWarp(warp2) && client.Game.Area == Area.ThroneOfDestruction;
            }, TimeSpan.FromSeconds(4)))
            {
                Log.Warning($"Teleport failed at location: {game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
                var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ThroneOfDestruction, client.Game.Me.Location);
                return isValidPoint;
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Checking whether moved to area failed");
                return false;
            }

            var pathToThrone = await _pathingService.GetPathToObjectWithOffset(client.Game, EntityCode.BaalPortal, 27, 65, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToThrone, MovementMode.Teleport))
            {
                Log.Error($"Teleporting to {Area.ThroneOfDestruction} starting location failed");
                return false;
            }

            if (!await _townManagementService.TakeTownPortalToTown(client))
            {
                return false;
            }

            Log.Information("Got to starting location");

            return true;
        }

        private async Task FollowToLocation(Client client, Point location)
        {
            if (!client.Game.IsInGame())
            {
                return;
            }

            var (targetLocation, tokenSource) = FollowTasks[client.Game.Me.Name.ToLower()];
            if (targetLocation == null || (targetLocation.Distance(location) > 10 && client.Game.Me.Location.Distance(location) < 1000))
            {
                tokenSource?.Cancel();
                var newSource = new CancellationTokenSource();
                FollowTasks[client.Game.Me.Name.ToLower()] = (location, newSource);
                await MoveToLocation(client, location, newSource.Token);
            }
        }

        private bool ShouldFollowLeadClient(Client client)
        {
            if (ShouldFollow.TryGetValue(client.Game.Me.Name.ToLower(), out var shouldFollow))
            {
                return shouldFollow;
            };

            return false;
        }

        private void SetShouldFollowLead(Client client, bool follow)
        {
            ShouldFollow[client.Game.Me.Name.ToLower()] = follow;
        }

        async Task GetTaskForWave(Client client, BaalManager baalManager)
        {
            if (client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] < 50 && !client.Game.Me.HasSkill(Skill.Teleport))
            {
                await BasicIdleClient(client, baalManager);
                return;
            }

            ElapsedEventHandler refreshHandler = (sender, args) =>
            {
                if (client.Game.IsInGame() && client.Game.Me != null)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }
            };
            using var executeRefresh = new ExecuteAtInterval(refreshHandler, TimeSpan.FromSeconds(30));
            executeRefresh.Start();

            switch (client.Game.Me.Class)
            {
                case CharacterClass.Amazon:
                    await BasicIdleClient(client, baalManager);

                    break;
                case CharacterClass.Sorceress:
                    if (client.Game.Me.HasSkill(Skill.StaticField))
                    {
                        await StaticSorcClient(client, baalManager);
                    }
                    else
                    {
                        await BasicIdleClient(client, baalManager);
                    }
                    break;
                case CharacterClass.Necromancer:
                    await BasicIdleClient(client, baalManager);
                    break;
                case CharacterClass.Paladin:
                    await BasicFollowClient(client, baalManager);
                    break;
                case CharacterClass.Barbarian:
                    bool shouldBo = client.Game.Me.Id == BoClientPlayerId;
                    await BarbClient(client, baalManager, shouldBo);
                    break;
                case CharacterClass.Druid:
                case CharacterClass.Assassin:
                    await BasicIdleClient(client, baalManager);
                    break;
            }
        }

        async Task GetTaskForBaal(Client client, BaalManager baalManager)
        {
            ElapsedEventHandler refreshHandler = (sender, args) =>
            {
                if (client.Game.IsInGame() && client.Game.Me != null)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }
            };
            using var executeRefresh = new ExecuteAtInterval(refreshHandler, TimeSpan.FromSeconds(30));
            executeRefresh.Start();

            switch (client.Game.Me.Class)
            {
                case CharacterClass.Sorceress:
                    if (client.Game.Me.HasSkill(Skill.StaticField))
                    {
                        if (client.Game.Area != Area.TheWorldStoneChamber && !await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                        {
                            await Task.Delay(100);
                            return await _townManagementService.TakeTownPortalToArea(client, client.Game.Players.First(p => p.Id == PortalClientPlayerId), Area.TheWorldStoneChamber);
                        }, TimeSpan.FromSeconds(30)))
                        {
                            Log.Warning($"Client {client.Game.Me.Name} stopped waiting for baal portal");
                            NextGame.TrySetResult(true);
                        }

                        await StaticSorcClient(client, baalManager);
                        NextGame.TrySetResult(true);
                    }
                    else
                    {
                        await NextGame.Task;
                    }
                    break;
                case CharacterClass.Paladin:
                    if (client.Game.Area != Area.TheWorldStoneChamber && !await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        await Task.Delay(100);
                        return await _townManagementService.TakeTownPortalToArea(client, client.Game.Players.First(p => p.Id == PortalClientPlayerId), Area.TheWorldStoneChamber);
                    }, TimeSpan.FromSeconds(30)))
                    {
                        Log.Warning($"Client {client.Game.Me.Name} stopped waiting for baal portal");
                        NextGame.TrySetResult(true);
                    }
                    await BasicFollowClient(client, baalManager);
                    break;
                default:
                    await NextGame.Task;
                    break;
            }

            await Task.Delay(1000);
            await PickupItemsFromPickupList(client, baalManager, 100);

            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }
        }

        async Task StaticSorcClient(Client client, BaalManager baalManager)
        {
            Log.Information($"Starting Sorc Client {client.Game.Me.Name}");
            ElapsedEventHandler staticFieldAction = (sender, args) =>
            {
                client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
            };
            using var executeStaticField = new ExecuteAtInterval(staticFieldAction, TimeSpan.FromSeconds(0.2));

            ElapsedEventHandler novaAction = (sender, args) =>
            {
                client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
            };

            using var executeNova = new ExecuteAtInterval(novaAction, TimeSpan.FromSeconds(0.2));

            bool hasUsedPotion = false;

            var runStopWatch = new Stopwatch();
            runStopWatch.Start();

            var random = new Random();
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.5)), NextGame.Task) && client.Game.IsInGame())
            {
                if (runStopWatch.Elapsed > TimeSpan.FromSeconds(50))
                {
                    Log.Warning($"Client {client.Game.Me.Name} waiting too long going next game");
                    NextGame.TrySetResult(true);
                    break;
                }

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                var monstersNearLead = leadPlayer != null ? baalManager.GetNearbyAliveMonsters(leadPlayer.Location, 20.0, 10) : new List<AliveMonster>();
                if (leadPlayer != null && monstersNearLead.Any() && leadPlayer.Location.Distance(client.Game.Me.Location) > 20)
                {
                    Log.Information($"{client.Game.Me.Name}, lead client in danger, moving to lead client");
                    executeStaticField.Stop();
                    executeNova.Stop();

                    var teleportPath = await _pathingService.GetPathToLocation(client.Game, monstersNearLead.First().Location, MovementMode.Teleport);
                    if (teleportPath.Count > 0)
                    {
                        Log.Information($"Client {client.Game.Me.Name} teleporting at {monstersNearLead.First().Location}");
                        await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.ToList(), MovementMode.Teleport);
                    }
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    Log.Information($"Lost bo on client {client.Game.Me.Name}, moving to barb for bo");
                    executeStaticField.Stop();
                    executeNova.Stop();
                    if (leadPlayer != null)
                    {
                        if (leadPlayer.Location.Distance(client.Game.Me.Location) > 10)
                        {
                            var teleportPathLead = await _pathingService.GetPathToLocation(client.Game, leadPlayer.Location, MovementMode.Teleport);
                            await MovementHelpers.TakePathOfLocations(client.Game, teleportPathLead.ToList(), MovementMode.Teleport);
                        }
                        else
                        {
                            var randomPointNear = leadPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                            await client.Game.TeleportToLocationAsync(randomPointNear);
                        }
                    }

                    continue;
                }

                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotions(2);
                    hasUsedPotion = true;
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
                }

                var nearbyAliveBaals = baalManager.GetNearbyAliveMonsters(client, 400.0, 10);
                if (((double)client.Game.Me.Life) / client.Game.Me.MaxLife <= 0.5 && nearbyAliveBaals.Any())
                {
                    executeStaticField.Stop();
                    executeNova.Stop();
                    if (!await TeleportToNearbySafeSpot(client, baalManager, client.Game.Me.Location, 15.0))
                    {
                        Log.Information($"Teleporting to nearby safespot {client.Game.Me.Name}");
                        continue;
                    }
                }

                if (nearbyAliveBaals.Count > 0)
                {
                    runStopWatch.Restart();
                    var nearestAlive = nearbyAliveBaals.FirstOrDefault();
                    var distanceToNearest = nearestAlive.Location.Distance(client.Game.Me.Location);

                    if (client.Game.Me.Location.Distance(nearestAlive.Location) > 5
                        && (!ClassHelpers.CanStaticEntity(client, nearestAlive.LifePercentage)
                        || client.Game.WorldObjects.TryGetValue((nearestAlive.Id, EntityType.NPC), out var monster) && monster.Effects.Contains(EntityEffect.Cold)))
                    {
                        executeStaticField.Stop();
                        executeNova.Stop();
                        Log.Information($"teleporting nearby due to low life frozen monsters with {client.Game.Me.Name} with distance {client.Game.Me.Location.Distance(nearestAlive.Location)}");
                        await client.Game.TeleportToLocationAsync(nearestAlive.Location);
                    }

                    if (await _attackService.IsInLineOfSight(client, nearestAlive.Location)
                        && (nearestAlive.LifePercentage < 30 || !ClassHelpers.CanStaticEntity(client, nearestAlive.LifePercentage))
                        && distanceToNearest < 10)
                    {
                        executeStaticField.Stop();

                        if (client.Game.Me.HasSkill(Skill.Nova))
                        {
                            if (!executeNova.IsRunning())
                            {
                                Log.Information($"Attacking with Nova {client.Game.Me.Name}");
                            }
                            client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                            executeNova.Start();
                        }
                        else if (client.Game.Me.HasSkill(Skill.FrozenOrb))
                        {
                            client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, nearestAlive.Location);
                        }

                    }
                    else if (await _attackService.IsInLineOfSight(client, nearestAlive.Location)
                        && distanceToNearest < 20)
                    {
                        if (client.Game.Me.HasSkill(Skill.StaticField) && ClassHelpers.CanStaticEntity(client, nearestAlive.LifePercentage))
                        {
                            if (!executeStaticField.IsRunning())
                            {
                                Log.Information($"Attacking with Static {client.Game.Me.Name}");
                            }
                            executeNova.Stop();
                            client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                            executeStaticField.Start();
                        }
                        else
                        {
                            if (client.Game.Me.HasSkill(Skill.Nova) && distanceToNearest < 10)
                            {
                                client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                                executeNova.Start();
                            }
                        }
                    }
                    else
                    {
                        if (client.Game.Me.Location.Distance(nearestAlive.Location) > 15 || !await _attackService.IsInLineOfSight(client, client.Game.Me.Location, nearestAlive.Location))
                        {
                            Log.Information($"teleporting nearby with {client.Game.Me.Name} with distance {client.Game.Me.Location.Distance(nearestAlive.Location)}");
                            var destination = nearestAlive.Location.GetPointBeforePointInSameDirection(client.Game.Me.Location, 10);
                            if (!await _attackService.IsInLineOfSight(client, destination, nearestAlive.Location))
                            {
                                Log.Information($"Not in sight, so teleporting directly {client.Game.Me.Name}");
                                destination = nearestAlive.Location;
                            }
                            await client.Game.TeleportToLocationAsync(destination);
                        }

                        if (!executeNova.IsRunning() && !executeStaticField.IsRunning())
                        {
                            Log.Information($"Not attacking at all {client.Game.Me.Name}");
                        }
                    }

                    continue;
                }

                executeStaticField.Stop();
                executeNova.Stop();
                await PickupItemsFromPickupList(client, baalManager, 100);
                await PickupNearbyPotionsIfNeeded(client, baalManager, 30);

                var baalThrone = client.Game.GetNPCsByCode(NPCCode.BaalThrone).FirstOrDefault();
                var baalPortal = client.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
                if (baalThrone == null)
                {
                    Log.Information($"Waves done, moving on {client.Game.Me.Name}");
                    break;
                }
                else if (baalPortal != null && client.Game.Me.Location.Distance(baalPortal.Location.Add(0, 30)) > 5)
                {
                    executeStaticField.Stop();
                    executeNova.Stop();
                    var pathToThrone = await _pathingService.GetPathToObjectWithOffset(client.Game, EntityCode.BaalPortal, 0, 30, MovementMode.Teleport);
                    if (pathToThrone.Count > 0)
                    {
                        Log.Information($"Client {client.Game.Me.Name} teleporting nearby {EntityCode.BaalPortal}");
                        await MovementHelpers.TakePathOfLocations(client.Game, pathToThrone.ToList(), MovementMode.Teleport);
                    }
                }
                else
                {
                    executeStaticField.Start();
                    executeNova.Stop();
                }
            }

            Log.Information($"Finished with Sorc Client {client.Game.Me.Name}");
            executeStaticField.Stop();
            executeNova.Stop();
        }

        private async Task<bool> TeleportToNearbySafeSpot(Client client, BaalManager baalManager, Point toLocation, double minDistance = 0, double maxDistance = 30)
        {
            var nearbyMonsters = baalManager.GetNearbyAliveMonsters(toLocation, 30.0, 100).Select(p => p.Location).ToList();
            return await _attackService.MoveToNearbySafeSpot(client, nearbyMonsters, toLocation, MovementMode.Teleport, minDistance, maxDistance);
        }

        async Task BasicIdleClient(Client client, BaalManager baalManager)
        {
            Log.Information($"Starting Basic idle Client {client.Game.Me.Name}");
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), NextGame.Task) && client.Game.IsInGame())
            {
            }

            Log.Information($"Stopped Idle Client {client.Game.Me.Name}");
        }

        async Task BasicFollowClient(Client client, BaalManager baalManager)
        {
            SetShouldFollowLead(client, true);

            ElapsedEventHandler staticFieldAction = (sender, args) =>
            {
                client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
            };
            using var executeStaticField = new ExecuteAtInterval(staticFieldAction, TimeSpan.FromSeconds(0.2));
            bool flipflop = true;
            client.Game.ChangeSkill(Skill.Conviction, Hand.Right);
            var timer = new Stopwatch();
            timer.Start();
            bool hasUsedPotion = false;
            var random = new Random();
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame())
            {
                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotions(2);
                    hasUsedPotion = true;
                }

                if (client.Game.Me.HasSkill(Skill.FrozenOrb))
                {
                    executeStaticField.Stop();
                    var nearest = baalManager.GetNearbyAliveMonsters(client, 20.0, 1).FirstOrDefault();
                    if (nearest != null)
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, nearest.Location);
                    }
                }

                if (client.Game.Me.HasSkill(Skill.StaticField))
                {
                    var nearest = baalManager.GetNearbyAliveMonsters(client, 20.0, 1).FirstOrDefault();
                    if (nearest != null && nearest.LifePercentage > 60)
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                        executeStaticField.Start();
                    }
                    else
                    {
                        executeStaticField.Stop();
                    }
                }

                var nearbyMonsters = baalManager.GetNearbyAliveMonsters(client, 20, 1);
                if (client.Game.Me.HasSkill(Skill.Conviction)
                    && !nearbyMonsters.Any(m => client.Game.WorldObjects.TryGetValue((m.Id, EntityType.NPC), out var monster) && monster.Effects.Contains(EntityEffect.Conviction)))
                {
                    client.Game.ChangeSkill(Skill.Conviction, Hand.Right);
                }
                else if (timer.Elapsed > TimeSpan.FromSeconds(4) && client.Game.Me.HasSkill(Skill.Conviction) && client.Game.Me.HasSkill(Skill.Fanaticism))
                {
                    client.Game.ChangeSkill(flipflop ? Skill.Fanaticism : Skill.Conviction, Hand.Right);
                    timer.Reset();
                    timer.Start();
                    flipflop = !flipflop;
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.Holyshield) && client.Game.Me.HasSkill(Skill.HolyShield))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
                    client.Game.ChangeSkill(Skill.Conviction, Hand.Right);
                }

                if (timer.Elapsed > TimeSpan.FromSeconds(5) && client.Game.Me.HasSkill(Skill.Teleport))
                {
                    var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                    if (leadPlayer != null)
                    {
                        var randomPointNear = leadPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                        await client.Game.TeleportToLocationAsync(randomPointNear);
                        timer.Restart();
                    }
                }

                if (!baalManager.GetNearbyAliveMonsters(client, 200, 1).Any())
                {
                    await PickupItemsFromPickupList(client, baalManager, 25);
                    await PickupNearbyPotionsIfNeeded(client, baalManager, 15);
                    SetShouldFollowLead(client, true);
                    var baalThrone = client.Game.GetNPCsByCode(NPCCode.BaalThrone).FirstOrDefault();
                    if (baalThrone == null)
                    {
                        SetShouldFollowLead(client, false);
                        break;
                    }
                }
            }
        }

        private async Task PickupNearbyPotionsIfNeeded(Client client, BaalManager baalManager, int distance)
        {
            var missingHealthPotions = client.Game.Belt.Height * 2 - client.Game.Belt.GetHealthPotionsInSlots(new List<int>() { 0, 1 }).Count;
            var missingManaPotions = client.Game.Belt.Height * 2 - client.Game.Belt.GetManaPotionsInSlots(new List<int>() { 2, 3 }).Count;
            var missingRevPotions = Math.Max(6 - client.Game.Inventory.Items.Count(i => i.Name == ItemName.FullRejuvenationPotion || i.Name == ItemName.RejuvenationPotion), 0);
            //Log.Information($"Client {client.Game.Me.Name} missing {missingHealthPotions} healthpotions and missing {missingManaPotions} mana");
            var pickitList = baalManager.GetNearbyPotions(client, new HashSet<ItemName> { ItemName.SuperHealingPotion }, (int)missingHealthPotions, distance);
            pickitList.AddRange(baalManager.GetNearbyPotions(client, new HashSet<ItemName> { ItemName.SuperManaPotion }, (int)missingManaPotions, distance));
            pickitList.AddRange(baalManager.GetNearbyPotions(client, new HashSet<ItemName> { ItemName.RejuvenationPotion, ItemName.FullRejuvenationPotion }, missingRevPotions, distance));
            foreach (var item in pickitList)
            {
                if (baalManager.GetNearbyAliveMonsters(client, 10, 1).Any())
                {
                    Log.Information($"Client {client.Game.Me.Name} not picking up {item.Name} due to nearby monsters");
                    continue;
                }
                Log.Information($"Client {client.Game.Me.Name} picking up {item.Name}");
                SetShouldFollowLead(client, false);
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
                        baalManager.PutPotionOnPickitList(client, item);
                    }
                }
            }

            //Log.Information($"Client {client.Game.Me.Name} got {client.Game.Belt.NumOfHealthPotions()} healthpotions and {client.Game.Belt.NumOfManaPotions()} mana");
        }

        private async Task PickupItemsFromPickupList(Client client, BaalManager baalManager, double distance)
        {
            var pickitList = baalManager.GetPickitList(client, distance);
            foreach (var item in pickitList)
            {
                if (item.Ground)
                {
                    SetShouldFollowLead(client, false);
                    if (client.Game.Inventory.FindFreeSpace(item) != null)
                    {
                        Log.Information($"Client {client.Game.Me.Name} picking up {item.Amount} {item.Name}");
                        await MoveToLocation(client, item.Location);
                        if (GeneralHelpers.TryWithTimeout((retryCount) =>
                        {
                            client.Game.MoveTo(item.Location);
                            client.Game.PickupItem(item);
                            return GeneralHelpers.TryWithTimeout((retryCount) =>
                            {
                                Thread.Sleep(50);
                                if (!item.IsGold && client.Game.Inventory.FindItemById(item.Id) == null)
                                {
                                    return false;
                                }

                                return true;
                            }, TimeSpan.FromSeconds(0.3));
                        }, TimeSpan.FromSeconds(3)))
                        {
                            InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                        }
                        else
                        {
                            Log.Warning($"Client {client.Game.Me.Name} failed picking up {item.Amount} {item.Name}");
                            baalManager.PutItemOnPickitList(client, item);
                        }
                    }
                    else
                    {
                        Log.Warning($"Client {client.Game.Me.Name} no space for {item.Amount} {item.Name}");
                        baalManager.PutItemOnPickitList(client, item);
                    }
                }
            }
        }

        async Task BarbClient(Client client, BaalManager baalManager, bool shouldBo)
        {
            Log.Information($"Starting BoBarb Client {client.Game.Me.Name}");
            bool hasUsedPotion = false;
            if (shouldBo)
            {
                await ClassHelpers.CastAllShouts(client);
            }

            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame())
            {
                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotions(2);
                    hasUsedPotion = true;
                }

                if (shouldBo)
                {
                    await ClassHelpers.CastAllShouts(client);
                }
                else
                {
                    var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                    if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 25)
                    {
                        continue;
                    }
                }

                var nearbyMonsters = baalManager.GetNearbyAliveMonsters(client, 20, 1);
                if (!nearbyMonsters.Any())
                {
                    await PickupItemsFromPickupList(client, baalManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, baalManager, 15);
                }
                else if (client.Game.Me.HasSkill(Skill.Whirlwind))
                {
                    var nearbyMonster = nearbyMonsters.FirstOrDefault();
                    if (nearbyMonster != null && (nearbyMonster.NPCCode == NPCCode.DrehyaTemple || nearbyMonster.Location.Distance(client.Game.Me.Location) < 5))
                    {
                        var wwDirection = client.Game.Me.Location.GetPointPastPointInSameDirection(nearbyMonster.Location, 6);
                        if (client.Game.Me.Location.Equals(nearbyMonster.Location))
                        {
                            wwDirection = new Point((ushort)(client.Game.Me.Location.X + 6), client.Game.Me.Location.Y);
                        }

                        var wwDistance = client.Game.Me.Location.Distance(wwDirection);
                        //Log.Information($"player loc: {game.Me.Location}, nearest: {nearest.Location} ww destination: {wwDirection}  ");
                        client.Game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                        Thread.Sleep((int)((wwDistance * 50 + 300)));
                    }
                }
            }

            Log.Information($"Stopped Barb Client {client.Game.Me.Name}");
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

        private async Task<bool> LeaveGameAndRejoinMCPWithRetry(Client client, AccountCharacter account)
        {
            if (!client.Chat.IsConnected())
            {
                if (!await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, account, 10))
                {
                    return false;
                }
            }

            if (client.Game.IsInGame())
            {
                Log.Information($"Leaving game with {client.LoggedInUserName()}");
                await client.Game.LeaveGame();
            }

            if (!client.RejoinMCP())
            {
                Log.Warning($"Disconnecting client {account.Username} since reconnecting to MCP failed, reconnecting to realm");
                return await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, account, 10);
            }

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

        void NewPlayerJoinGame(Client client, PlayerInGamePacket playerInGamePacket)
        {
            var relevantPlayer = client.Game.Players.Where(p => p.Id == playerInGamePacket.Id).FirstOrDefault();
            client.Game.InvitePlayer(relevantPlayer);
        }

        void PlayerInGame(string characterName)
        {
            if (PlayersInGame.TryGetValue(characterName.ToLower(), out var oldValue))
            {
                PlayersInGame.TryUpdate(characterName.ToLower(), new ManualResetEvent(true), oldValue);
            }
        }

        void TownPortalState(TownPortalStatePacket townPortalStatePacket)
        {
            if (townPortalStatePacket.Area == Area.ThroneOfDestruction)
            {
                BaalPortalOpen.TrySetResult(true);
            }
        }
    }
}
