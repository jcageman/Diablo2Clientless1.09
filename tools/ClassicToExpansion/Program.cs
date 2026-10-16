using ConsoleBot.Bots;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.MCP;
using D2NG.Mule;
using D2NG.Pickit;
using D2NG.Pickit.Nip;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Moves items from classic mules to the expansion mules. Classic and expansion characters cannot meet
// in a game, so the items travel through a temporary character:
//
//   1. the classic sources trade what may go to the temporary character, a classic one;
//   2. the temporary character is converted to expansion;
//   3. it mules everything on exactly as an expansion farmer would: the expansion mule config decides
//      which account takes an item, and every visited mule is recorded in the mule database;
//   4. once it is empty it is deleted and created again as classic, and the next round starts.
//
//   ClassicToExpansion --config <bot config> --muleconfig <classic mule config>
//       --expansion-muleconfig <expansion mule config> --pickitconfig <expansion pickit config>
//       [--nip <file>] [--exclude-characters <name>[,<name>...]]
//
// The sources, the characters that stay and the temporary character are the toExpansion block of the
// classic mule config:
//
//   "toExpansion": {
//     "accounts": [ "<account>", ... ],
//     "excludedCharacters": [ "<character that stays classic>", ... ],
//     "temp": { "account": "<account>", "password": "<password>", "character": "<name>", "class": "Sorceress" }
//   }
//
// Every character on those accounts is a source except the excluded ones and --exclude-characters. An
// item leaves a source only when an expansion mule account's nip takes it, and, with --nip, when that
// file matches it too. Stash, inventory and cube contents move; equipped and belt items, unidentified
// items and the cube itself stay.
//
// The temporary character is deleted, so it has to be one the tool owns: it is created when missing,
// and an existing one above level 1 is refused. Keep it on an account no mule config visits. An
// interrupted run resumes: a classic temporary character is filled further, an expansion one is emptied
// first. When the expansion mules have no room left the tool stops and leaves the rest on the temporary
// character.

Environment.Exit(await Run(args));

