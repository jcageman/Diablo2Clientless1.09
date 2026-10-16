using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.MCP;
using D2NG.Mule;
using D2NG.Pickit;
using Marten;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// Lists and creates characters on a mule account. The realm, key owner and game folder come from a bot
// config and the account's password from the mule config, so neither is ever typed or printed:
//
//   AccountTool --config <bot config> --muleconfig <mule config> --account <name> createaccount
//   AccountTool --config <bot config> --muleconfig <mule config> --account <name> list
//   AccountTool --config <bot config> --muleconfig <mule config> --account <name> create <name>:<Class>[:expansion][:hardcore] ...
//   AccountTool --config <bot config> --muleconfig <mule config> --account <name> inspect <character> [game description]
//
//   AccountTool --config <bot config> --muleconfig <mule config> --account <name> putaway <character> [game description]
//
//   AccountTool --config <bot config> --muleconfig <mule config> --pickitconfig <pickit config> --account <name> --list <tsv> [--dry-run] drop <character>|all [game description]
//
//   AccountTool --config <bot config> --muleconfig <mule config> --pickitconfig <pickit config> --account <name> record <character>[,<character>...] [game description]
//
// createaccount creates the account itself on the realm, with the password the mule config gives it,
// and then logs on to it to prove the account works. Add the account to the mule config first.
//
// inspect puts the character in a Normal game of its own and prints what it holds: the cursor, the
// inventory, the cube and the stash, per item name. It moves nothing.
// putaway does the same, then puts an item left on the cursor into a free stash cell, else a free
// inventory cell. It never drops: an item that fits nowhere stays on the cursor.
//
// drop throws away the magic, rare and crafted items a list names for this account: the classic-wipe.tsv
// the mule cleanup writes (account, character, quality, item, type, eth, sockets, stats). An item is
// dropped only when its name, quality, ethereal flag, sockets and every stat match a line, and each
// line drops at most one item. The cube is not opened. Afterwards the character is recorded in the
// mule database again, so the mule run sees the room it freed. --dry-run logs on and prints what
// would be dropped, and drops nothing. An account missing from the mule config can borrow a listed
// account's password with --password-of <account>.
//
// record puts each character in a game of its own and writes what it holds to the mule database, the
// same record a mule visit leaves. For a mule whose record went stale. It moves nothing.
//
// drop and record write the mule database, and the pickit config's inventory layout decides which
// items they record as untouchable, so both refuse to run without --pickitconfig.
//
// Character names take letters and at most one dash, fifteen at most; a digit is refused with 0x14.

// The client's listener threads are foreground threads and outlive Main, so the exit code is handed
// to Environment.Exit rather than returned.
Environment.Exit(await Run(args));

static async Task<int> Run(string[] args)
{
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
args = args.Where(a => !string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase)).ToArray();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        options[args[i][2..]] = args[++i];
    }
    else
    {
        rest.Add(args[i]);
    }
}

if (!options.TryGetValue("config", out var configPath) || !options.TryGetValue("muleconfig", out var muleConfigPath)
    || !options.TryGetValue("account", out var account) || rest.Count == 0)
{
    Console.Error.WriteLine("usage: AccountTool --config <bot config> --muleconfig <mule config> --account <name> list | create <name>:<Class>[:expansion][:hardcore] ...");
    return 2;
}

// Items cannot be told apart without the item tables, which live in a data folder beside the configs.
GameDataLocation.Use(options.TryGetValue("datadir", out var dataDirectory)
    ? dataDirectory
    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "data"));

var bot = Property(JsonDocument.Parse(File.ReadAllText(configPath)).RootElement, "bot");
var realm = Property(bot, "realm").GetString();
var keyOwner = Property(bot, "keyOwner").GetString();
var gameFolder = Property(bot, "gameFolder").GetString();

var muleConfig = Property(JsonDocument.Parse(File.ReadAllText(muleConfigPath)).RootElement, "mule");
var passwordAccount = options.GetValueOrDefault("password-of", account);
var muleAccount = Property(muleConfig, "accounts")
    .EnumerateArray()
    .FirstOrDefault(a => string.Equals(Property(a, "username").GetString(), passwordAccount, StringComparison.OrdinalIgnoreCase));
