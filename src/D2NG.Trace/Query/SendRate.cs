using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// An outgoing packet type a client sent at an unusual rate against its own baseline: more than
/// <see cref="Threshold"/> - <see cref="OverMedian"/> times the client's median per minute for that type in
/// this game (at least 1), and never under <see cref="Unusual"/> - in the sliding minute before each instant,
/// for at least <see cref="Sustained"/>, the sends spanning that long too. Earlier games are not consulted:
/// a burst that recurs every game would raise their medians and hide itself.
/// Types that come in bursts by nature are left out: walking and running, attacks and skills, the ping,
/// picking items up, and during a town stay item moves and NPC, waypoint and stash business. Reported with what the client was doing meanwhile (its activity states and step).
/// </summary>
public sealed class SendRateCheck : CompletedCheck
{
    public const double RareMedian = 2;

    public const double Unusual = 15;

    public static readonly TimeSpan Sustained = TimeSpan.FromSeconds(20);

    public override string Family => "bot";

    /// <summary>Walk and run (0x01-0x04), attacks and skills (0x05-0x11), the ping, and picking items up: a dead pack's loot comes in a heap.</summary>
    public static bool Bursty(byte type) => type is >= 0x01 and <= 0x11 or 0x16 || type == (byte)Core.D2GS.Packet.OutGoingPacket.Ping || type == 0x6D;

    /// <summary>What a town stay does by the dozen: moving items (0x16-0x2A), and interacting, talking and trading with NPCs, the waypoint and the stash (0x13, 0x2F-0x3A, 0x49, 0x4F, 0x59).</summary>
    public static bool TownBusiness(byte type) => type is >= 0x16 and <= 0x2A or 0x13 or >= 0x2F and <= 0x3A or 0x49 or 0x4F or 0x59;

    /// <summary>How many times its own median per minute a type must be sent in a sliding minute to be unusual, never under <see cref="Unusual"/>.</summary>
    public const double OverMedian = 8;

    public static double Threshold(double median) => Math.Max(Unusual, OverMedian * Math.Max(median, 1));

    public override IEnumerable<Incident> Run(CheckContext context)
    {
        var activity = context.Activity;
        var sends = activity.Sends.Where(s => !Bursty(s.Type) && !(TownBusiness(s.Type) && activity.AreaAt(s.At) is { } area && ActivityObserver.IsTown(area))).ToList();
        var medians = SendRates.Medians(sends, activity.Files);
        var found = new List<Incident>();
        foreach (var group in sends.GroupBy(s => s.Type))
        {
            var median = medians.GetValueOrDefault(group.Key);
            var times = group.Select(s => s.At).OrderBy(t => t).ToList();
            var counted = 0;
            foreach (var (from, to, peak) in Episodes(times, Threshold(median)))
            {
                var sent = times.Skip(counted).Where(t => t >= from - Window && t <= to).ToList();
                counted = times.FindLastIndex(t => t <= to) + 1;
                if (sent.Count == 0 || sent[^1] - sent[0] < Sustained)
                {
                    continue;
                }

                found.Add(Report(context, group.Key, sent[0], sent[^1], sent.Count, peak, median));
            }
        }

        return found;
    }

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static List<(TimeSpan From, TimeSpan To, int Peak)> Episodes(IReadOnlyList<TimeSpan> sorted) => Episodes(sorted, Unusual);

    /// <summary>
    /// The stretches of time in which the sends of the last minute numbered over <paramref name="threshold"/>,
    /// lasting at least <see cref="Sustained"/>, with the most sends any minute of them held. Stretches less
    /// than a minute apart are one, so no send is counted in two.
    /// </summary>
    public static List<(TimeSpan From, TimeSpan To, int Peak)> Episodes(IReadOnlyList<TimeSpan> sorted, double threshold)
    {
        var changes = sorted.Select(t => (At: t, Step: 1)).Concat(sorted.Select(t => (At: t + Window, Step: -1))).OrderBy(c => c.At).ThenBy(c => c.Step).ToList();
        var raw = new List<(TimeSpan From, TimeSpan To, int Peak)>();
        var count = 0;
        var peak = 0;
        TimeSpan? since = null;
        foreach (var (at, step) in changes)
        {
            count += step;
            if (count > threshold)
            {
                since ??= at;
                peak = Math.Max(peak, count);
            }
            else if (since is { } start)
            {
                raw.Add((start, at, peak));
                since = null;
                peak = 0;
            }
        }

        var merged = new List<(TimeSpan From, TimeSpan To, int Peak)>();
        foreach (var episode in raw)
        {
            if (merged.Count > 0 && episode.From - merged[^1].To < Window)
            {
                merged[^1] = (merged[^1].From, episode.To, Math.Max(merged[^1].Peak, episode.Peak));
            }
            else
            {
                merged.Add(episode);
            }
        }

        return [.. merged.Where(e => e.To - e.From >= Sustained)];
    }

