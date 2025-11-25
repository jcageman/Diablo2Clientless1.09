using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
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

namespace ConsoleBot.Bots
{
    public abstract class MultiClientBotBase : IBotInstance
    {
        protected readonly BotConfiguration _config;
        protected readonly IExternalMessagingClient _externalMessagingClient;
        protected readonly IMuleService _muleService;
        protected readonly IPathingService _pathingService;
        private readonly MultiClientConfiguration _multiClientConfig;
        protected TaskCompletionSource<bool> NextGame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, ManualResetEvent> PlayersInGame = new();
        protected HashSet<string> ClientsNeedingMule = [];
        private readonly ConcurrentDictionary<uint, Item> _pickitItemsOnGround = new();
        private readonly ConcurrentDictionary<uint, Item> _pickitPotionsOnGround = new();

        public MultiClientBotBase(IOptions<BotConfiguration> config, IOptions<MultiClientConfiguration> multiClientConfig,
            IExternalMessagingClient externalMessagingClient, IMuleService muleService, IPathingService pathingService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _muleService = muleService;
            _pathingService = pathingService;
            _multiClientConfig = multiClientConfig.Value;
        }

        public abstract string GetName();

        public async Task Run()
        {
            _multiClientConfig.Validate();
            var clients = new List<Client>();
            foreach (var account in _multiClientConfig.Accounts)
            {
                var client = new Client();
                client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
                client.Game.OnWorldItemEvent(i => HandleItemDrop(client.Game, i));
                _externalMessagingClient.RegisterClient(client);
                PostInitializeClient(client, account);
                PlayersInGame.TryAdd(account.Character.ToLower(), new ManualResetEvent(false));
                clients.Add(client);
            }

            var firstFiller = clients.First();
            firstFiller.OnReceivedPacketEvent(InComingPacket.PlayerInGame, (packet) => PlayerInGame(firstFiller, new PlayerInGamePacket(packet).Name));
            firstFiller.OnReceivedPacketEvent(InComingPacket.AssignPlayer, (packet) => PlayerInGame(firstFiller, new AssignPlayerPacket(packet).Name));
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
                _pickitItemsOnGround.Clear();
                _pickitPotionsOnGround.Clear();
                foreach (var playerInGame in PlayersInGame)
                {
                    playerInGame.Value.Reset();
                }

                NextGame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Log.Information($"Joining next game {_config.GameNamePrefix}{gameCount}");

                try
                {
                    var leaveAndRejoinTasks = clients.Select(async (client, index) =>
                    {
                        var account = _multiClientConfig.Accounts[index];
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
                    if(_multiClientConfig.ShouldCreateGames)
                    {
                        var result = await RealmConnectHelpers.CreateGameWithRetry(gameCount, firstFiller, _config, _multiClientConfig.Accounts.First());
                        gameCount = result.Item2;
                        if (!result.Item1)
                        {
                            gameCount++;
                            await Task.Delay(TimeSpan.FromSeconds(60));
                            continue;
                        }
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
                    var prepareTasks = new List<Task<bool>>();
                    for (int i = 0; i < clients.Count; i++)
                    {
                        var account = _multiClientConfig.Accounts[i];
                        var client = clients[i];
                        var numberOfSecondsToWait = i > 2 ? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
                        prepareTasks.Add(InternalPrepareForRun(client, account, numberOfSecondsToWait, gameCount));
                    }

                    var townResults = await Task.WhenAll(prepareTasks);
                    if (townResults.Any(r => !r))
                    {
                        var failedAccounts = string.Join(",", townResults.Select((r, idx) => (clients[idx], r)).Where(i => !i.r).Select(c => c.Item1.LoggedInUserName()));
                        Log.Warning($"One or more accounts {failedAccounts} failed there join or prepare task");
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

                await Task.WhenAny(PostInitializeAllJoined(clients), Task.Delay(TimeSpan.FromSeconds(2)));

                foreach (var player in firstFiller.Game.Players)
                {
                    if (firstFiller.Game.Me.Id == player.Id)
                    {
                        continue;
                    }

                    firstFiller.Game.InvitePlayer(player);
                }

                try
                {
                    var clientTasks = new List<Task<bool>>();
                    for (int i = 0; i < clients.Count; i++)
                    {
                        clientTasks.Add(PerformRun(clients[i], _multiClientConfig.Accounts[i]));
                    }

                    var clientResults = await Task.WhenAll(clientTasks);
                    if (clientResults.Any(r => !r))
                    {
                        Log.Warning($"One or more characters failed there run task");
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Failed one or more tasks with exception {e}");
                }

                Log.Information($"Going to next game");
                gameCount++;
            }
        }

        protected virtual Task PostInitializeAllJoined(List<Client> clients)
        {
            return Task.CompletedTask;
        }

        private async Task<bool> InternalPrepareForRun(Client client, AccountConfig account, TimeSpan waitToJoinTime, int gameCount)
        {
            await Task.Delay(waitToJoinTime);
            if (!client.Game.IsInGame() && !await RealmConnectHelpers.JoinGameWithRetry(gameCount, client, _config, account))
            {
                Log.Warning($"Client {client.LoggedInUserName()} failed to join game, retrying new game");
                return false;
            }

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

            return await PrepareForRun(client, account);
        }

        protected async Task PickupItemsAndPotions(Client client, AccountConfig account, double distance)
        {
            await PickupItemsFromPickupList(client, distance);
            await PickupNearbyPotionsIfNeeded(client, account, distance);
        }

        protected async Task<bool> IsNextGame()
        {
            return NextGame.Task == await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.05)), NextGame.Task);
        }

        protected virtual void PostInitializeClient(Client client, AccountConfig accountCharacter)
        {

        }

        protected virtual void ResetForNextRun()
        {

        }

        protected abstract Task<bool> PrepareForRun(Client client, AccountConfig account);

        protected abstract Task<bool> PerformRun(Client client, AccountConfig account);

        private static async Task LeaveGameAndDisconnectWithAllClients(List<Client> clients)
        {
            foreach (var client in clients)
            {
                if (client.Game.IsInGame())
                {
                    await client.Game.LeaveGame();
                }
                client.Disconnect();
            }
        }

        private async Task<bool> LeaveGameAndRejoinMCPWithRetry(Client client, AccountConfig account)
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

            if (!await client.RejoinMCP())
            {
                Log.Warning($"Disconnecting client {account.Username} since reconnecting to MCP failed, reconnecting to realm");
                return await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, account, 10);
            }

            return true;
        }

