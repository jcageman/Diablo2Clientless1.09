using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Mule;
using D2NG.Mule.Packing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Mule;

public class MuleService : IMuleService
{
    private static int GameCount;

    private readonly BotConfiguration _botConfig;
    private readonly MuleConfiguration _muleConfig;
    private readonly IExternalMessagingClient _externalMessagingClient;
    private readonly ILogger<MuleService> _logger;
    private readonly IMuleRepository _muleRepository;
    private readonly MuleTransfer _transfer;
    private readonly MuleItemRules _rules;
    private readonly ITownManagementService _townManagementService;

    public MuleService(IOptions<BotConfiguration> botConfig, IOptions<MuleConfiguration> muleConfig, IExternalMessagingClient externalMessagingClient, ILogger<MuleService> logger, IMuleRepository muleRepository = null, ITownManagementService townManagementService = null)
    {
        _botConfig = botConfig.Value;
        _muleConfig = muleConfig.Value;
        _externalMessagingClient = externalMessagingClient;
        _logger = logger;
        _muleRepository = muleRepository;
        _townManagementService = townManagementService;
        _transfer = new MuleTransfer(logger);
        _rules = new MuleItemRules(_muleConfig);
    }

    public async Task PrepareForMule(Client client)
    {
        if (_townManagementService == null)
        {
            return;
        }

        var unidentified = client.Game.Inventory.Items.Count(i => !i.IsIdentified) + client.Game.Cube.Items.Count(i => !i.IsIdentified);
        if (unidentified == 0)
        {
            return;
        }

        _logger.LogInformation("{Farmer}: identifying {Count} items at Cain before muling", client.Game.Me.Name, unidentified);
        if (!await _townManagementService.IdentifyAtCainAndSell(client))
        {
            _logger.LogWarning("{Farmer}: identifying before muling failed, muling what is identified", client.Game.Me.Name);
        }
    }

    public async Task<bool> MuleItemsForClient(Client client, Func<Item, bool> only = null)
    {
        var muleGameName = $"{_botConfig.GameNamePrefix}m{GameCount++}";
        if (!await client.CreateGame(Difficulty.Normal, muleGameName, _botConfig.GamePassword, _botConfig.GameDescriptions?.ElementAtOrDefault(0)))
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return false;
        }

        if (!await WaitForInitialize(client))
        {
            _logger.LogError("Fail to initialize client {ClientName}", client.LoggedInUserName());
            return false;
        }

        // A farmer mules after the game its full stash was found in, so it arrives carrying that game's
        // pickups unidentified. Muling takes identified items only, and left there they fill the
        // inventory every stash item passes through: one item per trade round. A filtered pass (the
        // cube bot's) keeps what it carries for its own reasons and is left alone.
        if (only == null && _townManagementService != null && !await _townManagementService.IdentifyAndSell(client))
        {
            _logger.LogWarning("{Farmer}: identifying and selling before muling failed, muling what is identified", client.Game.Me.Name);
        }

        InventoryHelpers.MoveCubeItemsToInventory(client.Game);
        var passStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        SaveFixture(client, $"{passStamp}-farmer-{client.Game.Me.Name}");

        var pass = new MulePass(client, muleGameName, passStamp);

        // Stash items leave through the inventory, so with the inventory full of items for later
        // accounts the earlier ones could take nothing. Handing over what is already in the
        // inventory first makes the room.
        if (InventoryBlocksStash(client, only))
        {
            _logger.LogInformation("{Farmer}: inventory blocks the stash items, muling the inventory first", client.Game.Me.Name);
            if (!await MuleToAccounts(pass, i => i.Container == ContainerType.Inventory && (only == null || only(i))))
            {
                return false;
            }

            InventoryHelpers.MoveCubeItemsToInventory(client.Game);
        }

        if (!await MuleToAccounts(pass, only))
        {
            return false;
        }

        await AlertWhenMulesAreFull(pass, only);

