using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Trace;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TraceTool;

/// <summary>The query commands. Every "next:" line reuses the target as it was typed, so it runs from the same working directory.</summary>
internal sealed partial class Commands(string[] args, TextWriter output)
{
    private const int DefaultLimit = 200;

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly string[] Names = ["incidents", "timeline", "state", "packets", "unit", "damage", "activity", "tiles", "seen", "kpi", "items", "mana", "find", "export"];

    private static readonly HashSet<string> TakesValue =
    [
        "--client", "--from", "--to", "--at", "--limit", "--datadir", "--radius", "--select", "--type", "--history", "--map-url", "--map-cache",
        "--kind", "--checks", "--at-events", "--code", "--area", "--since", "--point", "--still", "--clients", "--game-prefix", "--rules", "--items", "--item", "--game", "--motion", "--split", "--where", "--out",
    ];

    private string Value(string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private bool Flag(string name) => args.Contains(name);

    private int Limit => Value("--limit") is { } limit ? int.Parse(limit, CultureInfo.InvariantCulture) : DefaultLimit;

    private bool AsJson => Flag("--json");

    /// <summary>--items full: every item with what its packet decoded, not only name, quality and place.</summary>
    private bool FullItems => Value("--items") is { } items && (items == "full" ? true : items == "compact" ? false : throw new ArgumentException("--items takes full or compact"));

    private TimeSpan From => Time("--from", TimeSpan.MinValue);

    private TimeSpan To => Time("--to", TimeSpan.MaxValue);

    /// <summary>The one game the command answers for, which --from, --to and --at are read against; null over many games.</summary>
    private GameTrace _window;

    private readonly HashSet<string> _timeNoted = [];

    /// <summary>
    /// --from, --to or --at: against one game an offset inside it or a clock time (hh:mm:ss, local time) within it,
    /// see <see cref="TraceTime.Resolve"/>; over many games an offset only. A window that ends before it starts is an error.
    /// </summary>
    private TimeSpan Time(string name, TimeSpan none)
    {
        if (Value(name) is not { } text)
        {
            return none;
        }

        TimeSpan time;
        if (_window == null)
        {
            if (text.Trim().Split(':').Length == 3)
            {
                throw new ArgumentException($"{name} {text}: a value with two colons may be a clock time, which only a command on one game can read; over many games give the offset as mm:ss (minutes may run past 59) or seconds");
            }

            time = TraceTime.Parse(text);
        }
        else
        {
            time = TraceTime.Resolve(text, _window.StartUtc, _window.EndUtc, TimeZoneInfo.Local, out var note);
            if (note != null && _timeNoted.Add(name))
            {
                (AsJson ? Console.Error : output).WriteLine($"note: {name} {note}");
            }
        }

        if (name == "--to" && Value("--from") != null && time < From)
        {
            throw new ArgumentException($"--to {text} ({TraceTime.Format(time)}) is before --from {Value("--from")} ({TraceTime.Format(From)}): the window would be empty"
                + (_window == null ? "" : $"; {TraceTime.Span(_window.StartUtc, _window.EndUtc, TimeZoneInfo.Local)}"));
        }

        return time;
    }

    private string _typed;

    /// <summary>The arguments that are not options or option values: the command, then targets.</summary>
    private List<string> Positional()
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (TakesValue.Contains(args[i]))
            {
                i++;
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(args[i]);
            }
        }