        private static void HandleEventMessage(Client client, EventNotifyPacket eventNotifyPacket)
        {
            if (eventNotifyPacket.PlayerRelationType == PlayerRelationType.InvitesYouToParty)
            {
                var relevantPlayer = client.Game.Players.Where(p => p.Id == eventNotifyPacket.EntityId).FirstOrDefault();
                client.Game.AcceptInvite(relevantPlayer);
            }
        }

        private void PlayerInGame(Client client, string characterName)
        {
            if (PlayersInGame.TryGetValue(characterName.ToLower(), out var oldValue))
            {
                PlayersInGame.TryUpdate(characterName.ToLower(), new ManualResetEvent(true), oldValue);
                var relevantPlayer = client.Game.Players.Where(p => p.Name == characterName).FirstOrDefault();
                client.Game.InvitePlayer(relevantPlayer);
            }
            else
            {
                var relevantPlayer = client.Game.Players.Where(p => p.Name == characterName).FirstOrDefault();
                client.Game.InvitePlayer(relevantPlayer);
            }
        }

        private Task HandleItemDrop(Game game, Item item)
        {
            if (!item.Ground)
            {
                return Task.CompletedTask;
            }

            if (Pickit.Pickit.ShouldPickupItem(game, item, false))
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }

            if (item.Name == ItemName.RejuvenationPotion || item.Name == ItemName.FullRejuvenationPotion || item.Name == ItemName.SuperHealingPotion || item.Name == ItemName.SuperManaPotion)
            {
                _pickitPotionsOnGround.TryAdd(item.Id, item);
            }

            return Task.CompletedTask;
        }

        private void PutItemOnPickitList(Client client, Item item)
        {
            if (Pickit.Pickit.ShouldPickupItem(client.Game, item, false)
                && client.Game.Items.TryGetValue(item.Id, out var newItem)
                && newItem.Ground)
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
        }
        private void PutRejuvenationOnPickitList(Item item)
        {
            if (item.IsPotion && item.Ground)
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
        }

