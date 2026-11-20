using D2NG.Core.D2GS.Motion;
using D2NG.Core.MonsterData;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TraceTool;

/// <summary>
/// Trace accuracy: how far each monster position model is from where the server says the monster is,
/// at every packet that carries an exact position, over one game or every game under a folder.
/// </summary>
internal sealed class Accuracy(string[] args, TextWriter output)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly string[] Groupings = ["class", "packet", "path", "flags", "states", "since"];

    private string Value(string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public const string UsageLines = """
               Trace accuracy <game or date folder> [--client C] [--model legacy,anchored,simulated (default) | all | <names>] [--by class|packet|path|flags|states|since]
                              [--top 15] [--min-samples 10] [--sample <seconds>] [--json]     how far each monster position model is from the server's positions
                              models: legacy = the position bots kept before 15 Nov 2026, anchored = the last exact position, simulated = moved along its
                              path at its speed (needs monster speeds in the data folder's monster-resists.json; without them only legacy and anchored
                              are shown); all adds the variants. Bots run simulated (legacy before 15 Nov 2026).
                              A self section scores each client's own position against the positions the server stated.
               Trace accuracy <game or date folder> --compare-rules a,b [--client C] [--json]     each client replayed under client rules a and b (a version,
                              latest or recorded), scored side by side: live digests and cursor, monster life against life-stating packets, positions
        """;

    public static int Usage()
    {
        Console.Error.WriteLine("usage: " + UsageLines.Trim());
        return 2;
    }

    public int Run()
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return Usage();
        }

        var target = args[1];
        if (!TraceDataLocation.Use(Value("--datadir"), target))
        {
            Console.Error.WriteLine($"no item data found: item packets will not parse; pass --datadir or set {TraceDataLocation.EnvironmentVariable}");
        }

        if (Value("--compare-rules") is { } rules)
        {
            return CompareRules.Run(target, Value("--client"), rules, args.Contains("--json"), output);
        }

        var by = Value("--by") ?? "class";
        if (!Groupings.Contains(by))
        {
            return Usage();
        }

        var models = Models(Value("--model") ?? "legacy,anchored,simulated", MonsterSpeedTable.Shared.HasData);
        if (models == null)
        {
            return Usage();
        }

        var sample = TimeSpan.Zero;
        if (Value("--sample") is { } step)
        {
            if (!double.TryParse(step, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                return Usage();
            }

            sample = TimeSpan.FromSeconds(seconds);
        }

        var games = GameTrace.GamesUnder(target, (folder, reason) => Console.Error.WriteLine($"skipped {folder}: {reason}"));
        if (games.Count == 0)
        {
            output.WriteLine($"no game folders under {target}");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        var replays = new List<(GameTrace Game, string Label)>();
        foreach (var game in games)
        {
            var labels = Value("--client") is { } client ? [game.ResolveClient(client)] : game.Labels;
            replays.AddRange(labels.Select(label => (game, label)));
        }

        // Each client replays into its own game state and models, so clients run in parallel.
        var results = new MotionAccuracy[replays.Count];
        var selves = new SelfAccuracy[replays.Count];
        Parallel.For(0, replays.Count, i =>
        {
            results[i] = new MotionAccuracy(models(), sample);
            selves[i] = new SelfAccuracy();
            ClientReplay.Run(replays[i].Game, replays[i].Label, TimeSpan.MaxValue, [results[i], selves[i]]);
        });
        var accuracy = MotionAccuracy.Merge(models(), results);
        var clients = replays.Count;

        stopwatch.Stop();
        var report = new Report(accuracy, by, Int("--top", 15), Int("--min-samples", 10), sample);
        if (args.Contains("--json"))
        {
            var json = JsonSerializer.SerializeToNode(report.Json());
            json["self"] = JsonSerializer.SerializeToNode(SelfReport.Json(selves));
            output.WriteLine(json.ToJsonString(Indented));
            return 0;
        }

        output.WriteLine($"{games.Count} game(s), {clients} client replay(s), {accuracy.Observations.Count:N0} truth packets in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        output.WriteLine("n counts client observations: a server event seen by k clients counts k times. Errors in subtiles, median / p90 / max.");
        output.WriteLine("Bias: models are scored only at packets that carry a true position, and a 0x6D on the destination of a walk is exactly where legacy");
        output.WriteLine("(which puts a walking monster on its destination) is right, so legacy's error while a monster walks is understated here; --sample scores between packets.");
        if (!MonsterSpeedTable.Shared.HasData)
        {
            output.WriteLine($"no monster speeds in {D2NG.Core.GameDataLocation.MonsterResistsFile} (missing, or generated before speeds were extracted): simulated would read the legacy position and the variants the anchored one, so only legacy and anchored are shown");
        }

        var unplaced = accuracy.Models.Select((m, i) => (m.Name, Count: accuracy.Unplaced[i])).Where(u => u.Count > 0).ToList();
        if (unplaced.Count > 0)
        {
            output.WriteLine($"left out: {string.Join(", ", unplaced.Select(u => $"{u.Count:N0} truth packets where {u.Name} held the monster at (0, 0)"))} - a 0x67 of move type 20 carries no destination and legacy puts the monster on it; the bot's own position for it really is (0, 0) then");
        }

        report.Print(output);
        SelfReport.Print(output, selves);
        return 0;
    }

    private int Int(string name, int fallback) => Value(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

    /// <summary>
    /// "all", or a comma separated list of model names: legacy, anchored, simulated and the variants. Without
    /// monster speeds simulated reads legacy and every variant reads anchored, so only those two are kept.
    /// </summary>
    internal static Func<IReadOnlyList<IMotionModel>> Models(string names, bool speeds)
    {
        static List<IMotionModel> All() => [.. MotionAccuracy.StandardModels, .. MotionAccuracy.Variants()];
        var wanted = MotionAccuracy.ModelNames(names, speeds);
        if (wanted == null)
        {
            Console.Error.WriteLine($"models: all, or a list of {string.Join(", ", All().Select(m => m.Name))}");
            return null;
        }

        return () => [.. wanted.Select(w => All().First(m => m.Name == w))];
    }

    private sealed class Report(MotionAccuracy accuracy, string by, int top, int minSamples, TimeSpan sample)
    {
        private const double SpeedTolerance = 0.10;

        private readonly IReadOnlyList<string> _names = [.. accuracy.Models.Select(m => m.Name)];

        /// <summary>Monsters only: friendly and neutral units are reported apart.</summary>
        private IEnumerable<MotionObservation> Moving => accuracy.Observations.Where(o => o.Moving && !o.Friendly);

        private static string Key(MotionObservation o, string by) => by switch
        {
            "class" => ClassName(o.Class),
            "packet" => o.Packet,
            "path" => $"{o.ModeBefore} path {o.PathType}{(o.RunBonus > 0 ? $" v{o.RunBonus}" : "")}",
            "flags" => o.Flags,
            "states" => o.States,
            "since" => o.SinceAnchor switch { < 0.5 => "0-0.5s", < 1 => "0.5-1s", < 2 => "1-2s", < 3 => "2-3s", _ => ">=3s" },
            _ => "",
        };

        public static string ClassName(D2NG.Core.D2GS.Objects.NPCCode code)
            => Enum.IsDefined(code) ? $"{code} ({(int)code})"
                : MonsterSpeedTable.Shared.TryGetFileValue(code, out var speed) && !string.IsNullOrEmpty(speed.Class) ? $"{speed.Class} ({(int)code})" : $"class {(int)code}";

        public void Print(TextWriter output)
        {
            output.WriteLine();
            output.WriteLine("Overall, monsters (friendly and neutral units below, not in any other table):");
            var moving = Moving.ToList();
            Table(output, "", [("moving", moving), ("not moving", accuracy.Observations.Where(o => !o.Moving && !o.Friendly).ToList())]);
            output.WriteLine($"  {"moving, mean",-14} {moving.Count,7:N0}  {string.Join("  ", _names.Select((_, i) => Mean(moving.Select(o => o.Errors), i).ToString("F2", CultureInfo.InvariantCulture).PadLeft(18)))}");
            output.WriteLine();
            output.WriteLine("Friendly and neutral units (town folk, hirelings, summons, \"dummy\" classes; Core FriendlyUnits):");
            Table(output, "", [("moving", accuracy.Observations.Where(o => o.Moving && o.Friendly).ToList()), ("not moving", accuracy.Observations.Where(o => !o.Moving && o.Friendly).ToList())]);
            Section(output, "By the packet that gave the true position (moving before it):", "packet", int.MaxValue);
            if (by != "packet")
            {
                Section(output, $"By {by} (moving, {top} largest):", by, top);
            }

            var simulated = IndexOf("simulated");
            if (simulated >= 0)
            {
                output.WriteLine();
                output.WriteLine($"Worst classes by simulated median (moving, n >= {minSamples}):");
                var worst = Moving.GroupBy(o => Key(o, "class"))
                    .Select(g => (g.Key, Rows: g.ToList()))
                    .Where(g => g.Rows.Count >= minSamples)
                    .OrderByDescending(g => Stats(g.Rows, simulated).Median)
                    .Take(top)
                    .ToList();
                Table(output, "class", worst);
            }

            output.WriteLine();
            output.WriteLine("Not modelled yet, reported apart (moving): slowing states the client knows of, and champions");
            Table(output, "states", [.. Moving.Where(o => o.States is not "none" and not "other").GroupBy(o => o.States).Select(g => (g.Key, g.ToList()))]);
            Table(output, "flags", [.. Moving.Where(o => o.Flags.Contains("champion", StringComparison.Ordinal)).GroupBy(o => o.Flags).Select(g => (g.Key, g.ToList()))]);

            PrintSpeeds(output);
            PrintUnchecked(output);
            if (sample > TimeSpan.Zero)
            {
                PrintSamples(output);
            }

            if (IndexOf(MotionAccuracy.ReplicaName) >= 0 && simulated >= 0)
            {
                output.WriteLine();
                output.WriteLine($"game simulated vs {MotionAccuracy.ReplicaName}: {accuracy.Disagreements} disagreements. Both run the same MotionRules code, so this checks");
                output.WriteLine("the game's snapshot storage, clock conversion and chase target lookup, not the rules themselves.");
            }
        }

        private void PrintSamples(TextWriter output)
        {
            output.WriteLine();
            output.WriteLine($"Sampled every {sample.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s between two exact positions, against the straight-line interpolation of them at their record times.");
            output.WriteLine("Segments: walk = exact standing/acting position (0x6D, 0x6B, 0x6C), then 0x67, then 0x6D on that 0x67's destination;");
            output.WriteLine($"chase = 0x68 then the next 0x68. Nothing else about the unit in between, at most {MotionAccuracy.LongestSampledSegment.TotalSeconds:F0} s long. A walk is assumed");
            output.WriteLine("to start at the 0x67 and end at the 0x6D at constant speed, a chase to move straight at constant speed; neither is checked.");
            var monsters = accuracy.Samples.Where(s => !s.Friendly).ToList();
            SampleTable(output, "segment, monsters", [("all", monsters), .. monsters.GroupBy(s => s.Segment).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.ToList()))]);
            output.WriteLine($"  {"all, mean",-20} {monsters.Count,7:N0}  {string.Join("  ", _names.Select((_, i) => Mean(monsters.Select(s => s.Errors), i).ToString("F2", CultureInfo.InvariantCulture).PadLeft(18)))}");
            var friendly = accuracy.Samples.Where(s => s.Friendly).ToList();
            SampleTable(output, "friendly/neutral", [("all", friendly)]);
            output.WriteLine();
            output.WriteLine($"Sampled, monsters, by class ({top} largest):");
            SampleTable(output, "class", [.. monsters.GroupBy(s => ClassName(s.Class)).OrderByDescending(g => g.Count()).Take(top).Select(g => (g.Key, g.ToList()))]);
        }

        private void SampleTable(TextWriter output, string keyTitle, List<(string Key, List<MotionSample> Rows)> groups)
            => ErrorTable(output, keyTitle, [.. groups.Select(g => (g.Key, g.Rows.Select(r => r.Errors).ToList()))]);

        /// <summary>What the next recording should check: classes off their predicted walk speed, and classes never measured walking.</summary>
        private void PrintUnchecked(TextWriter output)
        {
            output.WriteLine();
            output.WriteLine($"Walk speed off the prediction by more than {SpeedTolerance:P0} (n >= {minSamples}):");
            var off = SpeedRows().Where(r => r.Predicted > 0 && Math.Abs(r.Measured / r.Predicted - 1) > SpeedTolerance).ToList();
            if (off.Count == 0)
            {
                output.WriteLine("  (none)");
            }

            foreach (var r in off)
            {
                output.WriteLine($"  {r.Class,-26} {r.Flags,-18} v{r.RunBonus,-3} path {r.PathType,-3} n {r.N,4}  predicted {r.Predicted,6:F2}  measured {r.Measured,6:F2}  ratio {r.Measured / r.Predicted:F2}  ({Confidence(r.ClassId)})");
            }

            output.WriteLine();
            output.WriteLine("Monster classes in these games with no clean walk sample (exact position, 0x67, 0x6D on its destination):");
            var walked = accuracy.SpeedSamples.Select(s => s.Class).ToHashSet();
            var unmeasured = accuracy.Classes.Where(c => !walked.Contains(c) && !MotionAccuracy.IsFriendly(c)).OrderBy(c => (int)c).ToList();
            if (unmeasured.Count == 0)
            {
                output.WriteLine("  (none)");
            }

            foreach (var code in unmeasured)
            {
                var observed = accuracy.Observations.Count(o => o.Class == code && o.Moving);
                output.WriteLine($"  {ClassName(code),-30} {observed,6:N0} moving observations  ({Confidence(code)})");
            }
        }

        private static string Confidence(D2NG.Core.D2GS.Objects.NPCCode code)
        {
            var table = MonsterSpeedTable.Shared;
            return table.Confidence(code) switch
            {
                SpeedConfidence.RealmOverride => $"realm override {table.Overrides[(int)code]}, measured",
                SpeedConfidence.Measured => "measured on the realm",
                SpeedConfidence.FileOnly => "file value only",
                _ => "no speed",
            };
        }

        private int IndexOf(string name) => _names.ToList().IndexOf(name);

        private void Section(TextWriter output, string title, string key, int limit)
        {
            output.WriteLine();
            output.WriteLine(title);
            Table(output, key, [.. Moving.GroupBy(o => Key(o, key)).OrderByDescending(g => g.Count()).Take(limit).Select(g => (g.Key, g.ToList()))]);
        }

        private void Table(TextWriter output, string keyTitle, List<(string Key, List<MotionObservation> Rows)> groups)
            => ErrorTable(output, keyTitle, [.. groups.Select(g => (g.Key, g.Rows.Select(r => r.Errors).ToList()))]);

        private void ErrorTable(TextWriter output, string keyTitle, List<(string Key, List<double[]> Rows)> groups)
        {
            if (groups.Count == 0)
            {
                output.WriteLine("  (none)");
                return;
            }

            var width = Math.Max(Math.Max(14, keyTitle.Length + 1), groups.Max(g => g.Key.Length) + 1);
            output.WriteLine($"  {keyTitle.PadRight(width)} {"n",7}  {string.Join("  ", _names.Select(n => n.PadLeft(18)))}");
            foreach (var (key, rows) in groups)
            {
                var cells = _names.Select((_, i) => Stats(rows, i)).Select(s => s.Count == 0 ? "-" : $"{s.Median,4:F1} /{s.P90,5:F1} /{s.Max,5:F0}");
                output.WriteLine($"  {key.PadRight(width)} {rows.Count,7:N0}  {string.Join("  ", cells.Select(c => c.PadLeft(18)))}");
            }
        }

        public static double Mean(IEnumerable<double[]> rows, int model)
        {
            var errors = rows.Select(r => r[model]).Where(e => !double.IsNaN(e)).ToList();
            return errors.Count == 0 ? 0 : errors.Average();
        }

        public static (int Count, double Median, double P90, double Max) Stats(List<MotionObservation> rows, int model)
            => Stats(rows.Select(r => r.Errors), model);

        public static (int Count, double Median, double P90, double Max) Stats(IEnumerable<double[]> rows, int model)
        {
            var errors = rows.Select(r => r[model]).Where(e => !double.IsNaN(e)).OrderBy(e => e).ToList();
            if (errors.Count == 0)
            {
                return (0, 0, 0, 0);
            }

            var middle = errors.Count / 2;
            var median = errors.Count % 2 == 1 ? errors[middle] : (errors[middle - 1] + errors[middle]) / 2;
            return (errors.Count, median, errors[Math.Min(errors.Count - 1, (int)(errors.Count * 0.9))], errors[^1]);
        }

        private List<SpeedRow> SpeedRows()
        {
            var table = MonsterSpeedTable.Shared;
            var rules = MotionRules.Default;
            var fileRules = new MotionRules { Speeds = table.WithOverrides(null) };
            var addedRules = new MotionRules { RunBonus = RunBonusFormula.Added };
            return [.. accuracy.SpeedSamples
                .DistinctBy(s => (s.Game, s.Unit, s.Start, s.Destination))
                .GroupBy(s => (s.Class, s.Flags, s.RunBonus, s.PathType))
                .Select(g =>
                {
                    var measured = g.Select(s => s.Measured).OrderBy(v => v).ToList();
                    var middle = measured.Count / 2;
                    var median = measured.Count % 2 == 1 ? measured[middle] : (measured[middle - 1] + measured[middle]) / 2;
                    var unit = g.First().MotionUnit;
                    table.TryGetFileValue(g.Key.Class, out var file);
                    table.TryGet(g.Key.Class, out var realm);
                    return new SpeedRow(g.Key.Class, ClassName(g.Key.Class), g.Key.Flags, g.Key.RunBonus, g.Key.PathType, measured.Count, file.Velocity, realm.Velocity,
                        fileRules.Speed(unit, g.Key.RunBonus), rules.Speed(unit, g.Key.RunBonus), addedRules.Speed(unit, g.Key.RunBonus), median);
                })
                .Where(r => r.N >= minSamples)
                .OrderByDescending(r => r.N)];
        }

        private void PrintSpeeds(TextWriter output)
        {
            output.WriteLine();
            output.WriteLine($"Walk speed, subtiles/s: exact position, 0x67, 0x6D on its destination; walks de-duplicated per game, unit, start and end (n >= {minSamples})");
            output.WriteLine($"  {"class",-26} {"flags",-18} {"v",3} {"path",4} {"n",5} {"vel bin",8} {"realm",6} {"pred bin",9} {"pred",6} {"pred v+",8} {"measured",9} {"ratio",6}");
            foreach (var r in SpeedRows())
            {
                output.WriteLine($"  {r.Class,-26} {r.Flags,-18} {r.RunBonus,3} {r.PathType,4} {r.N,5} {r.FileVelocity,8} {r.RealmVelocity,6} {r.PredictedFile,9:F2} {r.Predicted,6:F2} {r.PredictedAdded,8:F2} {r.Measured,9:F2} {(r.Predicted > 0 ? r.Measured / r.Predicted : 0),6:F2}");
            }
        }

        private sealed record SpeedRow([property: System.Text.Json.Serialization.JsonIgnore] D2NG.Core.D2GS.Objects.NPCCode ClassId, string Class, string Flags, byte RunBonus, byte PathType, int N, int FileVelocity, int RealmVelocity, double PredictedFile, double Predicted, double PredictedAdded, double Measured);

        public object Json()
        {
            object Groups(string key) => Moving.GroupBy(o => Key(o, key)).OrderByDescending(g => g.Count()).Select(g =>
            {
                var rows = g.ToList();
                return new { key = g.Key, n = rows.Count, models = _names.Select((name, i) => { var s = Stats(rows, i); return new { name, median = s.Median, p90 = s.P90, max = s.Max }; }) };
            }).ToList();

            object Overall(List<double[]> rows) => _names.Select((name, i) => { var s = Stats(rows, i); return new { name, n = s.Count, median = s.Median, p90 = s.P90, max = s.Max, mean = Mean(rows, i) }; }).ToList();

            var moving = Moving.ToList();
            return new
            {
                observations = accuracy.Observations.Count,
                note = "n counts client observations; monsters only, friendly and neutral units apart",
                overall = Overall([.. moving.Select(o => o.Errors)]),
                friendlyMoving = Overall([.. accuracy.Observations.Where(o => o.Moving && o.Friendly).Select(o => o.Errors)]),
                byPacket = Groups("packet"),
                by = new { key = by, groups = Groups(by) },
                speeds = SpeedRows(),
                sampled = sample > TimeSpan.Zero ? new
                {
                    stepSeconds = sample.TotalSeconds,
                    monsters = Overall([.. accuracy.Samples.Where(s => !s.Friendly).Select(s => s.Errors)]),
                    bySegment = accuracy.Samples.Where(s => !s.Friendly).GroupBy(s => s.Segment).Select(g => new { segment = g.Key, models = Overall([.. g.Select(s => s.Errors)]) }).ToList(),
                } : null,
                replicaDisagreements = accuracy.Disagreements,
            };
        }
    }
}
