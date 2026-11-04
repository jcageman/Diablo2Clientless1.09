using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// Lists which waypoints characters hold on a difficulty, and hands a waypoint from a character that has it
// to characters that do not:
//
//   WaypointShare --config <bot config> --muleconfig <mule config> --difficulty <Normal|Nightmare|Hell>
//       list <account>:<character>[,<account>:<character>...]
//   WaypointShare --config <bot config> --muleconfig <mule config> --difficulty <Normal|Nightmare|Hell>
//       --from <account>:<character> --to <account>:<character>[,...] share <Waypoint>
//
// list puts each character in a game of its own, walks it to its town waypoint and prints the menu.
// share has the giver create a game, take the waypoint and open a town portal beside it; each receiver
// joins, goes to that act's town, takes the portal and touches the waypoint, which activates it. A receiver
// must be able to reach the act's town by waypoint. Passwords come from the mule config, and the realm, key
// owner, game folder, game description and map API from the bot config.

Environment.Exit(await Run(args));

static async Task<int> Run(string[] args)
{
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
        || !options.TryGetValue("difficulty", out var difficultyName)
        || !Enum.TryParse<Difficulty>(difficultyName, ignoreCase: true, out var difficulty)
        || rest.Count < 2 || rest[0] is not ("list" or "share"))
    {
        Console.Error.WriteLine("usage: WaypointShare --config <bot config> --muleconfig <mule config> --difficulty <Normal|Nightmare|Hell> "
            + "list <account>:<character>[,...] | --from <account>:<character> --to <account>:<character>[,...] share <Waypoint>");
        return 2;
    }

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
        .WriteTo.File("waypoint-share.txt")
        .CreateLogger();

    GameDataLocation.Use(options.TryGetValue("datadir", out var dataDirectory)
        ? dataDirectory
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "data"));

    var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(configPath)).Build();
    var bot = config.GetSection("bot");
    var realm = bot["realm"];
    var keyOwner = bot["keyOwner"];
    var gameFolder = bot["gameFolder"];
    var description = bot.GetSection("gameDescriptions").GetChildren().Select(c => c.Value).FirstOrDefault();

    var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var account in JsonDocument.Parse(File.ReadAllText(muleConfigPath)).RootElement
                 .GetProperty("mule").GetProperty("accounts").EnumerateArray())
    {
        passwords[account.GetProperty("username").GetString()] = account.GetProperty("password").GetString();
    }

    var services = new ServiceCollection();
    services.AddLogging(builder => builder.AddSerilog());
    services.AddHttpClient();
    services.AddMemoryCache();
    services.AddSingleton<IExternalMessagingClient, SilentMessagingClient>();
    services.AddSingleton<ITownManagementService, TownManagementService>();
    services.RegisterNavigationServices(config);
    var provider = services.BuildServiceProvider();
    var tools = new Tools(
        realm, keyOwner, gameFolder, description, difficulty,
        provider.GetRequiredService<IPathingService>(),
        provider.GetRequiredService<IMapApiService>(),
        provider.GetRequiredService<ITownManagementService>());

    if (rest[0] == "list")
    {
        var failed = 0;
        foreach (var (role, index) in ParseRoles(rest[1], passwords).Select((r, i) => (r, i)))
        {
            // Games created from one machine within seconds of each other go unanswered.
            if (index > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            failed += await tools.List(role) ? 0 : 1;
        }

        return failed == 0 ? 0 : 1;
    }

    if (!Enum.TryParse<Waypoint>(rest[1], ignoreCase: true, out var waypoint)
        || !options.TryGetValue("from", out var fromSpec) || !options.TryGetValue("to", out var toSpec))
    {
        Console.Error.WriteLine("share needs --from, --to and a waypoint name such as CanyonOfTheMagi");
        return 2;
    }

    var giver = ParseRoles(fromSpec, passwords).Single();
    var receivers = ParseRoles(toSpec, passwords);
    return await tools.Share(giver, receivers, waypoint) ? 0 : 1;
}