        var stashInventoryItems = client.Game.Inventory.Items.Where(i => i.IsIdentified && D2NG.Pickit.Pickit.ShouldKeepItem(client.Game, i) && D2NG.Pickit.Pickit.CanTouchInventoryItem(client.Game, i)).ToList();
        InventoryHelpers.StashItemsAndGold(client.Game, stashInventoryItems, 0);
        await client.Game.LeaveGame();
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!await client.RejoinMCP())
        {
            // The items are with the mules; a farmer whose account was logged on elsewhere meanwhile only
            // needs a fresh logon, which the bot does before its next game.
            _logger.LogInformation("{Farmer}: muling done, but the realm session is gone; the bot reconnects before its next game", client.Game.Me.Name);
        }

        return true;
    }

    private sealed class MulePass(Client client, string gameName, string stamp)
    {
        public Client Client { get; } = client;
        public string GameName { get; } = gameName;
        public string Stamp { get; } = stamp;
        public int FailedJoins { get; set; }
        public HashSet<string> UnjoinedAccounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a stash item some account wants cannot enter the inventory, while the inventory
    /// holds items an account wants.
    /// </summary>
    private bool InventoryBlocksStash(Client client, Func<Item, bool> only)
    {
        var wanted = client.Game.Stash.Items.Concat(client.Game.Inventory.Items)
            .Where(i => IsMuleItem(client, i) && (only == null || only(i)) && _muleConfig.Accounts.Any(a => _rules.Accepts(a, i)))
            .ToList();
        return wanted.Any(i => i.Container == ContainerType.Inventory)
            && wanted.Any(i => i.Container != ContainerType.Inventory && client.Game.Inventory.FindFreeSpace(i) == null);
    }

    /// <summary>
    /// Visits the accounts in config order and hands each what it accepts. False when the pass has
    /// to stop: the farmer left the game or a mule could not be reached.
    /// </summary>
    private async Task<bool> MuleToAccounts(MulePass pass, Func<Item, bool> only)
    {
        var client = pass.Client;
        var muleGameName = pass.GameName;
        var passStamp = pass.Stamp;
        foreach (var account in _muleConfig.Accounts)
        {
            var accountCharacters = await GetAccountCharactersForMule(account);
            var held = account.PlaceWithSameClassification ? await HeldPerClassification(account, accountCharacters) : null;
            var remaining = accountCharacters.ToList();
            var accountJoinFailures = 0;

            foreach (var character in accountCharacters)
            {
                InventoryHelpers.CleanupCursorItem(client.Game);

                var candidates = remaining.ToList();
                remaining.Remove(character);
                if (GetMuleItems(client, account, only).Count == 0)
                {
                    break;
                }

                // With placement on, a character only gets the items it is the best home for; the
                // rest wait for theirs. Whatever a visited character could not take falls to the
                // next best one, because the visited character is no longer a candidate.
                List<Item> ForThisCharacter() => GetMuleItems(client, account, only)
                    .Where(i => held == null || MulePlacement.BestCharacter(i.Classification, candidates, held) == character)
                    .ToList();
                var muleItems = ForThisCharacter();
                if (muleItems.Count == 0)
                {
                    continue;
                }

                // A stash item reaches the trade screen only through the inventory. When none of
                // them fits there and nothing is in the inventory already, a visit hands over nothing
                // and only costs a mule game, so this account is left for a pass with more room.
                if (!CanHandOverAny(client.Game, muleItems))
                {
                    _logger.LogInformation("{Farmer}: {Count} items for {Account} are all in the stash and none fits the full inventory, skipping the account",
                        client.Game.Me.Name, muleItems.Count, account.Username);
                    break;
                }

                if (await SeenFullRecently(account.Username, character, muleItems.Select(MuleGrids.ShapeOf)))
                {
                    continue;
                }

                var muleClient = new Client();
                var accountCharacter = new AccountConfig()
                {
                    Username = account.Username,
                    Password = account.Password,
                    Character = character
                };

                if (!await RealmConnectHelpers.ConnectToRealm(
                    muleClient, _botConfig, accountCharacter))
                {
                    _logger.LogError("Fail to connect to realm with {Account} with character {Character}", account.Username, character);
                    return false;
                }

                if (!await muleClient.JoinGame(muleGameName, _botConfig.GamePassword))
                {
                    _logger.LogError("Fail to join game with {Account} with character {Character}", account.Username, character);
                    pass.FailedJoins++;
                    await Task.Delay(TimeSpan.FromSeconds(5) * pass.FailedJoins);
                    if (pass.FailedJoins > 5)
                    {
                        await client.Game.LeaveGame();
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        await client.RejoinMCP();
                        return false;
                    }

                    // Another bot logged on with the same account closes this one's realm session and
                    // leaves the account unable to join for minutes; every further character of it
                    // only spent the pass's failures. The next account that takes the items gets them.
                    if (++accountJoinFailures >= 2)
                    {
                        _logger.LogWarning("{Farmer}: {Account} failed to join twice in a row, leaving it for the next pass", client.Game.Me.Name, account.Username);
                        pass.UnjoinedAccounts.Add(account.Username);
                        break;
                    }

                    continue;
                }

                if (!await WaitForInitialize(muleClient))
                {
                    _logger.LogError("Fail to initialize client {ClientName}", muleClient.LoggedInUserName());
                    return false;
                }

                accountJoinFailures = 0;

                InventoryHelpers.CleanupCursorItem(muleClient.Game);
                SaveFixture(muleClient, $"{passStamp}-mule-{account.Username}-{character}");

                var timer = Stopwatch.StartNew();
                var result = await _transfer.Run(client, muleClient, ForThisCharacter);
                _logger.LogInformation("{Account}-{Character}: {Ending} after {Rounds} rounds, {Items} items / {Cells} cells moved, {Rejections} refusals, {Seconds:F0}s",
                    account.Username, character, result.Ending, result.Rounds, result.ItemsMoved, result.CellsMoved, result.Rejections, timer.Elapsed.TotalSeconds);

                if (result.Ending != TransferEnding.Failed)
                {
                    await RecordCharacter(account.Username, character, muleClient.Game);
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
                await muleClient.Game.LeaveGame();
                await Task.Delay(TimeSpan.FromSeconds(1));
                muleClient.Disconnect();
                if (result.Ending == TransferEnding.Failed)
                {
                    await client.Game.LeaveGame();
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await client.RejoinMCP();
                    return false;
                }

                if (result.Ending == TransferEnding.GiverInventoryBlocks)
                {
                    _logger.LogWarning("{Farmer}: inventory has no room to pull stash items through; leaving {Account} for the next account", client.Game.Me.Name, account.Username);
                    break;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the character's containers next to the log so a real mule situation can be replayed in a test.
    /// </summary>
    private void SaveFixture(Client client, string name)
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_botConfig.LogFile ?? "log.txt")) ?? ".", "mule-fixtures");
            var fixture = MuleFixture.From(client.Game, i => D2NG.Pickit.Pickit.CanTouchInventoryItem(client.Game, i));
            fixture.Save(Path.Combine(directory, $"{name}.json"));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not write mule fixture {Name}", name);
        }
    }

    private async Task<bool> SeenFullRecently(string accountName, string characterName, IEnumerable<Shape> carried)
    {
        if (_muleRepository == null)
        {
            return false;
        }

        try
        {
            var lastSeen = await _muleRepository.GetCharacter(accountName, characterName);
            if (!MuleSkipRule.ShouldSkip(lastSeen, DateTimeOffset.UtcNow, carried))
            {
                return false;
            }

            _logger.LogInformation("Skipping {Account}-{Character}, nothing we carry fits since {SeenAt} ({FreeCells} free cells)", accountName, characterName, lastSeen.SeenAt, lastSeen.FreeCells);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Mule database unavailable, visiting {Account}-{Character}", accountName, characterName);
            return false;
        }
    }

    /// <summary>
    /// True when at least one item can be put in a trade: it is in the inventory already, or it fits
    /// the inventory's free space on its way out of the stash.
    /// </summary>
    private static bool CanHandOverAny(Game game, IEnumerable<Item> items)
    {
        return items.Any(i => i.Container == ContainerType.Inventory || game.Inventory.FindFreeSpace(i) != null);
    }

    private static readonly TimeSpan FullAlertEvery = TimeSpan.FromHours(12);
    private static readonly Dictionary<string, DateTimeOffset> LastFullAlert = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// After a mule pass, items still here that an account's rules want mean every character of
    /// that account is full. Says so once per twelve hours per account, so a new account can be
    /// created and added to the mule config.
    /// </summary>
    private async Task AlertWhenMulesAreFull(MulePass pass, Func<Item, bool> only)
    {
        var client = pass.Client;
        foreach (var account in _muleConfig.Accounts)
        {
            var leftItems = GetMuleItems(client, account, only);
            var left = leftItems.Count;
            if (left == 0)
            {
                continue;
            }

            // Another process logged on to the account mid-pass, so its characters were never visited.
            if (pass.UnjoinedAccounts.Contains(account.Username))
            {
                _logger.LogWarning("{Count} items for {Account} stay on {Farmer}: the account could not be joined this pass",
                    left, account.Username, client.Game.Me.Name);
                continue;
            }

            // A trade takes stash items through the farmer's inventory. With that full nothing can be
            // offered, and blaming the mules sent "create the next account" for accounts with room.
            if (!CanHandOverAny(client.Game, leftItems))
            {
                _logger.LogWarning("{Count} items for {Account} stay on {Farmer}: her inventory has no room to hand them over",
                    left, account.Username, client.Game.Me.Name);
                continue;
            }

            lock (LastFullAlert)
            {
                if (LastFullAlert.TryGetValue(account.Username, out var last) && DateTimeOffset.UtcNow - last < FullAlertEvery)
                {
                    continue;
                }

                LastFullAlert[account.Username] = DateTimeOffset.UtcNow;
            }

            _logger.LogWarning("{Account} is full: {Count} items for it stay on {Farmer}", account.Username, left, client.Game.Me.Name);
            await _externalMessagingClient.SendMessage($"Mule account {account.Username} is full: {left} items for it stay on {client.Game.Me.Name}. Create the next account and add it to the mule config.");
        }
    }

    /// <summary>
    /// Per character of the account, how many items of each classification the mule database says it holds.
    /// </summary>
    private async Task<Dictionary<string, Dictionary<ClassificationType, int>>> HeldPerClassification(MuleAccount account, List<string> characters)
    {
        var held = new Dictionary<string, Dictionary<ClassificationType, int>>(StringComparer.OrdinalIgnoreCase);
        if (_muleRepository == null)
        {
            return held;
        }

        foreach (var character in characters)
        {
            try
            {
                var items = await _muleRepository.GetAllItemsOfCharacter(account.Username, character);
                held[character] = items
                    .Select(i => Enum.TryParse<ClassificationType>(i.ClassificationType, out var c) ? c : (ClassificationType?)null)
                    .Where(c => c != null)
                    .GroupBy(c => c.Value)
                    .ToDictionary(g => g.Key, g => g.Count());
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Mule database unavailable, placing items on {Account} in character order", account.Username);
                return held;
            }
        }

        return held;
    }

    private Task RecordCharacter(string accountName, string characterName, Game game)
    {
        return MuleClientSteps.RecordCharacter(_muleRepository, accountName, characterName, game, _logger);
    }

    private Task<bool> WaitForInitialize(Client client)
    {
        return MuleClientSteps.WaitForInitialize(client, _logger);
    }

    private async Task<List<string>> GetAccountCharactersForMule(MuleAccount account)
    {
        var characterNames = account.IncludedCharacters.Select(c => c.ToLower()).ToList();
        if (characterNames.Count == 0)
        {
            var client = new Client();
            var connect = client.Connect(
            _botConfig.Realm,
                _botConfig.KeyOwner,
                _botConfig.GameFolder);
            if (!connect)
            {
                return characterNames;
            }
            var characters = await client.Login(account.Username, account.Password);
            if (characters == null)
            {
                return characterNames;
            }

            characterNames = characters.Select(c => c.Name.ToLower()).ToList();
            client.Disconnect();
        }

        characterNames = characterNames.Except(account.ExcludedCharacters.Select(c => c.ToLower())).ToList();
        return characterNames;
    }

    private List<Item> GetMuleItems(Client client, MuleAccount muleAccount, Func<Item, bool> only)
    {
        var muleItems = client.Game.Stash.Items.Concat(client.Game.Inventory.Items).Where(i => IsMuleItem(client, i) && (only == null || only(i)));
        return muleItems.Where(i => _rules.Accepts(muleAccount, i)).ToList();
    }

    private bool IsMuleItem(Client client, Item item)
    {
        if (_rules.NeverMule(item))
        {
            return false;
        }

        var rightContainer = item.Container == ContainerType.Stash || item.Container == ContainerType.Stash2 || item.Container == ContainerType.Inventory;

        return rightContainer && item.IsIdentified && D2NG.Pickit.Pickit.CanTouchInventoryItem(client.Game, item);
    }
}
