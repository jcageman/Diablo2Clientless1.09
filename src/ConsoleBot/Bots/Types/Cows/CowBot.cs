using ConsoleBot.Bots;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Exceptions;
using ConsoleBot.Helpers;
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
using YamlDotNet.Core.Tokens;

namespace ConsoleBot.Bots.Types.Cows
{
    public class CowBot : IBotInstance
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;
        private TaskCompletionSource<bool> NextGame = new TaskCompletionSource<bool>();
        private TaskCompletionSource<bool> CowPortalOpen = new TaskCompletionSource<bool>();
        private ConcurrentDictionary<string, ManualResetEvent> PlayersInGame = new ConcurrentDictionary<string, ManualResetEvent>();
        private uint? LeadKillingPlayerId;
        private ConcurrentDictionary<string, bool> ShouldFollow = new ConcurrentDictionary<string, bool>();
        private ConcurrentDictionary<string, (Point, CancellationTokenSource)> FollowTasks = new ConcurrentDictionary<string, (Point, CancellationTokenSource)>();
        private static int isAnyClientGambling = 0;
        public CowBot(IOptions<BotConfiguration> config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService, IMapApiService mapApiService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _mapApiService = mapApiService;
        }

        public string GetName()
        {
            return "cows";
        }

        public async Task<int> Run()
        {
            var clients = new List<Client>();
            List<Tuple<string, string, string>> clientLogins = new List<Tuple<string, string, string>>();
            // add logins here for now

            foreach (var clientLogin in clientLogins)
            {
                var client = new Client();
                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                _externalMessagingClient.RegisterClient(client);
                PlayersInGame.TryAdd(clientLogin.Item3.ToLower(), new ManualResetEvent(false));
                ShouldFollow.TryAdd(clientLogin.Item3.ToLower(), false);
                FollowTasks.TryAdd(clientLogin.Item3.ToLower(), (null,new CancellationTokenSource()));
                client.OnReceivedPacketEvent(InComingPacket.EntityMove, async (packet) =>
                {
                    var entityMovePacket = new EntityMovePacket(packet);
                    if (entityMovePacket.UnitType == EntityType.Player && entityMovePacket.UnitId == LeadKillingPlayerId && ShouldFollowLeadClient(client))
                    {
                        await FollowToLocation(client, entityMovePacket.MoveToLocation);
                    }
                });

                client.OnReceivedPacketEvent(InComingPacket.ReassignPlayer, async (packet) =>
                {
                    var reassignPlayerPacket = new ReassignPlayerPacket(packet);
                    if (reassignPlayerPacket.UnitType == EntityType.Player && reassignPlayerPacket.UnitId == LeadKillingPlayerId && ShouldFollowLeadClient(client))
                    {
                        await FollowToLocation(client, reassignPlayerPacket.Location);
                    }
                });
                client.OnReceivedPacketEvent(InComingPacket.PartyAutomapInfo, async (packet) =>
                {
                    var partyAutomapInfoPacket = new PartyAutomapInfoPacket(packet);
                    if (partyAutomapInfoPacket.Id == LeadKillingPlayerId && ShouldFollowLeadClient(client))
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
                LeadKillingPlayerId = null;

                Log.Information($"Joining next game");

                var leaveAndRejoinTasks = clients.Select(async (client, index) => {
                    var clientLogin = clientLogins[(int)index];
                    await LeaveGameAndRejoinMCPWithRetry(client, clientLogin);
                }).ToList();
                await Task.WhenAll(leaveAndRejoinTasks);

                var result = await CreateGameWithRetry(gameCount, firstFiller, clientLogins.First());
                gameCount = result.Item2;
                if (!result.Item1)
                {
                    gameCount++;
                    Thread.Sleep(30000);
                    continue;
                }

                try
                {
                    var townTasks = new List<Task<bool>>();
                    var rand = new Random();
                    for (int i = 0; i < clients.Count(); i++)
                    {
                        var clientLogin = clientLogins[i];
                        var client = clients[i];
                        Thread.Sleep(TimeSpan.FromSeconds(1 + i * 0.7));
                        if (firstFiller != client && !await JoinGameWithRetry(gameCount, client, clientLogin))
                        {
                            Log.Warning($"Client {client.LoggedInUserName()} failed to join game, retrying new game");
                            gameCount++;
                            continue;
                        }

                        var randomMove = ((short)rand.Next(-7, 7), (short)rand.Next(-7, 7));
                        townTasks.Add(TownManagement(client, randomMove, _config));
                    }

                    var townResults = await Task.WhenAll(townTasks);
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
                    return 1;
                }

                LeadKillingPlayerId = firstFiller.Game.Players.FirstOrDefault(p => p.Class == CharacterClass.Amazon)?.Id;
                if (!LeadKillingPlayerId.HasValue)
                {
                    LeadKillingPlayerId = boClient.Game.Me.Id;
                }

                Log.Information($"Waiting for cow portal to open");
                await CowPortalOpen.Task;
                Log.Information($"Cow portal open, moving to cow level");

                var clientsForKilling = clients.Where(c => c.Game.Me.Class == CharacterClass.Amazon && c.Game.Me.HasSkill(Skill.MultipleShot)).ToList();
                //clientsForKilling.AddRange(clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && c.Game.Me.HasSkill(Skill.Nova)));
                Log.Information($"Selected {string.Join(",", clientsForKilling.Select(c => c.Game.Me.Name))} for killing");
                var cowManager = new CowManager(_pathingService, _mapApiService, clientsForKilling);

                try
                {
                    var clientTasks = clients
                        .Select(async client => await Task.Run(async () => await GetTaskForClient(client, cowManager, boClient)))
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

        private async Task<bool> TownManagement(Client client, (short X, short Y)move, BotConfiguration configuration)
        {
            var timer = new Stopwatch();
            timer.Start();
            while (client.Game.Me == null && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(100);
            }

            if(client.Game.Me == null)
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

            var game = client.Game;
            if(game.Act != Act.Act1)
            {
                Log.Error($"{client.Game.Me.Name} is not in act 1, not supported yet");
                return false;
            }
            var initialLocation = game.Me.Location;
            game.CleanupCursorItem();
            InventoryHelpers.CleanupPotionsInBelt(game);

            game.MoveTo(game.Me.Location.Add(move.X, move.Y));

            if(!await GeneralHelpers.PickupCorpseIfExists(client, _pathingService))
            {
                Log.Error($"{client.Game.Me.Name} failed to pickup corpse");
                return false;
            }

            var portalCharacter = configuration.Character.Equals(game.Me.Name, StringComparison.InvariantCultureIgnoreCase);

            var movementMode = game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;

            var unidentifiedItemCount = game.Inventory.Items.Count(i => !i.IsIdentified) +
game.Cube.Items.Count(i => !i.IsIdentified);
            if (unidentifiedItemCount > 6 || (portalCharacter && unidentifiedItemCount > 0))
            {
                Log.Information($"Visiting Deckard Cain with {unidentifiedItemCount} unidentified items");
                var deckhardCainCode = NPCHelpers.GetDeckardCainForAct(game.Act);

                var deckardCain = NPCHelpers.GetUniqueNPC(game, deckhardCainCode);
                if (deckardCain == null)
                {
                    Log.Warning($"Failed to find deckard cain at location {game.Me.Location}");
                }

                var pathDeckardCain = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, deckardCain.Location, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(game, pathDeckardCain, movementMode))
                {
                    Log.Warning($"Client {game.Me.Name} {movementMode} back failed at {game.Me.Location}");
                }
                else
                {
                    NPCHelpers.IdentifyItemsAtDeckardCain(game);
                }
            }

            var sellItemCount = game.Inventory.Items.Count(i => !Pickit.Pickit.ShouldKeepItem(game, i)) + game.Cube.Items.Count(i => !Pickit.Pickit.ShouldKeepItem(game, i));
            if (NPCHelpers.ShouldRefreshCharacterAtNPC(game) || sellItemCount > 5 || portalCharacter)
            {
                Log.Information($"Client {game.Me.Name} moving to Akara for refresh and selling {sellItemCount} items");
                var pathAkara = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, NPCCode.Akara, movementMode);
                if (pathAkara.Count > 0 && await MovementHelpers.TakePathOfLocations(game, pathAkara, movementMode))
                {
                    var akara = NPCHelpers.GetUniqueNPC(game, NPCCode.Akara);
                    if (akara == null)
                    {
                        Log.Warning($"Client {game.Me.Name} Did not find Akara at {game.Me.Location}");
                    }

                    var additionalBuys = new Dictionary<ItemName, int>();
                    var tomesOfTownPortal = game.Inventory.Items.Count(i => i.Name == ItemName.TomeOfTownPortal);
                    if(portalCharacter && tomesOfTownPortal < 2)
                    {
                        additionalBuys.Add(ItemName.TomeOfTownPortal, 1);
                    }

                    if (!NPCHelpers.SellItemsAndRefreshPotionsAtNPC(game, akara, additionalBuys))
                    {
                        Log.Warning($"Client {game.Me.Name} Selling items and refreshing potions failed at {game.Me.Location}");
                    }

                    var pathBack = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, initialLocation, movementMode);
                    if (pathBack.Count > 0 && !await MovementHelpers.TakePathOfLocations(game, pathBack, movementMode))
                    {
                        Log.Warning($"Client {game.Me.Name} {movementMode} back failed at {game.Me.Location}");
                    }
                    else
                    {
                        Log.Information($"Client {game.Me.Name} moved back from Akara to original position");
                    }
                }
                else
                {
                    Log.Warning($"Client {game.Me.Name} {movementMode} to Akara failed at {game.Me.Location}");
                }
            }

            if (NPCHelpers.ShouldGoToRepairNPC(game))
            {
                Log.Information($"Client {game.Me.Name} moving to Charsi for repair/arrows");
                var pathCharsi = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, NPCCode.Charsi, movementMode);
                if (pathCharsi.Count > 0 && await MovementHelpers.TakePathOfLocations(game, pathCharsi, movementMode))
                {
                    var charsi = NPCHelpers.GetUniqueNPC(game, NPCCode.Charsi);
                    if (charsi == null)
                    {
                        Log.Warning($"Client {game.Me.Name} Did not find Charsi at {game.Me.Location}");
                        return false;
                    }

                    if (!NPCHelpers.RepairItemsAndBuyArrows(game, charsi))
                    {
                        Log.Warning($"Client {game.Me.Name} Selling items and refreshing potions failed at {game.Me.Location}");
                    }

                    var pathBack = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, initialLocation, movementMode);
                    if (!await MovementHelpers.TakePathOfLocations(game, pathBack, movementMode))
                    {
                        Log.Warning($"Client {game.Me.Name} {movementMode} back failed at {game.Me.Location}");
                        return false;
                    }

                    Log.Information($"Client {game.Me.Name} moved back from Charsi to original position");
                }
                else
                {
                    Log.Warning($"Client {game.Me.Name} {movementMode} to Charsi failed at {game.Me.Location}");
                }
            }

            var pathStash = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, EntityCode.Stash, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(game, pathStash, movementMode))
            {
                Log.Warning($"{movementMode} failed at location {game.Me.Location}");
            }