static async Task<int> Run(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        options[args[i].TrimStart('-')] = args[i + 1];
    }

    if (!options.TryGetValue("config", out var configPath) || !options.TryGetValue("muleconfig", out var classicMuleConfigPath)
        || !options.TryGetValue("expansion-muleconfig", out var expansionMuleConfigPath) || !options.TryGetValue("pickitconfig", out var pickitConfigPath))
    {
        Console.Error.WriteLine("usage: ClassicToExpansion --config <bot config> --muleconfig <classic mule config> --expansion-muleconfig <expansion mule config> "
            + "--pickitconfig <expansion pickit config> [--nip <file>] [--exclude-characters <name>[,...]]");
        return 2;
    }

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
        .WriteTo.File("classic-to-expansion.txt")
        .CreateLogger();
    var loggerFactory = new SerilogLoggerFactory(Log.Logger);
    var logger = loggerFactory.CreateLogger("ClassicToExpansion");

    GameDataLocation.Use(options.TryGetValue("datadir", out var dataDirectory)
        ? dataDirectory
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "data"));

    var botConfig = Section<BotConfiguration>(configPath, "bot");
    botConfig.GameNamePrefix = $"cx{Random.Shared.Next(100, 999)}";
    var classicMuleConfig = Section<MuleConfiguration>(classicMuleConfigPath, "mule");
    var expansionMuleConfig = Section<MuleConfiguration>(expansionMuleConfigPath, "mule");
    var toExpansion = Section<ToExpansionConfiguration>(classicMuleConfigPath, "mule:toExpansion");
    Pickit.Configure(Section<PickitConfiguration>(pickitConfigPath, "pickit"));

    if (toExpansion?.Temp == null || toExpansion.Accounts.Count == 0 || !Enum.TryParse<CharacterClass>(toExpansion.Temp.Class, true, out var tempClass))
    {
        Console.Error.WriteLine("the classic mule config needs a toExpansion block with accounts and a temp (account, password, character, class)");
        return 2;
    }

    var passwords = classicMuleConfig.Accounts
        .GroupBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Password, StringComparer.OrdinalIgnoreCase);
    var missing = toExpansion.Accounts.Where(a => !passwords.ContainsKey(a)).ToList();
    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"not in the classic mule config: {string.Join(", ", missing)}");
        return 2;
    }

    var staying = new HashSet<string>(toExpansion.ExcludedCharacters, StringComparer.OrdinalIgnoreCase);
    foreach (var name in options.GetValueOrDefault("exclude-characters", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        staying.Add(name);
    }

    IReadOnlyList<NipRule> filter = null;
    if (options.TryGetValue("nip", out var nipPath))
    {
        var path = File.Exists(nipPath) ? nipPath : Path.Combine(expansionMuleConfig.NipDirectory ?? "", nipPath);
        filter = NipParser.ParseFile(path);
        if (filter.Count == 0)
        {
            Console.Error.WriteLine($"{path} holds no rules");
            return 2;
        }
    }

    var repository = string.IsNullOrEmpty(expansionMuleConfig.ConnectionString)
        ? null
        : new MuleRepository(DocumentStore.For(expansionMuleConfig.ConnectionString));
    var muleService = new MuleService(Options.Create(botConfig), Options.Create(expansionMuleConfig), new LogOnlyMessagingClient(logger),
        loggerFactory.CreateLogger<MuleService>(), repository);

    var temp = new Role(toExpansion.Temp.Account, toExpansion.Temp.Password, toExpansion.Temp.Character);
    var mover = new Mover(botConfig, logger, muleService, new MuleItemRules(expansionMuleConfig), expansionMuleConfig, filter, tempClass, repository);
    var sources = await mover.Sources(toExpansion.Accounts.Select(a => (a, passwords[a])), staying, temp);
    if (sources == null)
    {
        return 1;
    }

    logger.LogInformation("Sources: {Sources}; staying classic: {Staying}", string.Join(", ", sources), string.Join(", ", staying));
    return await mover.Run(sources, temp);
}

static T Section<T>(string path, string section) =>
    new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build().GetSection(section).Get<T>();

internal sealed class ToExpansionConfiguration
{
    public List<string> Accounts { get; set; } = [];

    public List<string> ExcludedCharacters { get; set; } = [];

    public TempCharacterConfiguration Temp { get; set; }
}

internal sealed class TempCharacterConfiguration
{
    public string Account { get; set; }

    public string Password { get; set; }

    public string Character { get; set; }

    public string Class { get; set; }
}

internal sealed record Role(string Account, string Password, string Character)
{
    public override string ToString() => $"{Account}-{Character}";
}

internal sealed class LogOnlyMessagingClient(Microsoft.Extensions.Logging.ILogger logger) : IExternalMessagingClient
{
    public void RegisterClient(Client client)
    {
    }

    public Task SendMessage(string message)
    {
        logger.LogWarning("{Message}", message);
        return Task.CompletedTask;
    }
}

