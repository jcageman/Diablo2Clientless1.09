using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>Finding the game a log line speaks of: its name and a wall-clock time.</summary>
public static class GameFinder
{
    /// <summary>The log's create and join lines come this long before a game's first trace file starts.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan After = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far a clock time in <paramref name="zone"/> lies outside a game's window - <see cref="Before"/> its first file
    /// to <see cref="After"/> its last record; zero inside. A time without a date is tried on the day before, of and after
    /// the game's start. Throws on a time it cannot read.
    /// </summary>
    public static TimeSpan DistanceTo(GameTrace game, string at, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTimeFromUtc(game.StartUtc, zone) - Before;
        var end = TimeZoneInfo.ConvertTimeFromUtc(game.EndUtc, zone) + After;
        List<DateTime> candidates;
        if (TryFull(at, out var full))
        {
            candidates = [full];
        }
        else if (TimeSpan.TryParseExact(at, [@"hh\:mm\:ss\.fff", @"hh\:mm\:ss", @"hh\:mm", @"h\:mm\:ss", @"h\:mm"], CultureInfo.InvariantCulture, out var clock))
        {
            candidates = [.. new[] { -1, 0, 1 }.Select(d => start.Date.AddDays(d) + clock)];
        }
        else
        {
            throw new ArgumentException($"--at {at}: give a local clock time hh:mm[:ss[.fff]] or \"yyyy-MM-dd hh:mm:ss\"");
        }

        return candidates.Select(c => c < start ? start - c : c > end ? c - end : TimeSpan.Zero).Min();
    }

    private static bool TryFull(string at, out DateTime full)
        => DateTime.TryParseExact(at, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out full);

    /// <summary>Whether the time names its date, so it can be matched over more than one day.</summary>
    public static bool HasDate(string at) => TryFull(at, out _);

    /// <summary>Each client of the game, with its trace files when they are not just "&lt;client&gt;.d2trace" (a rejoin's part 2, a renamed file).</summary>
    public static List<string> Clients(GameTrace game)
        => [.. game.Labels.Select(label =>
        {
            var files = game.FilesOf(label).Select(f => Path.GetFileName(f.Path)).ToList();
            return files.Count == 1 && Path.GetFileNameWithoutExtension(files[0]) == label ? label : $"{label} ({string.Join(", ", files)})";
        })];
}
