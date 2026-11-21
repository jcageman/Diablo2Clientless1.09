using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// The log lines of a process that belong to none of its games: a game takes the lines of its window
/// (<see cref="GameQuery.GameEntries"/>), and what falls between two windows - a mule detour, a restart, the
/// time between leaving one game and creating the next - goes to a bucket between those two games, so
/// every line of a process is in exactly one game or one bucket and no warning or error is lost.
/// </summary>
public static class BetweenGames
{
    public sealed class Bucket
    {
        public string Process { get; init; }

        /// <summary>The process's game before the lines, or null when they come before its first.</summary>
        public GameTrace Before { get; init; }

        /// <summary>The process's game after the lines, or null when they come after its last.</summary>
        public GameTrace After { get; init; }

        public List<SidecarEntry> Lines { get; } = [];

        public int Warnings => Lines.Count(l => l.Level == "Warning");

        public int Errors => Lines.Count(l => l.Level is "Error" or "Fatal");

        public IEnumerable<SidecarEntry> Notable => Lines.Where(l => l.Level is "Warning" or "Error" or "Fatal");
    }

    /// <summary>The buckets of the processes that played any of these games, over the date folders the games are in.</summary>
    public static List<Bucket> Of(IReadOnlyList<GameTrace> games, TraceSources sources)
    {
        var dateFolders = games.Select(g => g.DateFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = dateFolders.SelectMany(sources.GamesIn).GroupBy(GameQuery.Identity).Select(g => g.First()).OrderBy(g => g.StartUtc).ToList();
        var queries = all.Select(sources.QueryOf).ToList();
        var processes = games.Select(sources.QueryOf).SelectMany(q => q.Processes).ToHashSet(StringComparer.Ordinal);
        var assigned = new HashSet<SidecarEntry>(ReferenceEqualityComparer.Instance);
        foreach (var query in queries)
        {
            assigned.UnionWith(query.GameEntries());
        }

        var files = dateFolders.SelectMany(TraceSources.SidecarFilesIn).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lines = TraceSources.Sidecar(files, DateTime.MinValue, DateTime.MaxValue)
            .Where(e => (e.IsLog || e.Kind is "gap" or "trace-failed") && e.Process != null && processes.Contains(e.Process) && !assigned.Contains(e));
        var buckets = new Dictionary<(string, string, string), Bucket>();
        foreach (var line in lines)
        {
            var played = queries.Where(q => q.Processes.Contains(line.Process)).Select(q => q.Game).ToList();
            var before = played.LastOrDefault(g => g.StartUtc <= line.Utc);
            var after = played.FirstOrDefault(g => g.StartUtc > line.Utc);
            var key = (line.Process, before == null ? null : GameQuery.Identity(before), after == null ? null : GameQuery.Identity(after));
            if (!buckets.TryGetValue(key, out var bucket))
            {
                buckets[key] = bucket = new Bucket { Process = line.Process, Before = before, After = after };
            }

            bucket.Lines.Add(line);
        }

        return [.. buckets.Values.OrderBy(b => b.Lines[0].Utc)];
    }
}