if (muleAccount.ValueKind != JsonValueKind.Object)
{
    Console.Error.WriteLine($"{passwordAccount} is not in the mule config");
    return 2;
}

var password = Property(muleAccount, "password").GetString();

if (rest[0] is "record" or "drop")
{
    if (!options.TryGetValue("pickitconfig", out var pickitConfigPath))
    {
        Console.Error.WriteLine($"{rest[0]} needs --pickitconfig <pickit config>: without it every item would be recorded as touchable");
        return 2;
    }

    Pickit.Configure(new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(pickitConfigPath)).Build().GetSection("pickit").Get<PickitConfiguration>());
}

if (rest[0] == "record")
{
    if (rest.Count < 2)
    {
        Console.Error.WriteLine("usage: AccountTool ... --account <name> record <character>[,<character>...] [game description]");
        return 2;
    }

    var recorder = new MuleRepository(DocumentStore.For(Property(muleConfig, "connectionString").GetString()));
    var failedRecords = 0;
    foreach (var (name, index) in rest[1].Split(',').Select((n, i) => (n, i)))
    {
        // Games created from one machine within seconds of each other go unanswered.
        if (index > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(25));
        }

        failedRecords += await Record(realm, keyOwner, gameFolder, account, password, name, rest.ElementAtOrDefault(2) ?? "gs1", recorder) ? 0 : 1;
    }

    return failedRecords == 0 ? 0 : 1;
}

if (rest[0] == "drop")
{
    if (!options.TryGetValue("list", out var listPath) || rest.Count < 2)
    {
        Console.Error.WriteLine("usage: AccountTool ... --account <name> --list <tsv> [--dry-run] drop <character>|all [game description]");
        return 2;
    }

    var wanted = ReadDropList(listPath, account);
    var names = rest[1] == "all" ? wanted.Keys.OrderBy(n => n).ToList() : [rest[1].ToLowerInvariant()];
    var repository = dryRun ? null : new MuleRepository(DocumentStore.For(Property(muleConfig, "connectionString").GetString()));
    var failedCharacters = 0;
    foreach (var (name, index) in names.Select((n, i) => (n, i)))
    {
        // Games created from one machine within seconds of each other go unanswered.
        if (index > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(25));
        }

        var ok = await DropListed(realm, keyOwner, gameFolder, account, password, name, wanted.GetValueOrDefault(name) ?? [],
            rest.ElementAtOrDefault(2) ?? "gs1", dryRun, repository);
        failedCharacters += ok ? 0 : 1;
    }

    return failedCharacters == 0 ? 0 : 1;
}

var client = new Client();
if (!client.Connect(realm, keyOwner, gameFolder))
{
    Console.Error.WriteLine("could not connect to the realm");
    return 1;
}

if (rest[0] == "createaccount")
{
    if (!client.CreateAccount(account, password))
    {
        Console.Error.WriteLine($"creating {account} failed; it may exist already");
        return 1;
    }

    Console.WriteLine($"created account: {account}");
}

var characters = await client.Login(account, password);
if (characters == null)
{
    Console.Error.WriteLine($"login to {account} failed");
    return 1;
}

if (rest[0] is "list" or "createaccount")
{
    Print(characters);
    return 0;
}

if (rest[0] is "inspect" or "putaway")
{
    return await Inspect(client, characters, rest.ElementAtOrDefault(1), rest.ElementAtOrDefault(2) ?? "gs3", putAway: rest[0] == "putaway");
}

if (rest[0] != "create")
{
    Console.Error.WriteLine($"unknown command {rest[0]}");
    return 2;
}

var failed = 0;
foreach (var spec in rest.Skip(1))
{
    var parts = spec.Split(':');
    var flags = CharacterFlags.None;
    if (parts.Skip(2).Contains("expansion", StringComparer.OrdinalIgnoreCase))
    {
        flags |= CharacterFlags.Expansion;
    }

    if (parts.Skip(2).Contains("hardcore", StringComparer.OrdinalIgnoreCase))
    {
        flags |= CharacterFlags.Hardcore;
    }

    if (parts.Length < 2 || !Enum.TryParse<CharacterClass>(parts[1], true, out var characterClass))
    {
        Console.Error.WriteLine($"bad spec {spec}, expected name:Class[:expansion][:hardcore]");
        failed++;
        continue;
    }

    var created = await client.CreateCharacter(parts[0], characterClass, flags);
    Console.WriteLine(created == null ? $"failed: {parts[0]}" : $"created: {created.Name} {created.Class} expansion {created.IsExpansion}");
    if (created == null)
    {
        failed++;
    }
}