            var stashItemsResult = InventoryHelpers.StashItemsToKeep(game, _externalMessagingClient);
            if (stashItemsResult != Enums.MoveItemResult.Succes)
            {
                Log.Warning($"Stashing items failed with result {stashItemsResult}");
            }

            if (CubeHelpers.AnyGemsToTransmuteInStash(client.Game))
            {
                CubeHelpers.TransmuteGems(client.Game);
            }

            bool shouldGamble = client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.GoldInStash] > 7_000_000;
            if (shouldGamble && System.Threading.Interlocked.Exchange(ref isAnyClientGambling, 1) == 0)
            {
                Log.Information($"Gambling items at Gheed");
                var pathGheed = await _pathingService.GetPathToNPC(client.Game, NPCCode.Gheed, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathGheed, movementMode))
                {
                    Log.Warning($"{movementMode} to Gheed failed at {client.Game.Me.Location}");
                    return false;
                }

                var gheed = NPCHelpers.GetUniqueNPC(client.Game, NPCCode.Gheed);
                if (gheed == null)
                {
                    return false;
                }

                NPCHelpers.GambleItems(client.Game, gheed);
                System.Threading.Interlocked.Exchange(ref isAnyClientGambling, 0);
                var pathStash2 = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, EntityCode.Stash, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(game, pathStash2, movementMode))
                {
                    Log.Warning($"{movementMode} failed at location {game.Me.Location}");
                }
            }

            if (portalCharacter)
            {
                var tomesOfTp = game.Inventory.Items.Where(i => i.Name == ItemName.TomeOfTownPortal);
                if (tomesOfTp.Count() < 2)
                {
                    Log.Error($"Not enough tomes of town portal found");
                    return false;
                }

                var lowestQuantity = tomesOfTp.OrderBy(i => i.Amount).First();
                if (game.Cube.Items.Any())
                {
                    InventoryHelpers.MoveCubeItemsToInventory(game);
                }

                var freeSpace = game.Cube.FindFreeSpace(lowestQuantity);
                if (game.Cube.Items.Any() || freeSpace == null)
                {
                    Log.Error($"Something wrong with cube for transmute town portal");
                    return false;
                }

                if (InventoryHelpers.PutInventoryItemInCube(game, lowestQuantity, freeSpace) != Enums.MoveItemResult.Succes)
                {
                    Log.Error($"Moving tome of town portal to cube failed");
                    return false;
                }

                if (!InventoryHelpers.TransmuteItemsInCube(game, false))
                {
                    Log.Error($"Transmuting tome of town portal failed");
                    return false;
                }
            }

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
                    if (LeadKillingPlayerId == client.Game.Me.Id)
                    {
                        await AmaLeadClient(client, cowManager);
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
        }

        async Task AmaLeadClient(Client client, CowManager cowManager)
        {
            Log.Information($"Starting Ama Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
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
            CancellationTokenSource movementCancellationSource = null;
            Task movementTask = null;
            bool hasUsedPotion = false;
            Point currentCluster = null;
            timer.Start();
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if(timer.Elapsed > TimeSpan.FromMinutes(10))
                {
                    Log.Information($"{client.Game.Me.Name} leaving game, 10 minutes elapsed");
                    client.Game.LeaveGame();
                    break;
                }

                if(movementTask != null && movementTask.IsCompleted)
                {
                    movementCancellationSource = null;
                    movementTask = null;
                }

                if(cowManager.GetCowKingLocation().Distance(client.Game.Me.Location) < 50)
                {
                    Log.Information($"{client.Game.Me.Name} leaving game, due to King too nearby");
                    client.Game.LeaveGame();
                    break;
                }

                if(client.Game.Inventory.Items.Where(i => i.Name == D2NG.Core.D2GS.Items.ItemName.Arrows).Sum(i => i.Amount) < 200)
                {
                    Log.Information($"{client.Game.Me.Name} leaving game, due to low arrows");
                    client.Game.LeaveGame();
                    break;
                }

                if (movementTask == null && !cowManager.GetNearbyAliveMonsters(client, 20, 1).Any())
                {
                    await PickupItemsFromPickupList(client, cowManager, 10);
                    SetShouldFollowLead(client, false);
                }

                var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 35.0, 2);
                if (nearbyAliveCows.Any())
                {
                    var nearestHellBovine = nearbyAliveCows.FirstOrDefault();
                    var secondHellBovine = nearbyAliveCows.Skip(1).FirstOrDefault();
                    var distanceToNearest = nearestHellBovine.Location.Distance(client.Game.Me.Location);
                    var distanceSecondToNearest = secondHellBovine?.Location.Distance(client.Game.Me.Location);
                    if (!(await cowManager.IsInLineOfSight(client, nearestHellBovine.Location) || (nearestHellBovine.NPCCode == NPCCode.DrehyaTemple) && distanceToNearest > 10))
                    {
                        if (movementTask != null && !movementTask.IsCompleted)
                        {
                            continue;
                        }

                        executeGuided.Stop();
                        executeMultiShot.Stop();
                        var walkingPathNearest = await _pathingService.GetPathToLocation(client.Game, nearestHellBovine.Location, MovementMode.Walking);
                        Log.Information($"{client.Game.Me.Name} nearest is not in line of sight, moving to {nearestHellBovine.Location}");
                        movementCancellationSource = new CancellationTokenSource();
                        movementTask = MovementHelpers.TakePathOfLocations(client.Game, walkingPathNearest, MovementMode.Walking, movementCancellationSource.Token);
                    }
                    else if((nearestHellBovine.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted) || nearestHellBovine.NPCCode == NPCCode.DrehyaTemple || (distanceSecondToNearest.HasValue && distanceSecondToNearest - distanceToNearest > 10))
                        && client.Game.WorldObjects.TryGetValue((nearestHellBovine.Id, EntityType.NPC), out var cowEntity))
                    {
                        if (movementTask != null)
                        {
                            movementCancellationSource.Cancel();
                            movementCancellationSource = null;
                            movementTask = null;
                        }

                        entity = cowEntity;
                        executeGuided.Start();
                        executeMultiShot.Stop();
                    }
                    else
                    {
                        if (movementTask != null)
                        {
                            movementCancellationSource.Cancel();
                            movementCancellationSource = null;
                            movementTask = null;
                        }

                        targetLocation = nearestHellBovine.Location.GetPointBeforePointInSameDirection(client.Game.Me.Location, 15);
                        executeMultiShot.Start();
                        executeGuided.Stop();
                    }

                    continue;
                }

                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    hasUsedPotion = true;
                }

                if (currentCluster == null || client.Game.Me.Location.Distance(currentCluster) < 20)
                {
                    currentCluster = cowManager.GetNextCluster(client, currentCluster);
                    if (currentCluster == null)
                    {
                        Log.Information($"No cluster found for {client.Game.Me.Name}");
                        break;
                    }
                    Log.Information($"Client {client.Game.Me.Name} obtained next cluster at {currentCluster}");
                }

                if(movementTask != null && !movementTask.IsCompleted)
                {
                    continue;
                }

                executeMultiShot.Stop();
                executeGuided.Stop();
                var walkingPath = await _pathingService.GetPathToLocation(client.Game, currentCluster, MovementMode.Walking);
                movementCancellationSource = new CancellationTokenSource();
                movementTask = MovementHelpers.TakePathOfLocations(client.Game, walkingPath, MovementMode.Walking, movementCancellationSource.Token);
            }

            if (movementTask != null)
            {
                movementCancellationSource.Cancel();
                movementCancellationSource = null;
                await movementTask;
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

            bool hasUsedPotion = false;

            Point currentCluster = null;
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == LeadKillingPlayerId);
                if (!client.Game.Me.Effects.Contains(EntityEffect.BattleOrders) || !client.Game.Me.Effects.Contains(EntityEffect.Shout))
                {
                    Log.Information($"Lost bo on client {client.Game.Me.Name}, moving to barb for bo");
                    
                    if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 10)
                    {
                        var teleportPathLead = await _pathingService.GetPathToLocation(client.Game, leadPlayer.Location, MovementMode.Teleport);
                        await MovementHelpers.TakePathOfLocations(client.Game, teleportPathLead.ToList(), MovementMode.Teleport);
                    }

                    continue;
                }

                if(leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 15)
                {
                    executeStaticField.Stop();
                    executeNova.Stop();

                    var teleportPath = await _pathingService.GetPathToLocation(client.Game, leadPlayer.Location, MovementMode.Teleport);
                    if (teleportPath.Count > 0)
                    {
                        //Log.Information($"Client {client.Game.Me.Name} teleporting nearby lead player {leadPlayer.Location}");
                        await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.ToList(), MovementMode.Teleport);
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

                var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 30.0, 10);
                var lightningEnhancedCows = nearbyAliveCows.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted));
                if (clusterStopWatch.Elapsed > TimeSpan.FromSeconds(30) && currentCluster != null)
                {
                    Log.Information($"Taking too much time on cluster, skipping current cluster and moving to next cluster {client.Game.Me.Name}");
                    currentCluster = null;
                }

                if (lightningEnhancedCows && currentCluster != null)
                {
                    Log.Information($"Lightning enhanced cow nearby, giving up current cluster and moving to next cluster {client.Game.Me.Name}");
                    cowManager.GiveUpCluster(client, currentCluster);
                    currentCluster = null;
                }

                if (nearbyAliveCows.Any())
                {
                    var nearestAlive = nearbyAliveCows.FirstOrDefault();
                    var distanceToNearest = nearestAlive.Location.Distance(client.Game.Me.Location);
                    if (distanceToNearest > 20)
                    {
                        client.Game.TeleportToLocation(client.Game.Me.Location.GetPointBeforePointInSameDirection(nearestAlive.Location, 15));
                    }

                    if (client.Game.Me.HasSkill(Skill.FrostNova) && distanceToNearest < 10 && client.Game.WorldObjects.TryGetValue((nearestAlive.Id, EntityType.NPC), out var cow) && !cow.Effects.Contains(EntityEffect.Cold))
                    {
                        //Log.Information($"Nearby NPC is not frozen, recasting frost nova");
                        client.Game.UseRightHandSkillOnLocation(Skill.FrostNova, client.Game.Me.Location);
                    }

                    if (distanceToNearest < 5 && nearestAlive.LifePercentage > 50)
                    {
                        await TeleportToNearbySafeSpot(client, cowManager, client.Game.Me.Location);
                    }
                    if (nearestAlive.LifePercentage < 30 && distanceToNearest < 15)
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
                        client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                        executeStaticField.Start();
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
                /*
                if (currentCluster == null || client.Game.Me.Location.Distance(currentCluster) < 20)
                {
                    currentCluster = cowManager.GetNextCluster(client, currentCluster);
                    if (currentCluster == null)
                    {
                        Log.Information($"No cluster found for {client.Game.Me.Name}");
                        continue;
                    }

                    clusterStopWatch.Start();
                    Log.Information($"Client {client.Game.Me.Name} obtained next cluster at {currentCluster}");
                }

                executeStaticField.Stop();
                executeNova.Stop();

                var teleportPath = await _pathingService.GetPathToLocation(client.Game, currentCluster, MovementMode.Teleport);
                if(teleportPath.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting nearby cluster {currentCluster}");
                    if(teleportPath.Count > 1)
                    {
                        MovementHelpers.TakePathOfLocations(client.Game, teleportPath.SkipLast(1).ToList(), MovementMode.Teleport);
                    }

                    await TeleportToNearbySafeSpot(client, cowManager, teleportPath.Last());
                }
                */
            }

            Log.Information($"Stopped Sorc Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
            executeStaticField.Stop();
            executeNova.Stop();
            NextGame.TrySetResult(true);
        }

        private static async Task TeleportToNearbySafeSpot(Client client, CowManager cowManager, Point toLocation)
        {
            bool foundEmptySpot = false;
            foreach (var x in new List<short> { -15, 0, 15, -20, 20, 25 })
            {
                if (foundEmptySpot)
                {
                    break;
                }

                foreach (var y in new List<short> { -15, 0, 15, -20, 20, 25 })
                {
                    var distance = Math.Sqrt(Math.Pow(x,2) + Math.Pow(y,2));
                    if (distance == 0 || distance > 25)
                    {
                        continue;
                    }

                    var tryLocation = toLocation.Add(x, y);
                    if (cowManager.GetNearbyAliveMonsters(tryLocation, 10.0, 5).Count() < 5 && await cowManager.IsVisitable(client, tryLocation))
                    {
                        Log.Information($"Client {client.Game.Me.Name} found empty spot to teleport to at {tryLocation}");
                        if (!client.Game.IsInGame() || GeneralHelpers.TryWithTimeout((retryCount) => client.Game.TeleportToLocation(tryLocation), TimeSpan.FromSeconds(4)))
                        {
                            foundEmptySpot = true;
                            break;
                        }
                    }
                }
            }
        }

        async Task BasicIdleClient(Client client, CowManager cowManager)
        {
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
            }
        }

        async Task BasicFollowClient(Client client, CowManager cowManager)
        {
            SetShouldFollowLead(client, true);
            bool hasUsedPotion = false;
            while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
            {
                if (!hasUsedPotion && client.Game.Me.Effects.Contains(EntityEffect.BattleOrders))
                {
                    client.Game.UseHealthPotion();
                    client.Game.UseHealthPotion();
                    hasUsedPotion = true;
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
                    if(!GeneralHelpers.TryWithTimeout((retryCount) =>
                    {
                        client.Game.MoveTo(item.Location);
                        client.Game.PickupItem(item);
                        return GeneralHelpers.TryWithTimeout((retryCount) =>
                        {
                            Thread.Sleep(50);
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

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == LeadKillingPlayerId);
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
            SetShouldFollowLead(client, true);
            bool hasUsedPotion = false;
            if (shouldBo)
            {
                CastAllShouts(client);
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
                    CastAllShouts(client);
                }

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == LeadKillingPlayerId);
                if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 25)
                {
                    continue;
                }

                var nearbyMonsters = cowManager.GetNearbyAliveMonsters(client, 20, 1);
                if(!nearbyMonsters.Any())
                {
                    await PickupItemsFromPickupList(client, cowManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
                    SetShouldFollowLead(client, true);
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

            Log.Information($"Stopped Barb Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
        }

        private static void CastAllShouts(Client client)
        {

            if (client != null && client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) > 0)
            {
                var nearbyPlayers = client.Game.Players.Where(p => p.Location.Distance(client.Game.Me.Location) < 10);
                if (nearbyPlayers.Any(p => !p.Effects.Contains(EntityEffect.Battlecommand) || !p.Effects.Contains(EntityEffect.BattleOrders) || !p.Effects.Contains(EntityEffect.Shout)))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleCommand, client.Game.Me.Location);
                    Thread.Sleep(500);
                    client.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, client.Game.Me.Location);
                    Thread.Sleep(500);
                    client.Game.UseRightHandSkillOnLocation(Skill.Shout, client.Game.Me.Location);
                }
            }
        }

        async Task PalaFollowClient(Client client, CowManager cowManager)
        {

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

                var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == LeadKillingPlayerId);
                if (!cowManager.GetNearbyAliveMonsters(client, 20, 1).Any() && leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) < 15)
                {
                    await PickupItemsFromPickupList(client, cowManager, 15);
                    await PickupNearbyPotionsIfNeeded(client, cowManager, 15);
                    SetShouldFollowLead(client, true);
                }
            }
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

        private static async Task<bool> MoveToCowLevel(Client client, CowManager cowManager)
        {
            var cowPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).Where(t => t.TownPortalArea == Area.CowLevel).First();
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await client.Game.MoveToAsync(cowPortal);

                client.Game.InteractWithEntity(cowPortal);
                return GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    return client.Game.Area == Area.CowLevel;
                }, TimeSpan.FromSeconds(0.2));
            }, TimeSpan.FromSeconds(15)))
            {
                return false;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);
            foreach(var (x,y) in new List<(short,short)>{ (-3, -3), (3, 3),(-5,0),(5,0),(0,5),(0,-5)})
            {
                var newLocation = client.Game.Me.Location.Add(x, y);
                if (await cowManager.IsInLineOfSight(client, newLocation))
                {
                    await client.Game.MoveToAsync(newLocation);
                    break;
                }
            }

            return true;
        }

        private async Task<bool> JoinGameWithRetry(int gameCount, Client client, Tuple<string, string, string> clientLogin)
        {
            return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                bool joinGame = false;
                try
                {
                    joinGame = client.JoinGame($"{_config.GameNamePrefix}{gameCount}", _config.GamePassword);
                }
                catch
                {
                }

                if (!joinGame)
                {
                    var retryDuration = Math.Pow(1 + retryCount, 1.2) * TimeSpan.FromSeconds(1);
                    Log.Information($"Joining game failed for {client.LoggedInUserName()} retrying in {retryDuration.TotalSeconds} seconds");
                    await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config.Realm, _config.KeyOwner, _config.GameFolder, clientLogin.Item1, clientLogin.Item2, clientLogin.Item3, 10);
                    Thread.Sleep(retryDuration);
                }

                return joinGame;
            }, TimeSpan.FromSeconds(20));
        }

        private async Task<Tuple<bool, int>> CreateGameWithRetry(int gameCount, Client client, Tuple<string, string, string> clientLogin)
        {
            var newGameCount = gameCount;
            var result = await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                bool createGame = false;
                try
                {
                    createGame = client.CreateGame(Difficulty.Hell, $"{_config.GameNamePrefix}{newGameCount}", _config.GamePassword, _config.GameDescriptions[0]);
                }
                catch
                {
                }

                if (!createGame)
                {
                    newGameCount++;
                    var retryDuration = Math.Pow(1 + retryCount, 1.2) * TimeSpan.FromSeconds(1);
                    Log.Information($"Creating game failed for {client.LoggedInUserName()} retrying in {retryDuration.TotalSeconds} seconds");
                    await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config.Realm, _config.KeyOwner, _config.GameFolder, clientLogin.Item1, clientLogin.Item2, clientLogin.Item3, 10);
                    await Task.Delay(retryDuration);
                }

                return createGame;
            }, TimeSpan.FromSeconds(15));
            gameCount = newGameCount;
            return Tuple.Create(result, newGameCount);
        }

        private async Task LeaveGameAndRejoinMCPWithRetry(Client client, Tuple<string, string, string> clientLogin)
        {
            if (!client.Chat.IsConnected())
            {
                await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config.Realm, _config.KeyOwner, _config.GameFolder, clientLogin.Item1, clientLogin.Item2, clientLogin.Item3, 10);
            }

            if (client.Game.IsInGame())
            {
                Log.Information($"Leaving game with {client.LoggedInUserName()}");
                client.Game.LeaveGame();
            }

            if (!client.RejoinMCP())
            {
                Log.Warning($"Disconnecting client {clientLogin.Item1} since reconnecting to MCP failed, reconnecting to realm");
                await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config.Realm, _config.KeyOwner, _config.GameFolder, clientLogin.Item1, clientLogin.Item2, clientLogin.Item3, 10);
            }
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
