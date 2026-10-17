using ConsoleBot.Bots;
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
using Serilog;
using Serilog.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Gathers the items a nip file matches from every mule that holds them onto a few target characters
// of the same game mode - for example the tokens and essences that piled up across the general mules.
//
//   MuleConsolidate --config <bot config> --muleconfig <mule config> --pickitconfig <pickit config>
//       --nip <file> --to <account>:<character>[,<account>:<character>...]
//       [--password-configs <mule config>[,<mule config>...]] [--exclude-characters <name>[,...]] [--list]
//
// The mule database names the sources: every character outside the targets whose recorded items the
// nip matches. Sources of the other game mode than the targets are skipped. The targets fill in the
// order given. Passwords come from the mule configs, and a source account that is in none of them
// stops the run before anything moves: add its config with --password-configs. --nip is a path, or a file name
// in the mule config's nip directory. Every visited character is recorded in the mule database.
// --list prints the sources the database names and moves nothing.

Environment.Exit(await Run(args));

static async Task<int> Run(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var pairs = args.Where(a => !a.Equals("--list", StringComparison.OrdinalIgnoreCase)).ToArray();
    for (var i = 0; i + 1 < pairs.Length; i += 2)
    {
        options[pairs[i].TrimStart('-')] = pairs[i + 1];
    }

    if (!options.TryGetValue("config", out var configPath) || !options.TryGetValue("muleconfig", out var muleConfigPath)
        || !options.TryGetValue("pickitconfig", out var pickitConfigPath) || !options.TryGetValue("nip", out var nipPath)
        || !options.TryGetValue("to", out var toSpec))
    {
        Console.Error.WriteLine("usage: MuleConsolidate --config <bot config> --muleconfig <mule config> --pickitconfig <pickit config> "
            + "--nip <file> --to <account>:<character>[,...] [--password-configs <mule config>[,...]] [--exclude-characters <name>[,...]]");
        return 2;
    }

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
        .WriteTo.File("mule-consolidate.txt")
        .CreateLogger();
    var logger = new SerilogLoggerFactory(Log.Logger).CreateLogger("MuleConsolidate");

    GameDataLocation.Use(options.TryGetValue("datadir", out var dataDirectory)
        ? dataDirectory
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "data"));

    var botConfig = Section<BotConfiguration>(configPath, "bot");
    var muleConfig = Section<MuleConfiguration>(muleConfigPath, "mule");
    Pickit.Configure(Section<PickitConfiguration>(pickitConfigPath, "pickit"));

    var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in options.GetValueOrDefault("password-configs", muleConfigPath).Split(',', StringSplitOptions.RemoveEmptyEntries).Append(muleConfigPath))
    {
        foreach (var account in Section<MuleConfiguration>(path.Trim(), "mule").Accounts)
        {
            passwords.TryAdd(account.Username, account.Password);
        }
    }

    var targets = new List<Role>();
    foreach (var spec in toSpec.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = spec.Trim().Split(':');
        if (parts.Length != 2 || !passwords.TryGetValue(parts[0], out var password))
        {
            Console.Error.WriteLine($"{spec}: expected <account>:<character> with the account in a mule config");
            return 2;
        }

        targets.Add(new Role(parts[0], password, parts[1]));
    }

    var rules = NipParser.ParseFile(File.Exists(nipPath) ? nipPath : Path.Combine(muleConfig.NipDirectory ?? "", nipPath));
    bool Wanted(Item item) => rules.Any(rule => rule.MatchesExpression(new NipEvaluationContext(item)));

    var repository = new MuleRepository(DocumentStore.For(muleConfig.ConnectionString));
    var skip = new HashSet<string>(targets.Select(t => t.Character), StringComparer.OrdinalIgnoreCase);
    foreach (var name in options.GetValueOrDefault("exclude-characters", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        skip.Add(name);
    }

    var held = (await repository.GetAllItems(null, null, [], null))
        .Where(i => !skip.Contains(i.CharacterName) && Wanted(MuledItemStock.ToItem(i)))
        .GroupBy(i => (i.AccountName, i.CharacterName))
        .OrderBy(g => g.Key.AccountName).ThenBy(g => g.Key.CharacterName)
        .ToList();
    logger.LogInformation("{Count} items on {Sources} characters: {List}", held.Sum(g => g.Count()), held.Count,
        string.Join(", ", held.Select(g => $"{g.Key.CharacterName} {g.Count()}")));

    if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
    {
        return 0;
    }

    var unknown = held.Select(g => g.Key.AccountName).Where(a => !passwords.ContainsKey(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (unknown.Count > 0)
    {
        Console.Error.WriteLine($"no mule config has a password for {string.Join(", ", unknown)}; add their configs with --password-configs");
        return 2;
    }

    var sources = held.Select(g => new Role(g.Key.AccountName, passwords[g.Key.AccountName], g.Key.CharacterName)).ToList();

    var consolidator = new Consolidator(botConfig, logger, repository, Wanted);
    return await consolidator.Run(sources, targets);
}

static T Section<T>(string path, string section) =>
    new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build().GetSection(section).Get<T>();

internal sealed record Role(string Account, string Password, string Character)
{
    public override string ToString() => $"{Account}-{Character}";
}

internal sealed class Consolidator(BotConfiguration botConfig, Microsoft.Extensions.Logging.ILogger logger, IMuleRepository repository, Func<Item, bool> wanted)
{
    // Games created from one machine within seconds of each other go unanswered; 25 s apart was reliable.
    private static readonly TimeSpan GameCreationSpacing = TimeSpan.FromSeconds(25);

    private readonly MuleTransfer _transfer = new(logger);
    private DateTime _lastGameCreated = DateTime.MinValue;
    private string _gameName;
    private string _gamePassword;

    public async Task<int> Run(List<Role> sources, List<Role> targets)
    {
        var remaining = new Queue<Role>(sources);
        var moved = 0;
        foreach (var target in targets)
        {
            if (remaining.Count == 0)
            {
                break;
            }

            var receiver = new Client();
            try
            {
                var targetCharacter = await CreateGame(receiver, target);
                if (targetCharacter == null)
                {
                    return 1;
                }

                var full = false;
                while (remaining.Count > 0 && !full)
                {
                    var source = remaining.Peek();
                    var giver = new Client();
                    try
                    {
                        var joined = await Join(giver, source, targetCharacter.IsExpansion);
                        if (joined != JoinResult.Joined)
                        {
                            remaining.Dequeue();
                            continue;
                        }

                        var result = await _transfer.Run(giver, receiver, () => Offered(giver.Game));
                        moved += result.ItemsMoved;
                        // A full mule cannot pull stash items through its inventory; swapping them with
                        // inventory items it keeps gets them to the trade window. Any inventory item may take
                        // the stash cell, charms too: the mule keeps it either way.
                        while (result.Ending == TransferEnding.GiverInventoryBlocks
                            && StashSwap.SwapIntoInventory(giver.Game, Offered(giver.Game), i => !wanted(i), logger) > 0)
                        {
                            result = await _transfer.Run(giver, receiver, () => Offered(giver.Game));
                            moved += result.ItemsMoved;
                        }

                        logger.LogInformation("{Source} -> {Target}: {Ending}, {Left} matching items left on it",
                            source, target, result.Ending, Offered(giver.Game).Count);
                        await MuleClientSteps.RecordCharacter(repository, source.Account, source.Character, giver.Game, logger);
                        if (result.Ending == TransferEnding.Failed)
                        {
                            return 1;
                        }

                        if (Offered(giver.Game).Count == 0 || result.Ending != TransferEnding.ReceiverFull)
                        {
                            if (Offered(giver.Game).Count > 0)
                            {
                                logger.LogWarning("{Source}: {Count} matching items stay ({Ending})", source, Offered(giver.Game).Count, result.Ending);
                            }

                            remaining.Dequeue();
                        }
                        else
                        {
                            full = true;
                        }
                    }
                    finally
                    {
                        await Leave(giver);
                    }
                }

                await MuleClientSteps.RecordCharacter(repository, target.Account, target.Character, receiver.Game, logger);
            }
            finally
            {
                await Leave(receiver);
            }
        }

        logger.LogInformation("Moved {Moved} items; {Left} source characters not done{List}", moved, remaining.Count,
            remaining.Count == 0 ? "" : ": " + string.Join(", ", remaining));
        return remaining.Count == 0 ? 0 : 1;
    }

    private enum JoinResult
    {
        Joined,
        Skipped
    }

    private async Task<JoinResult> Join(Client client, Role role, bool expansion)
    {
        var characters = await Logon(client, role);
        var character = characters?.FirstOrDefault(c => c.Name.Equals(role.Character, StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            logger.LogWarning("{Role}: cannot log on or no such character, skipping", role);
            return JoinResult.Skipped;
        }

        if (character.IsExpansion != expansion)
        {
            logger.LogInformation("{Role} is {Mode}, the targets are not; skipping", role, character.IsExpansion ? "expansion" : "classic");
            return JoinResult.Skipped;
        }

        await client.SelectCharacter(character);
        if (!await client.JoinGame(_gameName, _gamePassword) || !await MuleClientSteps.WaitForInitialize(client, logger))
        {
            logger.LogWarning("{Role}: could not join, skipping", role);
            return JoinResult.Skipped;
        }

        InventoryHelpers.CleanupCursorItem(client.Game);
        return JoinResult.Joined;
    }

    private async Task<Character> CreateGame(Client client, Role role)
    {
        var characters = await Logon(client, role);
        var character = characters?.FirstOrDefault(c => c.Name.Equals(role.Character, StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            logger.LogError("{Role}: cannot log on or no such character", role);
            return null;
        }

        await client.SelectCharacter(character);
        var wait = _lastGameCreated + GameCreationSpacing - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        _lastGameCreated = DateTime.UtcNow;
        _gameName = $"mc{Random.Shared.Next(1000, 9999)}";
        _gamePassword = $"p{Random.Shared.Next(1000, 9999)}";
        if (!await client.CreateGame(Difficulty.Normal, _gameName, _gamePassword, botConfig.GameDescriptions.First())
            || !await MuleClientSteps.WaitForInitialize(client, logger))
        {
            logger.LogError("{Role}: could not create a game", role);
            return null;
        }

        return character;
    }

    private async Task<List<Character>> Logon(Client client, Role role)
    {
        if (!client.Connect(botConfig.Realm, botConfig.KeyOwner, botConfig.GameFolder))
        {
            return null;
        }

        return await client.Login(role.Account, role.Password);
    }

    private List<Item> Offered(Game game)
    {
        return game.Stash.Items.Concat(game.Inventory.Items).Where(i => i.IsIdentified && wanted(i)).ToList();
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
}
