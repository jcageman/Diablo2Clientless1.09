using ConsoleBot.Attack;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Services.MapApi;
using D2NG.Trace;
using D2NG.Trace.Query;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TargetingReplay;

// Counterfactual replay of the wall-aware whirl: for every whirl a client made in recorded games, the moment it was asked
// (from the trace), what the server made of it (the skill-move view) and what the wall-aware planner
// (ConsoleBot.Attack.WallAwareWhirlDecision) would have chosen from that moment.
// Usage: TargetingReplay <date folder>... [--datadir <dir>] [--map-cache <dir>] [--model live|server|both] [--worst 15]
// Run it from the folder holding the traces so the game refs print relative to it. It only reads: traces, sidecars and
// cached maps (fetched from the map service once per area when not cached).
// A game's arm (wallAwareWhirl) is the setting it recorded; a game recorded before settings were traced has it inferred
// from its wall-aware decisions. The switch is gone since: every Travincal game from then on fights wall-aware and records
// its other settings but no wallAwareWhirl, which reads as "true (no switch)".

string Value(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var folders = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        i++;
        continue;
    }

    folders.Add(args[i]);
}

if (folders.Count == 0)
{
    Console.Error.WriteLine("usage: TargetingReplay <date folder>... [--datadir <dir>] [--map-cache <dir>] [--model live|server|both] [--worst 15]");
    return 2;
}

TraceDataLocation.Use(Value("--datadir"), folders[0]);
Counterfactual.MapCache = Value("--map-cache") ?? Path.Combine(Path.GetTempPath(), "d2trace-maps");
var maps = new MapSource(null, Counterfactual.MapCache);
var modelArg = (Value("--model") ?? "both").ToLowerInvariant();
var worst = int.Parse(Value("--worst") ?? "15", CultureInfo.InvariantCulture);
foreach (var folder in folders)
{
    var timer = Stopwatch.StartNew();
    var games = GameTrace.GamesUnder(folder, (f, why) => Console.Error.WriteLine($"skipped {f}: {why}"));
    var results = new GameResult[games.Count];
    Parallel.For(0, games.Count, i => results[i] = games[i].InProgress ? GameResult.InProgress : Counterfactual.OfGame(games[i], maps, modelArg));
    Report.Day(folder, results, timer.Elapsed, worst);
}

return 0;

namespace TargetingReplay
{
    /// <summary>What the server made of a whirl; a hit is this client's when it was alone or its mana leeched, else the whirl is a party case.</summary>
    internal enum Outcome
    {
        Hit,
        WentNowhere,
        MovedHitNothing,
        HitInPlace,
        PartyUnattributed,
    }

    /// <summary>One whirl and what the planner would have done under one motion model.</summary>
    internal sealed record Row(string Game, string Client, string Arm, TimeSpan At, string Model, Outcome Outcome, bool LaneBlocked, int Hits, double Moved, double StallSeconds, Decision Decision, Point Me, Point Asked)
    {
        public bool Avoided => Outcome == Outcome.WentNowhere && Decision.Choice != WhirlChoice.Same;

        public bool Casts => Decision.Choice is WhirlChoice.Same or WhirlChoice.DifferentWhirl;

        /// <summary>A whirl that really hit, which the planner would not cast because of a wall: blocked, or walked along the wall first.</summary>
        public bool Rejected => Outcome == Outcome.Hit && Decision.Choice is WhirlChoice.Blocked or WhirlChoice.WalkAlongWall;

        /// <summary>A whirl that really hit, which the planner would put off: nothing with an open lane within whirl reach, so walk closer first.</summary>
        public bool Deferred => Outcome == Outcome.Hit && Decision.Choice is WhirlChoice.Approach or WhirlChoice.NoTarget;
    }

    /// <summary>The rows of a game, and the server's own count of skill moves that went nowhere (the Trace incident), for the arms' figure.</summary>
    internal sealed record GameResult(List<Row> Rows, string Arm, int Whirls, int NowhereWhirls, int NowhereIncidents, double NowhereSeconds, string Skipped)
    {
        public static readonly GameResult InProgress = new([], null, 0, 0, 0, 0, "in progress");
    }

    internal static class Counterfactual
    {
        /// <summary>A wall stall the planner avoids saves this little less: the walk it would take instead (the bot's WallWalkMs).</summary>
        public static readonly TimeSpan AlternativeCost = TimeSpan.FromSeconds(1.5);

        /// <summary>A whirl that went nowhere costs from its last ask to the next whirl's last ask, at most this long after it ends.</summary>
        public static readonly TimeSpan StallCap = TimeSpan.FromSeconds(5);

        private static readonly string[] WallDecisions = ["whirl blocked by wall", "whirl along wall", "council member across wall skipped", "whirl went nowhere"];

