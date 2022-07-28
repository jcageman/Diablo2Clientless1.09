using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
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

namespace ConsoleBot.Bots.Types.Cows
{
    public class CowBot : IBotInstance
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly ITownManagementService _townManagementService;
        private readonly IAttackService _attackService;
        private readonly IMapApiService _mapApiService;
        private readonly IMuleService _muleService;
        private readonly CowConfiguration _cowconfig;
        private TaskCompletionSource<bool> NextGame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool ShouldStop = false;
        private TaskCompletionSource<bool> CowPortalOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConcurrentDictionary<string, ManualResetEvent> PlayersInGame = new ConcurrentDictionary<string, ManualResetEvent>();
        private uint? BoClientPlayerId;
        private ConcurrentDictionary<string, bool> ShouldFollow = new ConcurrentDictionary<string, bool>();
        private ConcurrentDictionary<string, (Point, CancellationTokenSource)> FollowTasks = new ConcurrentDictionary<string, (Point, CancellationTokenSource)>();
        private HashSet<string> ClientsNeedingMule = new HashSet<string>();
        public CowBot(IOptions<BotConfiguration> config, IOptions<CowConfiguration> cowconfig,
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
            _cowconfig = cowconfig.Value;
        }

        public string GetName()
        {
            return "cows";
        }

