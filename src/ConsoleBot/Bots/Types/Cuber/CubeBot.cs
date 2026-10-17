using ConsoleBot.Enums;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Escape;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Mule;
using D2NG.Pickit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Cuber;

/// <summary>
/// Does all the cubing, so the farming bots only pick up and mule. Asks the mule database which
/// configured recipes the mules can feed; when there is something to make, it creates a game, the
/// mule characters holding the ingredients join and hand them over on the trade screen, it
/// transmutes, and it mules the results away like a farmer would. With nothing to make it waits
/// <see cref="CubeConfiguration.PollInterval"/> and asks again.
/// </summary>
/// <remarks>
/// Run it on its own game server (the game description in the bot config) so its games stay out of
/// the farmers' way, and one instance per game mode: it only plans with the accounts of its own mule
/// configuration.
/// </remarks>
public class CubeBot : IBotInstance
{
    private readonly BotConfiguration _config;
    private readonly CubeConfiguration _cubeConfig;
    private readonly MuleConfiguration _muleConfig;
    private readonly IExternalMessagingClient _externalMessagingClient;
    private readonly IMuleService _muleService;
    private readonly IMuleRepository _muleRepository;
    private readonly ILogger<CubeBot> _logger;
    private readonly MuleTransfer _transfer;
    private int _gameCount;

    public CubeBot(
        IOptions<BotConfiguration> config,
        IOptions<CubeConfiguration> cubeConfig,
        IOptions<MuleConfiguration> muleConfig,
        IExternalMessagingClient externalMessagingClient,
        IMuleService muleService,
        ILogger<CubeBot> logger,
        IMuleRepository muleRepository = null)
    {
        _config = config.Value;
        _cubeConfig = cubeConfig.Value;
        _muleConfig = muleConfig.Value;
        _externalMessagingClient = externalMessagingClient;
        _muleService = muleService;
        _muleRepository = muleRepository;
        _logger = logger;
        _transfer = new MuleTransfer(logger);
    }

    public string GetName()
    {
        return "cube";
    }

    public async Task Run()
    {
        _cubeConfig.Validate();
        if (_muleRepository == null)
        {
            throw new InvalidOperationException("The cube bot plans from the mule database; set mule:connectionString");
        }

        var client = new Client();
        _externalMessagingClient.RegisterClient(client);
        var failuresInARow = 0;
        while (true)
        {
            if (StrangerEscape.StopRequested)
            {
                _logger.LogCritical("{Account}: stopping, a stranger noticed the bot", client.LoggedInUserName());
                if (client.Game.IsInGame())
                {
                    await client.Game.LeaveGame();
                }

                client.Disconnect();
                return;
            }

            var plan = await LoadPlan();
            if (plan == null || plan.IsEmpty)
            {
                _logger.LogInformation("Nothing to cube, asking the mule database again in {Interval}", _cubeConfig.PollInterval);
                await Task.Delay(_cubeConfig.PollInterval);
                continue;
            }

            _logger.LogInformation("Cube run: {Transmutes}, from {Givers}",
                string.Join(", ", plan.Transmutes.Select(t => $"{t.Count}x {t.Recipe.Name}")),
                string.Join(", ", plan.Givers.Select(g => $"{g.Account}-{g.Character} ({g.Items.Count})")));

            // After a good run go straight on: the mules may hold more than one run collects. A run
            // that made nothing waits, or a mule that cannot join would be asked again and again.
            var failure = await RunPlan(client, plan);
            if (failure == null || StrangerEscape.StopRequested)
            {
                failuresInARow = 0;
                continue;
            }

            // A single failed run is usually gone by the next one - a mule still logged on from a
            // stopped run, a refused join - so only a run that fails again is worth a message.
            failuresInARow++;
            _logger.LogError("Cube run failed ({Count} in a row): {Failure}", failuresInARow, failure);
            if (failuresInARow >= 2)
            {
                await _externalMessagingClient.SendMessage($"Cube run failed {failuresInARow} times in a row: {failure}. Trying again in {_cubeConfig.PollInterval}");
            }

            await Task.Delay(_cubeConfig.PollInterval);
        }
    }