        public static GameResult OfGame(GameTrace game, MapSource maps, string modelArg)
        {
            var query = new GameQuery(game);
            var effective = GameSettings.ValueOf(query, "monsterMotionEffective");
            if (effective.Contains('|', StringComparison.Ordinal))
            {
                return new GameResult([], null, 0, 0, 0, 0, $"its clients ran different motion models ({effective})");
            }

            var live = effective == GameSettings.NotRecorded ? "Legacy" : effective;
            string[] models = modelArg switch
            {
                "live" => [live],
                "server" => ["server"],
                _ => [live, "server"],
            };
            var entries = query.GameEntries();
            var recordedArm = GameSettings.ValueOf(query, "wallAwareWhirl");
            if (recordedArm == GameSettings.NotRecorded && GameSettings.ValueOf(query, "movementSpeedFactor") != GameSettings.NotRecorded)
            {
                recordedArm = "true (no switch)";
            }

            var reference = Path.GetRelativePath(Environment.CurrentDirectory, game.Folder).Replace('\\', '/') + (game.GameCount > 1 ? $"#{game.Index}" : "");
            var rows = new List<Row>();
            var whirlCount = 0;
            var nowhereWhirls = 0;
            var incidents = 0;
            var seconds = 0.0;
            string arm = recordedArm != GameSettings.NotRecorded ? recordedArm : null;
            foreach (var client in game.Labels)
            {
                arm ??= ArmOf(game, client, entries);
                var combat = query.ReplayAll([client], TimeSpan.MaxValue, () => new CombatObserver(), o => [o])[0].Observers;
                var whirls = combat.Moves.Where(m => m.Skill == nameof(Skill.Whirlwind)).ToList();
                if (whirls.Count == 0)
                {
                    continue;
                }

                whirlCount += whirls.Count;
                foreach (var nowhere in new CombatCheck().Run(new CheckContext { Query = query, Client = client, Combat = combat }).Where(i => i.Kind == CombatCheck.NowhereKind))
                {
                    incidents++;
                    seconds += ((nowhere.End ?? nowhere.Offset) - nowhere.Offset).TotalSeconds;
                }

                var givenUp = GivenUp(query, client, entries);
                var capture = new MomentCapture(whirls.Select(w => w.Start));
                ClientReplay.Run(game, client, TimeSpan.MaxValue, [capture])?.Replayer.Dispose();
                var first = true;
                foreach (var model in models)
                {
                    // The bot remembers the direction it cast that went nowhere, from where it stood, as the trace's clock reads it.
                    var failedWhirls = new FailedWhirls();
                    for (var i = 0; i < whirls.Count; i++)
                    {
                        var whirl = whirls[i];
                        capture.Moments.TryGetValue(whirl.Start, out var moment);
                        var view = combat.View(whirl);
                        var outcome = OutcomeOf(combat, moment, whirl, view);
                        if (first && outcome == Outcome.WentNowhere)
                        {
                            nowhereWhirls++;
                        }

                        if (moment?.Area is not { } area || MapOf(maps, moment, area) is not { } map)
                        {
                            continue;
                        }

                        var laneBlocked = whirl.Asked != null && !WallAwareWhirl.IsLaneClear(map, moment.Self, whirl.Asked, WallAwareWhirl.EdgeSlack);
                        var now = DateTime.MinValue + whirl.Start;
                        var foes = moment.Units.Where(u => u.At(model) is not null).Select(u => new Foe(u.Id, u.Code, u.At(model), u.State, u.Life)).ToList();
                        var decision = WallAwareWhirlDecision.Decide(map, moment.Self, foes, whirl.Asked, (a, b) => GridPath.Length(map, a, b),
                            end => failedWhirls.Refuses(moment.Self, end, now),
                            givenUp.Where(g => g.At <= whirl.Start).Select(g => g.Id).ToHashSet());
                        if (outcome == Outcome.WentNowhere && whirl.Asked != null)
                        {
                            failedWhirls.Record(moment.Self, whirl.Asked, DateTime.MinValue + whirl.End);
                        }
                        var until = i + 1 < whirls.Count && whirls[i + 1].LastAskAt < whirl.End + StallCap ? whirls[i + 1].LastAskAt : whirl.End + StallCap;
                        var stall = outcome == Outcome.WentNowhere ? Math.Max(0, (until - whirl.LastAskAt - AlternativeCost).TotalSeconds) : 0;
                        rows.Add(new Row(reference, client, null, whirl.Start, model.ToLowerInvariant(), outcome, laneBlocked, view.Hits.Count, view.Moved, stall, decision, moment.Self, whirl.Asked));
                    }

                    first = false;
                }
            }

            return new GameResult([.. rows.Select(r => r with { Arm = arm })], arm, whirlCount, nowhereWhirls, incidents, seconds, null);
        }