        return positional;
    }

    public int Run()
    {
        var positional = Positional();
        if (positional.Count < 2)
        {
            return Usage();
        }

        var command = positional[0];
        var target = command == "kpi" && positional.Count >= 3 ? positional[2] : positional[1];
        _typed = target;
        using (Profile.Time("start: data location"))
        {
            if (!TraceDataLocation.Use(Value("--datadir"), target))
            {
                Console.Error.WriteLine($"no item data found: item packets will not parse; pass --datadir or set {TraceDataLocation.EnvironmentVariable}");
            }
        }

        System.Threading.Tasks.Task.Run(Replayer.WarmUp);

        using var commandPhase = Profile.Time("command (wall)");
        if (Value("--split") is { } split && command is "incidents" or "activity" or "kpi" && !GameTrace.IsGameTarget(target))
        {
            return Split(split, target, command switch
            {
                "incidents" => Incidents,
                "activity" => Activity,
                _ => KpiXp,
            });
        }

        return command switch
        {
            "incidents" => Incidents(target),
            "timeline" => Timeline(target),
            "state" when Value("--at-events") != null => StateAtEvents(target),
            "state" => State(target),
            "packets" => Packets(target),
            "items" => Items(target),
            "unit" when positional.Count >= 3 => Unit(target, positional[2]),
            "damage" => Damage(target),
            "mana" => Mana(target),
            "find" => Find(target),
            "export" => Export(target),
            "activity" => Activity(target),
            "tiles" => Tiles(target),
            "seen" => Seen(target),
            "kpi" when positional.Count >= 3 && positional[1] == "xp" => KpiXp(target),
            _ => Usage(),
        };
    }

    /// <summary>The whole usage text; <paramref name="more"/> are the other commands' lines, printed before the notes.</summary>
    public static int Usage(string more = null)
    {
        Console.Error.WriteLine("""
            usage: Trace incidents <game, date or root folder> [--kind K] [--checks state|bot|all] [--from T] [--to T] [--history FILE | --no-history] [--map-url URL] [--map-cache DIR] [--limit N] [--json]
                   Trace timeline <game> [--client C] [--from T] [--to T] [--debug] [--radius 25] [--all] [--moves|--idle] [--still 5] [--limit N] [--json]
                                  --idle lists standing still of --still seconds or more, in town too (marked "town idle"); a typed move
                                  the server's position does not confirm is marked NOT CONFIRMED
                   Trace activity <game or date folder> [--client C] [--from T] [--to T] [--by-step] [--town] [--skills] [--idle [--still 1]] [--latency] [--phases [--slowest 4]] [--limit N] [--json]
                   Trace activity <date or root folder> --party-idle [--limit N] [--json]
                                  from a party's first attack to its last, the time no member was fighting and what each was doing then
                   Trace activity <game, date or root folder> --area-spells [--by-distance] [--client C] [--json]
                                  each Static Field, Nova and Frost Nova credited with the monster life that fell within its radius
                                  of the caster in the next 0.6s: per client and skill the share of casts that took life, per cast and per second
                   Trace activity <game, date or root folder> --party-spread [--alone 30] [--limit N] [--json]
                                  each second of the hunt (the area the party spent most time in), every member's distance to the nearest
                                  other one: the share of it each spent alone, farther than --alone from all of them, and the median
                   Trace activity <date or root folder> --prepare [--limit N] [--json]
                                  a party bot's run waits for every member's prepare: per game whose ended last and how long it held the
                                  others, per client how often it was last and the length of its prepare
                   Trace activity <game, date or root folder> [--client C] [--from T] [--to T] --skill-moves [--limit N] [--json]
                                  each skill move the server played out (Whirlwind, Charge, Leap: the Skillmove effect; Teleport: cast to reassign):
                                  the point asked and how often, the server's positions before and after, its length, monsters hit along it, mana;
                                  over a folder one line per client: moves per skill, how many went nowhere and the seconds they took, the mean move
                                  --idle: each stand-still with what the client sent before and after, a cause, and its split into server wait (a request
                                  unanswered), typed wait and bot wait; --latency: client request to server answer per kind; --phases: per game out, town,
                                  travel, kill, loot, return (a farming client's outings split at its first and last attack); over a date folder the slowest games
                                  are set against the median game, each with a cause line from 5 games on (the top cause, the seconds it explains, the next
                                  two; a death always first), and every failed step gets one too, from its own window only: a death, chicken, lost connection
                                  or refused waypoint or portal in its last 5 s ended it and comes first; "trace ends mid-step" when nothing explains a step
                                  that never ended
                   Trace state <game> --client C --at T [--select self,units:40,players,portals,objects:20,inventory,bag,cube,stash,belt,equipped,cursor,quests,party] [--items full] [--motion legacy|anchored|simulated|server]
                                  --motion places monsters in units: legacy (default, a move's destination), anchored (last exact position),
                                  simulated (moved along its path; needs monster speeds), server (the last packet's position, with its kind and age)
                                  inventory lists bag (the inventory grid), cube and stash together; equipped lists the body slots, both weapon sets
                                  when the client knows the second, and the mercenary's items; --items full adds what each item packet decoded
                   Trace items <game> [--client C] [--from T] [--to T] [--item 0x1C9|name] [--kind K] [--items full] [--limit N] [--json]
                                  every item event per client from the packets - picked up, to cursor, put (with the cell), equipped, dropped,
                                  sold and bought or gambled (gold), identified, transmuted, used, traded, removed by the server - with when the
                                  client asked and the server answered; UNANSWERED marks a request the server never answered within 5s; a summary
                                  per client and for the game comes first
                   Trace state <date or root folder> --at-events kind[:name[:outcome]] [--select ...] [--limit N]     one JSON line per matching event
                   Trace packets <game> --client C [--from T] [--to T] [--type 0x0C,0x95] [--sent|--received] [--realm] [--limit N] [--json]
                   Trace unit <game> --client C <npc:0x1C9 | player:0x1 | object:0x2E | 0x1C9 | code name> [--from T] [--to T] [--limit N] [--json]
                   Trace damage <game> --client C [--from T] [--to T] [--limit N] [--json]
                   Trace mana <game> --client C [--from T] [--to T] [--all] [--limit N] [--json]
                                  every fall and rise of the client's mana by cause: its own casts (the skill, its typical cost), mana burn (a ManaBurn
                                  monster's attack named it in the second before), mana and rejuvenation potions (refilling 5 s), leech, regeneration,
                                  unattributed; with what the client was doing and the nearest ManaBurn monster. --all lists regeneration too
                   Trace export <game> --client C --at T [--radius 40] --out <file.json>
                                  a recorded moment as a game-free test fixture: the raw collision grid around the client (needs the map service),
                                  the client (class, position, life, mana, skills, potions) and every monster in sight with its position under each
                                  motion model and the server's; no names - the client is "self", other players "player1", "player2"...
                   Trace find <date or root folder> [--game NAME] [--at hh:mm[:ss] | --at "yyyy-MM-dd hh:mm:ss"] [--clients a,b] [--json]
                                  the game ref the other commands take for a game name and a log's wall-clock time (local; over a root folder with
                                  its date): ref, start and end
                                  clock, length, clients and their trace files; without a match within a game's window, the nearest games
                   Trace tiles <game> (--client C --at T | --point x,y --area A) [--radius 20] [--map-url URL] [--map-cache DIR]
                   Trace seen <game, date or root folder> --code <[npc:|object:]code name> [--area A] [--limit N] [--json]
                   Trace kpi xp <game, date or root folder> [--client C] [--json]
                   Trace kpi xp <date or root folder> --leave-after [--client C] [--json]     the xp/min in each 15 s after a game's first experience
                                  gain, and what every game would have made had it been left 10, 20, 30 ... s after it: when a run that ends by
                                  choice (a level cleared to the last pack) stops paying for itself
            """.TrimEnd());
        if (more != null)
        {
            Console.Error.WriteLine(more.TrimEnd());
        }

        Console.Error.WriteLine("""
            <game> is a game folder, or folder#N when the folder holds several games of the same name. A game name's folders in one date
              folder - <name>, <name>~2, <name>~3... and a <name>~<8 hex> fallback - are read together: files go to games by game server id
              wherever they landed, and #N numbers the games of all of them in start order; the folder named must hold a file of game #N (refs
              print the folder of its earliest file).
              A rejoin after midnight is found in the next date folder's <name> or <name>~N.
            over many games (incidents, activity, state --at-events, seen, kpi xp), --clients a,b keeps games with those clients, --game-prefix P games whose name starts with P and
              --since yyyy-MM-ddTHH:mm games started from then on; activity, incidents and kpi xp report one section per party (the games' set of clients).
              --where key=value[,key=value] keeps games played under those bot settings (value "none" for a game without the setting); incidents, activity and
              kpi xp take --split key: one block per value of the setting, "(not recorded)" last. Settings are what the bot wrote on entering each game -
              botType, monsterMotion (configured), monsterMotionEffective and the bot's own switches with the value in effect, defaults included
              (travincal: identifyWithTome, movementSpeedFactor; games recorded while the switch existed also have wallAwareWhirl - every Travincal
              game since has no such key and fights wall-aware); a game
              recorded before that takes monsterMotion and monsterMotionEffective from its process's startup log line (every sidecar of the process
              key, "a|b" when they differ).
            incidents compares a game with the earlier games of its bot under the same trace root (7 days); --history names the
              cache of what each game held (default %LOCALAPPDATA%\D2NG\Trace\trace-history.json); deleting it changes no answer.
            incidents' map checks and tiles read the map service (--map-url, default D2NG_MAP_URL or https://localhost:8080) and
              cache its answers in --map-cache (default D2NG_MAP_CACHE, else %TEMP%\d2trace-maps).
            times are offsets from the game start: mm:ss.fff, h:mm:ss or seconds. On one game --from, --to and --at also take a clock time,
              hh:mm:ss in this machine's local time (the bot's log is local, the trace header UTC; a game past midnight matches either day): a
              value with two colons that lies within the game's clock window is read as that clock time (write offsets mm:ss, minutes past 59 if
              need be), and a note says so in local and UTC. A value that is neither an offset inside the game nor a clock time within it is an
              error giving the game's span in both forms; so is an empty window. Over many games only offsets are taken.
              --limit caps list output (default 200, 0 for all).
            every command takes --datadir <dir>; without it the item data comes from D2NG_DATADIR or a data folder above the target.
            every command takes --no-cache: read and replay the trace files afresh instead of from %LOCALAPPDATA%\D2NG\Trace\cache (same output, slower).
            activity states, decided every 100 ms, the first that applies wins:
              out (no trace file open) > loading (not placed yet) > fighting (attack or skill other than Teleport, town portal, identify or a buff sent in the last 1.5s)
              > picking (item packet sent in the last 1.5s) > npc (NPC chat open, or interact/trade/waypoint packet in the last 2s)
              > buffing (an armor, shield, shout or orders cast in the last 1.5s) > town (in a town area)
              > teleporting / walking (moved over 2 subtiles in the last second; teleporting if Teleport was cast in it)
              > standing-waiting (inside a typed wait event) > standing-idle.
            the area is the one of the revealed map tile holding the position, or the area of an act load until a tile does; outside every
              revealed tile it is "unknown (last known: X)" - not town, and no area change. That means the client's believed position has left
              the map the server revealed to it: the client walked on alone while the server holds the character elsewhere (measured: median
              73, up to 326 subtiles apart). Treat it as a desync: see position mismatch incidents and the server's reassign in timeline.
            """ + TownUsage);
        return 2;
    }

    private string Ref(GameTrace game)
    {
        var (folder, index) = GameTrace.ParseTarget(_typed);
        var typedFolder = index == null ? _typed : _typed[.._typed.LastIndexOf('#')];
        var relative = Path.GetRelativePath(folder, game.Folder);
        var separator = typedFolder.Contains('\\') ? '\\' : '/';
        var path = relative == "." ? typedFolder : typedFolder.TrimEnd('/', '\\') + separator + relative.Replace('\\', separator);
        var fromHere = Path.GetRelativePath(Environment.CurrentDirectory, game.Folder);
        if (fromHere.Length < path.Length && !Path.IsPathRooted(fromHere))
        {
            path = fromHere.Replace('\\', separator);
        }

        return game.GameCount > 1 ? $"{path}#{game.Index}" : path;
    }

    private static string Shorten(string text) => text == null ? "" : text.Length > 160 ? text[..160] + ".." : text;

    private static GameQuery NoteSkipped(GameQuery query)
    {
        foreach (var (path, reason) in query.Game.Skipped)
        {
            Console.Error.WriteLine($"skipped {path}: {reason}");
        }

        return query;
    }

    private GameQuery Open(string target)
    {
        if (!GameTrace.IsGameTarget(target))
        {
            throw new InvalidDataException($"{target} is not a game folder (one holding .d2trace files)");
        }

        var query = NoteSkipped(GameQuery.Open(target));
        _window = query.Game;
        return query;
    }

    private List<GameTrace> Games(string target)
    {
        var games = GameTrace.GamesUnder(target, (folder, reason) => Console.Error.WriteLine($"skipped {folder}: {reason}"));
        if (Value("--since") is { } since)
        {
            var from = DateTime.Parse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).ToUniversalTime();
            games = [.. games.Where(g => g.StartUtc >= from)];
        }

        if (Value("--game-prefix") is { } prefix)
        {
            games = [.. games.Where(g => g.GameName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        }

        if (Value("--clients") is { } clients)
        {
            var wanted = clients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            games = [.. games.Where(g => wanted.All(w => g.Labels.Contains(w, StringComparer.OrdinalIgnoreCase)))];
        }

        if (Value("--where") is { } where)
        {
            var matches = GameSettings.Filter(where);
            games = [.. games.Where(g => matches(Shared.QueryOf(g)))];
        }

        if (_only != null)
        {
            games = [.. games.Where(g => _only.Contains(GameQuery.Identity(g)))];
        }

        return games;
    }

    private TraceSources _shared;

    /// <summary>The sidecars and realm traces every batch command of this run reads through, so a split reads each once.</summary>
    private TraceSources Shared => _shared ??= new TraceSources();

    /// <summary>While a --split runs one value's block: the games of that value.</summary>
    private HashSet<string> _only;

    /// <summary>
    /// --split key: the command once per value of the setting (see <see cref="GameSettings"/>), each block headed by the value
    /// and its game count; games without the setting form a "(not recorded)" block, last.
    /// </summary>
    private int Split(string key, string target, Func<string, int> command)
    {
        var games = Games(target);
        var blocks = games.GroupBy(g => GameSettings.ValueOf(Shared.QueryOf(g), key))
            .OrderBy(g => g.Key == GameSettings.NotRecorded)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (blocks.Count == 0)
        {
            output.WriteLine($"no game folders under {target}{FilterText()}");
            return 1;
        }

        var result = 0;
        foreach (var block in blocks)
        {
            output.WriteLine($"==== {key}={block.Key}: {block.Count()} game{(block.Count() == 1 ? "" : "s")}");
            _only = [.. block.Select(GameQuery.Identity)];
            result = Math.Max(result, command(target));
        }

        _only = null;
        return result;
    }

    private string FilterText()
        => (Value("--game-prefix") is { } prefix ? $", game names starting {prefix}" : "") + (Value("--clients") is { } clients ? $", with clients {clients}" : "")
            + (Value("--where") is { } where ? $", settings {where}" : "");

    /// <summary>The games grouped by their set of clients, a party per set, in the order each first played.</summary>
    private static List<(string Clients, List<GameTrace> Games)> Parties(IEnumerable<GameTrace> games) => D2NG.Trace.Query.Parties.Of(games);

    private static string GameRange(List<GameTrace> games)
        => games.Count == 0 ? "" : $"{games[0].StartUtc.ToLocalTime():yyyy-MM-dd HH:mm} to {games[^1].EndUtc.ToLocalTime():HH:mm}";

    private string Client(GameQuery query, bool required)
    {
        var name = Value("--client");
        if (name == null)
        {
            return required ? throw new ArgumentException($"--client is required; clients: {string.Join(", ", query.Game.Labels)}") : null;
        }

        return query.Game.ResolveClient(name);
    }

    private TraceHistory History(string target)
    {
        if (Flag("--no-history"))
        {
            return null;
        }

        var path = Value("--history") ?? TraceHistory.DefaultPath();
        var history = path == null ? TraceHistory.InMemory() : TraceHistory.Load(path);
        if (history.Problem != null)
        {
            Console.Error.WriteLine($"note: {history.Problem}");
        }

        return history;
    }

    /// <summary>The map cache grows by tens of megabytes a day of games; D2NG_MAP_CACHE keeps it off the system drive for good.</summary>
    private MapSource Maps() => new(Value("--map-url"), Value("--map-cache") ?? DefaultMapCache());

    private static string DefaultMapCache()
        => Environment.GetEnvironmentVariable("D2NG_MAP_CACHE") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Path.Combine(Path.GetTempPath(), "d2trace-maps");

    /// <summary>What the replay behind the answer assumed or could not know, once, before the answer: as note lines, or with --json as one {"notes": [...]} line.</summary>
    private void ReplayNotesHeader(GameTrace game, string client)
    {
        var notes = ReplayNotes.Of(game, client);
        if (notes.Count == 0)
        {
            return;
        }

        if (AsJson)
        {
            output.WriteLine(JsonSerializer.Serialize(new { notes }, Json));
            return;
        }

        foreach (var note in notes)
        {
            output.WriteLine($"note: {note}");
        }
    }

    private string Header(GameTrace game)
        => $"game {Ref(game)}:{game.GameName}, {string.Join(", ", game.Labels)}; started {game.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {(game.EndUtc - game.StartUtc).TotalMinutes:0.0} min"
            + (game.InProgress ? "; IN PROGRESS (a trace file was written under a minute ago; later runs will see more)" : "");

    /// <summary>", 1 in progress" when games are still being recorded, so two runs minutes apart that count differently do not read as a contradiction.</summary>
    private static string InProgressText(IEnumerable<GameTrace> games)
        => games.Count(g => g.InProgress) is > 0 and var running ? $", {running} IN PROGRESS (still being recorded)" : "";

    private int Incidents(string target)
    {
        var games = Games(target);
        if (games.Count == 0)
        {
            output.WriteLine($"no game folders under {target}{FilterText()}");
            return 1;
        }

        var sources = Shared;
        var history = History(target);
        var maps = Maps();
        var family = Value("--checks") ?? "all";
        if (family is not ("state" or "bot" or "all"))
        {
            throw new ArgumentException("--checks takes state, bot or all");
        }

        var kind = Value("--kind");
        if (games.Count == 1 && GameTrace.IsGameTarget(target))
        {
            _window = games[0];
        }

        var windowed = Value("--from") != null || Value("--to") != null;
        var (from, to) = (From, To);
        var results = new List<(GameTrace Game, List<Incident> Found)>();
        var notes = new List<string>();
        foreach (var game in games)
        {
            var query = NoteSkipped(sources.QueryOf(game));
            var (found, gameNotes) = D2NG.Trace.Query.Incidents.Find(query, new IncidentOptions { Checks = family, History = history, Maps = maps });
            notes.AddRange(gameNotes);
            results.Add((game, [.. D2NG.Trace.Query.Incidents.Within(found, from, to).Where(i => kind == null || string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase))]));
        }

        history?.Save();
        var all = results.SelectMany(r => r.Found).ToList();
        string Totals(IEnumerable<Incident> found)
        {
            var list = found.ToList();
            return list.Count == 0 ? "none" : string.Join(", ", list.GroupBy(i => i.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));
        }

        var batch = games.Count > 1;
        if (!AsJson)
        {
            output.WriteLine($"{games.Count} game{(games.Count == 1 ? "" : "s")}{(batch ? $" under {Path.GetFullPath(GameTrace.ParseTarget(target).Folder)}{FilterText()}" : "")}, {all.Count} incident{(all.Count == 1 ? "" : "s")}{(kind == null ? "" : $" of kind {kind}")}{(windowed ? $" whose time or episode overlaps {(from == TimeSpan.MinValue ? "the start" : TraceTime.Format(from))} to {(to == TimeSpan.MaxValue ? "the end" : TraceTime.Format(to))}{(batch ? " of each game" : "")}" : "")}, checks {family}: {Totals(all)}"
                + (batch ? "; game refs and next: lines are relative to the working directory this ran in, and run from there as printed" : ""));
        }

        var capped = new CappedOutput(output, Limit, batch ? "narrow with --kind, --clients, --game-prefix or to one game" : "narrow with --kind or --checks");
        foreach (var (clientSet, partyGames) in batch ? Parties(results.Select(r => r.Game)) : [(null, [.. results.Select(r => r.Game)])])
        {
            var party = results.Where(r => partyGames.Contains(r.Game)).ToList();
            if (batch && !AsJson)
            {
                capped.Line($"== party {clientSet}: {partyGames.Count} games ({GameRange(partyGames)}{InProgressText(partyGames)}), {party.Sum(r => r.Found.Count)} incidents: {Totals(party.SelectMany(r => r.Found))}");
                foreach (var line in IncidentSummary.Lines(party.SelectMany(r => r.Found.Select(i => (Ref(r.Game), i)))))
                {
                    capped.Line($"  {line}");
                }

                capped.Line("  per game:");
            }

            var clean = new List<GameTrace>();
            void FlushClean()
            {
                if (clean.Count > 0 && !AsJson && kind == null)
                {
                    capped.Line(clean.Count == 1
                        ? $"{Ref(clean[0])}, {(clean[0].EndUtc - clean[0].StartUtc).TotalMinutes:0.0} min{(clean[0].InProgress ? ", IN PROGRESS" : "")}: clean"
                        : $"{clean.Count} games clean: {Ref(clean[0])}..{Path.GetFileName(Ref(clean[^1]))}");
                }

                clean.Clear();
            }

            foreach (var (game, found) in party)
            {
                if (found.Count == 0)
                {
                    clean.Add(game);
                    continue;
                }

                FlushClean();
                var reference = Ref(game);
                if (!AsJson && batch)
                {
                    capped.Line($"{reference}, {(game.EndUtc - game.StartUtc).TotalMinutes:0.0} min{(game.InProgress ? ", IN PROGRESS" : "")}: {string.Join(", ", found.GroupBy(i => i.Kind).Select(g => $"{g.Count()} {g.Key}"))}");
                }
                else if (!AsJson)
                {
                    capped.Line(Header(game));
                }

                foreach (var incident in found)
                {
                    var next = D2NG.Trace.Query.Incidents.NextCommand(reference, incident);
                    capped.Line(AsJson
                        ? JsonSerializer.Serialize(new { game = reference, party = clientSet, t = TraceTime.Format(incident.Offset), end = incident.End is { } end ? TraceTime.Format(end) : null, client = incident.Client, kind = incident.Kind, detail = incident.Detail, source = incident.Source, next }, Json)
                        : $"{TraceTime.Format(incident.Offset)} {incident.Client ?? "-",-12} {incident.Kind,-18} {incident.Detail}\n    next: {next}");
                }
            }

            FlushClean();
        }

        if (!GameTrace.IsGameTarget(target) && kind == null)
        {
            var names = games.Select(g => g.GameName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var realm = sources.Realm(games.SelectMany(g => g.RealmFiles()).Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (var refused in realm.Answers.Where(a => a.Refused && (a.Game == null || !names.Contains(a.Game))))
            {
                capped.Line(AsJson
                    ? JsonSerializer.Serialize(new { game = refused.Game, utc = refused.Utc, client = refused.Character ?? refused.Instance, kind = $"{refused.Request} refused", detail = $"result 0x{refused.Result:X2} {refused.Meaning}" }, Json)
                    : $"{refused.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss} {refused.Character ?? refused.Instance,-12} {refused.Request} refused    game {refused.Game} (no game folder): result 0x{refused.Result:X2} {refused.Meaning}");
            }

            var buckets = BetweenGames.Of(games, sources);
            if (buckets.Count > 0 && !AsJson)
            {
                capped.Line($"between games: {buckets.Sum(b => b.Lines.Count)} log lines of {buckets.Select(b => b.Process).Distinct().Count()} processes fall in no game's window, {buckets.Sum(b => b.Warnings)} warnings and {buckets.Sum(b => b.Errors)} errors among them (listed):");
            }

            foreach (var bucket in buckets)
            {
                var where = bucket.Before == null ? $"before {Ref(bucket.After)}" : bucket.After == null ? $"after {Ref(bucket.Before)}" : $"between {Ref(bucket.Before)} and {Ref(bucket.After)}";
                if (AsJson)
                {
                    capped.Line(JsonSerializer.Serialize(new { kind = "between games", process = bucket.Process, where, lines = bucket.Lines.Count, warnings = bucket.Warnings, errors = bucket.Errors, notable = bucket.Notable.Select(l => new { utc = l.Utc, level = l.Level, message = l.Message }) }, Json));
                    continue;
                }

                capped.Line($"  process {bucket.Process} {where}: {bucket.Lines.Count} line{(bucket.Lines.Count == 1 ? "" : "s")}, {bucket.Warnings} warning{(bucket.Warnings == 1 ? "" : "s")}, {bucket.Errors} error{(bucket.Errors == 1 ? "" : "s")} ({bucket.Lines[0].Utc.ToLocalTime():HH:mm:ss}-{bucket.Lines[^1].Utc.ToLocalTime():HH:mm:ss})");
                foreach (var line in bucket.Notable)
                {
                    capped.Line($"    {line.Utc.ToLocalTime():HH:mm:ss.fff} {line.Level} {Shorten(line.Message)}");
                }
            }
        }

        capped.Finish();
        foreach (var note in notes.GroupBy(n => n))
        {
            var text = $"note: {note.Key}{(games.Count > 1 ? $" ({note.Count()} of {games.Count} games)" : "")}";
            if (AsJson)
            {
                Console.Error.WriteLine(text);
            }
            else
            {
                output.WriteLine(text);
            }
        }

        return 0;
    }

    private int Timeline(string target)
    {
        var query = Open(target);
        var options = new TimelineOptions
        {
            Client = Client(query, required: false),
            From = From,
            To = To,
            Debug = Flag("--debug"),
            Radius = Flag("--all") ? double.MaxValue : Value("--radius") is { } radius ? double.Parse(radius, CultureInfo.InvariantCulture) : 25,
            Moves = Flag("--moves"),
            Idle = Flag("--idle"),
            Still = Value("--still") is { } still ? TraceTime.Parse(still) : TimeSpan.FromSeconds(5),
        };
        var entries = D2NG.Trace.Query.Timeline.Build(query, options);
        if (!AsJson)
        {
            output.WriteLine($"{Header(query.Game)}; client ? = a log line naming no client of the game");
        }

        ReplayNotesHeader(query.Game, options.Client);
        var capped = new CappedOutput(output, Limit, "narrow with --from/--to or --client");
        foreach (var entry in entries)
        {
            capped.Line(AsJson
                ? JsonSerializer.Serialize(new { t = TraceTime.Format(entry.Offset), end = entry.End is { } end ? TraceTime.Format(end) : null, client = entry.Client, source = entry.Source, text = entry.Text }, Json)
                : D2NG.Trace.Query.Timeline.Format(entry));
        }

        capped.Finish();
        if (!AsJson && entries.Any(e => e.Text.StartsWith(GameEvents.WithoutEvidenceMark, StringComparison.Ordinal)))
        {
            output.WriteLine(GameEvents.WithoutEvidenceLegend);
        }
        foreach (var note in D2NG.Trace.Query.Timeline.Notes(query, options))
        {
            output.WriteLine($"note: {note}");
        }

        return 0;
    }

    private int State(string target)
    {
        var query = Open(target);
        var client = Client(query, required: true);
        var at = Value("--at") != null ? Time("--at", TimeSpan.Zero) : throw new ArgumentException("--at is required");
        ReplayNotesHeader(query.Game, client);
        output.WriteLine(StateQuery.Render(query, client, at, Value("--select"), FullItems, Value("--motion")));
        return 0;
    }

    /// <summary>One JSON line per matching event in every game, with the state of the event's client at that instant; one replay pass per client.</summary>
    private int StateAtEvents(string target)
    {
        var filter = new EventFilter(Value("--at-events"));
        var parts = StateQuery.ParseSelect(Value("--select") ?? "self");
        var sources = Shared;
        var capped = new CappedOutput(output, Limit, "narrow the event filter or the folder");
        var matched = 0;
        var expanded = 0;
        var skipped = new Dictionary<string, int>();
        foreach (var game in Games(target))
        {
            var query = new GameQuery(game, sources);
            var reference = Ref(game);
            var stops = new Dictionary<string, List<(TimeSpan, IEnumerable<(string, object)>)>>();
            void Stop(string client, TimeSpan at, Func<string, IEnumerable<(string, object)>> leading)
            {
                var (clients, notInGame) = EventFilter.ClientsOf(client, game.Labels);
                if (clients.Count > 1)
                {
                    expanded++;
                }

                foreach (var one in notInGame)
                {
                    var why = $"they name {one}, who is not a client of the game";
                    skipped[why] = skipped.GetValueOrDefault(why) + 1;
                }

                foreach (var one in clients)
                {
                    (stops.TryGetValue(one, out var list) ? list : stops[one] = []).Add((at, leading(one)));
                }
            }

            if (filter.WantsIncidents)
            {
                foreach (var incident in D2NG.Trace.Query.Incidents.Find(query, new IncidentOptions { History = TraceHistory.InMemory() }).Incidents.Where(filter.Matches))
                {
                    Stop(incident.Client, incident.Offset, one => [("game", reference), ("client", one), ("event", $"incident {incident.Kind}"), ("eventClient", incident.Client ?? "party"), ("detail", incident.Detail)]);
                }
            }
            else
            {
                foreach (var (client, at, description) in filter.Events(query))
                {
                    Stop(client, at, one => [("game", reference), ("client", one), ("event", description), ("eventClient", client ?? "party")]);
                }
            }

            if (stops.Count == 0)
            {
                continue;
            }

            foreach (var (_, snapshots) in query.ReplayAll(stops.Keys, TimeSpan.MaxValue, client => new SnapshotObserver([.. stops[client].OrderBy(s => s.Item1)], parts, FullItems), o => [o]))
            {
                foreach (var line in snapshots.Lines)
                {
                    matched++;
                    capped.Line(line);
                }
            }
        }

        capped.Finish();
        if (matched == 0)
        {
            output.WriteLine($"no event matched {Value("--at-events")}");
        }

        if (expanded > 0)
        {
            Console.Error.WriteLine($"note: {expanded} events of the party or of several clients gave one line per client of the game (eventClient says whose event it was)");
        }

        foreach (var (why, count) in skipped)
        {
            Console.Error.WriteLine($"note: {count} matched events skipped: {why}");
        }

        return 0;
    }

    private int Packets(string target)
    {
        var query = Open(target);
        var client = Client(query, required: true);
        var options = new PacketOptions
        {
            From = From,
            To = To,
            Types = Value("--type") is { } types ? [.. types.Split(',').Select(t => byte.Parse(t.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture))] : null,
            Sent = Flag("--sent") ? true : Flag("--received") ? false : null,
            Realm = Flag("--realm"),
        };
        ReplayNotesHeader(query.Game, client);
        var capped = new CappedOutput(output, Limit, "narrow with --from/--to, --type or --sent/--received");
        var marked = false;
        foreach (var line in PacketQueries.Packets(query, client, options))
        {
            marked |= line.Contains(PacketQueries.WithoutEvidenceMark, StringComparison.Ordinal);
            capped.Line(AsJson ? JsonSerializer.Serialize(new { line }, Json) : line);
        }

        capped.Finish();
        if (marked && !AsJson)
        {
            output.WriteLine(GameEvents.WithoutEvidenceLegend);
        }

        return 0;
    }

    private int Items(string target)
    {
        var query = Open(target);
        var client = Client(query, required: false);
        var (from, to) = (From, To);
        var item = Value("--item");
        var kind = Value("--kind");
        var clients = client == null ? query.Game.Labels.ToList() : [client];
        var ledgers = ItemMoves.Replay(query, clients);
        var window = ledgers.Select(l => (l.Client, Events: l.Ledger.Events.Concat(ItemMoves.Findings(l.Client, l.Check)).Where(e => e.At >= from && e.At <= to).ToList(), l.Check)).ToList();
        if (!AsJson)
        {
            output.WriteLine(Header(query.Game));
        }

        ReplayNotesHeader(query.Game, client);
        var summaries = window.Select(w => ItemMoves.Summarize(w.Client, w.Events)).ToList();
        var windowed = Value("--from") != null || Value("--to") != null;
        if (AsJson)
        {
            foreach (var summary in summaries.Append(ItemMoves.Total(summaries)))
            {
                output.WriteLine(JsonSerializer.Serialize(new { summary }, Json));
            }
        }
        else
        {
            output.WriteLine($"summary{(windowed ? $" of {(from == TimeSpan.MinValue ? "the start" : TraceTime.Format(from))} to {(to == TimeSpan.MaxValue ? "the end" : TraceTime.Format(to))}" : "")}:");
            foreach (var summary in summaries)
            {
                output.WriteLine($"  {ItemMoves.Format(summary)}");
            }

            if (summaries.Count > 1)
            {
                output.WriteLine($"  {ItemMoves.Format(ItemMoves.Total(summaries))}");
            }

            foreach (var (name, _, check) in window)
            {
                output.WriteLine($"  {name}: its items were held against the server's word {check.Checked} times and its grids' marked cells against its items {check.CellChecks} times over the whole game; "
                    + (check.Found.Count == 0 ? "all agreed" : $"{check.Found.Count} disagreed (in capitals below; also incidents --checks state)"));
            }
        }

        var events = window.SelectMany(w => w.Events)
            .Where(e => ItemMoves.Matches(e, item) && (kind == null || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.At)
            .ThenBy(e => e.Client, StringComparer.Ordinal)
            .ToList();
        var capped = new CappedOutput(output, Limit, "narrow with --from/--to, --client, --item or --kind");
        foreach (var e in events)
        {
            capped.Line(AsJson
                ? JsonSerializer.Serialize(new
                {
                    t = TraceTime.Format(e.At),
                    client = e.Client,
                    kind = e.Kind,
                    item = e.Item == 0 ? null : D2NG.Trace.Query.Names.Id(e.Item),
                    name = e.Name,
                    detail = e.Detail,
                    asked = e.Asked is { } asked ? TraceTime.Format(asked) : null,
                    request = e.Request,
                    repeats = e.Repeats,
                    answeredMs = e.Asked is { } a && e.Kind != ItemKinds.Unanswered ? Math.Round((e.At - a).TotalMilliseconds) : (double?)null,
                    unasked = e.Unasked,
                    gold = e.Gold,
                    pile = e.Pile,
                    details = e.Details,
                }, Json)
                : ItemMoves.Format(e, FullItems));
        }

        capped.Finish();
        if (events.Count == 0 && !AsJson)
        {
            output.WriteLine($"no item event{(item == null ? "" : $" about {item}")}{(kind == null ? "" : $" of kind {kind}")} in the window");
        }

        return 0;
    }

    private int Unit(string target, string unit)
    {
        var query = Open(target);
        var client = Client(query, required: true);
        ReplayNotesHeader(query.Game, client);
        if (!PacketQueries.TryUnit(unit, out var type, out var id))
        {
            var found = PacketQueries.FindUnits(query, client, unit);
            if (found.Count == 0)
            {
                output.WriteLine($"no monster or object whose code name contains '{unit}' was assigned to {client}");
                return 1;
            }

            var exact = found.Count(f => f.Exact);
            output.WriteLine(exact > 0
                ? $"{found.Count} units match '{unit}', {exact} by exact code name (listed first):"
                : $"{found.Count} units have '{unit}' in their code name, none exactly:");
            foreach (var f in found.Take(20))
            {
                output.WriteLine($"  {f.Type.ToString().ToLowerInvariant()}:{D2NG.Trace.Query.Names.Id(f.Id),-8} {f.Code,-22} first {TraceTime.Format(f.First)} at {f.At}, last named {TraceTime.Format(f.Last)}");
            }

            if (found.Count > 20)
            {
                output.WriteLine($"  ... {found.Count - 20} more; give type:id to see another");
            }

            (type, id) = (found[0].Type, found[0].Id);
            output.WriteLine($"history of {type.ToString().ToLowerInvariant()}:{D2NG.Trace.Query.Names.Id(id)} ({found[0].Code}):");
        }

        var history = new UnitHistory(type, id, From, To);
        var context = ClientReplay.Run(query.Game, client, To, [history]);
        context?.Replayer.Dispose();
        var capped = new CappedOutput(output, Limit, "narrow with --from/--to");
        foreach (var (packet, state) in history.Entries)
        {
            capped.Line(AsJson ? JsonSerializer.Serialize(new { packet, state = state.Trim() }, Json) : $"{packet}\n{state}");
        }

        capped.Finish();
        if (history.Entries.Count == 0)
        {
            output.WriteLine($"no packet of {client} names {(type == null ? "" : type.ToString().ToLowerInvariant() + ":")}{D2NG.Trace.Query.Names.Id(id)} in that window");
        }
        else if (type == null && history.Entries.Select(e => e.Packet[e.Packet.LastIndexOf('(')..]).Distinct().Count() > 1)
        {
            output.WriteLine($"note: ids are unique per entity type only, and this id names units of several types; give npc:{D2NG.Trace.Query.Names.Id(id)}, player:..., object:... to see one");
        }

        return 0;
    }

    private int Damage(string target)
    {
        var query = Open(target);
        var client = Client(query, required: true);
        var damage = new DamageObserver(From, To);
        var context = ClientReplay.Run(query.Game, client, To, [damage]);
        context?.Replayer.Dispose();
        ReplayNotesHeader(query.Game, client);
        if (AsJson)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                client,
                lost = damage.TotalLost,
                gained = damage.TotalGained,
                drops = damage.Drops,
                attackers = damage.Attackers.Values.OrderByDescending(a => a.Lost).Select(a => new { name = a.Name, basis = a.Basis, lost = Math.Round(a.Lost), drops = a.Drops, first = TraceTime.Format(a.First), last = TraceTime.Format(a.Last), how = a.Hows.ToArray() }),
            }, Json));
            return 0;
        }

        var capped = new CappedOutput(output, Limit, "narrow with --from/--to");
        foreach (var line in damage.Report(client))
        {
            capped.Line(line);
        }

        capped.Finish();
        return 0;
    }

    private const int LongestShown = 5;

    private int Activity(string target)
    {
        if (GameTrace.IsGameTarget(target))
        {
            return ActivityOfGame(Open(target));
        }

        var games = Games(target);
        if (games.Count == 0)
        {
            output.WriteLine($"no game folders under {target}{FilterText()}");
            return 1;
        }

        if (games.Count == 1 && !Flag("--prepare") && !Flag("--party-idle") && !Flag("--party-spread") && !Flag("--area-spells"))
        {
            _window = games[0];
            return ActivityOfGame(new GameQuery(games[0]));
        }

        return Flag("--skill-moves") ? SkillMovesOfBatch(games)
            : Flag("--prepare") ? PrepareHoldups(games)
            : Flag("--party-idle") ? PartyIdle(games)
            : Flag("--party-spread") ? PartySpreadOf(games)
            : Flag("--area-spells") ? AreaSpellsOf(games)
            : ActivityOfBatch(games);
    }

    private sealed record ClientActivity(string Client, ActivityObserver Activity, List<Stretch> Stretches, FightObserver Fights, TownObserver Town, List<TownStay> Stays, Dictionary<string, TimeSpan> Waiting, RequestObserver Requests, List<(TimeSpan Start, TimeSpan End, string Reason)> Waits);

    /// <summary>Whether the activity asked for needs requests paired with answers: stand-stills, latency or phases.</summary>
    private bool WantsRequests => Flag("--idle") || Flag("--latency") || Flag("--phases");

    private (List<ClientActivity> Clients, TimeSpan From, TimeSpan To) ComputeActivity(GameQuery query, string only, IReadOnlyList<StepSpan> steps)
    {
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var from = From < TimeSpan.Zero ? TimeSpan.Zero : From;
        var to = To > end ? end : To;
        var labels = only == null ? query.Game.Labels.ToList() : [only];
        var phase = Profile.Time("activity: replay all clients (wall)");
        var withStays = Flag("--town") || Flag("--by-step") || AsJson;
        var withRequests = WantsRequests;
        var replayed = query.ReplayAll(labels, TimeSpan.MaxValue,
            () => (Activity: new ActivityObserver(), Fights: new FightObserver(), Town: withStays ? new TownObserver() : null, Requests: withRequests ? new RequestObserver() : null),
            o => [o.Activity, o.Fights, .. o.Town == null ? [] : new IReplayObserver[] { o.Town }, .. o.Requests == null ? [] : new IReplayObserver[] { o.Requests }]);
        phase.Dispose();
        var clients = new List<ClientActivity>();
        foreach (var (client, o) in replayed)
        {
            phase = Profile.Time("activity: waits");
            var reasoned = ActivityObserver.WaitsWithReason(query, client, end);
            List<(TimeSpan Start, TimeSpan End)> waits = [.. reasoned.Select(w => (w.Start, w.End))];
            phase.Dispose();
            phase = Profile.Time("activity: town stays");
            List<TownStay> stays = withStays ? TownStays.Of(client, o.Activity, o.Town, waits, steps, TownStays.Chickens(query, client), end) : [];
            phase.Dispose();
            phase = Profile.Time("activity: stretches");
            var stretches = o.Activity.Stretches(from, to, waits);
            phase.Dispose();
            clients.Add(new ClientActivity(client, o.Activity, stretches, o.Fights, o.Town, stays, ActivityObserver.WaitingByReason(stretches, reasoned, from, to), o.Requests, reasoned));
        }

        return (clients, from, to);
    }

    private static List<TownStay> StaysWithin(IEnumerable<TownStay> stays, TimeSpan from, TimeSpan to)
        => [.. stays.Select(s => s.Within(from, to)).Where(s => s != null)];

    private int ActivityOfGame(GameQuery query)
    {
        if (Flag("--skill-moves"))
        {
            return SkillMoves(query);
        }

        var only = Client(query, required: false);
        var steps = Steps.Of(query);
        var (clients, from, to) = ComputeActivity(query, only, steps);
        var byStep = Flag("--by-step");
        if (AsJson)
        {
            ReplayNotesHeader(query.Game, only);
            foreach (var c in clients)
            {
                var split = new ActivitySplit();
                split.Add(c.Stretches, from, to);
                var skills = new SkillSplit();
                skills.Add(c.Fights, c.Stretches, from, to);
                var town = new TownSummary();
                town.Add(StaysWithin(c.Stays, from, to), c.Stretches);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    game = Ref(query.Game),
                    client = c.Client,
                    from = TraceTime.Format(from),
                    to = TraceTime.Format(to),
                    seconds = split.Totals.Where(t => t.Value > TimeSpan.Zero).ToDictionary(t => ActivityObserver.Name(t.Key), t => Math.Round(t.Value.TotalSeconds, 1)),
                    skills = skills.Skills.ToDictionary(s => s.Key, s => new { casts = s.Value.Casts, seconds = Math.Round(s.Value.Time.TotalSeconds, 1), hits = s.Value.Hits, lowManaSeconds = Math.Round(s.Value.LowMana.TotalSeconds, 1) }),
                    buffs = skills.Buffs.ToDictionary(s => s.Key, s => new { casts = s.Value.Casts, seconds = Math.Round(s.Value.Time.TotalSeconds, 1) }),
                    town = new
                    {
                        inTownAreas = Math.Round(town.Total.TotalSeconds, 1),
                        statesThere = town.States.Where(s => s.Value > TimeSpan.Zero).ToDictionary(s => ActivityObserver.Name(s.Key), s => Math.Round(s.Value.TotalSeconds, 1)),
                        byCause = town.ByCause.ToDictionary(t => t.Key, t => new { count = t.Value.Count, median = Math.Round(Steps.Percentiles(t.Value).Median, 1), total = Math.Round(t.Value.Sum(), 1) }),
                        inside = town.Parts.ToDictionary(p => p.Key, p => Math.Round(p.Value.TotalSeconds, 1)),
                    },
                    longest = c.Stretches.Where(s => ActivityObserver.Unproductive(s.State)).OrderByDescending(s => s.Duration).Take(LongestShown)
                        .Select(s => new { state = ActivityObserver.Name(s.State), t = TraceTime.Format(s.Start), seconds = Math.Round(s.Duration.TotalSeconds, 1), at = s.At == null ? null : new[] { s.At.X, s.At.Y }, area = s.Area }),
                    phases = Flag("--phases") ? FarmPhases.Of(c.Activity, c.Stretches, c.Requests, from, to).Where(p => p.Value > TimeSpan.Zero).ToDictionary(p => p.Key, p => Math.Round(p.Value.TotalSeconds, 1)) : null,
                    standStills = Flag("--idle") ? IdleAttribution.Of(c.Activity, c.Stretches, c.Requests, c.Waits, StillMinimum).Select(StillJson) : null,
                    latency = Flag("--latency") ? LatencyJson(c.Requests) : null,
                }, Json));
            }

            if (byStep)
            {
                foreach (var step in steps.Where(s => s.Leaf && s.End > from && s.Start < to))
                {
                    var split = StepSplit(step, clients);
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        step = step.Name,
                        attempt = step.Attempt,
                        client = step.Client,
                        parent = step.Parent?.Name,
                        t = TraceTime.Format(step.Start),
                        seconds = Math.Round(step.Duration.TotalSeconds, 1),
                        outcome = step.Outcome,
                        source = step.Source,
                        start = step.StartUnknown ? "unknown" : step.StartInferred ? "inferred" : "reported",
                        split = split.Totals.Where(t => t.Value > TimeSpan.Zero).ToDictionary(t => ActivityObserver.Name(t.Key), t => Math.Round(t.Value.TotalSeconds, 1)),
                        town = TownInStep(step, clients).ToDictionary(t => t.Cause, t => Math.Round(t.Seconds, 1)),
                        longestIdle = split.LongestIdle == null ? null : new { t = TraceTime.Format(split.LongestIdle.Start), seconds = Math.Round(split.LongestIdle.Duration.TotalSeconds, 1) },
                    }, Json));
                }
            }

            return 0;
        }

        output.WriteLine($"{Header(query.Game)}; {TraceTime.Format(from)} to {TraceTime.Format(to)} ({(to - from).TotalSeconds:0}s)");
        ReplayNotesHeader(query.Game, only);
        foreach (var c in clients)
        {
            var split = new ActivitySplit();
            split.Add(c.Stretches, from, to);
            var skills = new SkillSplit();
            skills.Add(c.Fights, c.Stretches, from, to);
            output.WriteLine($"{c.Client}: {split.Render()}");
            if (ActivityObserver.RenderWaiting(c.Waiting) is { Length: > 0 } waiting)
            {
                output.WriteLine($"  standing-waiting by typed wait reason: {waiting}");
            }

            output.WriteLine($"  fighting by skill: {skills.Short()}");
            foreach (var stretch in c.Stretches.Where(s => ActivityObserver.Unproductive(s.State)).OrderByDescending(s => s.Duration).Take(LongestShown).OrderBy(s => s.Start))
            {
                output.WriteLine($"  {TraceTime.Format(stretch.Start)} {ActivityObserver.Name(stretch.State),-16} {stretch.Duration.TotalSeconds,6:0.0}s at {D2NG.Trace.Query.Names.Point(stretch.At)} in {stretch.Area ?? "?"}");
            }

            if (Flag("--skills"))
            {
                output.WriteLine($"  skills (hits: NPCHit on a unit this client attacked in the second before, or the unit its latest cast at a point was meant for (the nearest monster in the cast's direction) within that skill's hit window, or beside it during its own skill move):");
                foreach (var line in skills.Lines())
                {
                    output.WriteLine($"    {line}");
                }
            }

            if (Flag("--town"))
            {
                var stays = StaysWithin(c.Stays, from, to);
                var town = new TownSummary();
                town.Add(stays, c.Stretches);
                output.WriteLine($"  {town.Reconcile()}");
                output.WriteLine($"  town by cause: {town.Causes()}");
                output.WriteLine($"  inside town: {town.Inside()}");
                foreach (var stay in stays)
                {
                    output.WriteLine($"    {TraceTime.Format(stay.Start)} {stay.Cause,-20} {stay.Duration.TotalSeconds,6:0.0}s{(stay.Step == null ? "" : $" during {stay.Step}")}: {string.Join(", ", stay.Split.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value.TotalSeconds:0}s"))}{(stay.Evidence == null ? "" : $"; before it: {stay.Evidence}")}");
                }
            }

            if (Flag("--phases"))
            {
                output.WriteLine($"  phases: {FarmPhases.Render(FarmPhases.Of(c.Activity, c.Stretches, c.Requests, from, to))}");
            }

            if (Flag("--idle"))
            {
                var stills = IdleAttribution.Of(c.Activity, c.Stretches, c.Requests, c.Waits, StillMinimum);
                output.WriteLine($"  {IdleAttribution.Summary(stills, StillMinimum)}");
                var shown = new CappedOutput(output, Limit, "raise --still or narrow with --from/--to");
                foreach (var still in stills)
                {
                    shown.Line($"    {TraceTime.Format(still.Start)} {IdleAttribution.Render(still)}");
                }

                shown.Finish();
            }
        }

        if (Flag("--town"))
        {
            output.WriteLine(TownHelp);
        }

        if (Flag("--latency"))
        {
            var latency = new LatencyTable();
            foreach (var c in clients)
            {
                latency.Add(c.Requests);
            }

            output.WriteLine($"request-to-answer latency{(clients.Count > 1 ? $" over {clients.Count} clients" : "")} (unanswered: no answer within {RequestObserver.AnswerTimeout.TotalSeconds:0}s):");
            foreach (var line in latency.Lines())
            {
                output.WriteLine($"  {line}");
            }
        }

        if (Flag("--idle"))
        {
            output.WriteLine(IdleHelp);
        }

        if (!byStep)
        {
            return 0;
        }

        var leaves = steps.Where(s => s.Leaf && s.End > from && s.Start < to && (only == null || s.Covers(only))).ToList();
        output.WriteLine(leaves.Count == 0
            ? "no steps: the sidecar holds no typed step event and no TIMING line for this game"
            : $"{leaves.Count} leaf steps (typed step events, or TIMING log lines). A party step's split adds up the seconds of every client, so it sums client-seconds and can exceed the step's length:");
        var capped = new CappedOutput(output, Limit, "narrow with --from/--to or --client");
        foreach (var step in leaves)
        {
            var split = StepSplit(step, clients);
            var length = step.StartUnknown ? "start unknown" : $"{step.Duration.TotalSeconds:0.0}s";
            capped.Line($"{TraceTime.Format(step.Start)} {step.Name}{(step.Attempt > 1 ? $" attempt {step.Attempt}" : "")} [{step.Client ?? "party"}] {length} {step.Outcome}{(step.StartInferred && !step.StartUnknown ? " (start taken from the step before)" : "")}{(step.Parent == null ? "" : $", within {step.Parent.Name}")}");
            capped.Line($"    {split.Render()}{(split.LongestIdle == null ? "" : $"; longest idle {split.LongestIdle.Duration.TotalSeconds:0.0}s at {TraceTime.Format(split.LongestIdle.Start)} {D2NG.Trace.Query.Names.Point(split.LongestIdle.At)} in {split.LongestIdle.Area ?? "?"}")}");
            var town = TownInStep(step, clients);
            if (town.Count > 0)
            {
                capped.Line($"    town in it by cause: {string.Join(", ", town.Select(t => $"{t.Cause} {t.Seconds:0}s"))}");
            }
        }

        capped.Finish();
        return 0;
    }

    private sealed record ClientGame(GameTrace Game, TimeSpan Length, Dictionary<string, TimeSpan> Phases, List<StandStill> Stills);

    /// <summary>A client's phases per game against the median game, its slowest games with what made them slow, and its stand-stills.</summary>
    private void WriteGamesOfClient(string client, List<ClientGame> games)
    {
        var slowest = Value("--slowest") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : 4;
        Dictionary<string, double> median = null;
        if (games.All(g => g.Phases != null))
        {
            median = FarmPhases.Order.ToDictionary(p => p, p => Steps.Percentiles([.. games.Select(g => g.Phases[p].TotalSeconds)]).Median, StringComparer.Ordinal);
            var medianLength = Steps.Percentiles([.. games.Select(g => g.Length.TotalSeconds)]).Median;
            output.WriteLine($"    phases, median game of {games.Count}: {medianLength:0.0}s - {string.Join(", ", FarmPhases.Order.Where(p => median[p] >= 0.05).Select(p => $"{p} {median[p]:0.0}s"))}");
        }

        if (games.All(g => g.Stills != null))
        {
            output.WriteLine($"    {IdleAttribution.Summary([.. games.SelectMany(g => g.Stills)], StillMinimum)}");
        }

        if (median == null && games.Any(g => g.Stills == null))
        {
            return;
        }

        var medianGame = Steps.Percentiles([.. games.Select(g => g.Length.TotalSeconds)]).Median;
        output.WriteLine($"    slowest {Math.Min(slowest, games.Count)} games{(median == null ? "" : ", each phase against the median game's")}:");
        foreach (var g in games.OrderByDescending(g => g.Length).ThenBy(g => Ref(g.Game), StringComparer.Ordinal).Take(slowest))
        {
            var phases = median == null ? "" : ": " + string.Join(", ", FarmPhases.Order.Where(p => g.Phases[p].TotalSeconds >= 0.05 || median[p] >= 0.05)
                .Select(p => $"{p} {g.Phases[p].TotalSeconds:0.0}s ({g.Phases[p].TotalSeconds - median[p]:+0.0;-0.0;0.0})"));
            output.WriteLine($"      {Ref(g.Game)} {g.Length.TotalSeconds:0.0}s ({g.Length.TotalSeconds - medianGame:+0.0;-0.0;0.0} on the median){phases}");
            if (median != null && games.Count >= CausesFrom)
            {
                output.WriteLine($"        {SlowCauses.Render(CausesOf(g.Game, client, TimeSpan.Zero, g.Length, g.Phases, median))}");
            }

            if (g.Stills is { Count: > 0 } stills)
            {
                foreach (var still in stills.OrderByDescending(s => s.Duration).Take(3).OrderBy(s => s.Start))
                {
                    output.WriteLine($"        {TraceTime.Format(still.Start)} {IdleAttribution.Render(still)}");
                }
            }
        }
    }

    /// <summary>Fewer games than this give no median worth calling a game slow against.</summary>
    private const int CausesFrom = 5;

    private readonly Dictionary<string, List<Incident>> _gameIncidents = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Game, string Client), ItemMoves.Summary> _itemSummaries = [];

    /// <summary>
    /// The causes of a client's slow window in a game (see <see cref="SlowCauses"/>): from its incidents, found once per game
    /// without the map checks (the causes read none of what only they find), its phases against the median game and its
    /// item ledger's summary, read once per game and client.
    /// </summary>
    private List<SlowCause> CausesOf(GameTrace game, string client, TimeSpan from, TimeSpan to, Dictionary<string, TimeSpan> phases, Dictionary<string, double> median, bool failed = false)
    {
        var query = Shared.QueryOf(game);
        var key = GameQuery.Identity(game);
        if (!_gameIncidents.TryGetValue(key, out var incidents))
        {
            incidents = D2NG.Trace.Query.Incidents.Find(query, new IncidentOptions()).Incidents;
            _gameIncidents[key] = incidents;
        }

        ItemMoves.Summary items = null;
        if (phases != null && game.Labels.Contains(client) && !_itemSummaries.TryGetValue((key, client), out items))
        {
            items = ItemMoves.Summarize(client, ItemMoves.Replay(query, [client])[0].Ledger.Events);
            _itemSummaries[(key, client)] = items;
        }

        return SlowCauses.Of(client, incidents, from, to, phases, median, items, failed);
    }

    private static object StillJson(StandStill s) => new
    {
        t = TraceTime.Format(s.Start),
        seconds = Math.Round(s.Duration.TotalSeconds, 1),
        state = s.State,
        cause = s.Cause,
        server = Math.Round(s.ServerWait.TotalSeconds, 1),
        typed = Math.Round(s.TypedWait.TotalSeconds, 1),
        bot = Math.Round(s.BotWait.TotalSeconds, 1),
        parts = s.Parts.ToDictionary(p => p.What, p => Math.Round(p.Time.TotalSeconds, 1)),
        before = s.Before == null ? null : new { action = s.Before.Text, secondsBefore = Math.Round((s.Start - s.Before.At).TotalSeconds, 2) },
        next = s.After == null ? null : new { action = s.After.Text, secondsIn = Math.Round((s.After.At - s.Start).TotalSeconds, 2) },
        at = s.At == null ? null : new[] { s.At.X, s.At.Y },
        area = s.Area,
    };

    private static object LatencyJson(params RequestObserver[] requests)
    {
        var table = new LatencyTable();
        foreach (var r in requests)
        {
            table.Add(r);
        }

        return table.Rows().Select(r => new { kind = r.Kind, answered = r.Answered, medianMs = Rounded(r.Median), p90Ms = Rounded(r.P90), maxMs = Rounded(r.Max), unanswered = r.Unanswered });
    }

    private static double? Rounded(double value) => double.IsNaN(value) ? null : Math.Round(value);

    private TimeSpan StillMinimum =>Value("--still") is { } still ? TraceTime.Parse(still) : TimeSpan.FromSeconds(1);

    private const string IdleHelp = "note: a stand-still is standing-idle, standing-waiting or standing in town with no NPC, item or fight; each 100 ms of it is server wait while a request the client sent was unanswered, else typed wait inside a typed wait event, else bot wait. before/next are the client's last action before it and its first after it began";

    private const string TownHelp ="note: town time exceeds the headline's town state because fighting, picking, npc and buffing outrank it there; causes and parts are defined in the usage text";

    private const string TownUsage = """
        activity --town: a stay is every slice in a town area with a trace file open. causes: game start (placed in town on joining), rejoin after chicken/leave/disconnect
          (a later file: chicken from the bot's typed chicken decision, leave when the previous file ended with the client sending leave-game, disconnect when it ended
          without), rejoin (no bot events recorded: cause unknown) with the packet evidence, death, waypoint back, portal back, act portal (a waypoint, town portal or
          other portal used in the 10s before), other - mostly walking across a town's edge and back. inside: vendor (a chat with a trade), healing (a healer chat, no
          trade), npc talk, waypoint/portal, stash, items, waiting for others (typed wait), walking, idle.
        activity's standing-waiting is split by the typed wait's reason (party, low life...) when the bot recorded typed waits.
        """;

    private static ActivitySplit StepSplit(StepSpan step, IEnumerable<ClientActivity> clients)
    {
        var split = new ActivitySplit();
        foreach (var c in clients.Where(c => step.Covers(c.Client)))
        {
            split.Add(c.Stretches, step.Start, step.End);
        }

        return split;
    }

    /// <summary>Client-seconds in town within a step, per cause of the stay.</summary>
    private static List<(string Cause, double Seconds)> TownInStep(StepSpan step, IEnumerable<ClientActivity> clients)
        => [.. clients.Where(c => step.Covers(c.Client))
            .SelectMany(c => c.Stays.Where(s => s.End > step.Start && s.Start < step.End)
                .Select(s => (s.Cause, Seconds: ((s.End < step.End ? s.End : step.End) - (s.Start > step.Start ? s.Start : step.Start)).TotalSeconds)))
            .GroupBy(s => s.Cause).Select(g => (g.Key, g.Sum(s => s.Seconds))).Where(t => t.Item2 > 0).OrderByDescending(t => t.Item2)];

    private sealed class StepTotals
    {
        public List<double> Seconds { get; } = [];

        public ActivitySplit Split { get; } = new();

        public List<string> NotOk { get; } = [];

        public Dictionary<string, double> Town { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Per party (client set): per client and per step name over its games - how often, median and 90th percentile seconds, and where the time went.</summary>
    private int ActivityOfBatch(List<GameTrace> games)
    {
        var sources = Shared;
        var timer = Stopwatch.StartNew();
        var groups = Parties(games);
        if (!AsJson)
        {
            output.WriteLine($"{games.Count} games under {Path.GetFullPath(GameTrace.ParseTarget(_typed).Folder)}{FilterText()}, {groups.Count} part{(groups.Count == 1 ? "y" : "ies")} (games grouped by their set of clients); game refs are relative to the working directory");
        }

        foreach (var (clientSet, partyGames) in groups)
        {
            var perStep = new Dictionary<string, StepTotals>();
            var perClient = new Dictionary<string, (ActivitySplit Split, SkillSplit Skills, TownSummary Town)>();
            var waitingPerClient = new Dictionary<string, Dictionary<string, TimeSpan>>();
            var latency = new LatencyTable();
            var gamesPerClient = new Dictionary<string, List<ClientGame>>(StringComparer.Ordinal);
            var failed = new List<(GameTrace Game, StepSpan Step)>();
            foreach (var game in partyGames)
            {
                var query = sources.QueryOf(game);
                var steps = Steps.Of(query);
                var (clients, from, to) = ComputeActivity(query, Value("--client") is { } name ? game.ResolveClient(name) : null, steps);
                foreach (var c in clients)
                {
                    if (c.Requests != null)
                    {
                        latency.Add(c.Requests);
                        (gamesPerClient.TryGetValue(c.Client, out var list) ? list : gamesPerClient[c.Client] = []).Add(new ClientGame(game, to - from,
                            Flag("--phases") ? FarmPhases.Of(c.Activity, c.Stretches, c.Requests, from, to) : null,
                            Flag("--idle") ? IdleAttribution.Of(c.Activity, c.Stretches, c.Requests, c.Waits, StillMinimum) : null));
                    }

                    if (!perClient.TryGetValue(c.Client, out var total))
                    {
                        perClient[c.Client] = total = (new ActivitySplit(), new SkillSplit(), new TownSummary());
                    }

                    total.Split.Add(c.Stretches, from, to);
                    total.Skills.Add(c.Fights, c.Stretches, from, to);
                    total.Town.Add(StaysWithin(c.Stays, from, to), c.Stretches);
                    var waiting = waitingPerClient.TryGetValue(c.Client, out var known) ? known : waitingPerClient[c.Client] = new(StringComparer.Ordinal);
                    foreach (var (reason, time) in c.Waiting)
                    {
                        waiting[reason] = waiting.GetValueOrDefault(reason) + time;
                    }
                }

                foreach (var step in steps.Where(s => s.Leaf && !s.StartInferred))
                {
                    if (!perStep.TryGetValue(step.Name, out var entry))
                    {
                        perStep[step.Name] = entry = new StepTotals();
                    }

                    entry.Seconds.Add(step.Duration.TotalSeconds);
                    entry.Split.Add(StepSplit(step, clients));
                    foreach (var (cause, seconds) in TownInStep(step, clients))
                    {
                        entry.Town[cause] = entry.Town.GetValueOrDefault(cause) + seconds;
                    }

                    if (step.Outcome is not "ok")
                    {
                        entry.NotOk.Add($"{Ref(game)} {step.Duration.TotalSeconds:0}s {step.Outcome}");
                        failed.Add((game, step));
                    }
                }
            }

            if (AsJson)
            {
                foreach (var (client, (split, skills, town)) in perClient)
                {
                    output.WriteLine(JsonSerializer.Serialize(new { party = clientSet, client, seconds = split.Totals.Where(t => t.Value > TimeSpan.Zero).ToDictionary(t => ActivityObserver.Name(t.Key), t => Math.Round(t.Value.TotalSeconds, 1)), skills = skills.Skills.ToDictionary(s => s.Key, s => new { casts = s.Value.Casts, seconds = Math.Round(s.Value.Time.TotalSeconds, 1), hits = s.Value.Hits }), buffs = skills.Buffs.ToDictionary(s => s.Key, s => new { casts = s.Value.Casts, seconds = Math.Round(s.Value.Time.TotalSeconds, 1) }), inTownAreas = Math.Round(town.Total.TotalSeconds, 1), town = town.ByCause.ToDictionary(t => t.Key, t => new { count = t.Value.Count, total = Math.Round(t.Value.Sum(), 1) }) }, Json));
                }

                foreach (var (name, entry) in perStep)
                {
                    var (median, p90) = Steps.Percentiles(entry.Seconds);
                    output.WriteLine(JsonSerializer.Serialize(new { party = clientSet, step = name, count = entry.Seconds.Count, failed = entry.NotOk.Count, notOk = entry.NotOk, median = Math.Round(median, 1), p90 = Math.Round(p90, 1), split = entry.Split.Totals.Where(t => t.Value > TimeSpan.Zero).ToDictionary(t => ActivityObserver.Name(t.Key), t => Math.Round(t.Value.TotalSeconds, 1)), town = entry.Town }, Json));
                }

                foreach (var (client, list) in gamesPerClient.OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    foreach (var g in list)
                    {
                        output.WriteLine(JsonSerializer.Serialize(new
                        {
                            party = clientSet,
                            game = Ref(g.Game),
                            client,
                            seconds = Math.Round(g.Length.TotalSeconds, 1),
                            phases = g.Phases?.Where(p => p.Value > TimeSpan.Zero).ToDictionary(p => p.Key, p => Math.Round(p.Value.TotalSeconds, 1)),
                            standStills = g.Stills?.Select(StillJson),
                        }, Json));
                    }
                }

                if (Flag("--latency"))
                {
                    output.WriteLine(JsonSerializer.Serialize(new { party = clientSet, latency = latency.Rows().Select(r => new { kind = r.Kind, answered = r.Answered, medianMs = Rounded(r.Median), p90Ms = Rounded(r.P90), maxMs = Rounded(r.Max), unanswered = r.Unanswered }) }, Json));
                }

                continue;
            }

            output.WriteLine($"== party {clientSet}: {partyGames.Count} games ({GameRange(partyGames)}{InProgressText(partyGames)}); per client over its games:");
            foreach (var (client, (split, skills, town)) in perClient.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"  {client}: {split.Render()}");
                if (waitingPerClient.TryGetValue(client, out var waits) && ActivityObserver.RenderWaiting(waits) is { Length: > 0 } waiting)
                {
                    output.WriteLine($"    standing-waiting by typed wait reason: {waiting}");
                }

                output.WriteLine($"    fighting by skill: {skills.Short()}");
                if (Flag("--skills"))
                {
                    foreach (var line in skills.Lines())
                    {
                        output.WriteLine($"      {line}");
                    }
                }

                if (Flag("--town"))
                {
                    output.WriteLine($"    {town.Reconcile()}");
                    output.WriteLine($"    town by cause: {town.Causes()}");
                    output.WriteLine($"    inside town: {town.Inside()}");
                }

                if (gamesPerClient.TryGetValue(client, out var clientGames))
                {
                    WriteGamesOfClient(client, clientGames);
                }
            }

            if (Flag("--latency"))
            {
                output.WriteLine($"  request-to-answer latency over the party's games (unanswered: no answer within {RequestObserver.AnswerTimeout.TotalSeconds:0}s):");
                foreach (var line in latency.Lines())
                {
                    output.WriteLine($"    {line}");
                }
            }

            if (Flag("--town"))
            {
                output.WriteLine($"  {TownHelp}");
            }

            output.WriteLine(perStep.Count == 0 ? "  no steps in these games' sidecars" : "  per step, slowest median first (a party step's split sums every client's seconds):");
            var capped = new CappedOutput(output, Limit, "narrow the folder, --clients or --game-prefix");
            foreach (var (name, entry) in perStep.OrderByDescending(s => Steps.Percentiles(s.Value.Seconds).Median))
            {
                var (median, p90) = Steps.Percentiles(entry.Seconds);
                var notOk = string.Join("; ", entry.NotOk.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
                capped.Line($"  {name}: {entry.Seconds.Count}x{(entry.NotOk.Count > 0 ? $" ({entry.NotOk.Count} not ok: {notOk})" : "")}, median {median:0}s, p90 {p90:0}s; {entry.Split.Render()}"
                    + (entry.Town.Count > 0 ? $"; town by cause {string.Join(", ", entry.Town.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value:0}s"))}" : ""));
            }

            if (Flag("--phases") && failed.Count > 0)
            {
                capped.Line($"  failed steps, each with its cause:");
                foreach (var (game, step) in failed)
                {
                    var client = step.Client is { } named && game.Labels.Contains(named) ? named : game.Labels[0];
                    var causes = CausesOf(game, client, step.Start, step.End, null, null, failed: true);
                    var line = causes.Count == 0 && step.Outcome == "unfinished" ? $"cause: {SlowCauses.TraceEnds} (no end event, and nothing in the step explains it)" : SlowCauses.Render(causes);
                    capped.Line($"    {Ref(game)} {TraceTime.Format(step.Start)} {step.Name} [{client}] {step.Duration.TotalSeconds:0}s {step.Outcome}: {line}");
                }
            }

            capped.Finish();
        }

        if (!AsJson)
        {
            output.WriteLine($"{timer.Elapsed.TotalSeconds:0.0}s to read");
        }

        return 0;
    }

    private int Tiles(string target)
    {
        var query = Open(target);
        var radius = Value("--radius") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : 20;
        var maps = Maps();
        Point center;
        Area area;
        uint mapId;
        D2NG.Core.D2GS.Enums.Difficulty difficulty;
        var marks = new List<(Point, char)>();
        if (Value("--point") is { } pointText)
        {
            var xy = pointText.Split(',').Select(v => ushort.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
            center = new Point(xy[0], xy[1]);
            area = Enum.TryParse<Area>(Value("--area") ?? throw new ArgumentException("--point needs --area"), true, out var named) ? named : throw new ArgumentException($"no area '{Value("--area")}'");
            var client = Client(query, required: false) ?? query.Game.Labels[0];
            var context = ClientReplay.Run(query.Game, client, Value("--at") != null ? Time("--at", TimeSpan.Zero) : TimeSpan.FromSeconds(30), []);
            (mapId, difficulty) = (context.Game.MapId, context.Game.Difficulty);
            context.Replayer.Dispose();
        }
        else
        {
            var client = Client(query, required: true);
            var at = Value("--at") != null ? Time("--at", TimeSpan.Zero) : throw new ArgumentException("--at is required (or --point x,y --area A)");
            var areas = new AreaTracker();
            var context = ClientReplay.Run(query.Game, client, at, [areas]) ?? throw new ArgumentException($"{client} has no trace before {TraceTime.Format(at)}");
            try
            {
                var game = context.Game;
                if (game.Me?.Location == null || (game.Me.Location.X == 0 && game.Me.Location.Y == 0))
                {
                    output.WriteLine($"{client} is not placed yet at {TraceTime.Format(at)}");
                    return 1;
                }

                center = game.Me.Location;
                var resolved = areas.Current(context);
                if (resolved == null && areas.LastKnown == null)
                {
                    output.WriteLine($"{client} at {D2NG.Trace.Query.Names.Point(center)} is outside every map tile revealed to it at {TraceTime.Format(context.Now)}, and no area is known yet; give --point x,y --area A");
                    return 1;
                }

                area = resolved ?? areas.LastKnown.Value;
                if (resolved == null)
                {
                    output.WriteLine($"note: the area is unknown - {D2NG.Trace.Query.Names.Point(center)} is outside every map tile revealed to {client}; showing the last known area, {area}");
                }
                (mapId, difficulty) = (game.MapId, game.Difficulty);
                foreach (var unit in game.WorldObjects.Values.OrderBy(u => u.Type).ThenBy(u => u.Id))
                {
                    var mark = unit.Type switch
                    {
                        EntityType.NPC when unit.State is EntityState.Dead or EntityState.Dieing => 'x',
                        EntityType.NPC => 'm',
                        EntityType.Object when unit.Code is EntityCode.TownPortal or EntityCode.RedTownPortal => 'P',
                        EntityType.Object => 'O',
                        _ => ' ',
                    };
                    if (mark != ' ')
                    {
                        marks.Add((unit.Location, mark));
                    }
                }

                marks.AddRange(game.Players.Where(p => p.Id != game.Me.Id && p.Location != null).Select(p => (p.Location, 'p')));
                marks.Add((center, '@'));
                output.WriteLine($"{client} at {D2NG.Trace.Query.Names.Point(center)} in {area} at {TraceTime.Format(context.Now)}, map seed 0x{mapId:X8} {difficulty}");
            }
            finally
            {
                context.Replayer.Dispose();
            }
        }

        AreaMapResult(maps, mapId, difficulty, area, center, radius, marks);
        return 0;
    }

    private void AreaMapResult(MapSource maps, uint mapId, D2NG.Core.D2GS.Enums.Difficulty difficulty, Area area, Point center, int radius, List<(Point, char)> marks)
    {
        D2NG.Navigation.Services.MapApi.AreaMap map;
        try
        {
            map = maps.Get(mapId, difficulty, area);
        }
        catch (MapUnavailableException e)
        {
            throw new IOException(e.Message);
        }

        var ground = TileGrid.Ground(map, center.X, center.Y);
        output.WriteLine($"the map marks {D2NG.Trace.Query.Names.Point(center)} {(ground == '.' ? "walkable" : ground == '#' ? "NOT walkable, a wall (blocks sight)" : ground == '~' ? "NOT walkable, but missiles cross it" : "outside the area")}");
        output.WriteLine("legend: . walkable  # wall (blocks walking and sight, so missiles)  ~ blocks walking only (water, a ledge: missiles cross it)  (blank) outside the area  D door (map preset)  o object (map preset)  @ this client  p player  m monster  x dead monster  O object  P portal");
        foreach (var line in TileGrid.Render(map, center, radius, marks))
        {
            output.WriteLine(line);
        }
    }

    private int Seen(string target)
    {
        var code = Value("--code") ?? throw new ArgumentException("--code is required: an NPC or object code name as Trace unit lists it, optionally npc:Name or object:Name");
        Area? area = Value("--area") is { } named ? Enum.TryParse<Area>(named, true, out var parsed) ? parsed : throw new ArgumentException($"no area '{named}'") : null;
        var sightings = new List<(GameTrace Game, string Client, Area Area, Point At, TimeSpan When, string Type)>();
        var sources = Shared;
        var games = Games(target);
        foreach (var game in games)
        {
            var query = sources.QueryOf(game);
            foreach (var (client, seen) in query.ReplayAll(game.Labels, TimeSpan.MaxValue, () => new SeenObserver(code, area), o => [o]))
            {
                sightings.AddRange(seen.Sightings.Select(s => (game, client, s.Area, s.At, s.When, s.Type)));
            }
        }

        if (sightings.Count == 0)
        {
            output.WriteLine($"no {code} assigned to any client in {games.Count} games (the name must be the exact code name, as Trace unit lists it)");
            return 1;
        }

        var spots = sightings.GroupBy(s => (s.Area, s.At.X, s.At.Y, s.Type))
            .Select(g => (g.Key.Area, At: new Point(g.Key.X, g.Key.Y), g.Key.Type, Count: g.Count(), Games: g.Select(s => s.Game).Distinct().Count(), First: g.First()))
            .OrderBy(s => s.Area).ThenByDescending(s => s.Games).ToList();
        if (AsJson)
        {
            foreach (var spot in spots)
            {
                output.WriteLine(JsonSerializer.Serialize(new { area = spot.Area.ToString(), type = spot.Type, at = new[] { spot.At.X, spot.At.Y }, sightings = spot.Count, games = spot.Games, example = $"{Ref(spot.First.Game)} --client {spot.First.Client} --at {TraceTime.Format(spot.First.When)}" }, Json));
            }

            return 0;
        }

        var byType = sightings.GroupBy(s => s.Type).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key} assignments").ToList();
        output.WriteLine($"{code}: {string.Join(" and ", byType)} in {sightings.Select(s => s.Game).Distinct().Count()} of {games.Count} games, {spots.Count} distinct spots (one line per spot and type; a unit seen by several clients counts once per client"
            + (byType.Count > 1 ? $"; the name is both an NPC and an object code - give npc:{SeenObserver.Split(code).Code} or object:{SeenObserver.Split(code).Code} for one" : "") + ")");
        var capped = new CappedOutput(output, Limit, "narrow with --area or npc:/object:");
        foreach (var group in spots.GroupBy(s => s.Area))
        {
            capped.Line($"  {group.Key}: {string.Join(", ", group.GroupBy(s => s.Type).Select(t => $"{t.Sum(s => s.Count)} {t.Key} assignments at {t.Count()} spots"))}");
            foreach (var spot in group.Take(20))
            {
                capped.Line($"    {D2NG.Trace.Query.Names.Point(spot.At),-14} {spot.Type,-6} {spot.Count}x in {spot.Games} game{(spot.Games == 1 ? "" : "s")}, e.g. {Ref(spot.First.Game)} {spot.First.Client} {TraceTime.Format(spot.First.When)}");
            }

            if (group.Count() > 20)
            {
                capped.Line($"    ... {group.Count() - 20} more spots");
            }
        }

        capped.Finish();
        return 0;
    }

    private int KpiXp(string target)
    {
        var games = Games(target);
        if (Flag("--leave-after"))
        {
            return LeaveAfter(games);
        }

        var only = Value("--client");
        var parties = Parties(games);
        var sources = Shared;
        if (!AsJson)
        {
            output.WriteLine($"experience from the 0x1A-0x1C packets (first one after a join = lifetime total), {games.Count} games{FilterText()} in {parties.Count} part{(parties.Count == 1 ? "y" : "ies")} (grouped by set of clients); clock = file spans plus gaps under {ExperienceKpi.GapLimit.TotalMinutes:0} min between them; a death is the server's \"was slain by\" message, life 0 then the gold on the person 0, or the corpse");
        }

        foreach (var (clientSet, partyGames) in parties)
        {
            var files = new List<ExperienceObserver.FileExperience>();
            foreach (var game in partyGames)
            {
                var query = sources.QueryOf(game);
                var labels = only == null ? game.Labels.ToList() : game.Labels.Where(l => string.Equals(l, only, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var (_, observer) in query.ReplayAll(labels, TimeSpan.MaxValue, () => new ExperienceObserver(), o => [o]))
                {
                    files.AddRange(observer.Files);
                }
            }

            var rows = ExperienceKpi.Compute(files);
            if (AsJson)
            {
                foreach (var row in rows)
                {
                    output.WriteLine(JsonSerializer.Serialize(new { party = clientSet, character = row.Character, games = row.Games, gained = row.Gained, perMinute = Math.Round(row.PerMinute), minutes = Math.Round(row.Clock.TotalMinutes, 1), excludedMinutes = Math.Round(row.Excluded.TotalMinutes, 1), excludedGaps = row.ExcludedGaps, deaths = row.Deaths, lostBetweenFiles = row.Lost, unexplainedFalls = row.UnexplainedFalls, first = row.Start, last = row.End }, Json));
                }

                continue;
            }

            output.WriteLine($"== party {clientSet}: {partyGames.Count} games ({GameRange(partyGames)}{InProgressText(partyGames)})");
            foreach (var row in rows)
            {
                output.WriteLine($"  {row.Character,-14} {row.PerMinute,9:N0} xp/min  net {row.Gained:N0} over {row.Clock.TotalMinutes:0.0} min in {row.Games} games ({row.Files} files); deaths {row.Deaths}; lost between files {row.Lost:N0}{(row.UnexplainedFalls > 0 ? $" ({row.UnexplainedFalls} fall{(row.UnexplainedFalls == 1 ? "" : "s")} after a file with no death: not counted as deaths)" : "")}; total {row.Start:N0} -> {row.End:N0}{(row.ExcludedGaps > 0 ? $"; left out {row.ExcludedGaps} gaps of {row.Excluded.TotalMinutes:0} min" : "")}");
            }
        }

        return 0;
    }

    /// <summary>Writes a synthetic game and times every query on it.</summary>
    public static int BenchQueries(string keep, int clients, int packets)
    {
        var root = keep ?? Path.Combine(Path.GetTempPath(), "d2trace-query-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writing = Stopwatch.StartNew();
            var folder = SyntheticGame.WriteBenchmark(root, clients, packets, TimeSpan.FromHours(1));
            Console.WriteLine($"synthetic game: {clients} clients x {packets:N0} packets over an hour, written in {writing.Elapsed.TotalSeconds:0.0}s to {folder}");
            var query = GameQuery.Open(folder);
            var parts = Stopwatch.StartNew();
            query.ReplayAll(query.Game.Labels, TimeSpan.MaxValue, () => 0, _ => []);
            Console.WriteLine($"{parts.Elapsed.TotalMilliseconds,8:N0} ms  (part) replaying every client side by side, no observers");
            parts.Restart();
            var lines = query.Sidecar.Count;
            Console.WriteLine($"{parts.Elapsed.TotalMilliseconds,8:N0} ms  (part) reading {lines:N0} sidecar lines");
            var client = "client1";
            var at = TraceTime.Format(TimeSpan.FromMinutes(45));
            var history = Path.Combine(root, "bench-history.json");
            foreach (var command in new[]
            {
                new[] { "incidents", folder, "--history", history, "--map-url", "http://127.0.0.1:9" },
                ["timeline", folder],
                ["timeline", folder, "--client", client, "--from", "30:00", "--to", "31:00"],
                ["timeline", folder, "--client", client, "--moves"],
                ["activity", folder],
                ["activity", folder, "--by-step"],
                ["state", folder, "--client", client, "--at", at],
                ["packets", folder, "--client", client, "--from", "30:00", "--to", "30:10"],
                ["unit", folder, "--client", client, "npc:0x1000"],
                ["damage", folder, "--client", client],
                ["kpi", "xp", folder],
            })
            {
                var timer = Stopwatch.StartNew();
                new Commands(command, TextWriter.Null).Run();
                Console.WriteLine($"{timer.Elapsed.TotalMilliseconds,8:N0} ms  Trace {string.Join(' ', command.Select(a => a == folder ? "<game>" : a == history ? "<history>" : a))}");
            }

            return 0;
        }
        finally
        {
            if (keep == null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