    private async Task<CubePlan> LoadPlan()
    {
        try
        {
            var accounts = (_muleConfig.Accounts ?? []).Select(a => MuleNames.Normalize(a.Username)).ToHashSet();
            var muled = await _muleRepository.GetAllItems(null, null, [], null);
            var held = muled
                .Where(i => accounts.Contains(MuleNames.Normalize(i.AccountName)) && !i.Untouchable)
                .Select(i => new HeldItem(i.AccountName, i.CharacterName, MuledItemStock.ToItem(i)))
                .ToList();
            var room = await RoomOfRerollOwners(held);
            return CubePlanner.Plan(_cubeConfig.Recipes, held, _cubeConfig.MaxItemsPerRun, i => !IsRerollable(i), h => CanHandOver(h, room));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read the mule database");
            return null;
        }
    }

    /// <summary>
    /// The free shapes of every mule holding something a reroll recipe could take, as the mule
    /// database last saw them.
    /// </summary>
    private async Task<Dictionary<(string, string), int[]>> RoomOfRerollOwners(List<HeldItem> held)
    {
        var owners = held
            .Where(h => _cubeConfig.Recipes.Any(r => r.RerollUntilKept && r.Requirements[0].Matches(h.Item)))
            .Select(h => (h.Account, h.Character))
            .Distinct();
        var room = new Dictionary<(string, string), int[]>();
        foreach (var owner in owners)
        {
            room[owner] = (await _muleRepository.GetCharacter(owner.Account, owner.Character))?.FitProfile;
        }

        return room;
    }

    /// <summary>
    /// A stash item reaches the trade screen only through the inventory, and the swap that makes room
    /// needs an inventory item of the same size. A mule with no spot left for the item's shape
    /// usually cannot hand it over, and a run planned around it would fail and wait an hour.
    /// </summary>
    private static bool CanHandOver(HeldItem held, Dictionary<(string, string), int[]> room)
    {
        if (!room.TryGetValue((held.Account, held.Character), out var profile) || profile == null)
        {
            return true;
        }

        var height = held.Item.Height;
        return height < profile.Length && profile[height] >= held.Item.Width;
    }

    /// <summary>
    /// Runs one plan and returns why it failed, or null when it made something and the results
    /// were muled away.
    /// </summary>
    private async Task<string> RunPlan(Client client, CubePlan plan)
    {
        if (!await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _cubeConfig, 10))
        {
            return "could not connect to the realm";
        }

        var gameName = $"{_config.GameNamePrefix}{++_gameCount % 100}";
        if (!await client.CreateGame(_config.Difficulty, gameName, _config.GamePassword, _config.GameDescriptions?.ElementAtOrDefault(0)))
        {
            return $"could not create game {gameName}";
        }

        if (!await MuleClientSteps.WaitForInitialize(client, _logger))
        {
            await client.Game.LeaveGame();
            return "the cube character did not get into its game";
        }

        _rolledThisRun.Clear();
        var received = 0;
        var emptyHanded = new List<string>();
        foreach (var giver in plan.Givers)
        {
            if (StrangerEscape.FleeRequested)
            {
                return "a stranger joined the game";
            }

            var handedOver = await CollectFrom(client, gameName, giver);
            received += handedOver;
            if (handedOver == 0)
            {
                emptyHanded.Add($"{giver.Account}-{giver.Character}");
            }
        }

        if (StrangerEscape.FleeRequested)
        {
            return "a stranger joined the game";
        }

        var made = await Transmute(client, plan);