// A second login on the same connection returns the list from before the creation; run list to check.
return failed == 0 ? 0 : 1;
}

static async Task<int> Inspect(Client client, List<Character> characters, string name, string description, bool putAway)
{
    var character = characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    if (character == null)
    {
        Console.Error.WriteLine($"no character {name} on this account");
        return 2;
    }

    await client.SelectCharacter(character);
    var gameName = $"in{Random.Shared.Next(1000, 9999)}";
    if (!await client.CreateGame(Difficulty.Normal, gameName, $"p{Random.Shared.Next(1000, 9999)}", description))
    {
        Console.Error.WriteLine("could not create a game");
        return 1;
    }

    for (var i = 0; i < 50 && client.Game.Me == null; i++)
    {
        await Task.Delay(100);
    }

    // Item packets keep arriving for a moment after joining.
    await Task.Delay(TimeSpan.FromSeconds(3));
    var game = client.Game;
    Console.WriteLine($"cursor: {(game.CursorItem == null ? "empty" : $"{game.CursorItem.Name} ({game.CursorItem.Quality})")}");
    foreach (var (label, items) in new[] { ("inventory", game.Inventory.Items), ("cube", game.Cube.Items), ("stash", game.Stash.Items) })
    {
        Console.WriteLine($"{label}: {items.Count} items");
        foreach (var group in items.GroupBy(i => $"{i.Name} ({i.Quality})").OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Count(),3} x {group.Key}");
        }
    }

    var result = 0;
    if (putAway && game.CursorItem != null)
    {
        result = await PutAwayCursorItem(game) ? 0 : 1;
    }

    await game.LeaveGame();
    return result;
}

static async Task<bool> PutAwayCursorItem(Game game)
{
    var held = game.CursorItem;
    var stash = game.GetEntityByCode(EntityCode.Stash).FirstOrDefault();
    if (stash != null)
    {
        var opened = await OpenStash(game, stash);
        var freeCell = game.Stash.FindFreeSpace(held);
        Console.WriteLine($"stash distance {game.Me.Location.Distance(stash.Location):F1}, opened {opened}, free cell {(freeCell == null ? "none" : $"{freeCell.X},{freeCell.Y}")}");
        // Holding an item, the server may open the stash without the confirmation OpenStash waits
        // for, so the insert is tried either way. Refused, the item simply stays on the cursor.
        if (freeCell is { } stashCell && await Place(game, held, stashCell, ItemContainer.Stash))
        {
            Console.WriteLine($"put {held.Name} into the stash at {stashCell.X},{stashCell.Y}");
            game.ClickButton(ClickType.CloseStash);
            return true;
        }

        game.ClickButton(ClickType.CloseStash);
    }

    if (game.Inventory.FindFreeSpace(held) is { } inventoryCell && await Place(game, held, inventoryCell, ItemContainer.Inventory))
    {
        Console.WriteLine($"put {held.Name} into the inventory at {inventoryCell.X},{inventoryCell.Y}");
        return true;
    }

    Console.Error.WriteLine($"{held.Name} fits nowhere; it stays on the cursor (not dropped)");
    return false;
}

static async Task<bool> OpenStash(Game game, Entity stash)
{
    // MoveToAsync sets the position it asked for; ask the server where the character really is,
    // and keep walking until that is next to the stash.
    for (var i = 0; i < 10; i++)
    {
        game.RequestUpdate(game.Me.Id);
        await Task.Delay(400);
        if (game.Me.Location.Distance(stash.Location) < 5)
        {
            break;
        }

        await game.MoveToAsync(stash);
        await Task.Delay(600);
    }

    var opened = false;
    for (var attempt = 0; attempt < 5 && !opened; attempt++)
    {
        opened = game.OpenStash(stash);
        if (!opened)
        {
            await Task.Delay(500);
        }
    }

    return opened;
}

