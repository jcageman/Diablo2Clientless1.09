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
        private readonly CowConfiguration _cowconfig;
        private TaskCompletionSource<bool> NextGame = new TaskCompletionSource<bool>();
        private TaskCompletionSource<bool> CowPortalOpen = new TaskCompletionSource<bool>();
        private ConcurrentDictionary<string, ManualResetEvent> PlayersInGame = new ConcurrentDictionary<string, ManualResetEvent>();
        private uint? BoClientPlayerId;
        private ConcurrentDictionary<string, bool> ShouldFollow = new ConcurrentDictionary<string, bool>();
        private ConcurrentDictionary<string, (Point, CancellationTokenSource)> FollowTasks = new ConcurrentDictionary<string, (Point, CancellationTokenSource)>();
        public CowBot(IOptions<BotConfiguration> config, IOptions<CowConfiguration> cowconfig,
            IExternalMessagingClient externalMessagingClient, IPathingService pathingService,
            ITownManagementService townManagementService,
            IAttackService attackService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _townManagementService = townManagementService;
            _attackService = attackService;
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
                FollowTasks.TryAdd(account.Character.ToLower(), (null,new CancellationTokenSource()));
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

                foreach(var task in FollowTasks)
                {
                    task.Value.Item2.Cancel();
                }

                CowPortalOpen = new TaskCompletionSource<bool>();
                NextGame = new TaskCompletionSource<bool>();
                BoClientPlayerId = null;

                Log.Information($"Joining next game");

                try
                {
                    var leaveAndRejoinTasks = clients.Select(async (client, index) => {
                        var account = _cowconfig.Accounts[(int)index];
                        return await LeaveGameAndRejoinMCPWithRetry(client, account);
                    }).ToList();
                    var rejoinResults = await Task.WhenAll(leaveAndRejoinTasks);
                    if(rejoinResults.Any(r => !r))
                    {
                        gameCount++;
                        continue;
                    }

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
                    LeaveGameAndDisconnectWithAllClients(clients);
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
                        Thread.Sleep(TimeSpan.FromSeconds(1 + i * 0.7));
                        if (firstFiller != client && !await RealmConnectHelpers.JoinGameWithRetry(gameCount, client, _config, account))
                        {
                            Log.Warning($"Client {client.LoggedInUserName()} failed to join game, retrying new game");
                            anyTownFailures = true;
                            break;
                        }

                       townTasks.Add(PrepareForCowsTasks(client));
                    }

                    var townResults = await Task.WhenAll(townTasks);
                    if(anyTownFailures)
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

                var killingClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && c.Game.Me.HasSkill(Skill.Nova)).ToList();
                Log.Information($"Selected {string.Join(",", killingClients.Select(c => c.Game.Me.Name))} for cow manager");
                var listeningClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && !killingClients.Contains(c)).ToList();
                listeningClients.Add(boClient);
                var cowManager = new CowManager(killingClients, listeningClients);

                try
                {
                    var clientTasks = clients
                        .Select(async client => await GetTaskForClient(client, cowManager, boClient))
                        .ToList();
                    await Task.WhenAll(clientTasks);
                }
                catch(Exception e)
                {
                    Log.Error($"Failed one or more tasks with exception {e}");
                }

                Log.Information($"Going to next game");
                gameCount++;
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
            
            if(!await _townManagementService.PerformTownTasks(client, townManagementOptions))
            {
                return false;
            }

            if(isPortalCharacter)
            {
                if(!await CreateCowLevel(client, initialLocation))
                {
                    return false;
                }
            }

            return true;
        }

        private static void LeaveGameAndDisconnectWithAllClients(List<Client> clients)
        {
            foreach (var client in clients)
            {
                if (client.Game.IsInGame())
                {
                    client.Game.LeaveGame();
                }
                client.Disconnect();
            }
        }


        private async Task<bool> CreateCowLevel(Client client, Point cowLevelLocation)
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

            var pathBack = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, cowLevelLocation, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(game, pathBack, movementMode))
            {
                Log.Warning($"Client {game.Me.Name} {movementMode} back failed at {game.Me.Location}");
                return false;
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
            if (game.Cube.Items.Any())
            {
                if(!InventoryHelpers.MoveCubeItemsToInventory(game))
                {
                    Log.Error($"Couldn't move all items out of cube");
                    return false;
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

            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
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

                var wirtsLegItem = client.Game.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg && i.Ground);
                if (client.Game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg) != null)
                {
                    return true;
                }

                if (wirtsLegItem == null)
                {
                    client.Game.InteractWithEntity(wirtsBody);
                    return false;
                }

                client.Game.PickupItem(wirtsLegItem);

                return client.Game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg) != null;
            }, TimeSpan.FromSeconds(15)))
            {
                Log.Error($"Getting leg failed, while it's at location: {client.Game.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg)?.Location} and i'm at location {client.Game.Me.Location}");
                return false;
            }
            
            if(!await _townManagementService.TakeTownPortalToTown(client))
            {
                return false;
            }

            Log.Information("Got leg and in town again");
            return true;
        }

        private async Task FollowToLocation(Client client, Point location)
        {
            if(!client.Game.IsInGame())
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

        async Task GetTaskForClient(Client client, CowManager cowManager, Client boClient)
        {
            if(!await MoveToCowLevel(client, cowManager))
            {
                Log.Information($"{client.Game.Me.Name}, couldn't move to the cow level, next game");
                NextGame.TrySetResult(true);
                return;
            }

            if(client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] < 50 && !client.Game.Me.HasSkill(Skill.Teleport))
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
                    if (client.Game.Me.HasSkill(Skill.GuidedArrow) && client.Game.Me.HasSkill(Skill.MultipleShot))
                    {
                        await BowAmaClient(client, cowManager);
                    }
                    else
                    {
                        await BasicFollowClient(client, cowManager);
                    }

                    break;
                case CharacterClass.Sorceress:
                    if (client.Game.Me.HasSkill(Skill.StaticField))
                    {
                        await StaticSorcClient(client, cowManager);
                    }
                    else
                    {
                        await BasicFollowClient(client, cowManager);
                    }

                    break;
                case CharacterClass.Necromancer:
                    await NecFollowClient(client, cowManager);
                    break;
                case CharacterClass.Paladin:
                    await PalaFollowClient(client, cowManager);
                    break;
                case CharacterClass.Barbarian:
                    bool shouldBo = client == boClient;
                    await BarbClient(client, cowManager, shouldBo);
                    break;
                case CharacterClass.Druid:
                case CharacterClass.Assassin:
                    throw new InvalidOperationException();
            }

            if(client.Game.IsInGame())
            {
                client.Game.LeaveGame();
            }
        }

        async Task BowAmaClient(Client client, CowManager cowManager)
        {
            Log.Information($"Starting Ama Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
            SetShouldFollowLead(client, true);
            Point targetLocation = null;
            Entity entity = null;
            ElapsedEventHandler multiShotAction = (sender, args) =>
            {
                if (targetLocation != null)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.MultipleShot, targetLocation);
                }
            };
            using var executeMultiShot = new ExecuteAtInterval(multiShotAction, TimeSpan.FromSeconds(0.2));

            ElapsedEventHandler guidedAction = (sender, args) =>
            {
                if (entity != null)
                {
                    client.Game.UseRightHandSkillOnEntity(Skill.GuidedArrow, entity);
                }
            };
            using var executeGuided = new ExecuteAtInterval(guidedAction, TimeSpan.FromSeconds(0.2));

            var timer = new Stopwatch();
            bool hasUsedPotion = false;
            timer.Start();
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.5)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if(client.Game.Inventory.Items.Where(i => i.Name == ItemName.Arrows).Sum(i => i.Amount) < 200)
                {
                    Log.Information($"{client.Game.Me.Name} leaving game, due to low arrows");
                    client.Game.LeaveGame();
                    break;
                }

                var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 35.0, 2);
                if(!nearbyAliveCows.Any())
                {
                    executeGuided.Stop();
                    executeMultiShot.Stop();
                    SetShouldFollowLead(client, true);
                }
                else if (nearbyAliveCows.Any())
                {
                    var nearestHellBovine = nearbyAliveCows.FirstOrDefault();
                    var secondHellBovine = nearbyAliveCows.Skip(1).FirstOrDefault();
                    var distanceToNearest = nearestHellBovine.Location.Distance(client.Game.Me.Location);
                    var distanceSecondToNearest = secondHellBovine?.Location.Distance(client.Game.Me.Location);
                    if (await _attackService.IsInLineOfSight(client, nearestHellBovine.Location))
                    {
                        if ((nearestHellBovine.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted) || (distanceSecondToNearest.HasValue && distanceSecondToNearest - distanceToNearest > 10))
                        && client.Game.WorldObjects.TryGetValue((nearestHellBovine.Id, EntityType.NPC), out var cowEntity))
                        {
                            entity = cowEntity;
                            executeGuided.Start();
                            executeMultiShot.Stop();
                        }
                        else
                        {
                            targetLocation = nearestHellBovine.Location.GetPointBeforePointInSameDirection(client.Game.Me.Location, 15);
                            executeMultiShot.Start();
                            executeGuided.Stop();
                        }
                    }

                    continue;
                }

                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    hasUsedPotion = true;
                }
            }

            NextGame.TrySetResult(true);
            executeGuided.Stop();
            executeMultiShot.Stop();
            Log.Information($"Stopped Ama Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
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
            var moveStopWatch = new Stopwatch();
            moveStopWatch.Start();

            bool hasUsedPotion = false;

            var random = new Random();

            Point currentCluster = null;
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.5)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                var cowsNearLead = cowManager.GetNearbyAliveMonsters(leadPlayer.Location, 20.0, 10);
                if (leadPlayer != null && leadPlayer.Location != currentCluster && cowsNearLead.Any())
                {
                    Log.Information($"{client.Game.Me.Name}, lead client in danger, moving to lead client");
                    if(currentCluster != null)
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
                        if (teleportPath.Count > 1)
                        {
                            await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.SkipLast(1).ToList(), MovementMode.Teleport);
                        }

                        await TeleportToNearbySafeSpot(client, cowManager, teleportPath.Last());
                        moveStopWatch.Restart();
                    }
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.BattleOrders) || !client.Game.Me.Effects.Contains(EntityEffect.Shout))
                {
                    Log.Information($"Lost bo on client {client.Game.Me.Name}, moving to barb for bo");

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

                if(!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    client.Game.UseHealthPotion();
                    hasUsedPotion = true;
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                }

                if (((double)client.Game.Me.Life) / client.Game.Me.MaxLife <= 0.5)
                {
                    await TeleportToNearbySafeSpot(client, cowManager, client.Game.Me.Location, 15.0);
                    moveStopWatch.Restart();
                }

                var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 30.0, 10);
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
                else if (nearbyAliveCows.Any() && cowManager.GetNearbyAliveMonsters(nearbyAliveCows.FirstOrDefault().Location, 15.0, 10).Count > 5)
                {
                    var nearestAlive = nearbyAliveCows.FirstOrDefault();
                    var distanceToNearest = nearestAlive.Location.Distance(client.Game.Me.Location);
                    if (distanceToNearest > 30)
                    {
                        await TeleportToNearbySafeSpot(client, cowManager, nearestAlive.Location);
                        moveStopWatch.Restart();
                    }
                    else if (moveStopWatch.Elapsed > TimeSpan.FromSeconds(4))
                    {
                        await TeleportToNearbySafeSpot(client, cowManager, client.Game.Me.Location);
                        moveStopWatch.Restart();
                    }

                    if (client.Game.Me.HasSkill(Skill.FrostNova) && distanceToNearest < 10 && client.Game.WorldObjects.TryGetValue((nearestAlive.Id, EntityType.NPC), out var cow) && !cow.Effects.Contains(EntityEffect.Cold))
                    {
                        //Log.Information($"Nearby NPC is not frozen, recasting frost nova");
                        client.Game.UseRightHandSkillOnLocation(Skill.FrostNova, client.Game.Me.Location);
                    }

                    if (nearestAlive.LifePercentage < 30 && distanceToNearest < 10)
                    {
                        executeStaticField.Stop();
                        if(client.Game.Me.HasSkill(Skill.Nova))
                        {
                            client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                            executeNova.Start();
                        }
                        else if(client.Game.Me.HasSkill(Skill.FrozenOrb))
                        {
                            client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, nearestAlive.Location);
                        }

                    }
                    else if (distanceToNearest < 20)
                    {
                        if (client.Game.Me.HasSkill(Skill.StaticField))
                        {
                            client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                            executeStaticField.Start();
                        }
                        executeNova.Stop();
                    }

                    continue;
                }

                var anyNearbyCows = cowManager.GetNearbyAliveMonsters(client, 20, 1).Any();
                if (!anyNearbyCows)
                {
                    executeStaticField.Stop();
                    executeNova.Stop();
                    await PickupItemsFromPickupList(client, cowManager, 30);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 30);

                    SetShouldFollowLead(client, false);
                }

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
                    }

                    await TeleportToNearbySafeSpot(client, cowManager, clusterPath.Last());
                    moveStopWatch.Restart();
                }
            }

            Log.Information($"Stopped Sorc Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
            executeStaticField.Stop();
            executeNova.Stop();
            NextGame.TrySetResult(true);
        }

        private async Task TeleportToNearbySafeSpot(Client client, CowManager cowManager, Point toLocation, double minDistance = 0)
        {
            var nearbyMonsters = cowManager.GetNearbyAliveMonsters(toLocation, 30.0, 100).Select(p => p.Location).ToList();
            await _attackService.MoveToNearbySafeSpot(client, nearbyMonsters, toLocation, MovementMode.Teleport, minDistance);
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

            var timer = new Stopwatch();
            timer.Start();
            bool hasUsedPotion = false;
            var random = new Random();
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    client.Game.UseHealthPotion();
                    hasUsedPotion = true;
                }

                if(client.Game.Me.HasSkill(Skill.FrozenOrb))
                {
                    var nearest = cowManager.GetNearbyAliveMonsters(client, 20.0, 1).FirstOrDefault();
                    if(nearest != null)
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, nearest.Location);
                    }
                }

                if(timer.Elapsed > TimeSpan.FromSeconds(5) && client.Game.Me.HasSkill(Skill.Teleport))
                {
                    var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                    if(leadPlayer != null)
                    {
                        var randomPointNear = leadPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                        await client.Game.TeleportToLocationAsync(randomPointNear);
                        timer.Restart();
                    }
                }

                if (!cowManager.GetNearbyAliveMonsters(client, 20, 1).Any())
                {
                    await PickupItemsFromPickupList(client, cowManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
                    SetShouldFollowLead(client, true);
                }
            }
        }

        private async Task PickupNearbyPotionsIfNeeded(Client client, CowManager cowManager, int distance)
        {
            var missingHealthPotions = client.Game.Belt.Height * 2 - client.Game.Belt.GetHealthPotionsInSlots(new List<int>() { 0, 1 }).Count;
            var missingManaPotions = client.Game.Belt.Height * 2 - client.Game.Belt.GetManaPotionsInSlots(new List<int>() { 2, 3 }).Count;
            //Log.Information($"Client {client.Game.Me.Name} missing {missingHealthPotions} healthpotions and missing {missingManaPotions} mana");
            var pickitList = cowManager.GetNearbyPotions(client, true, (int)missingHealthPotions, distance);
            pickitList.AddRange(cowManager.GetNearbyPotions(client, false, (int)missingManaPotions, distance));
            foreach (var item in pickitList)
            {
                if(cowManager.GetNearbyAliveMonsters(client, 10, 1).Any())
                {
                    Log.Information($"Client {client.Game.Me.Name} not picking up {item.Name} due to nearby cows");
                    continue;
                }
                Log.Information($"Client {client.Game.Me.Name} picking up {item.Name}");
                SetShouldFollowLead(client, false);
                await MoveToLocation(client, item.Location);
                if (item.Ground)
                {
                    if(!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
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
                        cowManager.PutPotionOnPickitList(client, item);
                    }
                }
            }

            //Log.Information($"Client {client.Game.Me.Name} got {client.Game.Belt.NumOfHealthPotions()} healthpotions and {client.Game.Belt.NumOfManaPotions()} mana");
        }

        private async Task PickupItemsFromPickupList(Client client, CowManager cowManager, double distance)
        {
            var pickitList = cowManager.GetPickitList(client, distance);
            foreach (var item in pickitList)
            {
                if (item.Ground)
                {
                    SetShouldFollowLead(client, false);
                    Log.Information($"Client {client.Game.Me.Name} picking up {item.Amount} {item.Name}");
                    await MoveToLocation(client, item.Location);
                    if (client.Game.Inventory.FindFreeSpace(item) != null && GeneralHelpers.TryWithTimeout((retryCount) =>
                    {
                        client.Game.MoveTo(item.Location);
                        client.Game.PickupItem(item);
                        return GeneralHelpers.TryWithTimeout((retryCount) =>
                        {
                            Thread.Sleep(50);
                            if(!item.IsGold && client.Game.Inventory.FindItemById(item.Id) == null)
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
                        cowManager.PutItemOnPickitList(client, item);
                    }
                }
            }
        }

        async Task NecFollowClient(Client client, CowManager cowManager)
        {
            Log.Information($"Starting Nec Client {client.Game.Me.Name}");
            SetShouldFollowLead(client, true);
            bool hasUsedPotion = false;

            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.5)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    client.Game.UseHealthPotion();
                    hasUsedPotion = true;
                }

                if (!client.Game.Me.Effects.Contains(EntityEffect.Bonearmor))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BoneArmor, client.Game.Me.Location);
                }

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 15)
                {
                    continue;
                }

                if (client.Game.Me.HasSkill(Skill.CorpseExplosion))
                {
                    var corpseExplosionCount = 0;
                    while (cowManager.CastCorpseExplosion(client) && corpseExplosionCount < 5)
                    {
                        Thread.Sleep(200);
                        corpseExplosionCount++;
                    }
                }

                if (client.Game.Me.HasSkill(Skill.AmplifyDamage))
                {
                    var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 25.0, 1);
                    if (nearbyAliveCows.Any())
                    {
                        var nearestAlive = nearbyAliveCows.FirstOrDefault();
                        client.Game.UseRightHandSkillOnLocation(Skill.AmplifyDamage, nearestAlive.Location);
                    }
                }

                if (!cowManager.GetNearbyAliveMonsters(client, 20, 1).Any())
                {
                    await PickupItemsFromPickupList(client, cowManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
                    SetShouldFollowLead(client, true);
                }
            }

            Log.Information($"Stopped Nec Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
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
                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    client.Game.UseHealthPotion();
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
                if(!nearbyMonsters.Any())
                {
                    await PickupItemsFromPickupList(client, cowManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
                }
                else if(client.Game.Me.HasSkill(Skill.Whirlwind))
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

            if(shouldBo)
            {
                NextGame.TrySetResult(true);
            }

            Log.Information($"Stopped Barb Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
        }

        async Task PalaFollowClient(Client client, CowManager cowManager)
        {
            Log.Information($"Starting Pala Client {client.Game.Me.Name}");
            SetShouldFollowLead(client, true);
            var timer = new Stopwatch();
            bool hasUsedPotion = false;
            bool flipflop = true;
            client.Game.ChangeSkill(Skill.Fanaticism, Hand.Right);
            timer.Start();
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if (!client.Game.Me.Effects.Contains(EntityEffect.Holyshield))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
                }

                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    client.Game.UseHealthPotion();
                    hasUsedPotion = true;
                }

                if (timer.Elapsed > TimeSpan.FromSeconds(4))
                {
                    client.Game.ChangeSkill(flipflop ? Skill.Concentration : Skill.Fanaticism, Hand.Right);
                    timer.Reset();
                    timer.Start();
                    flipflop = !flipflop;
                }

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
                if (!cowManager.GetNearbyAliveMonsters(client, 20, 1).Any() && leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) < 15)
                {
                    await PickupItemsFromPickupList(client, cowManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
                    SetShouldFollowLead(client, true);
                }
            }

            Log.Information($"Stopped Pala Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
        }

        private async Task MoveToLocation(Client client, Point location, CancellationToken? token = null)
        {
            var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
            var distance = client.Game.Me.Location.Distance(location);
            if (distance > 15)
            {
                var path = await _pathingService.GetPathToLocation(client.Game, location, movementMode);
                if(token.HasValue && token.Value.IsCancellationRequested)
                {
                    return;
                }
                await MovementHelpers.TakePathOfLocations(client.Game, path.ToList(), movementMode, token);
            }
            else
            {
                if(movementMode == MovementMode.Teleport)
                {
                    client.Game.TeleportToLocation(location);
                }
                else
                {
                    client.Game.MoveTo(location);
                }
            }
        }

        private async Task<bool> MoveToCowLevel(Client client, CowManager cowManager)
        {
            var cowPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).Where(t => t.TownPortalArea == Area.CowLevel).First();
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await client.Game.MoveToAsync(cowPortal);
                
                if(retryCount > 0 && retryCount % 5 == 0)
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

            foreach (var (x,y) in new List<(short,short)>{ (-3, -3), (3, 3),(-5,0),(5,0),(0,5),(0,-5)})
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
                client.Game.LeaveGame();
            }

            if (!client.RejoinMCP())
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
