using D2NG.Core;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.MonsterData;
using D2NG.Core.Tracing;
using D2NG.Trace;
using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TraceTool;

/// <summary>
/// Trace accuracy --compare-rules a,b: every client of every game replayed under two client rules versions, and what each
/// held scored side by side against what the server and the live bot said - the yardstick for a proposed rule.
/// </summary>
internal sealed class CompareRules(IReadOnlyList<GameTrace> games, Func<GameTrace, IReadOnlyList<string>> labels, TextWriter output)
{
    /// <summary>The games under <paramref name="target"/>, each client or the one named, scored under both rules of <paramref name="pair"/>.</summary>
    public static int Run(string target, string client, string pair, bool json, TextWriter output)
    {
        var games = GameTrace.GamesUnder(target, (folder, reason) => Console.Error.WriteLine($"skipped {folder}: {reason}"));
        if (games.Count == 0)
        {
            output.WriteLine($"no game folders under {target}");
            return 1;
        }

        return new CompareRules(games, game => client == null ? game.Labels : [game.ResolveClient(client)], output).Run(pair, json);
    }

    public int Run(string pair, bool json)
    {
        var parts = pair.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"--compare-rules {pair}: give two rules, such as 1,latest");
        }

        var replays = games.SelectMany(game => labels(game).Select(label => (game, label))).ToList();
        var passes = new List<Pass>();
        var asked = ReplayRules.OverrideText;
        try
        {
            foreach (var rules in parts)
            {
                ReplayRules.Use(rules);
                passes.Add(Score(rules, replays));
            }
        }
        finally
        {
            ReplayRules.Use(asked);
        }

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(passes.Select(p => p.Json()), Indented));
            return 0;
        }

        output.WriteLine($"{games.Count} game(s), {replays.Count} client replay(s), each replayed under rules {parts[0]} and {parts[1]}; recorded = the rules each trace's header names");
        Row("", passes.Select(p => $"rules {p.Name}"));
        Row("digests agreeing with live", passes.Select(p => Share(p.Score.DigestsAgreeing, p.Score.Digests)));
        Row("cursor agreeing with live", passes.Select(p => Share(p.Score.CursorAgreeing, p.Score.Digests)));
        output.WriteLine("monster life held when a packet states it (held differs by over 1%, mean absolute difference):");
        foreach (var kind in passes.SelectMany(p => p.Score.Life.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            Row($"  {kind}", passes.Select(p => p.Score.Life.TryGetValue(kind, out var life) ? $"{Share(life.N - life.Agreeing, life.N)} off, {life.AbsoluteSum / Math.Max(1, life.N):F1}%" : "-"));
        }

        output.WriteLine("monster position error at truth packets, subtiles median / p90:");
        foreach (var model in passes[0].Positions.Keys)
        {
            Row($"  {model}", passes.Select(p => p.Positions.TryGetValue(model, out var errors) ? $"{Percentile(errors, 0.5):F1} / {Percentile(errors, 0.9):F1} (n {errors.Count:N0})" : "-"));
        }

        Row("self: server corrections, median distance", passes.Select(p => $"{p.Corrections.Count:N0}, {Percentile(p.Corrections, 0.5):F1}"));
        Row("self: seconds in another area than the server's", passes.Select(p => $"{p.AreaDifferent:F0}"));
        return 0;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private void Row(string name, IEnumerable<string> cells) => output.WriteLine($"{name,-48} {string.Join("", cells.Select(c => $"{c,-32}"))}".TrimEnd());

    private static string Share(long part, long whole) => whole == 0 ? "-" : $"{part:N0}/{whole:N0} ({100.0 * part / whole:F1}%)";

    private static double Percentile(List<double> values, double at)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(at * sorted.Count))];
    }

    private static Pass Score(string name, List<(GameTrace Game, string Label)> replays)
    {
        var models = MotionAccuracy.StandardModels.Where(m => MonsterSpeedTable.Shared.HasData || m != GameMotionModel.Simulated).ToList();
        var scores = new RulesScore[replays.Count];
        var motions = new MotionAccuracy[replays.Count];
        var selves = new SelfAccuracy[replays.Count];
        Parallel.For(0, replays.Count, i =>
        {
            scores[i] = new RulesScore();
            motions[i] = new MotionAccuracy(models);
            selves[i] = new SelfAccuracy();
            ClientReplay.Run(replays[i].Game, replays[i].Label, TimeSpan.MaxValue, [scores[i], motions[i], selves[i]])?.Replayer.Dispose();
        });

        var motion = MotionAccuracy.Merge(models, motions);
        var positions = models.Select((m, index) => (m.Name, Errors: motion.Observations.Select(o => o.Errors[index]).Where(e => !double.IsNaN(e)).ToList()))
            .ToDictionary(m => m.Name, m => m.Errors);
        return new Pass(name, RulesScore.Merge(scores), positions, [.. selves.SelectMany(s => s.Corrections.Where(c => !c.Warp).Select(c => c.Distance))], selves.Sum(s => s.AreaDifferent));
    }

    private sealed record Pass(string Name, RulesScore Score, Dictionary<string, List<double>> Positions, List<double> Corrections, double AreaDifferent)
    {
        public object Json() => new
        {
            rules = Name,
            digests = Score.Digests,
            digestsAgreeing = Score.DigestsAgreeing,
            cursorAgreeing = Score.CursorAgreeing,
            life = Score.Life.ToDictionary(l => l.Key, l => new { n = l.Value.N, agreeing = l.Value.Agreeing, meanAbsolute = l.Value.AbsoluteSum / Math.Max(1, l.Value.N) }),
            positions = Positions.ToDictionary(p => p.Key, p => new { n = p.Value.Count, median = Percentile(p.Value, 0.5), p90 = Percentile(p.Value, 0.9) }),
            selfCorrections = Corrections.Count,
            selfCorrectionMedian = Percentile(Corrections, 0.5),
            selfAreaDifferentSeconds = AreaDifferent,
        };
    }
}
