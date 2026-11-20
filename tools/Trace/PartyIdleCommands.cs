using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceTool;

/// <summary>activity --party-idle: the stretches of a hunt in which no member of the party was fighting.</summary>
internal sealed partial class Commands
{
    /// <summary>
    /// From the party's first attack to its last in the area it fought in most, the time in which none of its clients was
    /// fighting there, and what each was
    /// doing in it. A client's own idle time says little in a party - the walkers stop while the sorceresses clear - but
    /// time in which nobody attacks is time the party kills nothing.
    /// </summary>
    private int PartyIdle(List<GameTrace> games)
    {
        var sources = Shared;
        var hunt = TimeSpan.Zero;
        var idle = TimeSpan.Zero;
        var gaps = new List<double>();
        var doing = new Dictionary<string, Dictionary<ActivityState, TimeSpan>>(StringComparer.Ordinal);
        var counted = 0;
        var capped = new CappedOutput(output, Limit, "narrow with --since or --game-prefix");
        if (!AsJson)
        {
            output.WriteLine($"{games.Count} games under {Path.GetFullPath(GameTrace.ParseTarget(_typed).Folder)}{FilterText()}; time in the hunt no member was fighting");
        }

        foreach (var game in games)
        {
            var query = sources.QueryOf(game);
            var (clients, _, _) = ComputeActivity(query, null, Steps.Of(query));
            // In the area the party fought in most: a fight on the way there, in a town or a passage, is not the hunt.
            var fights = clients.SelectMany(c => c.Stretches.Where(s => s.State == ActivityState.Fighting && s.Area != null)).ToList();
            var farm = fights.GroupBy(s => s.Area).OrderByDescending(g => g.Sum(s => s.Duration.Ticks)).Select(g => g.Key).FirstOrDefault();
            var fighting = fights.Where(s => s.Area == farm).Select(s => (s.Start, s.End)).OrderBy(s => s.Start).ToList();
            if (fighting.Count == 0)
            {
                continue;
            }

            counted++;
            var (start, end) = (fighting[0].Start, fighting.Max(s => s.End));
            var holes = new List<(TimeSpan Start, TimeSpan End)>();
            var reached = start;
            foreach (var (from, to) in fighting)
            {
                if (from > reached)
                {
                    holes.Add((reached, from));
                }

                if (to > reached)
                {
                    reached = to;
                }
            }

            var gameIdle = TimeSpan.FromTicks(holes.Sum(h => (h.End - h.Start).Ticks));
            hunt += end - start;
            idle += gameIdle;
            gaps.AddRange(holes.Select(h => (h.End - h.Start).TotalSeconds));
            foreach (var c in clients)
            {
                var states = doing.TryGetValue(c.Client, out var known) ? known : doing[c.Client] = [];
                foreach (var stretch in c.Stretches)
                {
                    foreach (var (from, to) in holes)
                    {
                        var overlap = (stretch.End < to ? stretch.End : to) - (stretch.Start > from ? stretch.Start : from);
                        if (overlap > TimeSpan.Zero)
                        {
                            states[stretch.State] = states.GetValueOrDefault(stretch.State) + overlap;
                        }
                    }
                }
            }

            var longest = holes.Count == 0 ? 0 : holes.Max(h => (h.End - h.Start).TotalSeconds);
            if (AsJson)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    game = Ref(query.Game),
                    hunt = Math.Round((end - start).TotalSeconds, 1),
                    nobodyFighting = Math.Round(gameIdle.TotalSeconds, 1),
                    gaps = holes.Count,
                    longest = Math.Round(longest, 1),
                }, Json));
                continue;
            }

            capped.Line($"  {Ref(query.Game),-28} {farm} {(end - start).TotalSeconds,5:0}s, nobody fighting {gameIdle.TotalSeconds,5:0.0}s ({100 * gameIdle.TotalSeconds / Math.Max(1, (end - start).TotalSeconds),4:0}%) in {holes.Count} gaps, longest {longest:0.0}s");
        }

        if (AsJson)
        {
            return 0;
        }

        capped.Finish();
        if (counted == 0)
        {
            output.WriteLine("no fighting in these games");
            return 0;
        }

        output.WriteLine($"over {counted} games: hunt {hunt.TotalSeconds / counted:0}s a game, nobody fighting {idle.TotalSeconds / counted:0.0}s a game ({100 * idle.TotalSeconds / hunt.TotalSeconds:0}%), "
            + $"{gaps.Count(g => g >= 2)} gaps of 2s or more");
        output.WriteLine("what each member was doing while nobody fought:");
        foreach (var (client, states) in doing.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var total = states.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
            if (total <= TimeSpan.Zero)
            {
                continue;
            }

            output.WriteLine($"  {client}: " + string.Join(", ", states.OrderByDescending(s => s.Value).Where(s => s.Value.TotalSeconds >= 0.5)
                .Select(s => $"{s.Key.ToString().ToLowerInvariant()} {100 * s.Value.TotalSeconds / total.TotalSeconds:0}%")));
        }

        output.WriteLine("note: fighting is an attack or skill other than teleport, town portal or identify sent within the last 1.5 s, so a gap is at least that long after the last one");
        return 0;
    }
}