static Dictionary<string, Dictionary<string, int>> ReadDropList(string path, string account)
{
    var droppable = new[] { "Magical", "Rare", "Crafted" };
    var byCharacter = new Dictionary<string, Dictionary<string, int>>();
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var c = line.Split('\t');
        if (c.Length < 8 || !string.Equals(c[0], account, StringComparison.OrdinalIgnoreCase) || !droppable.Contains(c[2]))
        {
            continue;
        }

        var character = c[1].ToLowerInvariant();
        if (!byCharacter.TryGetValue(character, out var counts))
        {
            counts = byCharacter[character] = [];
        }

        var signature = Signature(c[2], c[3], c[5] == "eth", c[6], c[7].Length == 0 ? [] : c[7].Split("; "));
        counts[signature] = counts.GetValueOrDefault(signature) + 1;
    }

    return byCharacter;
}

static string Signature(string quality, string name, bool ethereal, string sockets, IEnumerable<string> stats) =>
    $"{quality}|{name}|{(ethereal ? "eth" : "")}|{sockets}|{string.Join(";", stats.OrderBy(s => s, StringComparer.Ordinal))}";

static string SignatureOf(Item item, string account, string character)
{
    var muled = item.MapToMuleItem(account, character);
    return Signature(muled.QualityType, muled.ItemName, muled.Ethereal, muled.Sockets.ToString(), muled.Stats.Select(s => $"{s.Type} {s.Value}"));
}

static async Task<bool> DropListed(string realm, string keyOwner, string gameFolder, string account, string password, string name,
    Dictionary<string, int> wanted, string description, bool dryRun, IMuleRepository repository)
{
    var client = new Client();
    if (!client.Connect(realm, keyOwner, gameFolder))
    {
        Console.Error.WriteLine("could not connect to the realm");
        return false;
    }

    try
    {
        var characters = await client.Login(account, password);
        var character = characters?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            Console.Error.WriteLine($"no character {name} on {account}");
            return false;
        }

        await client.SelectCharacter(character);
        if (!await client.CreateGame(Difficulty.Normal, $"dr{Random.Shared.Next(1000, 9999)}", $"p{Random.Shared.Next(1000, 9999)}", description))
        {
            Console.Error.WriteLine($"{character.Name}: could not create a game");
            return false;
        }

        for (var i = 0; i < 50 && client.Game.Me == null; i++)
        {
            await Task.Delay(100);
        }

        // Item packets keep arriving for a moment after joining.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var game = client.Game;
        if (game.CursorItem != null)
        {
            Console.Error.WriteLine($"{character.Name}: the cursor holds {game.CursorItem.Name}; run putaway first");
            await game.LeaveGame();
            return false;
        }

        var remaining = new Dictionary<string, int>(wanted);
        var matches = new List<(string Where, Item Item)>();
        foreach (var (where, items) in new[] { ("stash", game.Stash.Items), ("inventory", game.Inventory.Items), ("cube", game.Cube.Items) })
        {
            foreach (var item in items)
            {
                var signature = SignatureOf(item, account, character.Name);
                if (remaining.GetValueOrDefault(signature) > 0)
                {
                    remaining[signature]--;
                    matches.Add((where, item));
                }
            }
        }

        var droppable = matches.Where(m => m.Where != "cube").ToList();
        Console.WriteLine($"{character.Name}: {wanted.Values.Sum()} listed, {droppable.Count} found in stash and inventory, "
            + $"{matches.Count - droppable.Count} in the cube (left alone), {remaining.Values.Sum()} not found");
        foreach (var (where, item) in matches)
        {
            var verb = where == "cube" ? "keep" : dryRun ? "would drop" : "drop";
            Console.WriteLine($"  {verb} [{where}] {item.Quality} {item.Name}: {string.Join(", ", item.MapToMuleItem(account, character.Name).Stats.Select(s => $"{s.Type} {s.Value}"))}");
        }

        foreach (var missing in remaining.Where(r => r.Value > 0))
        {
            Console.WriteLine($"  not found x{missing.Value}: {missing.Key}");
        }

        if (dryRun || droppable.Count == 0)
        {
            await game.LeaveGame();
            return true;
        }

        var stash = game.GetEntityByCode(EntityCode.Stash).FirstOrDefault();
        if (droppable.Any(m => m.Where == "stash") && (stash == null || !await OpenStash(game, stash)))
        {
            Console.Error.WriteLine($"{character.Name}: could not open the stash; dropping nothing");
            await game.LeaveGame();
            return false;
        }

        var dropped = 0;
        foreach (var (_, item) in droppable)
        {
            if (!await DropOne(game, item))
            {
                Console.Error.WriteLine($"{character.Name}: dropping {item.Name} failed");
                if (game.CursorItem != null)
                {
                    await PutAwayCursorItem(game);
                }

                break;
            }

            dropped++;
        }

        game.ClickButton(ClickType.CloseStash);
        Console.WriteLine($"{character.Name}: dropped {dropped} of {droppable.Count}");
        await Task.Delay(TimeSpan.FromSeconds(1));
        var snapshot = MuleCharacterSnapshot.Take(game, i => Pickit.CanTouchInventoryItem(game, i));
        await repository.UpdateCharacter(account, character.Name, snapshot);
        Console.WriteLine($"{character.Name}: recorded {snapshot.Items.Count} items and {snapshot.FreeCells} free cells in the mule database");
        await game.LeaveGame();
        return dropped == droppable.Count;
    }
    finally
    {
        client.Disconnect();
    }
}

