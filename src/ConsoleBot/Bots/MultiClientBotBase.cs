using ConsoleBot.Chicken;
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

namespace ConsoleBot.Bots;

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
    private readonly SpatialGrid<Item> _pickitItemsOnGround = new();
    private readonly ConcurrentDictionary<uint, int> _pickitAttempts = new();
    private readonly ConcurrentDictionary<uint, bool> _pickedUp = new();
    private readonly ConcurrentDictionary<uint, int> _pickitReAdds = new();

    /// <summary>
    /// How many times a single item is put back on the list after a failed pickup. Enough that an
    /// item merely out of sight survives, few enough that one that genuinely cannot be taken stops
    /// the character retrying it forever instead of moving on.
    /// </summary>
    private const int MaxPickupAttempts = 3;

    /// <summary>
    /// How many times an item is put back regardless of why the pickup failed, so one that can
    /// never be reached at all is eventually left rather than retried for the whole game.
    /// </summary>
    private const int MaxPickupReAdds = 12;

    /// <summary>
    /// Close enough that a failed pickup is about the item rather than about the walk. Measured
    /// failures land in two groups: right on top of the item at nought to seven units, or fifty to
    /// a hundred and seventy away having never arrived. At five this counted the first group as
    /// walks and let them run to the loose bound - thirteen round trips for an item instead of
    /// three.
    /// </summary>
    private const double WithinReach = 12;

    /// <summary>How far a character will go for a rejuvenation, whatever its normal pickup range.</summary>
    private const double RejuvenationPickupRadius = 60;

    /// <summary>How many full rejuvenations a character will carry. They are the best thing it can
    /// be holding when something goes wrong, so they displace lesser potions rather than queue
    /// behind them.</summary>
    private const int FullRejuvenationsWanted = 6;
    private readonly SpatialGrid<Item> _pickitPotionsOnGround = new();
    private readonly List<Client> _clients = [];

    /// <summary>
    /// How much closer another client has to be before a drop is left to it. Without a margin the
    /// two nearest clients hand the same item back and forth.
    /// </summary>
    private const double ItemClaimMargin = 5;

    /// <summary>
    /// How long a drop is reserved for the client nearest to it. After this it goes to whoever can
    /// reach it, because the nearest client may be one that is never going to walk over: it could
    /// be fighting, or only looting around its group.
    /// </summary>
    private static readonly TimeSpan ItemClaimGrace = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<uint, DateTime> _itemFirstSeen = new();

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
            ChickenService.Attach(client, account.Chicken ?? _config.Chicken);
            client.OnReceivedPacketEvent(InComingPacket.EventMessage, (packet) => HandleEventMessage(client, new EventNotifyPacket(packet)));
            client.Game.OnWorldItemEvent(i => HandleItemDrop(client.Game, i));
            _externalMessagingClient.RegisterClient(client);
            PostInitializeClient(client, account);
            PlayersInGame.TryAdd(account.Character.ToLower(), new ManualResetEvent(false));
            clients.Add(client);
            _clients.Add(client);
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
            _itemFirstSeen.Clear();
            _pickitAttempts.Clear();
            _pickedUp.Clear();
            _pickitReAdds.Clear();
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

                    // Whether a mule run actually emptied anything only went to the chat client, so
                    // a mule that offloaded nothing looked exactly like one that worked, and the
                    // same items came back for another run next game.
                    var before = InventoryHelpers.ItemsWorthKeeping(foundClient.Game).Count;
                    var muled = await _muleService.MuleItemsForClient(foundClient);
                    var after = InventoryHelpers.ItemsWorthKeeping(foundClient.Game).Count;
                    if (!muled)
                    {
                        Log.Warning("{Client} mule run failed, still carrying {After} of {Before} items", client, after, before);
                        await _externalMessagingClient.SendMessage($"{client}: failed mule");
                    }
                    else
                    {
                        if (after >= before)
                        {
                            Log.Warning("{Client} mule run finished but offloaded nothing, still carrying {After} items: the mules have no room either", client, after);
                        }
                        else
                        {
                            Log.Information("{Client} muled {Offloaded} items, {After} left", client, before - after, after);
                        }

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

    /// <summary>
    /// Picks up drops and tops up potions. <paramref name="anchor"/> is the point loot is measured
    /// from, defaulting to the client itself; a client travelling with a group passes the group's
    /// position so fetching an item can never walk it away from everyone else. Potions stay
    /// measured from the client, since running dry is a survival problem rather than a loot one.
    /// </summary>
    /// <summary>
    /// Where the nearest item waiting to be picked up is, anywhere in the level, or null when there
    /// is nothing left to collect. For a character that has run out of things to kill but can still
    /// cross the level quickly.
    /// </summary>
    protected Point NearestPickitLocation(Client client, Point from)
    {
        Point nearest = null;
        var nearestDistance = double.MaxValue;

        // Potions as well as loot. A rejuvenation is refused by the pickit rules, so the only thing
        // that ever collects one is the potion path, and that only looks within its own radius at
        // the moment it drops. Anything it missed lies there for the rest of the game unless the
        // character with nothing left to do goes and gets it.
        foreach (var item in _pickitItemsOnGround.Snapshot().Concat(_pickitPotionsOnGround.Snapshot()))
        {
            if (IsReservedForAnotherClient(client, item))
            {
                continue;
            }

            var distance = from.Distance(item.Location);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = item.Location;
            }
        }

        return nearest;
    }

    protected async Task PickupItemsAndPotions(Client client, AccountConfig account, double distance, Point anchor = null)
    {
        await PickupItemsFromPickupList(client, distance, anchor);
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

        PickitAudit.LogItemDrop(game, item, shouldPickupGoldItems: false);

        // An item coming back into view raises this again for something already collected or
        // already given up on, which put it straight back on the list and undid the give-up bound
        // entirely - the same breastplate was approached fifteen times, each approach from further
        // away than the last.
        if (_pickedUp.ContainsKey(item.Id) || _pickitReAdds.GetValueOrDefault(item.Id) > MaxPickupReAdds)
        {
            return Task.CompletedTask;
        }

        if (D2NG.Pickit.Pickit.ShouldPickupItem(game, item, false))
        {
            _pickitItemsOnGround.TryAdd(item.Id, item.Location, item);
            _itemFirstSeen.TryAdd(item.Id, DateTime.UtcNow);
        }

        if (item.Name == ItemName.RejuvenationPotion || item.Name == ItemName.FullRejuvenationPotion || item.Name == ItemName.SuperHealingPotion || item.Name == ItemName.SuperManaPotion)
        {
            _pickitPotionsOnGround.TryAdd(item.Id, item.Location, item);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Puts an item back without counting the attempt, for when the reason it was not picked up has
    /// nothing to do with the item.
    /// </summary>
    private void ReturnItemToPickitList(Client client, Item item)
    {
        if (!D2NG.Pickit.Pickit.ShouldPickupItem(client.Game, item, false) || _pickedUp.ContainsKey(item.Id))
        {
            return;
        }

        _pickitItemsOnGround.TryAdd(item.Id, item.Location, item);
    }

    private void PutItemOnPickitList(Client client, Item item)
    {
        if (!D2NG.Pickit.Pickit.ShouldPickupItem(client.Game, item, false))
        {
            return;
        }

        // Out of view is not the same as taken. An item this client can no longer see is missing
        // from Game.Items altogether, and requiring it to be found there dropped it from the list
        // for the rest of the game - which is how a rare on the far side of the level is walked
        // away from and never collected. Only an item we can still see, and can see is no longer on
        // the ground, has actually gone.
        if (_pickedUp.ContainsKey(item.Id))
        {
            return;
        }

        if (client.Game.Items.TryGetValue(item.Id, out var known) && !known.Ground)
        {
            return;
        }

        // Standing on the spot and unable to see it means it is gone, not merely out of sight - at
        // zero distance this client would see it if it were there. Treating those two the same is
        // what had three characters in turn walk to the same vanished breastplate, and every one of
        // them counted as a fresh failure for the next.
        if (client.Game.Me.Location.Distance(item.Location) <= WithinReach
            && !client.Game.Items.ContainsKey(item.Id))
        {
            _pickedUp[item.Id] = true;
            Log.Debug($"{item.Name} at {item.Location} is not there any more, dropping it for every client");
            return;
        }

        // Bounded. Putting it back unconditionally turned every item that could not be taken into a
        // character standing on the spot retrying it, which costs far more than the item is worth.
        // Only failures with the character actually standing on the item say anything about the
        // item: those are the ones that are no longer really there. Failing from fifty units away
        // means the walk failed, which is about this attempt and not about the item, and counting
        // it threw away perfectly good rares nobody had reached yet.
        var distance = client.Game.Me.Location.Distance(item.Location);
        var attempts = distance <= WithinReach
            ? _pickitAttempts.AddOrUpdate(item.Id, 1, (_, count) => count + 1)
            : _pickitAttempts.GetValueOrDefault(item.Id);

        // A separate, looser bound so an item nobody can ever reach still stops being retried.
        var reAdds = _pickitReAdds.AddOrUpdate(item.Id, 1, (_, count) => count + 1);

        if (attempts > MaxPickupAttempts || reAdds > MaxPickupReAdds)
        {
            // Said out loud: an item the bot walks away from is the one thing here worth money, and
            // without this the only evidence is a rare still lying on the floor at the end.
            Log.Warning("Client {ClientName} gave up on {Item} at {Location} after {Tries} tries and {ReAdds} approaches, {FreeCells} free cells, {Distance:F0} away",
                client.Game.Me.Name, item.Name, item.Location, attempts, reAdds,
                client.Game.Inventory.FreeCellCount(), distance);
            return;
        }

        _pickitItemsOnGround.TryAdd(item.Id, item.Location, item);
    }
    private void PutRejuvenationOnPickitList(Client client, Item item)
    {
        if (!item.IsPotion || !item.Ground)
        {
            return;
        }

        // Bounded like any other pickup. A potion is only counted as collected once it reaches the
        // belt, so one that lands anywhere else never satisfies the shortfall that sent the
        // character after it: it walked back for the same potion every few seconds and held the
        // whole party each time it did.
        if (_pickitAttempts.AddOrUpdate(item.Id, 1, (_, count) => count + 1) > MaxPickupAttempts)
        {
            Log.Debug($"Client {client.Game.Me.Name} gave up on {item.Name} after {MaxPickupAttempts} tries");
            return;
        }

        _pickitItemsOnGround.TryAdd(item.Id, item.Location, item);
    }

    private List<Item> GetPickitList(Client client, double distance, Point anchor)
    {
        var resultPickitList = new List<Item>();
        var listItems = _pickitItemsOnGround.Within(anchor ?? client.Game.Me.Location, distance);
        foreach (var tryItem in listItems)
        {
            if (IsReservedForAnotherClient(client, tryItem))
            {
                continue;
            }

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

    /// <summary>
    /// Whether another client in the same area stands closer to <paramref name="location"/> and
    /// should be the one to walk over for it. Potions are deliberately not shared this way: those
    /// go to whoever is short of them.
    /// </summary>
    private bool IsReservedForAnotherClient(Client client, Item item)
    {
        if (_itemFirstSeen.TryGetValue(item.Id, out var seen) && DateTime.UtcNow - seen > ItemClaimGrace)
        {
            return false;
        }

        return IsNearerToAnotherClient(client, item.Location);
    }

    private bool IsNearerToAnotherClient(Client client, Point location)
    {
        var myDistance = client.Game.Me.Location.Distance(location);
        foreach (var other in _clients)
        {
            if (other == client || !other.Game.IsInGame() || other.Game.Me == null || other.Game.Area != client.Game.Area)
            {
                continue;
            }

            if (other.Game.Me.Location.Distance(location) + ItemClaimMargin < myDistance)
            {
                return true;
            }
        }

        return false;
    }

    private List<Item> GetPotionPickupList(Client client, double distance, int nofRevPotions, int nofHealthPotions, int nofManaPotions)
    {
        var resultPickitList = new List<Item>();

        // Full rejuvenations first, and taken whether or not the rejuvenation count is already met:
        // a full one is worth more than the lesser one it displaces, which is worth more again than
        // any plain potion. Room is made for it below rather than the pickup being skipped.
        var rejuvenationRange = Math.Max(distance, RejuvenationPickupRadius);
        var fullRejuvenations = TakePotionsByName(client, rejuvenationRange, FullRejuvenationsWanted, ItemName.FullRejuvenationPotion);
        resultPickitList.AddRange(fullRejuvenations);

        // Further than the rest. A rejuvenation refills life and mana at once and is the only thing
        // that saves a character already in trouble, so it is worth a walk that a healing potion is
        // not - and the party fights on top of them without ever looking this far for one.
        resultPickitList.AddRange(TakePotionsOfType(client, rejuvenationRange, Math.Max(nofRevPotions - fullRejuvenations.Count, 0), ClassificationType.RejuvenationPotion));
        resultPickitList.AddRange(TakePotionsOfType(client, distance, nofHealthPotions, ClassificationType.HealthPotion));
        resultPickitList.AddRange(TakePotionsOfType(client, distance, nofManaPotions, ClassificationType.ManaPotion));
        return resultPickitList;
    }

    /// <summary>
    /// The same as <see cref="TakePotionsOfType"/> but by name, so the full rejuvenation can be
    /// preferred over the lesser one - both share a classification.
    /// </summary>
    private List<Item> TakePotionsByName(Client client, double distance, int nofPotions, ItemName name)
    {
        var resultPickitList = new List<Item>();
        foreach (var tryItem in _pickitPotionsOnGround.Within(client.Game.Me.Location, distance).Where(i => i.Name == name))
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

    private List<Item> TakePotionsOfType(Client client, double distance, int nofPotions, ClassificationType classificationType)
    {
        var resultPickitList = new List<Item>();
        var potionsToTryPick = _pickitPotionsOnGround.Within(client.Game.Me.Location, distance)
            .Where(i => i.Classification == classificationType);
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

            // An empty path leaves the character exactly where it was, and the caller then does
            // whatever it wanted to do there from wherever it still is - which reads in the log as
            // a pickup failing from a hundred units away for no stated reason.
            if (path.Count == 0)
            {
                Log.Warning("Client {ClientName} found no {MovementMode} path from {From} to {To}, {Distance:F0} away",
                    client.Game.Me.Name, movementMode, client.Game.Me.Location, location, distance);
                return;
            }

            await MovementHelpers.TakePathOfLocations(client.Game, path.ToList(), movementMode, token);
            var remaining = client.Game.Me.Location.Distance(location);
            var cancelled = token.HasValue && token.Value.IsCancellationRequested;
            if (!cancelled && remaining > 15 && remaining > distance - 5)
            {
                Log.Warning("Client {ClientName} did not get anywhere {MovementMode} towards {To}, still {Remaining:F0} away over {Steps} steps",
                    client.Game.Me.Name, movementMode, location, remaining, path.Count);
            }
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
            // A full rejuvenation outranks anything it would have to displace, so if there is
            // nowhere to put it, drink a plain potion to make the room rather than walk past it.
            // Drinking one that is already needed costs nothing; the character was going to drink
            // it anyway, and a full rejuvenation in hand is worth more than a healing potion in
            // reserve.
            if (item.Name == ItemName.FullRejuvenationPotion && client.Game.Inventory.FindFreeSpace(item) == null)
            {
                var lesser = client.Game.Inventory.Items.FirstOrDefault(i =>
                    i.Classification == ClassificationType.HealthPotion
                    || i.Classification == ClassificationType.ManaPotion);
                if (lesser != null)
                {
                    Log.Information($"Client {client.Game.Me.Name} drinking {lesser.Name} to make room for {item.Name}");
                    client.Game.UsePotion(lesser);
                    await Task.Delay(150);
                }
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
                        // Either container counts. Only the belt did, but a potion goes to the
                        // inventory whenever the belt has no room for it - and rejuvenations are
                        // counted in the inventory in the first place. So a potion that was picked
                        // up perfectly well reported failure, went back on the list, and was walked
                        // back for again: full rejuvenations were never collected at all, and the
                        // amazon fetched the same mana potion every few seconds all game.
                        return client.Game.Belt.FindItemById(item.Id) != null
                            || client.Game.Inventory.FindItemById(item.Id) != null;
                    }, TimeSpan.FromSeconds(0.2));
                }, TimeSpan.FromSeconds(3)))
                {
                    PutRejuvenationOnPickitList(client, item);
                }
            }
        }
    }

    private async Task PickupItemsFromPickupList(Client client, double distance, Point anchor = null)
    {
        var maxPicks = 3;
        var picks = 0;
        var startLocation = client.Game.Me.Location;
        var pickitList = new List<Item>();
        do
        {
            picks++;
            pickitList = GetPickitList(client, distance, anchor);
            foreach (var item in pickitList)
            {
                if (client.Game.Me.HasSkill(Skill.Vigor))
                {
                    client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
                }
                if (item.Ground)
                {
                    // Before walking, not after. A character with a full inventory used to cross the
                    // level to every item it had no room for, fail, put it straight back on the list
                    // and set off again: one full inventory produced 14,000 attempts in eight
                    // minutes and dragged the party along for all of them. Space can still turn up
                    // when potions are drunk or items go to the cube, so the item stays on the list.
                    if (client.Game.Inventory.FindFreeSpace(item) == null)
                    {
                        // Untouched. Having no room says nothing about the item - it is about this
                        // character - and counting it retired perfectly good loot for everyone once
                        // one full character had skipped it a dozen times, while five other clients
                        // with seventy free cells never got the chance.
                        ReturnItemToPickitList(client, item);
                        continue;
                    }

                    Log.Information($"Client {client.Game.Me.Name} picking up {item.Amount} {item.Name}");
                    await MoveToLocation(client, item.Location);

                    // An item that left this client's sight and came back is a new entity with a new
                    // id, and the list is still holding the old one. Picking up a stale id does
                    // nothing at all, which is what a character standing on an item and failing to
                    // take it four times over with sixty free cells looks like.
                    var target = item;
                    var live = client.Game.Items.Values.FirstOrDefault(i => i.Ground
                        && i.Name == item.Name
                        && i.Id != item.Id
                        && i.Location.Distance(item.Location) <= 2);
                    if (live != null)
                    {
                        Log.Warning("Client {ClientName} found {Item} at {Location} under a new id {NewId}, the list had {OldId}",
                            client.Game.Me.Name, item.Name, item.Location, live.Id, item.Id);
                        target = live;
                    }

                    if (await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        await client.Game.MoveToAsync(target.Location);
                        client.Game.PickupItem(target);
                        return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                        {
                            await Task.Delay(50);
                            if (!target.IsGold && client.Game.Inventory.FindItemById(target.Id) == null)
                            {
                                return false;
                            }

                            return true;
                        }, TimeSpan.FromSeconds(0.2));
                    }, TimeSpan.FromSeconds(3)))
                    {
                        // The clients share one list, so one of them taking an item is the only
                        // reliable way any of the others can tell "already collected" apart from
                        // "out of my sight" - from a client that cannot see the tile, both look
                        // exactly like an item missing from Game.Items.
                        _pickedUp[item.Id] = true;
                        _pickedUp[target.Id] = true;
                        InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                    }
                    else
                    {
                        PutItemOnPickitList(client, item);
                    }
                }
            }
        }
        while (pickitList.Count != 0
            && picks < maxPicks
            && client.Game.Me.Location.Distance(startLocation) < distance);
    }
}