static List<Role> ParseRoles(string spec, Dictionary<string, string> passwords)
{
    var roles = new List<Role>();
    foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var pieces = part.Split(':');
        if (pieces.Length != 2 || !passwords.TryGetValue(pieces[0], out var password))
        {
            throw new ArgumentException($"{part} is not <account>:<character> with the account in the mule config");
        }

        roles.Add(new Role(pieces[0], password, pieces[1]));
    }

    return roles;
}

internal sealed class SilentMessagingClient : IExternalMessagingClient
{
    public void RegisterClient(Client client)
    {
    }

    public Task SendMessage(string message) => Task.CompletedTask;
}

internal sealed record Role(string Account, string Password, string Character);

internal sealed class Tools(
    string realm,
    string keyOwner,
    string gameFolder,
    string description,
    Difficulty difficulty,
    IPathingService pathingService,
    IMapApiService mapApiService,
    ITownManagementService townManagementService)
{
    public async Task<bool> List(Role role)
    {
        var client = await Login(role);
        if (client == null)
        {
            return false;
        }

        try
        {
            if (!await CreateGame(client, role, out var _))
            {
                return false;
            }

            var waypoints = await ReadTownWaypoints(client);
            if (waypoints == null)
            {
                Console.Error.WriteLine($"{role.Character}: the waypoint menu did not open");
                return false;
            }

            Console.WriteLine($"{role.Character} on {difficulty}: {string.Join(", ", WaypointExtensions.BitOrder.Where(waypoints.Contains))}");
            await client.Game.LeaveGame();
            return true;
        }
        finally
        {
            client.Disconnect();
        }
    }

    public async Task<bool> Share(Role giverRole, List<Role> receiverRoles, Waypoint waypoint)
    {
        var area = waypoint.ToArea();
        var act = area.MapToAct();
        var giver = await Login(giverRole);
        if (giver == null)
        {
            return false;
        }

        var receivers = new List<Client>();
        try
        {
            if (!await CreateGame(giver, giverRole, out var game))
            {
                return false;
            }

            if (!await townManagementService.SwitchAct(giver, act)
                || !await townManagementService.TakeWaypoint(giver, waypoint)
                || giver.Game.Area != area)
            {
                Console.Error.WriteLine($"{giverRole.Character} could not take {waypoint}; does it hold it on {difficulty}?");
                return false;
            }

            if (!await townManagementService.CreateTownPortal(giver))
            {
                Console.Error.WriteLine($"{giverRole.Character} could not open a town portal in {area}");
                return false;
            }

            var shared = 0;
            foreach (var role in receiverRoles)
            {
                var receiver = await Login(role);
                if (receiver == null)
                {
                    continue;
                }

                receivers.Add(receiver);
                if (await Receive(receiver, role, game, giver, act, area, waypoint))
                {
                    shared++;
                }
            }

            Console.WriteLine($"{waypoint} on {difficulty}: shared with {shared} of {receiverRoles.Count}");
            return shared == receiverRoles.Count;
        }
        finally
        {
            foreach (var receiver in receivers)
            {
                await LeaveQuietly(receiver);
            }

            await LeaveQuietly(giver);
        }
    }

    private async Task<bool> Receive(Client receiver, Role role, (string Name, string Password) game, Client giver, Act act,
        Area area, Waypoint waypoint)
    {
        if (!await receiver.JoinGame(game.Name, game.Password)
            || !GeneralHelpers.TryWithTimeout(_ => receiver.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine($"{role.Character} could not join {game.Name}");
            return false;
        }

        await Task.Delay(1000);
        var giverAsSeen = receiver.Game.Players.FirstOrDefault(p =>
            p.Name.Equals(giver.Game.Me.Name, StringComparison.OrdinalIgnoreCase));

        // A town portal only takes party members of its owner: without the party the server ignored every
        // click while the receiver stood on the portal.
        var receiverAsSeen = giver.Game.Players.FirstOrDefault(p =>
            p.Name.Equals(receiver.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        if (giverAsSeen != null && receiverAsSeen != null)
        {
            giver.Game.InvitePlayer(receiverAsSeen);
            await Task.Delay(500);
            receiver.Game.AcceptInvite(giverAsSeen);
            if (!GeneralHelpers.TryWithTimeout(_ => receiver.Game.IsInPartyWith(giverAsSeen), TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine($"{role.Character} did not end up in a party with {giver.Game.Me.Name}");
            }
        }
        if (!await townManagementService.SwitchAct(receiver, act)
            || giverAsSeen == null
            || !await townManagementService.TakeTownPortalToArea(receiver, giverAsSeen, area))
        {
            Console.Error.WriteLine($"{role.Character} could not reach {area} through the portal");
            return false;
        }

        var code = waypoint.ToEntityCode();
        WorldObject target = null;
        for (var attempt = 0; attempt < 3 && target == null; attempt++)
        {
            target = WaitForObject(receiver, code);
            if (target == null)
            {
                var mode = MovementModeOf(receiver.Game);
                var path = await pathingService.GetPathToObject(receiver.Game, code, mode);
                await MovementHelpers.TakePathOfLocations(receiver.Game, path, mode);
            }
        }

        if (target == null)
        {
            Console.Error.WriteLine($"{role.Character} sees no waypoint in {area}");
            return false;
        }

        await MovementHelpers.MoveToWorldObject(receiver.Game, pathingService, mapApiService, target, MovementModeOf(receiver.Game));
        var waypoints = receiver.Game.ReadWaypoints(target);
        if (waypoints == null || !waypoints.Contains(waypoint))
        {
            Console.Error.WriteLine($"{role.Character} touched the waypoint but does not hold {waypoint}");
            return false;
        }

        Console.WriteLine($"{role.Character} now holds {waypoint} on {difficulty}");
        return true;
    }

    private static WorldObject WaitForObject(Client client, EntityCode code)
    {
        WorldObject found = null;
        GeneralHelpers.TryWithTimeout(_ =>
        {
            found = client.Game.GetEntityByCode(code)
                .OrderBy(o => o.Location.Distance(client.Game.Me.Location))
                .FirstOrDefault();
            return found != null;
        }, TimeSpan.FromSeconds(3));
        return found;
    }

    private async Task<IReadOnlyCollection<Waypoint>> ReadTownWaypoints(Client client)
    {
        var mode = MovementModeOf(client.Game);
        var path = await pathingService.ToTownWayPoint(client.Game, mode);
        await MovementHelpers.TakePathOfLocations(client.Game, path, mode);
        var townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).FirstOrDefault();
        if (townWaypoint == null)
        {
            return null;
        }

        await client.Game.MoveToAsync(townWaypoint);
        return client.Game.ReadWaypoints(townWaypoint);
    }

    private async Task<Client> Login(Role role)
    {
        var client = new Client();
        if (!client.Connect(realm, keyOwner, gameFolder))
        {
            Console.Error.WriteLine("could not connect to the realm");
            return null;
        }

        var character = (await client.Login(role.Account, role.Password))
            ?.FirstOrDefault(c => c.Name.Equals(role.Character, StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            Console.Error.WriteLine($"no character {role.Character} on {role.Account}");
            client.Disconnect();
            return null;
        }

        await client.SelectCharacter(character);
        return client;
    }

    private Task<bool> CreateGame(Client client, Role role, out (string Name, string Password) game)
    {
        game = ($"wp{Random.Shared.Next(1000, 9999)}", $"p{Random.Shared.Next(1000, 9999)}");
        var created = game;
        return CreateGameCore(client, role, created);
    }

    private async Task<bool> CreateGameCore(Client client, Role role, (string Name, string Password) game)
    {
        if (!await client.CreateGame(difficulty, game.Name, game.Password, description))
        {
            Console.Error.WriteLine($"{role.Character}: could not create a {difficulty} game");
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout(_ => client.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine($"{role.Character}: never appeared in {game.Name}");
            return false;
        }

        await Task.Delay(1500);
        return true;
    }

    private static async Task LeaveQuietly(Client client)
    {
        try
        {
            if (client.Game?.IsInGame() == true)
            {
                await client.Game.LeaveGame();
            }
        }
        catch (Exception e)
        {
            Log.Warning("Leaving failed: {Error}", e.Message);
        }

        client.Disconnect();
    }

    private static MovementMode MovementModeOf(Game game)
    {
        return game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
    }
}