internal sealed class Mover(
    BotConfiguration botConfig,
    Microsoft.Extensions.Logging.ILogger logger,
    MuleService muleService,
    MuleItemRules expansionRules,
    MuleConfiguration expansionMuleConfig,
    IReadOnlyList<NipRule> filter,
    CharacterClass tempClass,
    IMuleRepository repository)
{
    // Games created from one machine within seconds of each other go unanswered; 25 s apart was reliable.
    private static readonly TimeSpan GameCreationSpacing = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan JoinRetryDelay = TimeSpan.FromSeconds(10);
    private const int JoinRetries = 3;

    private readonly MuleTransfer _transfer = new(logger);
    private DateTime _lastGameCreated = DateTime.MinValue;
    private int _gameCount;

    /// <summary>
    /// Every classic character on the source accounts except the ones that stay. Null when an account
    /// cannot be read.
    /// </summary>
    public async Task<List<Role>> Sources(IEnumerable<(string Account, string Password)> accounts, HashSet<string> staying, Role temp)
    {
        var sources = new List<Role>();
        foreach (var (account, password) in accounts)
        {
            var client = new Client();
            try
            {
                var characters = await Logon(client, new Role(account, password, null));
                if (characters == null)
                {
                    return null;
                }

                sources.AddRange(characters
                    .Where(c => !c.IsExpansion && !staying.Contains(c.Name) && !c.Name.Equals(temp.Character, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new Role(account, password, c.Name)));
            }
            finally
            {
                client.Disconnect();
            }
        }

        return sources;
    }

    public async Task<int> Run(List<Role> sources, Role temp)
    {
        var remaining = sources.ToList();
        for (var round = 1; ; round++)
        {
            var tempCharacter = await EnsureTemp(temp);
            if (tempCharacter == null)
            {
                return 1;
            }

            if (!tempCharacter.IsExpansion)
            {
                logger.LogInformation("Round {Round}: {Sources} -> {Temp}", round, string.Join(", ", remaining), temp);
                var tempHolds = await Fill(remaining, temp);
                if (tempHolds == null)
                {
                    return 1;
                }

                if (tempHolds == 0)
                {
                    logger.LogInformation("Nothing left to move");
                    return await Delete(temp) ? 0 : 1;
                }

                if (!await Convert(temp))
                {
                    return 1;
                }
            }

            logger.LogInformation("Round {Round}: {Temp} mules to the expansion mules", round, temp);
            var emptied = await Empty(temp);
            if (emptied == null)
            {
                return 1;
            }

            if (emptied == false)
            {
                logger.LogWarning("The expansion mules have no room for what {Temp} holds; it stays there. Add mules and run again to continue", temp);
                return 1;
            }

            if (!await Delete(temp))
            {
                return 1;
            }

            if (remaining.Count == 0)
            {
                logger.LogInformation("Every source is done");
                return 0;
            }
        }
    }

    /// <summary>
    /// Returns the temporary character, created as classic when it does not exist. An existing one above
    /// level 1 is somebody's real character and is refused, because the tool deletes it later.
    /// </summary>
    private async Task<Character> EnsureTemp(Role temp)
    {
        var client = new Client();
        try
        {
            var characters = await Logon(client, temp with { Character = null });
            if (characters == null)
            {
                return null;
            }

            var existing = Find(characters, temp.Character);
            if (existing != null)
            {
                if (existing.Level > 1 || existing.IsHardCore)
                {
                    logger.LogError("{Temp} exists at level {Level}{Hardcore}; pick a name the tool may create and delete",
                        temp, existing.Level, existing.IsHardCore ? " (hardcore)" : "");
                    return null;
                }

                return existing;
            }

            var created = await client.CreateCharacter(temp.Character, tempClass, CharacterFlags.None);
            if (created == null)
            {
                logger.LogError("Creating {Temp} failed; names take letters and at most one dash, fifteen at most", temp);
            }

            return created;
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>
    /// The classic temporary character opens a game and the sources join one after another, each handing
    /// over what may go, until the temporary character is full or the sources are done. Sources that are
    /// done leave <paramref name="remaining"/>. Returns how many items the temporary character holds, or
    /// null on a failure.
    /// </summary>
    private async Task<int?> Fill(List<Role> remaining, Role temp)
    {
        var receiver = new Client();
        try
        {
            if (!await CreateGame(receiver, temp, out var gameName, out var password))
            {
                return null;
            }

            foreach (var source in remaining.ToList())
            {
                var giver = new Client();
                try
                {
                    // A character joining right after another one of its account left is refused with
                    // FFFFFFFF for a few seconds; the account is still in the game for the server.
                    var joined = await Join(giver, source, gameName, password);
                    for (var attempt = 1; !joined && attempt <= JoinRetries; attempt++)
                    {
                        giver.Disconnect();
                        await Task.Delay(JoinRetryDelay * attempt);
                        giver = new Client();
                        joined = await Join(giver, source, gameName, password);
                    }

                    if (!joined)
                    {
                        return null;
                    }

                    var ending = await Give(giver, receiver, source, temp);
                    if (ending == TransferEnding.Failed)
                    {
                        return null;
                    }

                    // The source is a mule too: what it has left is what the planners must see.
                    await MuleClientSteps.RecordCharacter(repository, source.Account, source.Character, giver.Game, logger);

                    if (ending == TransferEnding.GiverEmpty)
                    {
                        remaining.Remove(source);
                        continue;
                    }

                    // The planner calls a full temporary character GiverInventoryBlocks as soon as the source's
                    // inventory is full too, so anything short of empty ends the round and the source comes
                    // back in the next one. Only when nothing at all fits an empty temporary character is the
                    // source given up on, or the rounds would never end.
                    if (Holdings(receiver.Game).Count == 0)
                    {
                        logger.LogWarning("{Source}: {Ending} with nothing on {Temp}, leaving it for a later run", source, ending, temp);
                        remaining.Remove(source);
                        continue;
                    }

                    break;
                }
                finally
                {
                    await Leave(giver);
                }
            }

            return Holdings(receiver.Game).Count;
        }
        finally
        {
            await Leave(receiver);
        }
    }

    /// <summary>
    /// One source's hand-over, cube contents included as far as its inventory lets them out.
    /// </summary>
    private async Task<TransferEnding> Give(Client giver, Client receiver, Role source, Role temp)
    {
        while (true)
        {
            var result = await _transfer.Run(giver, receiver, () => Offered(giver.Game, giver.Game.Stash.Items.Concat(giver.Game.Inventory.Items)));
            logger.LogInformation("{Source} -> {Temp}: {Ending} after {Rounds} rounds, {Items} items / {Cells} cells",
                source, temp, result.Ending, result.Rounds, result.ItemsMoved, result.CellsMoved);
            if (result.Ending != TransferEnding.GiverEmpty)
            {
                return result.Ending;
            }

            var inCube = Offered(giver.Game, giver.Game.Cube.Items);
            if (inCube.Count == 0)
            {
                return TransferEnding.GiverEmpty;
            }

            var pulled = inCube.Count(item => InventoryHelpers.PutCubeItemInInventory(giver.Game, item) == MoveItemResult.Succes);
            if (pulled == 0)
            {
                logger.LogWarning("{Source}: {Count} items stay in the cube; the cube has to be in the inventory with room beside it", source, inCube.Count);
                return TransferEnding.GiverEmpty;
            }
        }
    }

    /// <summary>
    /// The converted temporary character mules everything on through the expansion mule config, the same
    /// pass an expansion farmer runs. True when it ends empty, false when the mules had no room for the
    /// rest, null on a failure.
    /// </summary>
    private async Task<bool?> Empty(Role temp)
    {
        var client = new Client();
        try
        {
            var characters = await Logon(client, temp);
            var character = characters == null ? null : Find(characters, temp.Character);
            if (character == null)
            {
                return null;
            }

            await client.SelectCharacter(character);
            await PaceGameCreation();
            if (!await muleService.MuleItemsForClient(client))
            {
                logger.LogError("{Temp}: the mule pass failed", temp);
                return null;
            }

            var game = client.Game;
            var left = Holdings(game);
            logger.LogInformation("{Temp}: {Left} items left after the mule pass{Names}", temp, left.Count,
                left.Count == 0 ? "" : ": " + string.Join(", ", left.GroupBy(i => i.Name).Select(g => $"{g.Count()}x {g.Key}")));
            return left.Count == 0 && game.CursorItem == null;
        }
        finally
        {
            await Leave(client);
        }
    }

    private async Task<bool> Convert(Role temp)
    {
        var client = new Client();
        try
        {
            if (await Logon(client, temp) == null)
            {
                return false;
            }

            var converted = await client.UpgradeCharacter(temp.Character);
            if (converted?.IsExpansion != true)
            {
                logger.LogError("Converting {Temp} to expansion failed; its items stay on it", temp);
                return false;
            }

            logger.LogInformation("{Temp} is expansion now", temp);
            return true;
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>
    /// Deletes the temporary character, which has just been seen holding nothing.
    /// </summary>
    private async Task<bool> Delete(Role temp)
    {
        var client = new Client();
        try
        {
            return await Logon(client, temp) != null && await client.DeleteCharacter(temp.Character);
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>
    /// What may leave a classic source: identified, not the cube, wanted by an expansion mule account and
    /// matched by the --nip filter when there is one. Unidentified items are left out because the expansion
    /// mule pass would not take them off the temporary character.
    /// </summary>
    private List<Item> Offered(Game game, IEnumerable<Item> items)
    {
        return items
            .Where(i => i.Name != ItemName.HoradricCube && i.IsIdentified)
            .Where(i => !expansionRules.NeverMule(i) && expansionMuleConfig.Accounts.Any(a => expansionRules.Accepts(a, i)))
            .Where(i => filter == null || filter.Any(rule => rule.MatchesExpression(new NipEvaluationContext(i)
            {
                KeepRuleSources = () => Pickit.KeepRuleSources(i)
            })))
            .ToList();
    }

    private static List<Item> Holdings(Game game)
    {
        return game.Stash.Items.Concat(game.Inventory.Items).Concat(game.Cube.Items)
            .Where(i => i.Name != ItemName.HoradricCube)
            .ToList();
    }

    private Task<bool> CreateGame(Client client, Role role, out string gameName, out string password)
    {
        var name = gameName = $"cx{Random.Shared.Next(100, 999)}{_gameCount++}";
        var pass = password = $"p{Random.Shared.Next(1000, 9999)}";
        return CreateGame(client, role, name, pass);
    }

    private async Task<bool> CreateGame(Client client, Role role, string gameName, string password)
    {
        var characters = await Logon(client, role);
        var character = characters == null ? null : Find(characters, role.Character);
        if (character == null)
        {
            return false;
        }

        await client.SelectCharacter(character);
        await PaceGameCreation();
        logger.LogInformation("{Role} creates game {Game} with password {Password}", role, gameName, password);
        if (!await client.CreateGame(Difficulty.Normal, gameName, password, botConfig.GameDescriptions?.FirstOrDefault() ?? "gs1"))
        {
            logger.LogError("{Role}: could not create a game", role);
            return false;
        }

        return await MuleClientSteps.WaitForInitialize(client, logger);
    }

    private async Task PaceGameCreation()
    {
        var wait = _lastGameCreated + GameCreationSpacing - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        _lastGameCreated = DateTime.UtcNow;
    }

    private async Task<bool> Join(Client client, Role role, string gameName, string password)
    {
        var characters = await Logon(client, role);
        var character = characters == null ? null : Find(characters, role.Character);
        if (character == null)
        {
            return false;
        }

        await client.SelectCharacter(character);
        if (!await client.JoinGame(gameName, password))
        {
            logger.LogError("{Role}: could not join {Game}", role, gameName);
            return false;
        }

        if (!await MuleClientSteps.WaitForInitialize(client, logger))
        {
            return false;
        }

        InventoryHelpers.CleanupCursorItem(client.Game);
        return true;
    }

    private async Task<List<Character>> Logon(Client client, Role role)
    {
        if (!client.Connect(botConfig.Realm, botConfig.KeyOwner, botConfig.GameFolder))
        {
            logger.LogError("Could not connect to the realm");
            return null;
        }

        var characters = await client.Login(role.Account, role.Password);
        if (characters == null)
        {
            logger.LogError("Logon to {Account} failed", role.Account);
        }
        else if (role.Character != null && Find(characters, role.Character) == null)
        {
            logger.LogError("{Account} has no character {Character}", role.Account, role.Character);
        }

        return characters;
    }

    private static async Task Leave(Client client)
    {
        if (client.Game?.Me != null && client.Game.IsInGame())
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await client.Game.LeaveGame();
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        client.Disconnect();
    }

    private static Character Find(List<Character> characters, string name) =>
        characters.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
