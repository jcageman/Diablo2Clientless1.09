using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
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

    public MuleService(IOptions<BotConfiguration> botConfig, IOptions<MuleConfiguration> muleConfig, IExternalMessagingClient externalMessagingClient, ILogger<MuleService> logger, IMuleRepository muleRepository = null)
    {
        _botConfig = botConfig.Value;
        _muleConfig = muleConfig.Value;
        _externalMessagingClient = externalMessagingClient;
        _logger = logger;
        _muleRepository = muleRepository;
        _transfer = new MuleTransfer(logger);
    }

    public async Task<bool> MuleItemsForClient(Client client)
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

        InventoryHelpers.MoveCubeItemsToInventory(client.Game);
        var passStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        SaveFixture(client, $"{passStamp}-farmer-{client.Game.Me.Name}");

        var failedToJoinCount = 0;

        foreach (var account in _muleConfig.Accounts)
        {
            var accountCharacters = await GetAccountCharactersForMule(account);

            foreach (var character in accountCharacters)
            {
                InventoryHelpers.CleanupCursorItem(client.Game);

                var muleItems = GetMuleItems(client, account);
                if (muleItems.Count == 0)
                {
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
                    failedToJoinCount++;
                    await Task.Delay(TimeSpan.FromSeconds(5) * failedToJoinCount);
                    if (failedToJoinCount > 5)
                    {
                        await client.Game.LeaveGame();
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        await client.RejoinMCP();
                        return false;
                    }
                    continue;
                }

                if (!await WaitForInitialize(muleClient))
                {
                    _logger.LogError("Fail to initialize client {ClientName}", muleClient.LoggedInUserName());
                    return false;
                }

                InventoryHelpers.CleanupCursorItem(muleClient.Game);
                SaveFixture(muleClient, $"{passStamp}-mule-{account.Username}-{character}");

                var timer = Stopwatch.StartNew();
                var result = await _transfer.Run(client, muleClient, () => GetMuleItems(client, account));
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
                    _logger.LogWarning("{Farmer}: inventory has no room to pull stash items through; muling stops for this game", client.Game.Me.Name);
                    break;
                }
            }
        }

        var stashInventoryItems = client.Game.Inventory.Items.Where(i => i.IsIdentified && D2NG.Pickit.Pickit.ShouldKeepItem(client.Game, i) && D2NG.Pickit.Pickit.CanTouchInventoryItem(client.Game, i)).ToList();
        InventoryHelpers.StashItemsAndGold(client.Game, stashInventoryItems, 0);
        await client.Game.LeaveGame();
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!await client.RejoinMCP())
        {
            return false;
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

    private async Task RecordCharacter(string accountName, string characterName, Game game)
    {
        if (_muleRepository == null)
        {
            return;
        }

        try
        {
            var snapshot = MuleCharacterSnapshot.Take(game);
            await _muleRepository.UpdateCharacter(accountName, characterName, snapshot);
            _logger.LogInformation("Recorded {Account}-{Character}: {Items} items, {FreeCells} free cells, widest fit per height {Profile}",
                accountName, characterName, snapshot.Items.Count, snapshot.FreeCells, string.Join("/", snapshot.FitProfile.Skip(1)));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Mule database unavailable, not recording {Account}-{Character}", accountName, characterName);
        }
    }

    private async Task<bool> WaitForInitialize(Client client)
    {
        var timer = new Stopwatch();
        timer.Start();
        while (client.Game.Me == null && timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(100);
        }

        if (client.Game.Me == null)
        {
            _logger.LogError("{ClientName} failed to initialize Me", client.Game.Me?.Name);
            return false;
        }

        client.Game.RequestUpdate(client.Game.Me.Id);
        if (!GeneralHelpers.TryWithTimeout(
            (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
            TimeSpan.FromSeconds(5)))
        {
            _logger.LogError("{ClientName} failed to initialize current location", client.Game.Me?.Name);
            return false;
        }

        return true;
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

    private List<Item> GetMuleItems(Client client, MuleAccount muleAccount)
    {
        var muleItems = client.Game.Stash.Items.Concat(client.Game.Inventory.Items).Where(i => IsMuleItem(client, i));
        if (muleAccount.MatchesAny.Count == 0)
        {
            return muleItems.ToList();
        }

        return muleItems.Where(i => muleAccount.MatchesAny.Any(f => MatchesRule(i, f))).ToList();
    }

    private static bool MatchesRule(Item item, MuleRule muleRule)
    {
        return muleRule.MatchesAll.All(f => MatchesFilter(item, f));
    }

    private static bool MatchesFilter(Item item, MuleFilter filter)
    {
        bool isMatch = true;
        if (filter.ItemName != null)
        {
            isMatch &= item.Name == filter.ItemName;
        }

        if (filter.ClassificationType != null)
        {
            isMatch &= item.Classification == filter.ClassificationType;
        }

        if (filter.QualityType != null)
        {
            isMatch &= item.Quality == filter.QualityType;
        }

        if (filter.NotFilter.HasValue && filter.NotFilter.Value)
        {
            isMatch = !isMatch;
        }

        return isMatch;
    }

    // Used when the configuration does not say otherwise: a flawless gem is not worth a mule slot.
    private static readonly List<MuleFilter> DefaultNeverMule =
    [
        new() { ItemName = ItemName.FlawlessSkull },
        new() { ItemName = ItemName.FlawlessAmethyst },
        new() { ItemName = ItemName.FlawlessDiamond },
        new() { ItemName = ItemName.FlawlessEmerald },
        new() { ItemName = ItemName.FlawlessRuby },
        new() { ItemName = ItemName.FlawlessSapphire },
        new() { ItemName = ItemName.FlawlessTopaz }
    ];

    private bool IsMuleItem(Client client, Item item)
    {
        var neverMule = _muleConfig.NeverMule ?? DefaultNeverMule;
        if (neverMule.Any(filter => MatchesFilter(item, filter)))
        {
            return false;
        }

        var rightContainer = item.Container == ContainerType.Stash || item.Container == ContainerType.Stash2 || item.Container == ContainerType.Inventory;

        return rightContainer && item.IsIdentified && D2NG.Pickit.Pickit.CanTouchInventoryItem(client.Game, item);
    }
}