        /// <summary>
        /// A hit counts as this client's when it was alone with no mercenary, or when its mana rose during the whirl past
        /// regeneration (leech); otherwise the whirl is a party case. A whirl must move <see cref="CombatCheck.Nowhere"/> or more to
        /// have really hit: one that moved 0 collapses to a point, and anything hit beside him there would count.
        /// </summary>
        private static Outcome OutcomeOf(CombatObserver combat, Moment moment, SkillMove whirl, SkillMoveView view)
        {
            var solo = moment == null || (moment.OtherPlayers == 0 && !moment.Mercenary);
            var samples = combat.Mana.Where(m => m.At >= whirl.Start && m.At <= whirl.End + TimeSpan.FromMilliseconds(600)).ToList();
            var regeneration = ManaObserver.RegenerationMax(samples.Count > 0 ? samples[0].Max : 0);
            var leech = samples.Zip(samples.Skip(1), (a, b) => b.Mana - a.Mana).Any(rise => rise > regeneration);
            var hit = view.Hits.Count > 0;
            var own = hit && (solo || leech);
            var moved = view.Moved >= CombatCheck.Nowhere;
            return (moved, hit, own) switch
            {
                (_, true, false) => Outcome.PartyUnattributed,
                (true, true, true) => Outcome.Hit,
                (false, true, true) => Outcome.HitInPlace,
                (true, false, _) => Outcome.MovedHitNothing,
                _ => Outcome.WentNowhere,
            };
        }

        /// <summary>The members the bot gave up on, from its own "leaving it" warning (KillCouncilMembers' no-progress rule).</summary>
        private static List<(uint Id, TimeSpan At)> GivenUp(GameQuery query, string client, IReadOnlyList<SidecarEntry> entries)
        {
            var found = new List<(uint, TimeSpan)>();
            foreach (var entry in entries.Where(e => e.IsLog && e.Message != null && e.Message.EndsWith("leaving it", StringComparison.Ordinal)))
            {
                if (entry.Json.TryGetProperty("p", out var p) && p.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.Number
                    && p.TryGetProperty("Character", out var character) && character.GetString() == client)
                {
                    found.Add((id.GetUInt32(), query.Offset(entry)));
                }
            }

            return found;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string File, string Client), bool> WallAware = new();

        /// <summary>
        /// A bot built before the settings were traced says nothing of wallAwareWhirl (one built after the switch was removed
        /// says nothing either, but records its other settings and is wall-aware): its arm is "on" when the client logged a
        /// wall-aware decision anywhere in the events file its events of this game came from (one bot process; a second bot
        /// started in the same second writes events-X-2, a file of its own), "off" when that file holds none.
        /// </summary>
        private static string ArmOf(GameTrace game, string client, IReadOnlyList<SidecarEntry> entries)
        {
            var files = entries.Where(e => !e.IsLog && e.Client == client && e.SourceName != null).Select(e => e.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0)
            {
                return null;
            }

            var on = files.Any(name => WallAware.GetOrAdd((name, client), key => FileHasWallDecision(game, key.File, key.Client)));
            return on ? "true (inferred)" : "false (inferred)";
        }

        private static bool FileHasWallDecision(GameTrace game, string name, string client)
        {
            var path = game.NearbyDateFolders().Select(f => Path.Combine(f, name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? name : name + ".jsonl")).FirstOrDefault(File.Exists);
            if (path == null)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"k\":\"decision\"", StringComparison.Ordinal) || !WallDecisions.Any(d => line.Contains($"\"name\":\"{d}\"", StringComparison.Ordinal)))
                {
                    continue;
                }

                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("c", out var c) && c.GetString() == client)
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly Dictionary<(uint, Area), AreaMap> Maps = [];

        private static MapSource Source;

        /// <summary>The cache folder of the map source given on the command line, for the fresh ones made after a failure.</summary>
        public static string MapCache { get; set; }

        /// <summary>The area's map as the bot reads it: always Difficulty.Normal (AttackService.LaneMap); the layout is the same in every difficulty.</summary>
        private static AreaMap MapOf(MapSource maps, Moment moment, Area area)
        {
            var key = (moment.MapId, area);
            lock (Maps)
            {
                if (Maps.TryGetValue(key, out var known))
                {
                    return known;
                }
            }

            AreaMap map = null;
            for (var attempt = 1; ; attempt++)
            {
                var source = Source ?? maps;
                try
                {
                    map = source.Get(moment.MapId, Difficulty.Normal, area);
                    break;
                }
                catch (MapUnavailableException e) when (e.WholeService && attempt < 4)
                {
                    // The live bots share the service; one slow answer marks a MapSource unavailable for good, so wait and start a fresh one.
                    Thread.Sleep(TimeSpan.FromSeconds(5 * attempt));
                    lock (Maps)
                    {
                        Source = new MapSource(maps.Url, MapCache);
                    }
                }
                catch (MapUnavailableException e)
                {
                    Console.Error.WriteLine($"no map for {area} of 0x{moment.MapId:X8}: {e.Message}");
                    break;
                }
            }

            lock (Maps)
            {
                Maps[key] = map;
            }

            return map;
        }
    }
}
