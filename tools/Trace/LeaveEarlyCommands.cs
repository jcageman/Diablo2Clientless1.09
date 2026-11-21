using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceTool;

/// <summary>kpi xp --leave-after: what leaving each game earlier would have earned.</summary>
internal sealed partial class Commands
{
    private static readonly TimeSpan LeaveAfterStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeaveAfterBin = TimeSpan.FromSeconds(15);
    private const int LeaveAfterMinGames = 5;

    /// <summary>
    /// Per party, one character's experience curve through each game, replayed from the experience packets: the rate in
    /// each stretch after the first gain, and the rate every game would have made had it been left a fixed time after
    /// its first gain. Experience is shared by the party, so one character stands for all of it: --client, else the
    /// one with experience in the most games.
    /// </summary>
    private int LeaveAfter(List<GameTrace> games)
    {
        var only = Value("--client");
        var sources = Shared;
        if (!AsJson)
        {
            output.WriteLine($"{games.Count} games under {Path.GetFullPath(GameTrace.ParseTarget(_typed).Folder)}{FilterText()}; experience by time after each game's first gain, and what leaving earlier would have made");
        }

        var skipped = 0;
        foreach (var (clientSet, partyGames) in Parties(games))
        {
            var files = new List<ExperienceCurveObserver.FileCurve>();
            foreach (var game in partyGames)
            {
                var query = sources.QueryOf(game);
                var labels = only == null ? game.Labels.ToList() : game.Labels.Where(l => string.Equals(l, only, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var (_, observer) in query.ReplayAll(labels, TimeSpan.MaxValue, () => new ExperienceCurveObserver(), o => [o]))
                {
                    files.AddRange(observer.Files);
                }
            }

            var perGame = ExperienceCurveObserver.PerGame(files);
            var character = perGame.Where(g => g.Gained > 0).GroupBy(g => g.Character, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();
            var result = character == null ? null : LeaveEarly.Compute(perGame.Where(g => string.Equals(g.Character, character, StringComparison.OrdinalIgnoreCase)).ToList(), LeaveAfterStep, LeaveAfterBin, LeaveAfterMinGames);
            if (result == null || result.Games < LeaveAfterMinGames)
            {
                skipped++;
                continue;
            }

            if (AsJson)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    party = clientSet,
                    character,
                    games = result.Games,
                    betweenSeconds = Math.Round(result.Between.TotalSeconds, 1),
                    firstGainSeconds = Math.Round(result.FirstGain.TotalSeconds, 1),
                    lengthSeconds = Math.Round(result.Length.TotalSeconds, 1),
                    fullPerMinute = Math.Round(result.FullPerMinute),
                    marginal = result.Marginal.Select(b => new { from = b.From.TotalSeconds, perMinute = Math.Round(b.PerMinute), games = b.Games }),
                    cuts = result.Cuts.Select(c => new { after = c.After.TotalSeconds, perMinute = Math.Round(c.PerMinute), change = Math.Round(c.Change * 100, 2), gamesCut = c.GamesCut }),
                }, Json));
                continue;
            }

            output.WriteLine($"== party {clientSet}: {result.Games} games with experience, measured on {character}");
            output.WriteLine($"  medians: first gain {result.FirstGain.TotalSeconds:0.0}s after the game's start, game {result.Length.TotalSeconds:0}s long, {result.Between.TotalSeconds:0.0}s between games");
            output.WriteLine($"  as played: {result.FullPerMinute:N0} xp/min");
            output.WriteLine($"  xp/min in each {LeaveAfterBin.TotalSeconds:0}s after the first gain (games still running):");
            foreach (var b in result.Marginal)
            {
                output.WriteLine($"    {b.From.TotalSeconds,4:0}-{(b.From + LeaveAfterBin).TotalSeconds,4:0}s {b.PerMinute,9:N0}  ({b.Games}){(b.PerMinute < result.FullPerMinute ? "  below the average" : "")}");
            }

            output.WriteLine("  every game left this long after its first gain:");
            foreach (var c in result.Cuts.Where(c => c.GamesCut > 0 && c.Change > -0.1))
            {
                output.WriteLine($"    {c.After.TotalSeconds,4:0}s {c.PerMinute,9:N0} xp/min {c.Change * 100,6:+0.0;-0.0}%  {c.GamesCut} games cut");
            }

            if (result.Best is { } best && best.Change > 0)
            {
                output.WriteLine($"  best: {best.After.TotalSeconds:0}s after the first gain, {best.Change * 100:+0.0}%");
            }
        }

        if (AsJson)
        {
            return 0;
        }

        if (skipped > 0)
        {
            output.WriteLine($"{skipped} part{(skipped == 1 ? "y" : "ies")} with fewer than {LeaveAfterMinGames} games with experience left out");
        }

        output.WriteLine($"note: a cut loses the experience after it, leaving takes {LeaveEarly.LeaveTakes.TotalSeconds:0}s and the next game starts as it did; the best cut of these games "
            + "reads better than it will do on new ones, so check it on another day's games. A bot's own clock starts elsewhere (a hunt setting off): translate by the first gain.");
        return 0;
    }
}
