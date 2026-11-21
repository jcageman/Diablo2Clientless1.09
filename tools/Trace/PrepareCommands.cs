using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceTool;

/// <summary>activity --prepare: whose prepare a party bot's run waited for.</summary>
internal sealed partial class Commands
{
    /// <summary>
    /// A party's run starts once every member's prepare step has ended, so the last one to end holds the others for the
    /// time between it and the one before it. Per game: the last two and that hold; per client: how often it was last,
    /// the seconds it held the party and the length of its own prepare. Read from the typed steps only, no replay.
    /// </summary>
    private int PrepareHoldups(List<GameTrace> games)
    {
        var sources = Shared;
        var lastIn = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var lengths = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var failed = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new List<string>();
        var withSteps = 0;
        foreach (var game in games)
        {
            var query = sources.QueryOf(game);
            var prepares = Steps.Of(query).Where(s => s.Client != null && s.Name == "prepare").ToList();
            if (prepares.Count == 0)
            {
                continue;
            }

            withSteps++;
            foreach (var step in prepares)
            {
                (lengths.TryGetValue(step.Client, out var list) ? list : lengths[step.Client] = []).Add(step.Duration.TotalSeconds);
                if (step.Outcome != "ok")
                {
                    failed[step.Client] = failed.GetValueOrDefault(step.Client) + 1;
                }
            }

            var ends = prepares.GroupBy(s => s.Client).Select(g => g.OrderBy(s => s.End).Last()).OrderBy(s => s.End).ToList();
            var last = ends[^1];
            var held = ends.Count > 1 ? (last.End - ends[^2].End).TotalSeconds : 0;
            (lastIn.TryGetValue(last.Client, out var heldList) ? heldList : lastIn[last.Client] = []).Add(held);
            if (AsJson)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    game = Ref(query.Game),
                    last = last.Client,
                    lastEnd = TraceTime.Format(last.End),
                    lastOutcome = last.Outcome,
                    before = ends.Count > 1 ? ends[^2].Client : null,
                    beforeEnd = ends.Count > 1 ? TraceTime.Format(ends[^2].End) : null,
                    heldSeconds = Math.Round(held, 1),
                }, Json));
                continue;
            }

            lines.Add($"  {Ref(query.Game),-28} last {last.Client} at {TraceTime.Format(last.End)} (prepare {last.Duration.TotalSeconds:0.0}s{(last.Outcome == "ok" ? "" : $", {last.Outcome}")})"
                + (ends.Count > 1 ? $", before it {ends[^2].Client} at {TraceTime.Format(ends[^2].End)}: held the party {held:0.0}s" : ""));
        }

        if (AsJson)
        {
            return 0;
        }

        output.WriteLine($"{games.Count} games under {Path.GetFullPath(GameTrace.ParseTarget(_typed).Folder)}{FilterText()}; whose prepare the party waited for");
        if (withSteps == 0)
        {
            output.WriteLine("no prepare steps in these games' sidecars");
            return 0;
        }

        var capped = new CappedOutput(output, Limit, "narrow with --since or --game-prefix");
        foreach (var line in lines)
        {
            capped.Line(line);
        }

        capped.Finish();
        output.WriteLine("per client:");
        foreach (var (client, list) in lengths.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var held = lastIn.GetValueOrDefault(client) ?? [];
            var (median, p90) = Steps.Percentiles(list);
            output.WriteLine($"  {client}: last in {held.Count} of {withSteps} games, held the party {held.Sum():0.0}s in all"
                + (held.Count > 0 ? $" (median {Steps.Percentiles(held).Median:0.0}s)" : "")
                + $"; its prepare median {median:0.0}s, p90 {p90:0.0}s{(failed.GetValueOrDefault(client) is > 0 and var f ? $", {f} not ok" : "")}");
        }

        output.WriteLine("note: the party's run starts when the last prepare ends; a run let go early (the cow portal character's) starts before it");
        return 0;
    }
}
