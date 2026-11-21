using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceTool;

/// <summary>activity --party-spread: how much of the hunt each member spent away from the rest of the party.</summary>
internal sealed partial class Commands
{
    private const double DefaultAloneDistance = 30;

    private int PartySpreadOf(List<GameTrace> games)
    {
        var alone = Value("--alone") is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : DefaultAloneDistance;
        var sources = Shared;
        var capped = new CappedOutput(output, Limit, "narrow with --since or --game-prefix");
        if (!AsJson)
        {
            output.WriteLine($"{games.Count} games under {Path.GetFullPath(GameTrace.ParseTarget(_typed).Folder)}{FilterText()}; each second of the hunt, every member's distance to the nearest other one; alone = farther than {alone:0} from all");
        }

        var summaries = new List<string>();
        var skipped = 0;
        foreach (var (clientSet, partyGames) in Parties(games))
        {
            var present = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            var aloneTime = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            var nearest = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var counted = 0;
            foreach (var game in partyGames)
            {
                var query = sources.QueryOf(game);
                var samples = query.ReplayAll(query.Game.Labels.ToList(), TimeSpan.MaxValue, () => new PositionObserver(), o => [o])
                    .ToDictionary(r => r.Item1, r => (IReadOnlyList<PartySpread.Sample>)r.Item2.Samples);
                var result = PartySpread.Compute(samples, alone, TimeSpan.FromSeconds(1));
                if (result.Clients.Count == 0)
                {
                    continue;
                }

                counted++;
                foreach (var c in result.Clients)
                {
                    present[c.Client] = present.GetValueOrDefault(c.Client) + c.Present;
                    aloneTime[c.Client] = aloneTime.GetValueOrDefault(c.Client) + c.Alone;
                    (nearest.TryGetValue(c.Client, out var list) ? list : nearest[c.Client] = []).Add(c.MedianNearest);
                }

                if (AsJson)
                {
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        party = clientSet,
                        game = Ref(query.Game),
                        area = result.Area,
                        seconds = Math.Round((result.To - result.From).TotalSeconds),
                        clients = result.Clients.Select(c => new { client = c.Client, present = Math.Round(c.Present.TotalSeconds), alone = Math.Round(c.Alone.TotalSeconds), medianNearest = Math.Round(c.MedianNearest, 1) }),
                    }, Json));
                    continue;
                }

                capped.Line($"  {Ref(query.Game),-28} {result.Area} {(result.To - result.From).TotalSeconds,4:0}s  alone: "
                    + string.Join(", ", result.Clients.Select(c => $"{c.Client} {100 * c.AloneShare:0}%")));
            }

            if (counted < 2)
            {
                skipped++;
                continue;
            }

            summaries.Add($"== party {clientSet}: {counted} games; per member, time alone (farther than {alone:0} from every other member) and the median distance to the nearest one");
            foreach (var (client, time) in present.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var list = nearest[client].OrderBy(v => v).ToList();
                summaries.Add($"  {client,-14} alone {100 * (time > TimeSpan.Zero ? aloneTime[client] / time : 0),3:0}% of {time.TotalMinutes:0.0} min; median nearest {list[list.Count / 2]:0} (per-game medians)");
            }
        }

        if (AsJson)
        {
            return 0;
        }

        capped.Finish();
        summaries.ForEach(output.WriteLine);
        if (skipped > 0)
        {
            output.WriteLine($"{skipped} part{(skipped == 1 ? "y" : "ies")} with fewer than 2 such games left out of the summary (a mule detour is one)");
        }

        output.WriteLine("note: sorceresses that teleport ahead read as alone by design; the walkers' share is what a straggler wait changes");
        return 0;
    }
}