        public async Task Run()
        {
            _cowconfig.Validate();
            var clients = new List<Client>();
            foreach (var account in _cowconfig.Accounts)
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
                    if (entityMovePacket.UnitType == EntityType.Player && entityMovePacket.UnitId == BoClientPlayerId && ShouldFollowLeadClient(client))
                    {
                        await FollowToLocation(client, entityMovePacket.MoveToLocation);
                    }
                });

                client.OnReceivedPacketEvent(InComingPacket.ReassignPlayer, async (packet) =>
                {
                    var reassignPlayerPacket = new ReassignPlayerPacket(packet);
                    if (reassignPlayerPacket.UnitType == EntityType.Player && reassignPlayerPacket.UnitId == BoClientPlayerId && ShouldFollowLeadClient(client))
                    {
                        await FollowToLocation(client, reassignPlayerPacket.Location);
                    }
                });
                client.OnReceivedPacketEvent(InComingPacket.PartyAutomapInfo, async (packet) =>
                {
                    var partyAutomapInfoPacket = new PartyAutomapInfoPacket(packet);
                    if (partyAutomapInfoPacket.Id == BoClientPlayerId && ShouldFollowLeadClient(client))
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

                if (chatPacket.Message == "stop")
                {
                    ShouldStop = true;
                    NextGame.TrySetResult(true);
                    Log.Information($"Stopping run due to receiving stop message");
                }
            });

            int gameCount = 1;
            while (!ShouldStop)
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

                CowPortalOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                NextGame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                BoClientPlayerId = null;

                Log.Information($"Joining next game");

                try
                {
                    var leaveAndRejoinTasks = clients.Select(async (client, index) =>
                    {
                        var account = _cowconfig.Accounts[index];
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

                    var result = await RealmConnectHelpers.CreateGameWithRetry(gameCount, firstFiller, _config, _cowconfig.Accounts.First());
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
                        var account = _cowconfig.Accounts[i];
                        var client = clients[i];
                        var numberOfSecondsToWait = (i > 3 ? 15 : 0);
                        Thread.Sleep(TimeSpan.FromSeconds(numberOfSecondsToWait));
                        if (firstFiller != client && !await RealmConnectHelpers.JoinGameWithRetry(gameCount, client, _config, account))
                        {
                            Log.Warning($"Client {client.LoggedInUserName()} failed to join game, retrying new game");
                            anyTownFailures = true;
                            break;
                        }

                        townTasks.Add(PrepareForCowsTasks(client));
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

                Log.Information($"Waiting for cow portal to open");
                await CowPortalOpen.Task;
                Log.Information($"Cow portal open, moving to cow level");

                var killingClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && c.Game.Me.Skills.GetValueOrDefault(Skill.Nova) >= 20).ToList();
                Log.Information($"Selected {string.Join(",", killingClients.Select(c => c.Game.Me.Name))} for cow manager");
                var listeningClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && !killingClients.Contains(c)).ToList();
                listeningClients.Add(boClient);
                var cowManager = new CowManager(killingClients, listeningClients, _mapApiService);

                try
                {
                    var clientTasks = new List<Task<bool>>();
                    foreach (var client in clients)
                    {
                        clientTasks.Add(RunCows(client, cowManager));
                    }

                    await Task.WhenAll(clientTasks);
                }
                catch (Exception e)
                {
                    Log.Error($"Failed one or more tasks with exception {e}");
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
                Log.Information($"Going to next game");
                gameCount++;
            }

            if (ShouldStop)
            {
                await LeaveGameAndDisconnectWithAllClients(clients);
            }
        }

        private async Task<bool> PrepareForCowsTasks(Client client)
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

            var initialLocation = client.Game.Me.Location;

            var townManagementOptions = new TownManagementOptions()
            {
                Act = Act.Act1,
            };

            var isPortalCharacter = _cowconfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.InvariantCultureIgnoreCase);
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
                if (!await CreateCowLevel(client))
                {
                    return false;
                }
            }
            else
            {
                var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
                var pathBack = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.RogueEncampment, client.Game.Me.Location, initialLocation, movementMode);
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
            await Task.WhenAll(clients.Select(async c =>
            {
                if (c.Game.IsInGame())
                {
                    await c.Game.LeaveGame();
                }
                c.Disconnect();
            }).ToList());
        }

        private async Task<bool> RunCows(Client client, CowManager cowManager)
        {
            var isPortalCharacter = _cowconfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.InvariantCultureIgnoreCase);
            if (isPortalCharacter)
            {
                if (!await ArrangeBoAtCata2(client))
                {
                    NextGame.TrySetResult(true);
                    return false;
                }

                if (!await ArrangeStartingPosition(client, cowManager))
                {
                    NextGame.TrySetResult(true);
                    return false;
                }

                Log.Information($"Client {client.Game.Me.Name} arranged start position");
            }
            else
            {
                var portalPlayer = client.Game.Players.Single(p => _cowconfig.PortalCharacterName.Equals(p.Name, StringComparison.InvariantCultureIgnoreCase));
                if (!await GetBoAtCata2(client, portalPlayer))
                {
                    NextGame.TrySetResult(true);
                    return false;
                }

                if (client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] <= 80)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }

                if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.2));
                    return await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.CowLevel);
                }, TimeSpan.FromSeconds(30)))
                {
                    Log.Warning($"Client {client.Game.Me.Name} stopped waiting for cow level to start");
                    NextGame.TrySetResult(true);
                    return false;
                }

                Log.Information($"Client {client.Game.Me.Name} ín cow level");
            }

            await GetTaskForClient(client, cowManager);
            return true;
        }

        private async Task<bool> GetBoAtCata2(Client client, Player portalPlayer)
        {
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                return await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.CatacombsLevel2);
            }, TimeSpan.FromSeconds(20)))
            {
                Log.Warning($"Client {client.Game.Me.Name} taking portal to cata2 failed");
                return false;
            }

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
                    return !ClassHelpers.AnyPlayerIsMissingShouts(client);
                }, TimeSpan.FromSeconds(15));
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                return await _townManagementService.TakeTownPortalToTown(client);
            }, TimeSpan.FromSeconds(20)))
            {
                Log.Warning($"Client {client.Game.Me.Name} taking portal to town failed");
                return false;
            }

            return true;
        }

        private async Task<bool> ArrangeStartingPosition(Client client, CowManager cowManager)
        {
            if (!await MoveToCowLevel(client))
            {
                Log.Information($"{client.Game.Me.Name}, couldn't move to the cow level, next game");
                NextGame.TrySetResult(true);
                return false;
            }

            var startLocations = await cowManager.GetPossibleStartingLocations(client.Game);
            foreach (var location in startLocations)
            {
                Log.Information($"Client {client.Game.Me.Name} starting location {location}");

                var teleportPath = await _pathingService.GetPathToLocation(client.Game, location, MovementMode.Teleport);
                if (teleportPath.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting to starting location {location}");
                    await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.ToList(), MovementMode.Teleport);
                    var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 35.0, 100);
                    if (!nearbyAliveCows.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)))
                    {
                        if (!await _townManagementService.CreateTownPortal(client))
                        {
                            continue;
                        }
                        return true;
                    }
                }
            }

            return false;
        }

        private async Task<bool> ArrangeBoAtCata2(Client client)
        {
            Log.Information($"Client {client.Game.Me.Name} taking waypoint to {Waypoint.CatacombsLevel2}");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.CatacombsLevel2))
            {
                return false;
            }

            Log.Information($"Client {client.Game.Me.Name} creating town portal at {Waypoint.CatacombsLevel2}");
            if (!await _townManagementService.CreateTownPortal(client))
            {
                return false;
            }

            Log.Information($"Client {client.Game.Me.Name} waiting for bo on all players at {Waypoint.CatacombsLevel2}");
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
                return !ClassHelpers.AnyPlayerIsMissingShouts(client);
            }, TimeSpan.FromSeconds(15));

            Log.Information($"Client {client.Game.Me.Name} waiting for bo done at {Waypoint.CatacombsLevel2}");

            if (!await _townManagementService.TakeTownPortalToTown(client))
            {
                return false;
            }
            Log.Information($"Client {client.Game.Me.Name} back to town");
            return true;
        }

        private async Task<bool> CreateCowLevel(Client client)
        {
            var game = client.Game;
            var movementMode = game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
            if (!game.Inventory.Items.Any(i => i.Name == ItemName.WirtsLeg))
            {
                if (!await GetWirtsLeg(client))
                {
                    return false;
                }
            }

            var wirtsleg = game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg);
            if (wirtsleg == null)
            {
                Log.Error($"Wirts leg not found");
                return false;
            }

            var tomesOfTp = game.Inventory.Items.Where(i => i.Name == ItemName.TomeOfTownPortal);
            if (tomesOfTp.Count() < 2)
            {
                Log.Error($"Not enough tomes of town portal found");
                return false;
            }

            var lowestQuantity = tomesOfTp.OrderBy(i => i.Amount).First();
            var droppedItems = new List<Item>();
            if (game.Cube.Items.Any())
            {
                if (!InventoryHelpers.MoveCubeItemsToInventory(game))
                {
                    Log.Warning($"Couldn't move all items out of cube, dropping cube items for now");
                    foreach (var item in game.Cube.Items)
                    {
                        if (InventoryHelpers.DropItemFromCube(game, item) != MoveItemResult.Succes)
                        {
                            Log.Error($"Failed to drop item out of cube");
                            return false;
                        }
                        droppedItems.Add(item);
                    }
                }
            }

            var freeSpaceTownPortal = game.Cube.FindFreeSpace(lowestQuantity);
            if (game.Cube.Items.Any() || freeSpaceTownPortal == null)
            {
                Log.Error($"Something wrong with cube for transmute town portal");
                return false;
            }

            if (InventoryHelpers.PutInventoryItemInCube(game, lowestQuantity, freeSpaceTownPortal) != Enums.MoveItemResult.Succes)
            {
                Log.Error($"Moving tome of town portal to cube failed");
                return false;
            }

            var freeSpaceLeg = game.Cube.FindFreeSpace(wirtsleg);
            if (freeSpaceLeg == null)
            {
                Log.Error($"No space found for leg, which is weird");
                return false;
            }

            if (InventoryHelpers.PutInventoryItemInCube(game, wirtsleg, freeSpaceLeg) != Enums.MoveItemResult.Succes)
            {
                Log.Error($"Moving wirts leg to cube failed");
                return false;
            }

            if (!InventoryHelpers.TransmuteItemsInCube(game, false))
            {
                Log.Error($"Transmuting leg and tome failed");
                return false;
            }

            foreach (var droppedItem in droppedItems)
            {
                if (!await GeneralHelpers.TryWithTimeout((async retryCount =>
                {
                    await client.Game.MoveToAsync(droppedItem);
                    client.Game.PickupItem(droppedItem);
                    if (!GeneralHelpers.TryWithTimeout((retryCount =>
                    {
                        if (client.Game.Inventory.FindItemById(droppedItem.Id) == null)
                        {
                            return false;
                        }

                        return true;
                    }), TimeSpan.FromSeconds(0.5)))
                    {
                        return false;
                    }

                    return true;
                }), TimeSpan.FromSeconds(3)))
                {
                    Log.Warning($"Picking up item {droppedItem.GetFullDescription()} at location {droppedItem.Location} from location {client.Game.Me.Location} failed");
                }
            }

            return true;
        }

        private async Task<bool> GetWirtsLeg(Client client)
        {
            var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTownWayPoint, MovementMode.Teleport))
            {
                Log.Information($"Teleporting to {client.Game.Act} waypoint failed");
                return false;
            }

            var townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).Single();
            Log.Information("Taking waypoint to StonyFields");
            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                client.Game.TakeWaypoint(townWaypoint, Waypoint.StonyFields);
                return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == Waypoint.StonyFields.ToArea(), TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Error($"Taking waypoint to to {Waypoint.StonyFields} waypoint failed");
                return false;
            }
            var pathToPortal = await _pathingService.GetPathToObject(client.Game, EntityCode.TristamPortal, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPortal, MovementMode.Teleport))
            {
                Log.Error($"Teleporting to {EntityCode.TristamPortal}  failed");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (client.Game.Area == Area.Tristram)
                {
                    return true;
                }

                var tristamPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).FirstOrDefault(t => t.TownPortalArea == Area.Tristram);
                if (tristamPortal == null)
                {
                    return false;
                }
                await client.Game.MoveToAsync(tristamPortal);

                client.Game.InteractWithEntity(tristamPortal);
                if (!GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    return client.Game.Area == Area.Tristram;
                }, TimeSpan.FromSeconds(0.2)))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                    return false;
                }

                return true;

            }, TimeSpan.FromSeconds(15)))
            {
                Log.Error($"Moving to Tristam failed");
                return false;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);

            Log.Information("Arrived in Tristam, teleporting to leg");

            var pathToLeg = await _pathingService.GetPathToObject(client.Game, EntityCode.WirtsBody, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToLeg, MovementMode.Teleport))
            {
                Log.Error($"Teleporting to leg failed");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (retryCount % 4 == 0)
                {
                    if (client.Game.Me.HasSkill(Skill.Nova))
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                    }
                    else if (client.Game.Me.HasSkill(Skill.FrozenOrb))
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, client.Game.Me.Location);
                    }
                }
                var wirtsBody = client.Game.GetEntityByCode(EntityCode.WirtsBody).FirstOrDefault();
                if (wirtsBody == null)
                {
                    return false;
                }

                var wirtsLegItem = client.Game.Items.Values.FirstOrDefault(i => i.Name == ItemName.WirtsLeg && i.Ground);
                if (client.Game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg) != null)
                {
                    return true;
                }



                if (wirtsLegItem == null)
                {
                    client.Game.MoveTo(wirtsBody);
                    await Task.Delay(100);
                    client.Game.InteractWithEntity(wirtsBody);
                    return false;
                }


                if (client.Game.Inventory.FindFreeSpace(wirtsLegItem) == null)
                {
                    InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                }

                client.Game.PickupItem(wirtsLegItem);


                return client.Game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg) != null;
            }, TimeSpan.FromSeconds(15)))
            {
                Log.Error($"Getting leg failed, while it's at location: {client.Game.Items.Values.FirstOrDefault(i => i.Name == ItemName.WirtsLeg)?.Location} and i'm at location {client.Game.Me.Location}");
                return false;
            }

            if (!await _townManagementService.TakeTownPortalToTown(client))
            {
                return false;
            }

            Log.Information("Got leg and in town again");
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

        async Task GetTaskForClient(Client client, CowManager cowManager)
        {
            if (client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] < 50 && !client.Game.Me.HasSkill(Skill.Teleport))
            {
                await BasicIdleClient(client, cowManager);
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
                    await BasicFollowClient(client, cowManager);

                    break;
                case CharacterClass.Sorceress:
                    if (client.Game.Me.HasSkill(Skill.StaticField) && client.Game.Me.Skills.GetValueOrDefault(Skill.Nova) >= 20)
                    {
                        await StaticSorcClient(client, cowManager);
                    }
                    else
                    {
                        await BasicFollowClient(client, cowManager);
                    }
                    break;
                case CharacterClass.Necromancer:
                    await BasicFollowClient(client, cowManager);
                    break;
                case CharacterClass.Paladin:
                    await BasicFollowClient(client, cowManager);
                    break;
                case CharacterClass.Barbarian:
                    bool shouldBo = client.Game.Me.Id == BoClientPlayerId;
                    await BarbClient(client, cowManager, shouldBo);
                    break;
                case CharacterClass.Druid:
                case CharacterClass.Assassin:
                    await BasicFollowClient(client, cowManager);
                    break;
            }
        }

        async Task StaticSorcClient(Client client, CowManager cowManager)
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

            var clusterStopWatch = new Stopwatch();

            bool hasUsedPotion = false;

            var random = new Random();

            Point currentCluster = null;
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.5)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                var cowsNearLead = leadPlayer != null ? cowManager.GetNearbyAliveMonsters(leadPlayer.Location, 20.0, 10) : new List<AliveMonster>();
                if (leadPlayer != null && leadPlayer.Location != currentCluster && cowsNearLead.Any() && leadPlayer.Location.Distance(client.Game.Me.Location) > 20)
                {
                    if (cowsNearLead.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)))
                    {
                        Log.Information($"{client.Game.Me.Name}, lightning enhanced cow nearby, next game");
                        NextGame.SetResult(true);
                    }
                    Log.Information($"{client.Game.Me.Name}, lead client in danger, moving to lead client");
                    if (currentCluster != null)
                    {
                        cowManager.GiveUpCluster(currentCluster);
                    }

                    currentCluster = cowsNearLead.FirstOrDefault().Location;
                    executeStaticField.Stop();
                    executeNova.Stop();

                    var teleportPath = await _pathingService.GetPathToLocation(client.Game, currentCluster, MovementMode.Teleport);
                    if (teleportPath.Count > 0)
                    {
                        Log.Information($"Client {client.Game.Me.Name} teleporting nearby cluster {currentCluster}");
                        await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.ToList(), MovementMode.Teleport);
                    }
                }

                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders))
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

                    if (clusterStopWatch.Elapsed > TimeSpan.FromSeconds(60))
                    {
                        Log.Information($"Lost bo on client {client.Game.Me.Name} and receiving new bo took too long");
                        break;
                    }

                    continue;
                }

                if (!hasUsedPotion && client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotions();
                    client.Game.UseHealthPotions();
                    hasUsedPotion = true;
                }

                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                }

                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
                }

                var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 30.0, 10);
                if (((double)client.Game.Me.Life) / client.Game.Me.MaxLife <= 0.5 && nearbyAliveCows.Any())
                {
                    executeStaticField.Stop();
                    executeNova.Stop();
                    if (!await TeleportToNearbySafeSpot(client, cowManager, client.Game.Me.Location, 15.0))
                    {
                        Log.Information($"Teleporting to nearby safespot {client.Game.Me.Name}");
                        continue;
                    }
                }

                var lightningEnhancedCows = nearbyAliveCows.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted));
                if (clusterStopWatch.Elapsed > TimeSpan.FromSeconds(40) && currentCluster != null && leadPlayer?.Location != currentCluster)
                {
                    Log.Information($"Taking too much time on cluster, skipping current cluster and moving to next cluster {client.Game.Me.Name}");
                    clusterStopWatch.Restart();
                }
                else if (lightningEnhancedCows && leadPlayer?.Location != currentCluster)
                {
                    Log.Information($"Lightning enhanced cow nearby, giving up current cluster and moving to next cluster {client.Game.Me.Name}");
                }
                else if (nearbyAliveCows.Count > 4)
                {
                    var nearestAlive = nearbyAliveCows.FirstOrDefault();
                    var distanceToNearest = nearestAlive.Location.Distance(client.Game.Me.Location);

                    if (client.Game.Me.Location.Distance(nearestAlive.Location) > 5
                        && (!ClassHelpers.CanStaticEntity(client, nearestAlive.LifePercentage)
                        || client.Game.WorldObjects.TryGetValue((nearestAlive.Id, EntityType.NPC), out var cow) && cow.Effects.Contains(EntityEffect.Cold)))
                    {
                        executeStaticField.Stop();
                        executeNova.Stop();
                        Log.Information($"teleporting nearby due to low life frozen cows with {client.Game.Me.Name} with distance {client.Game.Me.Location.Distance(nearestAlive.Location)}");
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
                await PickupItemsFromPickupList(client, cowManager, 100);
                await PickupNearbyPotionsIfNeeded(client, cowManager, 30);

                SetShouldFollowLead(client, false);

                currentCluster = cowManager.GetNextCluster(client, currentCluster);
                if (currentCluster == null)
                {
                    Log.Information($"No cluster found for {client.Game.Me.Name}");
                    continue;
                }

                clusterStopWatch.Restart();
                Log.Information($"Client {client.Game.Me.Name} obtained next cluster at {currentCluster}");

                executeStaticField.Stop();
                executeNova.Stop();

                var clusterPath = await _pathingService.GetPathToLocation(client.Game, currentCluster, MovementMode.Teleport);
                if (clusterPath.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting nearby cluster {currentCluster}");
                    if (clusterPath.Count > 1)
                    {
                        await MovementHelpers.TakePathOfLocations(client.Game, clusterPath.SkipLast(1).ToList(), MovementMode.Teleport);
                        await TeleportToNearbySafeSpot(client, cowManager, clusterPath.Last(), 15.0);
                    }
                }
            }

            Log.Information($"Stopped Sorc Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
            executeStaticField.Stop();
            executeNova.Stop();
            NextGame.TrySetResult(true);
        }

        private async Task<bool> TeleportToNearbySafeSpot(Client client, CowManager cowManager, Point toLocation, double minDistance = 0, double maxDistance = 30)
        {
            var nearbyMonsters = cowManager.GetNearbyAliveMonsters(toLocation, 30.0, 100).Select(p => p.Location).ToList();
            return await _attackService.MoveToNearbySafeSpot(client, nearbyMonsters, toLocation, MovementMode.Teleport, minDistance, maxDistance);
        }

        async Task BasicIdleClient(Client client, CowManager cowManager)
        {
            Log.Information($"Starting Basic idle Client {client.Game.Me.Name}");
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
            }

            Log.Information($"Stopped Idle Client {client.Game.Me.Name}");
        }

        async Task BasicFollowClient(Client client, CowManager cowManager)
        {
            SetShouldFollowLead(client, true);

            ElapsedEventHandler staticFieldAction = (sender, args) =>
            {
                client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
            };
            using var executeStaticField = new ExecuteAtInterval(staticFieldAction, TimeSpan.FromSeconds(0.2));

            var timer = new Stopwatch();
            timer.Start();
            bool hasUsedPotion = false;
            var random = new Random();
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if (!hasUsedPotion && client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotions();
                    client.Game.UseHealthPotions();
                    hasUsedPotion = true;
                }

                if (client.Game.Me.HasSkill(Skill.FrozenOrb))
                {
                    executeStaticField.Stop();
                    var nearest = cowManager.GetNearbyAliveMonsters(client, 20.0, 1).FirstOrDefault();
                    if (nearest != null)
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, nearest.Location);
                    }
                }

                if (client.Game.Me.HasSkill(Skill.StaticField))
                {
                    var nearest = cowManager.GetNearbyAliveMonsters(client, 20.0, 1).FirstOrDefault();
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

                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                }

                if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
                }

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 10)
                {
                    await FollowToLocation(client, leadPlayer.Location);
                }

                if (timer.Elapsed > TimeSpan.FromSeconds(5) && client.Game.Me.HasSkill(Skill.Teleport))
                {
                    if (leadPlayer != null)
                    {
                        var randomPointNear = leadPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                        await client.Game.TeleportToLocationAsync(randomPointNear);
                        timer.Restart();
                    }
                }

                if (!cowManager.GetNearbyAliveMonsters(client, 20, 1).Any())
                {
                    await PickupItemsFromPickupList(client, cowManager, 25);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
                    SetShouldFollowLead(client, true);
                }
            }
        }

        private async Task PickupNearbyPotionsIfNeeded(Client client, CowManager cowManager, int distance)
        {
            var missingHealthPotions = client.Game.Belt.Height * 2 - client.Game.Belt.GetHealthPotionsInSlots(new List<int>() { 0, 1 }).Count;
            var missingManaPotions = client.Game.Belt.Height * 2 - client.Game.Belt.GetManaPotionsInSlots(new List<int>() { 2, 3 }).Count;
            var missingRevPotions = Math.Max(6 - client.Game.Inventory.Items.Count(i => i.Name == ItemName.FullRejuvenationPotion || i.Name == ItemName.RejuvenationPotion), 0);
            //Log.Information($"Client {client.Game.Me.Name} missing {missingHealthPotions} healthpotions and missing {missingManaPotions} mana");
            var pickitList = cowManager.GetNearbyPotions(client, new HashSet<ItemName> { ItemName.SuperHealingPotion }, (int)missingHealthPotions, distance);
            pickitList.AddRange(cowManager.GetNearbyPotions(client, new HashSet<ItemName> { ItemName.SuperManaPotion }, (int)missingManaPotions, distance));
            pickitList.AddRange(cowManager.GetNearbyPotions(client, new HashSet<ItemName> { ItemName.RejuvenationPotion, ItemName.FullRejuvenationPotion }, missingRevPotions, distance));
            foreach (var item in pickitList)
            {
                if (cowManager.GetNearbyAliveMonsters(client, 10, 1).Any())
                {
                    Log.Information($"Client {client.Game.Me.Name} not picking up {item.Name} due to nearby cows");
                    continue;
                }
                Log.Information($"Client {client.Game.Me.Name} picking up {item.Name}");
                SetShouldFollowLead(client, false);
                ;
                if (item.Ground)
                {
                    if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                     {
                         if (!await MoveToLocation(client, item.Location))
                         {
                             return false;
                         }
                         client.Game.PickupItem(item);
                         return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                         {
                             await Task.Delay(50);
                             return client.Game.Belt.FindItemById(item.Id) != null;
                         }, TimeSpan.FromSeconds(0.2));
                     }, TimeSpan.FromSeconds(3)))
                    {
                        cowManager.PutPotionOnPickitList(client, item);
                    }
                }
            }

            //Log.Information($"Client {client.Game.Me.Name} got {client.Game.Belt.NumOfHealthPotions()} healthpotions and {client.Game.Belt.NumOfManaPotions()} mana");
        }

        private async Task PickupItemsFromPickupList(Client client, CowManager cowManager, double distance)
        {
            var maxPicks = 3;
            var picks = 0;
            var pickitList = new List<Item>();
            do
            {
                picks++;
                pickitList = cowManager.GetPickitList(client, distance);
                foreach (var pickItem in pickitList)
                {
                    if (pickItem.Ground)
                    {
                        SetShouldFollowLead(client, false);
                        InventoryHelpers.IdentifyItems(client.Game);
                        if (client.Game.Inventory.FindFreeSpace(pickItem) != null)
                        {
                            Log.Information($"Client {client.Game.Me.Name} picking up {pickItem.Amount} {pickItem.Name} ({pickItem.Id})");
                            if (await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                            {
                                if (!(await MoveToLocation(client, pickItem.Location)))
                                {
                                    return false;
                                }

                                var item = client.Game.FindItemById(pickItem.Id);
                                if (item == null || !item.Ground)
                                {
                                    return true;
                                }

                                await client.Game.MoveToAsync(item);
                                client.Game.PickupItem(item);
                                return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                                {
                                    await Task.Delay(50);
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
                                Log.Warning($"Client {client.Game.Me.Name} failed picking up {pickItem.Amount} {pickItem.Name} ({pickItem.Id})");
                                cowManager.PutItemOnPickitList(client, pickItem);
                            }
                        }
                        else
                        {
                            Log.Warning($"Client {client.Game.Me.Name} no space for {pickItem.Amount} {pickItem.Name} ({pickItem.Id})");
                            cowManager.PutItemOnPickitList(client, pickItem);
                        }
                    }
                }
            }
            while (pickitList.Count != 0 && picks < maxPicks);
        }

        async Task BarbClient(Client client, CowManager cowManager, bool shouldBo)
        {
            Log.Information($"Starting BoBarb Client {client.Game.Me.Name}");
            bool hasUsedPotion = false;
            if (shouldBo)
            {
                await ClassHelpers.CastAllShouts(client);
            }

            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if (!hasUsedPotion && client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotions();
                    client.Game.UseHealthPotions();
                    hasUsedPotion = true;
                }

                if (shouldBo)
                {
                    await ClassHelpers.CastAllShouts(client);
                }

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 25)
                {
                    continue;
                }

                var nearbyMonsters = cowManager.GetNearbyAliveMonsters(client, 20, 1);
                if (!nearbyMonsters.Any())
                {
                    await PickupItemsFromPickupList(client, cowManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
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

            if (shouldBo)
            {
                NextGame.TrySetResult(true);
            }

            Log.Information($"Stopped Barb Client {client.Game.Me?.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
        }

        private async Task<bool> MoveToLocation(Client client, Point location, CancellationToken? token = null)
        {
            var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
            var distance = client.Game.Me.Location.Distance(location);
            if (distance > 15)
            {
                var path = await _pathingService.GetPathToLocation(client.Game, location, movementMode);
                if (token.HasValue && token.Value.IsCancellationRequested)
                {
                    return true;
                }
                return await MovementHelpers.TakePathOfLocations(client.Game, path.ToList(), movementMode, token);
            }
            else
            {
                if (movementMode == MovementMode.Teleport)
                {
                    return await client.Game.TeleportToLocationAsync(location);
                }
                else
                {
                    return await client.Game.MoveToAsync(location);
                }
            }
        }

        private async Task<bool> MoveToCowLevel(Client client)
        {
            var cowPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).Where(t => t.TownPortalArea == Area.CowLevel).First();
            var pathBack = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.RogueEncampment, client.Game.Me.Location, cowPortal.Location, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBack, MovementMode.Teleport))
            {
                Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} to {EntityCode.RedTownPortal} failed at {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await client.Game.MoveToAsync(cowPortal);

                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }

                client.Game.InteractWithEntity(cowPortal);
                return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(50);
                    return client.Game.Area == Area.CowLevel;
                }, TimeSpan.FromSeconds(0.5));
            }, TimeSpan.FromSeconds(10)))
            {
                return false;
            }
            client.Game.RequestUpdate(client.Game.Me.Id);
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }

                await Task.Delay(100);

                return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.CowLevel, client.Game.Me.Location);
            }, TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            foreach (var (x, y) in new List<(short, short)> { (-3, -3), (3, 3), (-5, 0), (5, 0), (0, 5), (0, -5) })
            {
                var newLocation = client.Game.Me.Location.Add(x, y);
                if (await _attackService.IsInLineOfSight(client, newLocation))
                {
                    await client.Game.MoveToAsync(newLocation);
                    break;
                }
            }

            return true;
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
            if (townPortalStatePacket.Area == Area.CowLevel)
            {
                CowPortalOpen.TrySetResult(true);
            }
        }
    }
}