        await client.Game.LeaveGame();
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!await client.RejoinMCP() && !await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _cubeConfig, 10))
        {
            return "could not get back to the lobby after cubing";
        }

        // Results and anything that could not be cubed go back out through the normal mule rules. A
        // refused game logon is usually over a few seconds later, so one more try before the run counts
        // as failed and the items wait an hour on the cube character.
        var muled = await _muleService.MuleItemsForClient(client, IsCubeItem);
        if (!muled)
        {
            _logger.LogWarning("{Cuber}: handing the results back failed, trying once more in {Delay}", client.Game.Me?.Name, JoinRetryDelay);
            await Task.Delay(JoinRetryDelay);
            if (await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _cubeConfig, 10))
            {
                muled = await _muleService.MuleItemsForClient(client, IsCubeItem);
            }
        }
        if (made == 0)
        {
            return received == 0
                ? $"no ingredients arrived from {string.Join(", ", emptyHanded)}"
                : $"{received} ingredients arrived, but not enough for any planned transmute or the transmute failed";
        }

        return muled ? null : $"made {made} transmutes but muling the results failed";
    }

    /// <summary>
    /// Longer than the game server's connection window, so a logon it refused for coming too soon is
    /// allowed on the next try.
    /// </summary>
    private static readonly TimeSpan JoinRetryDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The game server sometimes closes a game logon straight away ("FFFFFFFF instead of A7 01"): too
    /// many connections from this machine at once, or the account's previous game not closed yet. A
    /// real client tries again a little later, so a mule does too, from a fresh realm logon.
    /// </summary>
    private async Task<bool> JoinWithRetry(Client muleClient, AccountConfig accountCharacter, string gameName)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await RealmConnectHelpers.ConnectToRealm(muleClient, _config, accountCharacter)
                && await muleClient.JoinGame(gameName, _config.GamePassword)
                && await MuleClientSteps.WaitForInitialize(muleClient, _logger))
            {
                return true;
            }

            if (attempt < 3)
            {
                _logger.LogWarning("{Account}-{Character} could not join {Game}, trying again in {Delay}",
                    accountCharacter.Username, accountCharacter.Character, gameName, JoinRetryDelay);
                if (muleClient.Game?.IsInGame() == true)
                {
                    await muleClient.Game.LeaveGame();
                }

                muleClient.Disconnect();
                await Task.Delay(JoinRetryDelay);
            }
        }

        return false;
    }

    /// <summary>
    /// Has one mule join and hand over its part of the plan; returns how many items it handed over.
    /// </summary>
    private async Task<int> CollectFrom(Client cuber, string gameName, GiverPlan giver)
    {
        var account = _muleConfig.Accounts.FirstOrDefault(a => MuleNames.Normalize(a.Username) == MuleNames.Normalize(giver.Account));
        if (account == null)
        {
            _logger.LogWarning("{Account} is in the mule database but not in the mule configuration, skipping it", giver.Account);
            return 0;
        }

        var muleClient = new Client();
        StrangerEscape.Watch(muleClient, shortLived: true);
        try
        {
            var accountCharacter = new AccountConfig { Username = account.Username, Password = account.Password, Character = giver.Character };
            if (!await JoinWithRetry(muleClient, accountCharacter, gameName))
            {
                _logger.LogError("{Account}-{Character} could not join {Game}", account.Username, giver.Character, gameName);
                return 0;
            }

            var wanted = giver.Items.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
            var before = CountOnCuber(cuber, wanted.Keys);
            // Starter scrolls go first: two free cells may spare the swap, and a scroll must never be
            // swapped into the stash, where it would take a cell for good.
            _transfer.DropStarterScrolls(muleClient);
            MakeRoomToHandOver(muleClient, cuber, wanted, before);
            var result = await _transfer.Run(muleClient, cuber, () => StillWanted(muleClient, cuber, wanted, before));
            _logger.LogInformation("{Account}-{Character} -> {Cuber}: {Ending}, {Items} items handed over",
                account.Username, giver.Character, cuber.Game.Me.Name, result.Ending, result.ItemsMoved);

            await MuleClientSteps.RecordCharacter(_muleRepository, account.Username, giver.Character, muleClient.Game, _logger);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await muleClient.Game.LeaveGame();
            return result.ItemsMoved;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Collecting from {Account}-{Character} failed", giver.Account, giver.Character);
            return 0;
        }
        finally
        {
            // A failed join, initialisation or trade still leaves the character in the game. Leaving
            // first, as a real client does: dropping the connection instead leaves a ghost that blocks
            // this account's next game logon for minutes.
            try
            {
                if (muleClient.Game?.IsInGame() == true)
                {
                    await muleClient.Game.LeaveGame();
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "{Account}-{Character} could not leave the game before disconnecting", giver.Account, giver.Character);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
            muleClient.Disconnect();
        }
    }

    /// <summary>
    /// What a mule may hand over: an item some recipe takes, judged on the live item. Matching on name
    /// and quality alone would hand over any magic grand charm - an item level 90 skiller as readily as
    /// the item level 91 stock the plan named - and a reroll's item must also not be a keeper already.
    /// </summary>
    private bool IsIngredient(Item item)
    {
        return _cubeConfig.Recipes.Any(r => r.Requirements.Where((requirement, index) =>
            requirement.Matches(item) && (!r.RerollUntilKept || index != 0 || IsRerollable(item))).Any());
    }

    /// <summary>
    /// A full mule cannot take stash items into its inventory to trade them. It swaps them with
    /// inventory items it is not handing over instead, so nothing has to leave its containers.
    /// </summary>
    private void MakeRoomToHandOver(Client mule, Client cuber, Dictionary<(ItemName, QualityType), int> wanted, Dictionary<(ItemName, QualityType), int> before)
    {
        var inStash = StillWanted(mule, cuber, wanted, before)
            .Where(i => i.Container is ContainerType.Stash or ContainerType.Stash2)
            .ToList();
        if (inStash.Count == 0 || mule.Game.Inventory.FindFreeSpace(inStash[0]) != null)
        {
            return;
        }

        _logger.LogInformation("{Mule}: inventory full, swapping {Count} stash items into it", mule.Game.Me.Name, inStash.Count);
        StashSwap.SwapIntoInventory(
            mule.Game,
            inStash,
            i => Pickit.CanTouchInventoryItem(mule.Game, i) && !wanted.ContainsKey(Key(i))
                && i.Name is not (ItemName.ScrollofTownPortal or ItemName.ScrollofIdentify),
            _logger);
    }

    /// <summary>
    /// What the mule still has to hand over: the planned amount per kind, minus what already arrived.
    /// </summary>
    private List<Item> StillWanted(Client mule, Client cuber, Dictionary<(ItemName, QualityType), int> wanted, Dictionary<(ItemName, QualityType), int> before)
    {
        var now = CountOnCuber(cuber, wanted.Keys);
        var offered = new List<Item>();
        // Inventory first: a stash item can only be traded through the inventory, so with a full
        // inventory holding the same kind, offering the stash copy hands over nothing. Items are
        // matched by name and quality, so a finished roll must not stand in for the sword planned
        // for a reroll.
        var available = mule.Game.Inventory.Items.Concat(mule.Game.Stash.Items)
            .Where(i => i.IsIdentified && Pickit.CanTouchInventoryItem(mule.Game, i))
            .Where(IsIngredient)
            .ToList();
        foreach (var (key, amount) in wanted)
        {
            var missing = amount - (now[key] - before[key]);
            offered.AddRange(available.Where(i => Key(i) == key).Take(Math.Max(0, missing)));
        }

        return offered;
    }

    private static Dictionary<(ItemName, QualityType), int> CountOnCuber(Client cuber, IEnumerable<(ItemName, QualityType)> keys)
    {
        var items = CubeCharacterItems(cuber);
        return keys.ToDictionary(k => k, k => items.Count(i => Key(i) == k));
    }

    /// <summary>
    /// Runs the planned transmutes with what the cube character holds; returns how many succeeded.
    /// </summary>
    private async Task<int> Transmute(Client cuber, CubePlan plan)
    {
        var game = cuber.Game;
        if (game.Inventory.FindItemByName(ItemName.HoradricCube) == null)
        {
            _logger.LogError("{Cuber} carries no Horadric Cube in its inventory, nothing is transmuted", game.Me.Name);
            return 0;
        }

        if (game.Cube.Items.Count != 0 && !InventoryHelpers.MoveCubeItemsToInventory(game))
        {
            _logger.LogError("{Cuber}: could not empty the cube", game.Me.Name);
            return 0;
        }

        var total = 0;
        foreach (var transmute in plan.Transmutes)
        {
            if (transmute.Recipe.RerollUntilKept)
            {
                var rolls = await Reroll(cuber, transmute);
                _logger.LogInformation("{Cuber}: made {Made} of {Planned} {Recipe}", game.Me.Name, rolls, transmute.Count, transmute.Recipe.Name);
                total += rolls;
                continue;
            }

            var made = 0;
            for (var i = 0; i < transmute.Count; i++)
            {
                var outputs = TransmuteOnce(cuber, transmute.Recipe);
                if (outputs == null)
                {
                    break;
                }

                made++;
                if (transmute.Recipe.RerollUntilKept)
                {
                    var rolled = outputs.FirstOrDefault(transmute.Recipe.Result.Matches);
                    var description = rolled == null ? "nothing" : Describe(rolled);
                    if (rolled != null && IsFinished(rolled))
                    {
                        _logger.LogInformation("{Cuber}: {Recipe} roll {Roll} is a keeper: {Item}", game.Me.Name, transmute.Recipe.Name, made, description);
                        await _externalMessagingClient.SendMessage($"{transmute.Recipe.Name} keeper after {made} rolls: {description}");
                        break;
                    }

                    _logger.LogInformation("{Cuber}: {Recipe} roll {Roll}: {Item}", game.Me.Name, transmute.Recipe.Name, made, description);
                }
            }

            _logger.LogInformation("{Cuber}: made {Made} of {Planned} {Recipe}", game.Me.Name, made, transmute.Count, transmute.Recipe.Name);
            total += made;
        }

        return total;
    }

    /// <summary>
    /// Rolls one item again and again with the cube kept open: the rolled item stays in the cube and
    /// each roll only adds the other ingredients. The stash is opened only to bring those into the
    /// inventory, in batches. Stops at a keeper, when the ingredients run out, or after the planned
    /// rolls; returns how many rolls were made.
    /// </summary>
    private async Task<int> Reroll(Client cuber, PlannedTransmute transmute)
    {
        var game = cuber.Game;
        var recipe = transmute.Recipe;
        var others = recipe.Requirements.Skip(1).ToList();
        var item = CubeCharacterItems(cuber)
            .Where(i => recipe.Requirements[0].Matches(i) && IsRerollable(i))
            .OrderByDescending(i => i.Quality != QualityType.Normal && i.Quality != QualityType.Superior)
            .FirstOrDefault();
        if (item == null)
        {
            // Names what was considered and why it was passed over, since the plan is made from the mule
            // database and the live item can disagree with it.
            var candidates = game.Stash.Items.Concat(game.Inventory.Items)
                .Where(i => i.Classification == recipe.Result.Classification || recipe.Result.ItemNames?.Contains(i.Name) == true)
                .Select(i => $"{Describe(i)} [{i.Container}, touchable {Pickit.CanTouchInventoryItem(game, i)}, matches {recipe.Requirements[0].Matches(i)}, kept by {string.Join("+", Pickit.KeepRuleSources(i))}]")
                .ToList();
            _logger.LogInformation("{Cuber}: nothing left to roll for {Recipe}; candidates: {Candidates}", game.Me.Name, recipe.Name,
                candidates.Count == 0 ? "none" : string.Join("; ", candidates));
            return 0;
        }

        var made = 0;
        var cubeOpen = false;
        try
        {
            while (made < transmute.Count)
            {
                var inputs = InventoryInputs(cuber, others);
                var itemReachable = game.Cube.FindItemById(item.Id) != null || game.Inventory.FindItemById(item.Id) != null;
                if (inputs == null || !itemReachable)
                {
                    if (cubeOpen)
                    {
                        game.ClickButton(ClickType.CloseHoradricCube);
                        cubeOpen = false;
                    }

                    BringToInventory(cuber, item, others, transmute.Count - made);
                    if (game.Cube.FindItemById(item.Id) == null && game.Inventory.FindItemById(item.Id) == null)
                    {
                        _logger.LogError("{Cuber}: no inventory room for the {Item} to roll, and the stash is too full to make it", game.Me.Name, item.Name);
                        break;
                    }

                    inputs = InventoryInputs(cuber, others);
                    if (inputs == null)
                    {
                        _logger.LogInformation("{Cuber}: not enough left for {Recipe}", game.Me.Name, recipe.Name);
                        break;
                    }
                }

                if (!cubeOpen)
                {
                    var cube = game.Inventory.FindItemByName(ItemName.HoradricCube);
                    if (cube == null || !game.ActivateCube(cube))
                    {
                        _logger.LogError("{Cuber}: could not open the cube for {Recipe}", game.Me.Name, recipe.Name);
                        break;
                    }

                    cubeOpen = true;
                }

                if (game.Cube.FindItemById(item.Id) == null && !PutInOpenCube(game, game.Inventory.FindItemById(item.Id)))
                {
                    break;
                }

                if (!inputs.All(input => PutInOpenCube(game, input)) || !InventoryHelpers.TransmuteInOpenCube(game))
                {
                    _logger.LogError("{Cuber}: transmuting {Recipe} failed", game.Me.Name, recipe.Name);
                    break;
                }

                made++;
                var rolled = game.Cube.Items.FirstOrDefault(recipe.Result.Matches);
                if (rolled == null)
                {
                    _logger.LogWarning("{Cuber}: {Recipe} made {Items}, not what the recipe expects", game.Me.Name, recipe.Name, string.Join(", ", game.Cube.Items.Select(i => i.Name)));
                    break;
                }

                item = rolled;
                if (IsFinished(rolled))
                {
                    _logger.LogInformation("{Cuber}: {Recipe} roll {Roll} is a keeper: {Item}", game.Me.Name, recipe.Name, made, Describe(rolled));
                    await _externalMessagingClient.SendMessage($"{recipe.Name} keeper after {made} rolls: {Describe(rolled)}");
                    break;
                }

                _logger.LogInformation("{Cuber}: {Recipe} roll {Roll}: {Item}", game.Me.Name, recipe.Name, made, Describe(rolled));
            }
        }
        finally
        {
            if (cubeOpen)
            {
                game.ClickButton(ClickType.CloseHoradricCube);
            }
        }

        if (game.Cube.Items.Count != 0 && !InventoryHelpers.MoveCubeItemsToInventory(game))
        {
            // The inventory still holds the ingredients for rolls that were not needed; a keeper ends
            // the rolling early. Stash those, so the result fits and goes back to the mules.
            var spare = game.Inventory.Items.Where(i => Pickit.CanTouchInventoryItem(game, i) && IsCubeItem(i)).ToList();
            InventoryHelpers.StashItemsAndGold(game, spare, 0);
            if (!InventoryHelpers.MoveCubeItemsToInventory(game))
            {
                _logger.LogError("{Cuber}: could not take the {Recipe} result out of the cube", game.Me.Name, recipe.Name);
            }
        }

        if (made > 0)
        {
            _rolledThisRun.Add(Describe(item));
        }

        return made;
    }

    /// <summary>
    /// The other ingredients of one roll, from the inventory only; null when they are not all there.
    /// </summary>
    private static List<Item> InventoryInputs(Client cuber, List<RecipeRequirement> requirements)
    {
        var inventory = cuber.Game.Inventory.Items.Where(i => Pickit.CanTouchInventoryItem(cuber.Game, i)).ToList();
        var inputs = new List<Item>();
        foreach (var requirement in requirements)
        {
            var matching = inventory.Where(i => requirement.Matches(i) && !inputs.Contains(i)).Take(requirement.Amount).ToList();
            if (matching.Count < requirement.Amount)
            {
                return null;
            }

            inputs.AddRange(matching);
        }

        return inputs;
    }

    /// <summary>
    /// One stash visit: the rolled item if it is stashed, then as many of the other ingredients as
    /// the remaining rolls need and the inventory holds.
    /// </summary>
    private void BringToInventory(Client cuber, Item item, List<RecipeRequirement> requirements, int rollsLeft)
    {
        var game = cuber.Game;
        KeepInventoryRoom(game, item, requirements);
        var stashed = game.Stash.Items.Where(i => Pickit.CanTouchInventoryItem(game, i)).ToList();
        var wanted = stashed.Where(i => i.Id == item.Id).ToList();
        foreach (var requirement in requirements)
        {
            wanted.AddRange(stashed.Where(i => requirement.Matches(i) && i.Id != item.Id).Take(requirement.Amount * rollsLeft));
        }

        if (wanted.Count > 0)
        {
            InventoryHelpers.MoveStashItemsToInventory(game, wanted);
        }
    }

    /// <summary>
    /// The rolled item and one roll's ingredients have to fit the inventory: that is where they go into
    /// the cube from. Ingredients that arrived in the trade and stayed in the inventory fill it, so
    /// everything beyond one roll's worth goes back to the stash first, while the stash has room.
    /// </summary>
    private void KeepInventoryRoom(Game game, Item item, List<RecipeRequirement> requirements)
    {
        if (game.Stash.FindItemById(item.Id) == null || game.Inventory.FindFreeSpace(item) != null)
        {
            return;
        }

        var spare = game.Inventory.Items
            .Where(i => i.Id != item.Id && Pickit.CanTouchInventoryItem(game, i) && IsCubeItem(i))
            .ToList();
        var oneRoll = new List<Item>();
        foreach (var requirement in requirements)
        {
            oneRoll.AddRange(spare.Where(i => requirement.Matches(i) && !oneRoll.Contains(i)).Take(requirement.Amount));
        }

        var toStash = spare.Except(oneRoll).ToList();
        if (toStash.Count > 0)
        {
            _logger.LogInformation("{Cuber}: stashing {Count} ingredients to make inventory room for the {Item}", game.Me.Name, toStash.Count, item.Name);
            InventoryHelpers.StashItemsAndGold(game, toStash, 0);
        }
    }

    private bool PutInOpenCube(Game game, Item item)
    {
        var freeSpace = item == null ? null : game.Cube.FindFreeSpace(item);
        if (freeSpace == null || !InventoryHelpers.MoveIntoOpenCube(game, item, freeSpace))
        {
            _logger.LogError("{Cuber}: could not put {Item} in the cube", game.Me.Name, item?.Name);
            return false;
        }

        return true;
    }

    /// <summary>
    /// One transmute; returns what came out of the cube, or null when it could not be made.
    /// </summary>
    private List<Item> TransmuteOnce(Client cuber, CubeRecipe recipe)
    {
        var game = cuber.Game;
        var inputs = new List<Item>();
        foreach (var requirement in recipe.Requirements)
        {
            // A reroll takes the item rolled last over a fresh base, and never one already kept.
            var matching = CubeCharacterItems(cuber)
                .Where(i => requirement.Matches(i) && inputs.All(chosen => chosen.Id != i.Id))
                .Where(i => !recipe.RerollUntilKept || requirement != recipe.Requirements[0] || IsRerollable(i))
                .OrderByDescending(i => recipe.RerollUntilKept && i.Quality != QualityType.Normal && i.Quality != QualityType.Superior)
                .Take(requirement.Amount)
                .ToList();
            if (matching.Count < requirement.Amount)
            {
                _logger.LogInformation("{Cuber}: not enough left for {Recipe}", game.Me.Name, recipe.Name);
                return null;
            }

            inputs.AddRange(matching);
        }

        var stashed = inputs.Where(i => i.Container is ContainerType.Stash or ContainerType.Stash2).ToList();
        if (stashed.Any(i => game.Inventory.FindFreeSpace(i) == null))
        {
            // Results of earlier transmutes pile up in the inventory; put them away to make room.
            var results = game.Inventory.Items
                .Where(i => Pickit.CanTouchInventoryItem(game, i) && IsCubeItem(i) && inputs.All(input => input.Id != i.Id))
                .ToList();
            if (results.Count > 0)
            {
                InventoryHelpers.StashItemsAndGold(game, results, 0);
            }
        }

        if (stashed.Count > 0 && InventoryHelpers.MoveStashItemsToInventory(game, stashed) != MoveItemResult.Succes)
        {
            _logger.LogError("{Cuber}: could not take the {Recipe} ingredients out of the stash", game.Me.Name, recipe.Name);
            return null;
        }

        InventoryHelpers.CleanupCursorItem(game);
        foreach (var input in inputs)
        {
            var inventoryItem = game.Inventory.FindItemById(input.Id);
            var freeSpace = inventoryItem == null ? null : game.Cube.FindFreeSpace(inventoryItem);
            if (freeSpace == null || InventoryHelpers.PutInventoryItemInCube(game, inventoryItem, freeSpace) != MoveItemResult.Succes)
            {
                _logger.LogError("{Cuber}: could not put {Item} in the cube", game.Me.Name, input.Name);
                InventoryHelpers.MoveCubeItemsToInventory(game);
                return null;
            }
        }

        if (!InventoryHelpers.TransmuteItemsInCube(game, true))
        {
            _logger.LogError("{Cuber}: transmuting {Recipe} failed", game.Me.Name, recipe.Name);
            InventoryHelpers.MoveCubeItemsToInventory(game);
            return null;
        }

        var outputs = game.Cube.Items;
        foreach (var output in outputs)
        {
            if (!recipe.Result.Matches(output))
            {
                _logger.LogWarning("{Cuber}: {Recipe} made {Item}, not what the recipe expects", game.Me.Name, recipe.Name, output.Name);
            }
        }

        return InventoryHelpers.MoveCubeItemsToInventory(game) ? outputs : null;
    }

    /// <summary>
    /// An item a keep rule outside <see cref="RerollStockNip"/> keeps: what a reroll is after. Stock
    /// kept only as crafting material - white bases, junk magic Colossus Blades - does not count.
    /// </summary>
    private static bool IsFinished(Item item)
    {
        return Pickit.KeepRuleSources(item)
            .Any(source => !string.Equals(source, RerollStockNip, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a reroll may take: not finished, and not a white item with sockets, which is a runeword
    /// base. Rolled swords have sockets too, so only white ones are spared for it.
    /// </summary>
    private static bool IsRerollable(Item item)
    {
        return !IsFinished(item) && !(item.Quality is QualityType.Normal or QualityType.Superior && item.Sockets > 0);
    }

    private const string RerollStockNip = "CraftBases.nip";

    private static string Describe(Item item)
    {
        // The item level shows whether a reroll kept it: a grand charm below 91 can no longer roll the best life.
        return $"{item.Quality} {item.Name} ilvl {item.Level}{(item.Sockets > 0 ? $" {item.Sockets}os" : "")}: {string.Join(", ", item.Properties.Values.Select(p => SkillTabNames.IsSkillTab(p.Type) ? $"+{p.Value} {SkillTabNames.Name(p.SkillTab)}" : $"{p.Type} {p.Value}"))}";
    }

    /// <summary>
    /// The cube character's own stash and inventory, minus what it must keep (the cube itself, tomes).
    /// </summary>
    private static List<Item> CubeCharacterItems(Client cuber)
    {
        return cuber.Game.Stash.Items.Concat(cuber.Game.Inventory.Items)
            .Where(i => Pickit.CanTouchInventoryItem(cuber.Game, i))
            .ToList();
    }

    /// <summary>
    /// What goes back to the mules after a run: ingredients, what plain recipes made, and what this
    /// run rolled. A reroll's result ("any magic grand charm") also describes the cube character's own
    /// gear, so for reroll recipes only real stock and this run's rolls count - the cube characters
    /// are played characters, and their own charms stay on them.
    /// </summary>
    private bool IsCubeItem(Item item)
    {
        if (_rolledThisRun.Contains(Describe(item)))
        {
            return true;
        }

        return _cubeConfig.Recipes.Any(r => r.RerollUntilKept
            ? (r.Requirements[0].Matches(item) && IsRerollable(item)) || r.Requirements.Skip(1).Any(q => q.Matches(item))
            : r.Result.Matches(item) || r.Requirements.Any(q => q.Matches(item)));
    }

    /// <summary>
    /// The items this run rolled, by description: item ids do not survive into the mule game.
    /// </summary>
    private readonly HashSet<string> _rolledThisRun = [];

    private static (ItemName, QualityType) Key(Item item)
    {
        return (item.Name, item.Quality);
    }
}
