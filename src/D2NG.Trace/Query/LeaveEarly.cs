using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>One character's experience through one game, as offsets from the game's start.</summary>
public sealed record GameExperience(string Game, string Character, DateTime StartUtc, TimeSpan Length, IReadOnlyList<(TimeSpan At, long Gained)> Points)
{
    public long Gained => Points.Count == 0 ? 0 : Points[^1].Gained;

    public TimeSpan? FirstGain => Points.Where(p => p.Gained > 0).Select(p => (TimeSpan?)p.At).FirstOrDefault();

    public long GainedBy(TimeSpan at)
    {
        long gained = 0;
        foreach (var (when, total) in Points)
        {
            if (when > at)
            {
                break;
            }

            gained = total;
        }

        return gained;
    }
}

/// <summary>
/// What leaving each game earlier would have earned: every game cut a fixed time after its first experience gain,
/// the experience after the cut lost, leaving taking <see cref="LeaveTakes"/>, and the time between games as it was.
/// A run that ends by choice - a level cleared to the last pack - pays off only while it earns faster than a new game
/// would with its setup counted, and the marginal rate per stretch shows where that stops.
/// </summary>
public static class LeaveEarly
{
    public static readonly TimeSpan LeaveTakes = TimeSpan.FromSeconds(1);

    public sealed record Bin(TimeSpan From, double PerMinute, int Games);

    public sealed record Cut(TimeSpan After, double PerMinute, double Change, int GamesCut);

    public sealed record Result(int Games, TimeSpan Between, TimeSpan FirstGain, TimeSpan Length, double FullPerMinute, IReadOnlyList<Bin> Marginal, IReadOnlyList<Cut> Cuts)
    {
        public Cut Best => Cuts.Count == 0 ? null : Cuts.MaxBy(c => c.PerMinute);
    }

    /// <summary>Games without a gain are left out; the time between games is the median of the gaps under <see cref="ExperienceKpi.GapLimit"/>.</summary>
    public static Result Compute(IReadOnlyList<GameExperience> all, TimeSpan step, TimeSpan bin, int minGames = 5)
    {
        var games = all.Where(g => g.FirstGain != null && g.Gained > 0).OrderBy(g => g.StartUtc).ToList();
        if (games.Count == 0)
        {
            return new Result(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, [], []);
        }

        var gaps = all.OrderBy(g => g.StartUtc).Zip(all.OrderBy(g => g.StartUtc).Skip(1), (a, b) => b.StartUtc - (a.StartUtc + a.Length))
            .Where(g => g >= TimeSpan.Zero && g <= ExperienceKpi.GapLimit).OrderBy(g => g).ToList();
        var between = gaps.Count == 0 ? TimeSpan.Zero : gaps[gaps.Count / 2];
        var full = PerMinute(games.Sum(g => g.Gained), games.Sum(g => (g.Length + between).TotalSeconds));

        var marginal = new List<Bin>();
        for (var from = TimeSpan.Zero; ; from += bin)
        {
            var live = games.Where(g => g.Length > g.FirstGain.Value + from).ToList();
            if (live.Count < minGames)
            {
                break;
            }

            var gained = live.Sum(g => g.GainedBy(g.FirstGain.Value + from + bin) - g.GainedBy(g.FirstGain.Value + from));
            var seconds = live.Sum(g => Math.Min(bin.TotalSeconds, (g.Length - g.FirstGain.Value - from).TotalSeconds));
            marginal.Add(new Bin(from, PerMinute(gained, seconds), live.Count));
        }

        var cuts = new List<Cut>();
        var longest = games.Max(g => g.Length - g.FirstGain.Value);
        for (var after = step; after < longest; after += step)
        {
            var gained = games.Sum(g => g.GainedBy(g.FirstGain.Value + after));
            var seconds = games.Sum(g => (Shorter(g.Length, g.FirstGain.Value + after + LeaveTakes) + between).TotalSeconds);
            var perMinute = PerMinute(gained, seconds);
            cuts.Add(new Cut(after, perMinute, full > 0 ? perMinute / full - 1 : 0, games.Count(g => g.Length > g.FirstGain.Value + after + LeaveTakes)));
        }

        var firstGains = games.Select(g => g.FirstGain.Value).OrderBy(t => t).ToList();
        var lengths = games.Select(g => g.Length).OrderBy(t => t).ToList();
        return new Result(games.Count, between, firstGains[firstGains.Count / 2], lengths[lengths.Count / 2], full, marginal, cuts);
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static double PerMinute(long gained, double seconds) => seconds > 0 ? gained / seconds * 60 : 0;
}

/// <summary>A character's lifetime experience after every experience packet (0x1A-0x1C), per trace file, at offsets from the game's start.</summary>
public sealed class ExperienceCurveObserver : IReplayObserver
{
    public sealed record FileCurve(string Character, string Game, DateTime GameStartUtc, TimeSpan End, IReadOnlyList<(TimeSpan At, long Total)> Totals);

    public List<FileCurve> Files { get; } = [];

    private List<(TimeSpan At, long Total)> _totals = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        if (record.Kind == TraceRecordKind.D2gsReceived && record.Payload.Length > 0 && record.Payload[0] is 0x1A or 0x1B or 0x1C && context.Game.Me != null)
        {
            _totals.Add((context.Offset(record), context.Game.Me.Experience));
        }
    }

    public void Finished(ReplayContext context)
    {
        Files.Add(new FileCurve(context.Client, TraceHistory.Key(context.GameTrace), context.GameTrace.StartUtc, context.Now, _totals));
        _totals = [];
    }

    /// <summary>One curve per game and character: the files of a rejoin joined, the gain counted from the game's first total.</summary>
    public static List<GameExperience> PerGame(IEnumerable<FileCurve> files)
        => files.GroupBy(f => (f.Game, f.Character))
            .Select(group =>
            {
                var totals = group.SelectMany(f => f.Totals).OrderBy(t => t.At).ToList();
                var first = totals.Count == 0 ? 0 : totals[0].Total;
                return new GameExperience(group.Key.Game, group.Key.Character, group.First().GameStartUtc, group.Max(f => f.End),
                    totals.Select(t => (t.At, t.Total - first)).ToList());
            })
            .ToList();
}