static async Task<bool> Record(string realm, string keyOwner, string gameFolder, string account, string password, string name,
    string description, IMuleRepository repository)
{
    var client = new Client();
    if (!client.Connect(realm, keyOwner, gameFolder))
    {
        Console.Error.WriteLine("could not connect to the realm");
        return false;
    }

    try
    {
        var characters = await client.Login(account, password);
        var character = characters?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            Console.Error.WriteLine($"no character {name} on {account}");
            return false;
        }

        await client.SelectCharacter(character);
        if (!await client.CreateGame(Difficulty.Normal, $"rc{Random.Shared.Next(1000, 9999)}", $"p{Random.Shared.Next(1000, 9999)}", description))
        {
            Console.Error.WriteLine($"{character.Name}: could not create a game");
            return false;
        }

        for (var i = 0; i < 50 && client.Game.Me == null; i++)
        {
            await Task.Delay(100);
        }

        // Item packets keep arriving for a moment after joining.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var snapshot = MuleCharacterSnapshot.Take(client.Game, i => Pickit.CanTouchInventoryItem(client.Game, i));
        await repository.UpdateCharacter(account, character.Name, snapshot);
        Console.WriteLine($"{character.Name}: recorded {snapshot.Items.Count} items and {snapshot.FreeCells} free cells");
        await client.Game.LeaveGame();
        return true;
    }
    finally
    {
        client.Disconnect();
    }
}

static async Task<bool> DropOne(Game game, Item item)
{
    game.RemoveItemFromContainer(item);
    if (!await WaitFor(() => game.CursorItem?.Id == item.Id))
    {
        return false;
    }

    game.DropItem(item);
    return await WaitFor(() => game.CursorItem == null);
}

static async Task<bool> WaitFor(Func<bool> condition)
{
    for (var i = 0; i < 20; i++)
    {
        if (condition())
        {
            return true;
        }

        await Task.Delay(100);
    }

    return false;
}

static async Task<bool> Place(Game game, Item item, D2NG.Core.D2GS.Point cell, ItemContainer container)
{
    game.InsertItemIntoContainer(item, cell, container);
    for (var i = 0; i < 20; i++)
    {
        if (game.CursorItem == null)
        {
            return true;
        }

        await Task.Delay(100);
    }

    return false;
}

static void Print(List<Character> characters)
{
    foreach (var c in characters)
    {
        Console.WriteLine($"{c.Name,-16} {c.Class,-12} level {c.Level,2} expansion {c.IsExpansion} hardcore {c.IsHardCore}");
    }

    Console.WriteLine($"{characters.Count} characters");
}

static JsonElement Property(JsonElement element, string name) =>
    element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
