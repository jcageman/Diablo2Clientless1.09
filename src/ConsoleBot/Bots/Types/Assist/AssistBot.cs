using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.TownManagement;
using D2NG.Core;
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Assist
{
    public class AssistBot : IBotInstance
    {
        private bool ShouldStop = false;
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;
        private readonly ITownManagementService _townManagementService;
        private readonly IAttackService _attackService;
        private readonly AssistConfiguration _assistConfig;

        public AssistBot(
            IOptions<BotConfiguration> config,
            IOptions<AssistConfiguration> assistConfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMapApiService mapApiService,
            ITownManagementService townManagementService,
            IAttackService attackService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _mapApiService = mapApiService;
            _assistConfig = assistConfig.Value;
            _townManagementService = townManagementService;
            _attackService = attackService;
        }

        public string GetName()
        {
            return "assist";
        }

        public async Task Run()
        {
            if(ShouldStop)
            {
                Log.Information($"Stopped bot due to receiving stop message");
                await Task.Delay(TimeSpan.FromMinutes(1));
                return;
            }

            _assistConfig.Validate();
            var clients = new List<Tuple<AccountConfig, Client, AssistBotClientState>>();
            foreach (var account in _assistConfig.Accounts)
            {
                var client = new Client();
                var accountAndClient = Tuple.Create(account, client, new AssistBotClientState());
                if (IsHostClient(account))
                {
                    client.OnReceivedPacketEvent(InComingPacket.PlayerInGame, (packet) => NewPlayerJoinGame(client, new PlayerInGamePacket(packet)));
                }

                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                client.OnReceivedPacketEvent(InComingPacket.ReceiveChat, (packet) =>
                {
                    var chatPacket = new ChatPacket(packet);
                    if (chatPacket.Message == "stop")
                    {
                        accountAndClient.Item3.ShouldStop = true;
                        Log.Information($"{accountAndClient.Item1.Character} Stopping bot due to receiving stop message");
                    }
                    else if (chatPacket.Message == "ng")
                    {
                        accountAndClient.Item3.NextGame = true;
                        Log.Information($"{accountAndClient.Item1.Character} Going next game");
                    }
                    else if (chatPacket.Message == "nofollow")
                    {
                        accountAndClient.Item3.ShouldFollow = false;
                        Log.Information($"{accountAndClient.Item1.Character} Stopping follow");
                    }
                    else if (chatPacket.Message == "follow")
                    {
                        accountAndClient.Item3.ShouldFollow = true;
                        Log.Information($"{accountAndClient.Item1.Character} Starting follow");
                    }
                    else if (chatPacket.Message == "heal")
                    {
                        accountAndClient.Item3.ShouldHeal = true;
                        Log.Information($"{accountAndClient.Item1.Character} Starting healing if in town");
                    }
                    else if (chatPacket.Message == "town")
                    {
                        accountAndClient.Item3.ShouldGoToTown = true;
                        Log.Information($"{accountAndClient.Item1.Character} Going to town");
                    }
                    else if (chatPacket.Message == "nextlevel")
                    {
                        accountAndClient.Item3.GoNextLevel = true;
                        Log.Information($"{accountAndClient.Item1.Character} Going to next level");
                    }
                    else if (chatPacket.Message.StartsWith("towp"))
                    {
                        var parsedMessage = chatPacket.Message[5..];
                        if (Enum.TryParse<Waypoint>(parsedMessage, out var waypoint))
                        {
                            accountAndClient.Item3.GoToWaypoint = waypoint;
                            Log.Information($"{accountAndClient.Item1.Character} Going to area waypoint if character has it");
                        }
                        else
                        {
                            Log.Error($"{parsedMessage} Does not exist");
                        }
                    }

                });
                _externalMessagingClient.RegisterClient(client);
                clients.Add(accountAndClient);
            }

            int gameCount = 1;

            while (!clients.All(c => c.Item3.ShouldStop))
            {
                var activeClients = clients.Where(c => !c.Item3.ShouldStop);
                var leaveTasks = activeClients.Select(async (c, i) =>
                {
                    c.Item3.NextGame = false;
                    return await LeaveGameAndRejoinMCPWithRetry(c.Item2, c.Item1);
                }).ToList();
                var leaveResults = await Task.WhenAll(leaveTasks);
                if (leaveResults.Any(r => !r))
                {
                    Log.Warning($"One or more characters failed to leave and rejoin");
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));

                var leadClient = activeClients.FirstOrDefault(c => IsHostClient(c.Item1));
                if (leadClient != null)
                {
                    var result = await RealmConnectHelpers.CreateGameWithRetry(gameCount, leadClient.Item2, _config, leadClient.Item1);
                    gameCount = result.Item2;
                    if (!result.Item1)
                    {
                        continue;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }

                await GameLoop(activeClients.ToList(), gameCount);
                gameCount++;
            }

            if (clients.Any(c => c.Item3.ShouldStop))
            {
                ShouldStop = true;
                await LeaveGameAndDisconnectWithAllClients(clients.Select(c => c.Item2).ToList());
            }
        }

        private bool IsHostClient(AccountConfig accountCharacter)
        {
            return accountCharacter.Character.Equals(_assistConfig.HostCharacterName, StringComparison.CurrentCultureIgnoreCase);
        }

        private bool IsLeadClient(AccountConfig accountCharacter)
        {
            return accountCharacter.Character.Equals(_assistConfig.LeadCharacterName, StringComparison.CurrentCultureIgnoreCase);
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

        private async Task<bool> LeaveGameAndRejoinMCPWithRetry(Client client, AccountConfig cowAccount)
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

        public async Task GameLoop(List<Tuple<AccountConfig, Client, AssistBotClientState>> clients, int gameCount)
        {
            var nextGameCancellation = new CancellationTokenSource();
            var gameTasks = clients.Select(async (c, i) =>
            {
                if (!IsHostClient(c.Item1))
                {
                    var numberOfSecondsToWait = (i > 3 ? 15 : 0);
                    Log.Information($"Waiting {numberOfSecondsToWait} seconds for joining game with {c.Item1.Character}");
                    await Task.Delay(TimeSpan.FromSeconds(numberOfSecondsToWait));
                    Log.Information($"Starting joining game with {c.Item1.Character}");
                    if (!await RealmConnectHelpers.JoinGameWithRetry(gameCount, c.Item2, _config, c.Item1))
                    {
                        return false;
                    }
                }

                Log.Information("In game");
                var client = c.Item2;
                client.Game.RequestUpdate(client.Game.Me.Id);
                if (!await GeneralHelpers.TryWithTimeout(
                    async (_) =>
                    {
                        await Task.Delay(100);
                        return client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0;
                    },
                    TimeSpan.FromSeconds(10)))
                {
                    return false;
                }

                var townManagementOptions = new TownManagementOptions(c.Item1, client.Game.Act)
                {
                    HealthPotionsToBuy = Math.Max(0, client.Game.Belt.Height * 2 + 10 - InventoryHelpers.GetTotalHealthPotions(client.Game)),
                    ManaPotionsToBuy = Math.Max(0, client.Game.Belt.Height * 2 + 5 - InventoryHelpers.GetTotalManaPotions(client.Game))
                };

                var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
                if (!townTaskResult.Succes)
                {
                    return false;
                }

                Log.Information($"Starting {client.Game.Me.Name} with life {client.Game.Me.Life} out of {client.Game.Me.MaxLife}" +
                    $" and {InventoryHelpers.GetTotalHealthPotions(client.Game)} healthpotions and {InventoryHelpers.GetTotalManaPotions(client.Game)} mana potions");

                var leadPlayer = GetLeadPlayer(client);
                if (!await GeneralHelpers.TryWithTimeout(
                    async (i) =>
                    {
                        await Task.Delay(100);
                        leadPlayer = GetLeadPlayer(client);
                        return leadPlayer != null;
                    },
                    TimeSpan.FromSeconds(60)))
                {
                    Log.Warning($"LeadPlayer {_assistConfig.LeadCharacterName} not found, restarting");
                    return false;
                }

                if (!await GeneralHelpers.TryWithTimeout(
                    async (_) =>
                    {
                        await Task.Delay(100);
                        if (i > 1 && i % 10 == 0 && client.Game.IsInGame())
                        {
                            client.Game.SendInGameMessage($"{client.Game.Me.Name} i am still waiting for invite");
                        }
                        leadPlayer = GetLeadPlayer(client);
                        return leadPlayer != null && leadPlayer.Area != null;
                    },
                    TimeSpan.FromSeconds(60)))
                {
                    Log.Warning($"LeadPlayer {_assistConfig.LeadCharacterName} found, but no area found");
                    return false;
                }

                foreach(var player in client.Game.Players)
                {
                    if(player.Id != client.Game.Me.Id)
                    {
                        client.Game.AllowLootCorpse(player);
                    }
                }

                while (!c.Item3.ShouldStop && !c.Item3.NextGame && !nextGameCancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                    if(client.Game.IsInGame())
                    {
                        await AssistLeadClient(client, c.Item1, c.Item3);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        if (await client.RejoinMCP() || await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, c.Item1, 10))
                        {
                            await RealmConnectHelpers.JoinGameWithRetry(gameCount, c.Item2, _config, c.Item1);
                        }
                    }
                    
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

        async Task<bool> AssistLeadClient(Client client, AccountConfig accountConfig, AssistBotClientState state)
        {
            var movementMode = GetMovementMode(client.Game);
            if (state.GoNextLevel)
            {
                return await GoNextLevel(client, state, movementMode);
            }
            else if(state.GoToWaypoint != null)
            {
                return await GoToAreaWaypoint(client, state, movementMode);
            }
            else if(state.ShouldGoToTown)
            {
                if (!client.Game.IsInTown())
                {
                    if (!await _townManagementService.TakeTownPortalToTown(client))
                    {
                        Log.Warning($"Failed to take town portal to Town");
                        return false;
                    }
                }
                state.ShouldGoToTown = false;
            }
            else if(state.ShouldHeal)
            {
                return await HealInTown(client, accountConfig, state);
            }

            if (!state.ShouldHeal && InventoryHelpers.GetTotalHealthPotions(client.Game) < 3
                || InventoryHelpers.GetTotalManaPotions(client.Game) == 0
                || NPCHelpers.ShouldGoToRepairNPC(client.Game))
            {
                Log.Information($"{client.Game.Me.Name} is low on potions or needs repairs");
                state.ShouldHeal = true;
            }

            if (client.Game.IsInTown() && !state.ShouldFollow)
            {
                return true;
            }

            var leadPlayer = GetLeadPlayer(client);
            if (state.ShouldFollow && !await GeneralHelpers.TryWithTimeout(
            async (_) =>
            {
                return await GetToLeadArea(client, leadPlayer);
            },
            TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Could not move to lead area");
                return false;
            }

            if (state.ShouldFollow && leadPlayer.Location.Distance(client.Game.Me.Location) > 20)
            {
                var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, leadPlayer.Location, movementMode, cts.Token);
            }
            else
            {
                if (!client.Game.IsInTown())
                {
                    if (state.ShouldFollow)
                    {
                        await _attackService.AssistPlayer(client, leadPlayer);
                    }
                    else
                    {
                        await _attackService.AssistPlayer(client, client.Game.Me);
                    }

                    if (!NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, 20).Any())
                    {
                        await PickupNearbyItems(client);
                        await PickupNearbyPotionsIfNeeded(client);
                    }
                }
            }

            return true;
        }

        private async Task<bool> HealInTown(Client client, AccountConfig accountConfig, AssistBotClientState state)
        {
            if (!client.Game.IsInTown())
            {
                if (!await _townManagementService.TakeTownPortalToTown(client))
                {
                    Log.Warning($"Failed to take town portal to Town");
                    return false;
                }
            }

            var townManagementOptions = new TownManagementOptions(accountConfig, client.Game.Act)
            {
                HealthPotionsToBuy = Math.Max(0, client.Game.Belt.Height * 2 + 10 - InventoryHelpers.GetTotalHealthPotions(client.Game)),
                ManaPotionsToBuy = Math.Max(0, client.Game.Belt.Height * 2 + 5 - InventoryHelpers.GetTotalManaPotions(client.Game))
            };
            var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
            if (!townTaskResult.Succes)
            {
                return false;
            }

            state.ShouldHeal = false;
            return true;
        }

        private async Task<bool> GoToAreaWaypoint(Client client, AssistBotClientState state, MovementMode movementMode)
        {
            var currentArea = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, client.Game.Area);
            if (currentArea == null)
            {
                return false;
            }

            if(client.Game.IsInTown())
            {
                var townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).Single();
                Log.Information($"Taking waypoint to {state.GoToWaypoint}");
                if (!GeneralHelpers.TryWithTimeout((_) =>
                {
                    client.Game.TakeWaypoint(townWaypoint, state.GoToWaypoint.Value);
                    return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == state.GoToWaypoint.Value.ToArea(), TimeSpan.FromSeconds(2));
                }, TimeSpan.FromSeconds(5)))
                {
                    Log.Error($"Taking waypoint to to {state.GoToWaypoint} waypoint failed");
                    return false;
                }
            }
            else
            {
                var waypoint = client.Game.Area.ToWaypoint();
                if (waypoint != null)
                {
                    var entityCode = waypoint.Value.ToEntityCode();
                    var pathToWaypoint = await _pathingService.GetPathToObject(client.Game, entityCode, movementMode);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToWaypoint, movementMode))
                    {
                        Log.Warning($"Teleporting to {waypoint} waypoint failed at location {client.Game.Me.Location}");
                        return false;
                    }
                }
            }

            state.GoToWaypoint = null;
            state.ShouldGoToTown = true;
            return true;
        }

        private async Task<bool> GoNextLevel(Client client, AssistBotClientState state, MovementMode movementMode)
        {
            var currentArea = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, client.Game.Area);
            if (currentArea == null)
            {
                return false;
            }
            var entry = currentArea.AdjacentLevels.FirstOrDefault(a => a.Key > client.Game.Area && a.Value.Exits?.Count > 0);
            if (entry.Value != null)
            {
                if (!await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, entry.Value.Exits[0], movementMode))
                {
                    return false;
                }

                var warp = client.Game.GetNearestWarp();
                if (warp != null && warp.Location.Distance(client.Game.Me.Location) < 20)
                {
                    await MovementHelpers.TakeWarp(client.Game, _pathingService, _mapApiService, movementMode, warp, entry.Key);
                }
            }

            state.GoNextLevel = false;
            state.ShouldGoToTown = true;
            return true;
        }

        private async Task<bool> PickupNearbyItems(Client client)
        {
            var pickupItems = client.Game.Items.Values
                .Where(i => i.Ground
                && Pickit.GoldItems.ShouldPickupItem(i)
                && i.Name != ItemName.FlawlessDiamond
                && i.Name != ItemName.FlawlessSkull
                && i.Name != ItemName.FlawlessRuby
                && i.Classification != ClassificationType.Essence)
                .Where(n => n.Location.Distance(client.Game.Me.Location) < 20)
                .OrderBy(n => n.Location.Distance(client.Game.Me.Location))
                .ToList();

            if (pickupItems.Count > 0)
            {
                Log.Information($"Killed Nearby monsters, picking up {pickupItems.Count()} items ");
            }

            foreach (var item in pickupItems)
            {
                if (item.Location.Distance(client.Game.Me.Location) > 20)
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

                if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                {
                    if (client.Game.Me.Location.Distance(item.Location) >= 5)
                    {
                        if (GetMovementMode(client.Game) == MovementMode.Teleport)
                        {
                            await client.Game.TeleportToLocationAsync(item.Location);
                        }
                        else
                        {
                            await client.Game.MoveToAsync(item.Location);
                        }
                        return false;
                    }
                    else
                    {
                        client.Game.PickupItem(item);
                        await Task.Delay(50);
                        if (client.Game.Inventory.FindItemById(item.Id) == null && !item.IsGold)
                        {
                            return false;
                        }
                    }

                    return true;
                }, TimeSpan.FromSeconds(3)))
                {
                    Log.Warning($"Picking up item {item.GetFullDescription()} at location {item.Location} from location {client.Game.Me.Location} failed");
                }
            }

            return true;
        }

        private async Task PickupNearbyPotionsIfNeeded(Client client)
        {
            var missingHealthPotions = 10 + client.Game.Belt.Height * 2 - InventoryHelpers.GetTotalHealthPotions(client.Game);
            var missingManaPotions = 5 + client.Game.Belt.Height * 2 - InventoryHelpers.GetTotalManaPotions(client.Game);
            var missingRevPotions = Math.Max(6 - client.Game.Inventory.Items.Count(i => i.Classification == ClassificationType.RejuvenationPotion), 0);
            var pickitList = client.Game.Items.Values
                .Where(i => i.Ground && i.Classification == ClassificationType.HealthPotion &&
                    i.Location.Distance(client.Game.Me.Location) < 20)
                .OrderByDescending(i => (int)i.Name)
                .Take((int)missingHealthPotions)
                .ToList();

            pickitList.AddRange(client.Game.Items.Values
                .Where(i => i.Ground && i.Classification == ClassificationType.ManaPotion &&
                    i.Location.Distance(client.Game.Me.Location) < 20)
                .OrderByDescending(i => (int)i.Name)
                .Take((int)missingManaPotions)
                .ToList());

            pickitList.AddRange(client.Game.Items.Values
                .Where(i => i.Ground && i.Classification == ClassificationType.RejuvenationPotion &&
                    i.Location.Distance(client.Game.Me.Location) < 20)
                .OrderByDescending(i => (int)i.Name)
                .Take((int)missingRevPotions)
                .ToList());

            foreach (var item in pickitList)
            {
                if (item.Ground && client.Game.Inventory.HasAnyFreeSpace())
                {
                    Log.Information($"Client {client.Game.Me.Name} picking up {item.Name}");
                    await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        if (!await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, item.Location, GetMovementMode(client.Game)))
                        {
                            return false;
                        }
                        client.Game.PickupItem(item);
                        return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                        {
                            await Task.Delay(50);
                            return (client.Game.Items.TryGetValue(item.Id, out var newItem) && !newItem.Ground) || client.Game.Belt.FindItemById(item.Id) != null || client.Game.Inventory.FindItemById(item.Id) != null;
                        }, TimeSpan.FromSeconds(0.2));
                    }, TimeSpan.FromSeconds(3));
                }
            }

            //Log.Information($"Client {client.Game.Me.Name} got {client.Game.Belt.NumOfHealthPotions()} healthpotions and {client.Game.Belt.NumOfManaPotions()} mana");
        }

        private static MovementMode GetMovementMode(Game game)
        {
            return game.Me.HasSkill(Skill.Teleport) && game.Me.Attributes.TryGetValue(D2NG.Core.D2GS.Players.Attribute.Level, out var level) && level > 30 ? MovementMode.Teleport : MovementMode.Walking;
        }

        private async Task<bool> GetToLeadArea(Client client, Player leadPlayer)
        {
            if (!leadPlayer.Act.HasValue || !leadPlayer.Area.HasValue)
            {
                return false;
            }

            var leadPlayerAct = leadPlayer.Act.Value;
            var leadPlayerArea = leadPlayer.Area.Value;
            var clientAct = client.Game.Act;
            var clientArea = client.Game.Area;
            if (!await _townManagementService.SwitchAct(client, leadPlayerAct))
            {
                Log.Warning($"Failed to switch act");
                return false;
            }

            leadPlayerArea = await _mapApiService.GetAreaFromLocation(client.Game.MapId, Difficulty.Normal, leadPlayer.Location, leadPlayerAct, leadPlayerArea) ?? leadPlayerArea;
            clientArea = await _mapApiService.GetAreaFromLocation(client.Game.MapId, Difficulty.Normal, client.Game.Me.Location, clientAct, clientArea) ?? clientArea;
            if (leadPlayerArea != clientArea && leadPlayerArea != WayPointHelpers.MapTownArea(client.Game.Act))
            {
                var movementMode = GetMovementMode(client.Game);
                var warp = client.Game.GetNearestWarp();
                if (warp != null && warp.Location.Distance(client.Game.Me.Location) < 20)
                {
                    if (!await MovementHelpers.TakeWarp(client.Game, _pathingService, _mapApiService, movementMode, warp, leadPlayerArea))
                    {
                        return false;
                    }
                }
            }

            var redPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal)
                .FirstOrDefault(t => t.TownPortalArea == leadPlayerArea);
            if (redPortal != null)
            {
                Log.Information($"Found red portal going to lead player area {leadPlayerArea}");
                if (!await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, redPortal.Location, GetMovementMode(client.Game)))
                {
                    return false;
                }

                await client.Game.MoveToAsync(redPortal);
                client.Game.InteractWithEntity(redPortal);
                if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(100);
                    return client.Game.Area == redPortal.TownPortalArea;
                }, TimeSpan.FromSeconds(0.5)))
                {
                    return false;
                }

                return true;
            }

            if (leadPlayerArea != clientArea && leadPlayer.Location.Distance(client.Game.Me.Location) <= 50)
            {
                Log.Information($"Noticed area transition from {clientArea} to {leadPlayerArea}");
                var distance = leadPlayer.Location.Distance(client.Game.Me.Location);
                var targetPoint = client.Game.Me.Location.GetPointBeforePointInSameDirection(leadPlayer.Location, Math.Max(0, distance - 10));
                await client.Game.MoveToAsync(targetPoint);
            }
            else if (leadPlayerArea != clientArea && leadPlayer.Location.Distance(client.Game.Me.Location) > 50)
            {
                var targetTownArea = WayPointHelpers.MapTownArea(client.Game.Act);
                if (clientArea != targetTownArea)
                {
                    if (!await _townManagementService.TakeTownPortalToTown(client))
                    {
                        Log.Warning($"Failed to take town portal to {targetTownArea}");
                        return false;
                    }
                }

                if (!await _townManagementService.TakeTownPortalToArea(client, leadPlayer, leadPlayerArea))
                {
                    Log.Warning($"Failed to take town portal to {leadPlayerArea}");
                    return false;
                }
            }

            return true;
        }

        Player GetLeadPlayer(Client client)
        {
            return client.Game.Players.FirstOrDefault(p => p.Name.Equals(_assistConfig.LeadCharacterName, StringComparison.CurrentCultureIgnoreCase));
        }

        static void NewPlayerJoinGame(Client client, PlayerInGamePacket playerInGamePacket)
        {
            var relevantPlayer = client.Game.Players.Where(p => p.Id == playerInGamePacket.Id).FirstOrDefault();
            client.Game.InvitePlayer(relevantPlayer);
        }

        static void HandleEventMessage(Client client, EventNotifyPacket eventNotifyPacket)
        {
            if (eventNotifyPacket.PlayerRelationType == PlayerRelationType.InvitesYouToParty)
            {
                var relevantPlayer = client.Game.Players.Where(p => p.Id == eventNotifyPacket.EntityId).FirstOrDefault();
                client.Game.AcceptInvite(relevantPlayer);
            }
        }
    }
}