        private List<Item> GetPickitList(Client client, double distance)
        {
            var resultPickitList = new List<Item>();
            var listItems = _pickitItemsOnGround.Values.Where(i => client.Game.Me.Location.Distance(i.Location) < distance).ToList();
            foreach (var tryItem in listItems)
            {
                if (_pickitItemsOnGround.TryRemove(tryItem.Id, out var item))
                {
                    resultPickitList.Add(item);
                    if (resultPickitList.Count == 2)
                    {
                        break;
                    }
                }
            }

            return resultPickitList;
        }

        private List<Item> GetPotionPickupList(Client client, double distance, int nofRevPotions, int nofHealthPotions, int nofManaPotions)
        {
            var resultPickitList = new List<Item>();
            resultPickitList.AddRange(TakePotionsOfType(client, distance, nofRevPotions, ClassificationType.RejuvenationPotion));
            resultPickitList.AddRange(TakePotionsOfType(client, distance, nofHealthPotions, ClassificationType.HealthPotion));
            resultPickitList.AddRange(TakePotionsOfType(client, distance, nofManaPotions, ClassificationType.ManaPotion));
            return resultPickitList;
        }

        private List<Item> TakePotionsOfType(Client client, double distance, int nofPotions, ClassificationType classificationType)
        {
            var resultPickitList = new List<Item>();
            var potionsToTryPick = _pickitPotionsOnGround.Values.Where(i =>
            client.Game.Me.Location.Distance(i.Location) < distance
            && i.Classification == classificationType).ToList();
            foreach (var tryItem in potionsToTryPick)
            {
                if (resultPickitList.Count >= nofPotions)
                {
                    break;
                }

                if (_pickitPotionsOnGround.TryRemove(tryItem.Id, out var item))
                {
                    resultPickitList.Add(item);
                }
            }
            return resultPickitList;
        }

        protected async Task MoveToLocation(Client client, Point location, CancellationToken? token = null)
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

        private async Task PickupNearbyPotionsIfNeeded(Client client, AccountConfig account, double distance)
        {
            var totalRejuvanationPotions = client.Game.Inventory.Items.Count(i => i.Name == ItemName.RejuvenationPotion || i.Name == ItemName.FullRejuvenationPotion);

            var missingHealthPotions = (int)client.Game.Belt.Height * account.HealthSlots.Count - client.Game.Belt.GetHealthPotionsInSlots(account.HealthSlots).Count;
            var missingManaPotions = (int)client.Game.Belt.Height * account.ManaSlots.Count - client.Game.Belt.GetManaPotionsInSlots(account.ManaSlots).Count;
            var missingRevPotions = Math.Max(6 - client.Game.Inventory.Items.Count(i => i.Name == ItemName.FullRejuvenationPotion || i.Name == ItemName.RejuvenationPotion), 0);
            //Log.Information($"Client {client.Game.Me.Name} missing {missingHealthPotions} healthpotions and missing {missingManaPotions} mana");
            var pickitList = GetPotionPickupList(client, distance, missingRevPotions, missingHealthPotions, missingManaPotions);
            foreach (var item in pickitList)
            {
                if(await IsNextGame() && item.Classification != ClassificationType.RejuvenationPotion)
                {
                    continue;
                }

                if (client.Game.Me.HasSkill(Skill.Vigor))
                {
                    client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
                }
                Log.Information($"Client {client.Game.Me.Name} picking up {item.Name}");
                await MoveToLocation(client, item.Location);
                if (item.Ground)
                {
                    if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        await client.Game.MoveToAsync(item.Location);
                        client.Game.PickupItem(item);
                        return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                        {
                            await Task.Delay(50);
                            return client.Game.Belt.FindItemById(item.Id) != null;
                        }, TimeSpan.FromSeconds(0.2));
                    }, TimeSpan.FromSeconds(3)))
                    {
                        PutRejuvenationOnPickitList(item);
                    }
                }
            }
        }

        private async Task PickupItemsFromPickupList(Client client, double distance)
        {
            var maxPicks = 3;
            var picks = 0;
            var pickitList = new List<Item>();
            do
            {
                picks++;
                pickitList = pickitList = GetPickitList(client, distance);
                foreach (var item in pickitList)
                {
                    if (client.Game.Me.HasSkill(Skill.Vigor))
                    {
                        client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
                    }
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
                            PutItemOnPickitList(client, item);
                        }
                    }
                }
            }
            while (pickitList.Count != 0 && picks < maxPicks);
        }
    }
}
