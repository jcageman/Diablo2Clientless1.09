using D2NG.Core.D2GS;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// How far apart a party is while it hunts: each second, every member's distance to the nearest other member in the
/// area the party spent most time in, and the share of it a member spent alone - farther than a distance from all of
/// them. A walker left alone is what a straggler wait is for: the cow party's amazon crossing the gap to a lead ninety
/// units ahead left the game three times in six.
/// </summary>
public static class PartySpread
{
    /// <summary>A position older than this no longer says where the client is: it left, or the replay has nothing newer.</summary>
    public static readonly TimeSpan Staleness = TimeSpan.FromSeconds(2);

    public sealed record Sample(TimeSpan At, Point Location, string Area);

    public sealed record ClientSpread(string Client, TimeSpan Present, TimeSpan Alone, double MedianNearest)
    {
        public double AloneShare => Present > TimeSpan.Zero ? Alone / Present : 0;
    }

    public sealed record Result(string Area, TimeSpan From, TimeSpan To, IReadOnlyList<ClientSpread> Clients);

    public static Result Compute(IReadOnlyDictionary<string, IReadOnlyList<Sample>> samples, double aloneDistance, TimeSpan tick)
    {
        var area = samples.Values.SelectMany(s => s).Where(s => s.Area != null).GroupBy(s => s.Area)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        // Every sample, not only the area's: the latest one says whether the client is still there, so leaving it ends its
        // presence at once rather than a staleness later.
        var all = samples.ToDictionary(c => c.Key, c => c.Value.OrderBy(s => s.At).ToList());
        var inArea = all.Values.Select(list => list.Where(s => s.Area == area).ToList()).Where(list => list.Count > 0).ToList();
        if (area == null || inArea.Count < 2)
        {
            return new Result(area, TimeSpan.Zero, TimeSpan.Zero, []);
        }

        var from = inArea.Min(s => s[0].At);
        var to = inArea.Max(s => s[^1].At);
        var alone = all.Keys.ToDictionary(c => c, _ => TimeSpan.Zero);
        var there = all.Keys.ToDictionary(c => c, _ => TimeSpan.Zero);
        var nearest = all.Keys.ToDictionary(c => c, _ => new List<double>());
        var cursor = all.Keys.ToDictionary(c => c, _ => 0);
        for (var t = from; t <= to; t += tick)
        {
            var at = new List<(string Client, Point Location)>();
            foreach (var (client, list) in all)
            {
                var i = cursor[client];
                while (i + 1 < list.Count && list[i + 1].At <= t)
                {
                    i++;
                }

                cursor[client] = i;
                if (list.Count > 0 && list[i].At <= t && t - list[i].At <= Staleness && list[i].Area == area)
                {
                    at.Add((client, list[i].Location));
                }
            }

            if (at.Count < 2)
            {
                continue;
            }

            foreach (var (client, location) in at)
            {
                var distance = at.Where(o => o.Client != client).Min(o => location.Distance(o.Location));
                there[client] += tick;
                nearest[client].Add(distance);
                if (distance > aloneDistance)
                {
                    alone[client] += tick;
                }
            }
        }

        return new Result(area, from, to, all.Keys.Where(c => there[c] > TimeSpan.Zero).OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => new ClientSpread(c, there[c], alone[c], Median(nearest[c]))).ToList());
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        return values[values.Count / 2];
    }
}

/// <summary>The client's own position at most every <see cref="Every"/>, with its area, at offsets from the game's start.</summary>
public sealed class PositionObserver : IReplayObserver
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(0.5);

    public List<PartySpread.Sample> Samples { get; } = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        if (context.Game.Me?.Location is not { } location)
        {
            return;
        }

        var at = context.Offset(record);
        if (Samples.Count > 0 && at - Samples[^1].At < Every)
        {
            return;
        }

        Samples.Add(new PartySpread.Sample(at, location, context.Game.Area.ToString()));
    }
}