    private static Incident Report(CheckContext context, byte type, TimeSpan from, TimeSpan to, int count, int peak, double median)
    {
        var stretches = context.Activity.Stretches(from, to, ActivityObserver.Waits(context.Query, context.Client, to));
        var split = new ActivitySplit();
        split.Add(stretches, from, to);
        var step = context.Steps.Where(s => s.Covers(context.Client) && s.Start <= to && s.End >= from && s.Duration > TimeSpan.Zero).OrderBy(s => s.Duration).FirstOrDefault();
        return new Incident(from, context.Client, "unusual send rate",
            $"{Names.Outgoing(type)} (0x{type:X2}) sent {count} times in {(to - from).TotalSeconds:0}s until {TraceTime.Format(to)}, up to {peak} in one minute, against a median of {median:0.#}/min over this client's minutes in the game (unusual: over {Threshold(median):0} in a minute)"
            + $"; meanwhile {split.Render()}{(step == null ? "" : $", during step {step.Name}")}")
        { End = to };
    }
}

/// <summary>How often a client sends each packet type: the median count per minute over the minutes it had a trace file open.</summary>
public static class SendRates
{
    public static string Key(byte type) => $"0x{type:X2}";

    public static Dictionary<byte, double> Medians(IReadOnlyList<(TimeSpan At, byte Type)> sends, IReadOnlyList<(TimeSpan Start, TimeSpan End)> files)
    {
        var minutes = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var (start, end) in files)
        {
            for (var t = start; t < end; t += TimeSpan.FromMinutes(1))
            {
                minutes.Add((t, t + TimeSpan.FromMinutes(1) < end ? t + TimeSpan.FromMinutes(1) : end));
            }
        }

        var medians = new Dictionary<byte, double>();
        if (minutes.Count == 0)
        {
            return medians;
        }

        foreach (var group in sends.GroupBy(s => s.Type))
        {
            var counts = new double[minutes.Count];
            foreach (var (at, _) in group)
            {
                var index = minutes.FindIndex(m => m.Start <= at && at < m.End);
                if (index >= 0)
                {
                    counts[index]++;
                }
            }

            medians[group.Key] = Steps.Percentiles(counts).Median;
        }

        return medians;
    }

    /// <summary>The medians per type over every client of a game, read straight from its trace files with no replay: for the history of earlier games.</summary>
    public static Dictionary<string, double> OfGame(GameTrace game)
    {
        var perType = new Dictionary<byte, List<double>>();
        foreach (var label in game.Labels)
        {
            var sends = new List<(TimeSpan At, byte Type)>();
            var files = new List<(TimeSpan Start, TimeSpan End)>();
            foreach (var file in game.FilesOf(label))
            {
                var start = file.Header.StartUtc - game.StartUtc;
                try
                {
                    using var reader = new TraceReader(file.Path);
                    var last = TimeSpan.Zero;
                    foreach (var record in reader.Records())
                    {
                        last = record.Elapsed;
                        if (record.Kind == TraceRecordKind.D2gsSent && record.Payload.Length > 0 && !SendRateCheck.Bursty(record.Payload[0]) && !SendRateCheck.TownBusiness(record.Payload[0]))
                        {
                            sends.Add((start + record.Elapsed, record.Payload[0]));
                        }
                    }

                    files.Add((start, start + last));
                }
                catch (Exception e) when (e is IOException or TraceFormatException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var (type, median) in Medians(sends, files))
            {
                (perType.TryGetValue(type, out var list) ? list : perType[type] = []).Add(median);
            }
        }

        return perType.ToDictionary(p => Key(p.Key), p => p.Value.Max(), StringComparer.Ordinal);
    }
}
