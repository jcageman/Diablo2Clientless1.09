using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace D2NG.Trace.Query;

public sealed class TimelineOptions
{
    public string Client { get; init; }

    public TimeSpan From { get; init; } = TimeSpan.MinValue;

    public TimeSpan To { get; init; } = TimeSpan.MaxValue;

    public bool Debug { get; init; }

    public double Radius { get; init; } = 25;

    /// <summary>Only the client's own movement, area changes and stand-still stretches.</summary>
    public bool Moves { get; init; }

    /// <summary>Only the stand-still stretches.</summary>
    public bool Idle { get; init; }

    /// <summary>The shortest stand-still stretch shown with Moves or Idle.</summary>
    public TimeSpan Still { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// A game in time order, one line per event: what the replay shows (<see cref="GameEvents"/>), the
/// typed events the bot wrote and its log lines (Information and above unless Debug). A log line
/// naming no client of the game is shown for every client, its client given as "?".
/// </summary>
public static class Timeline
{
    public static List<TimelineEntry> Build(GameQuery query, TimelineOptions options)
    {
        if (options.Moves || options.Idle)
        {
            return Movement(query, options);
        }

        var clients = options.Client == null ? query.Game.Labels.ToList() : [options.Client];
        var entries = new List<TimelineEntry>();
        var server = new Dictionary<string, ServerPositions>();
        var moveEnd = options.To == TimeSpan.MaxValue ? options.To : options.To + ServerPositions.Answer;
        var phase = Profile.Time("timeline: replay all clients (wall)");
        foreach (var (client, (events, positions)) in query.ReplayAll(clients, moveEnd, () => (new GameEvents(options.Radius, options.From), new ServerPositions()), e => [e.Item1, e.Item2]))
        {
            entries.AddRange(events.Entries.Where(e => e.Offset <= options.To));
            server[client] = positions;
        }

        phase.Dispose();
        using var rest = Profile.Time("timeline: sidecar lines, order and merge");

        foreach (var entry in query.GameEntries())
        {
            var offset = query.Offset(entry);
            if (offset < options.From || offset > options.To)
            {
                continue;
            }

            if (entry.IsLog)
            {
                if (!options.Debug && entry.Level is "Debug" or "Verbose")
                {
                    continue;
                }

                var owners = query.Attribution.ClientsOf(entry);
                if (options.Client != null && owners.Count > 0 && !owners.Contains(options.Client))
                {
                    continue;
                }

                entries.Add(new TimelineEntry(offset, owners.Count == 0 ? "?" : string.Join("+", owners), "log", $"{Level(entry.Level)} {OneLine(entry.Message)}"));
                continue;
            }

            if (options.Client != null && entry.Client != options.Client && !GameQuery.IsStopRequest(entry))
            {
                continue;
            }

            var text = Describe(entry);
            if (entry.Client != null && server.TryGetValue(entry.Client, out var positions) && positions.Unconfirmed(entry, offset) is { } unconfirmed)
            {
                text += $" - NOT CONFIRMED: {unconfirmed}";
            }

            entries.Add(new TimelineEntry(offset, entry.Client ?? "?", entry.Kind, text));
        }

        var ordered = entries.Where(e => e.Last >= options.From && e.Offset <= options.To).Select(e => Since(e, options.From)).OrderBy(e => e.Offset).ToList();
        return options.Client == null ? MergeShared(ordered, query.Game.Labels) : ordered;
    }

    /// <summary>A line that spans time and began before the window says so.</summary>
    private static TimelineEntry Since(TimelineEntry entry, TimeSpan from)
        => entry.Offset < from && from != TimeSpan.MinValue ? entry with { Text = $"{entry.Text} (since {TraceTime.Format(entry.Offset)}, before --from)" } : entry;

    /// <summary>What the lines cannot show: a --debug asked of a game whose sidecar holds no Debug line.</summary>
    public static List<string> Notes(GameQuery query, TimelineOptions options)
    {
        var notes = new List<string>();
        if (options.Debug && !options.Moves && !options.Idle && !query.GameEntries().Any(e => e.IsLog && e.Level is "Debug" or "Verbose"))
        {
            notes.Add("no Debug lines were recorded for this game (the bot ran at Information); --debug adds nothing here");
        }

        return notes;
    }

    /// <summary>
    /// Own movement: area changes and the server moving the client (from the replay), one line per walking
    /// or teleporting stretch with where it went, and standing stretches of at least the Still length.
    /// </summary>
    private static List<TimelineEntry> Movement(GameQuery query, TimelineOptions options)
    {
        var clients = options.Client == null ? query.Game.Labels.ToList() : [options.Client];
        var end = query.Game.EndUtc - query.Game.StartUtc;
        var from = options.From < TimeSpan.Zero ? TimeSpan.Zero : options.From;
        var to = options.To > end ? end : options.To;
        var entries = new List<TimelineEntry>();
        foreach (var (client, (events, activity)) in query.ReplayAll(clients, options.To, () => (new GameEvents(options.Radius, options.From), new ActivityObserver()), o => [o.Item1, o.Item2]))
        {
            if (!options.Idle)
            {
                entries.AddRange(events.Entries.Where(e => e.Text.StartsWith("area ", StringComparison.Ordinal) || e.Text.StartsWith("in ", StringComparison.Ordinal)
                    || e.Text.Contains("reassigned by the server", StringComparison.Ordinal) || e.Text.Contains("walk verify", StringComparison.Ordinal)));
            }

            foreach (var stretch in activity.Stretches(from, to, ActivityObserver.Waits(query, client, end)))
            {
                switch (stretch.State)
                {
                    case ActivityState.Walking or ActivityState.Teleporting when !options.Idle:
                        var left = activity.PositionAt(stretch.Start - ActivityObserver.MoveWindow) ?? stretch.At;
                        var arrived = activity.PositionAt(stretch.End);
                        entries.Add(new TimelineEntry(stretch.Start, client, "activity",
                            $"{(stretch.State == ActivityState.Walking ? "walked" : "teleported")} {stretch.Duration.TotalSeconds:0.0}s {Names.Point(left)} -> {Names.Point(arrived)}{(left != null && arrived != null ? $", {left.Distance(arrived):0} subtiles" : "")} in {stretch.Area ?? "?"}", stretch.End));
                        break;
                    case ActivityState.StandingIdle or ActivityState.StandingWaiting when stretch.Duration >= options.Still:
                        entries.Add(new TimelineEntry(stretch.Start, client, "activity",
                            $"{ActivityObserver.Name(stretch.State)} {stretch.Duration.TotalSeconds:0.0}s at {Names.Point(stretch.At)} in {stretch.Area ?? "?"}", stretch.End));
                        break;
                    case ActivityState.Town when options.Idle:
                        foreach (var (start, stop, at) in StillWithin(activity, stretch.Start, stretch.End).Where(s => s.Stop - s.Start >= options.Still))
                        {
                            entries.Add(new TimelineEntry(start, client, "activity",
                                $"town idle {(stop - start).TotalSeconds:0.0}s at {Names.Point(at)} in {activity.AreaName(start) ?? "?"} (in town, standing, no NPC, trade, item or fight)", stop));
                        }

                        break;
                }
            }
        }

        return [.. entries.Where(e => e.Last >= options.From && e.Offset <= options.To).OrderBy(e => e.Offset)];
    }

    /// <summary>The parts of a town stretch where the client did not move more than the walking distance in a second.</summary>
    public static IEnumerable<(TimeSpan Start, TimeSpan Stop, Core.D2GS.Point At)> StillWithin(ActivityObserver activity, TimeSpan from, TimeSpan to)
    {
        TimeSpan? start = null;
        Core.D2GS.Point at = null;
        for (var t = from; t < to; t += ActivityObserver.Slice)
        {
            var here = activity.PositionAt(t);
            var before = activity.PositionAt(t - ActivityObserver.MoveWindow) ?? here;
            var still = here != null && before != null && here.Distance(before) <= ActivityObserver.MoveDistance
                && activity.AreaAt(t) is { } area && ActivityObserver.IsTown(area);
            if (still && start == null)
            {
                (start, at) = (t, here);
            }
            else if (!still && start is { } began)
            {
                yield return (began, t, at);
                start = null;
            }
        }

        if (start is { } open)
        {
            yield return (open, to, at);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex Distance = new(@", \d+ away$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly TimeSpan SharedWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// What several clients saw alike - a unit dying, a portal, a player joining - is one line, its
    /// clients joined with + ("all" when every client of the game saw it).
    /// </summary>
    private static List<TimelineEntry> MergeShared(List<TimelineEntry> entries, IReadOnlyList<string> labels)
    {
        var merged = new List<TimelineEntry>();
        var open = new Dictionary<string, (int Index, List<string> Clients)>();
        foreach (var entry in entries)
        {
            if (entry.Source != "game" || entry.Text.StartsWith('x') || entry.Text.StartsWith("cast ", StringComparison.Ordinal))
            {
                merged.Add(entry);
                continue;
            }

            var key = Distance.Replace(entry.Text, "");
            if (open.TryGetValue(key, out var shared) && entry.Offset - merged[shared.Index].Offset <= SharedWindow && !shared.Clients.Contains(entry.Client))
            {
                shared.Clients.Add(entry.Client);
                var clients = shared.Clients.Count == labels.Count && labels.Count > 1 ? "all" : string.Join("+", shared.Clients);
                merged[shared.Index] = merged[shared.Index] with { Client = clients, Text = key };
                continue;
            }

            open[key] = (merged.Count, [entry.Client]);
            merged.Add(entry);
        }

        return merged;
    }

    public static string Format(TimelineEntry entry) => $"{TraceTime.Format(entry.Offset)} {entry.Client,-12} {entry.Text}";

    /// <summary>A typed event as one line.</summary>
    public static string Describe(SidecarEntry entry)
    {
        switch (entry.Kind)
        {
            case "step":
                var outcome = entry.Str("outcome");
                var attempt = entry.Num("attempt") is > 1 and var number ? $" attempt {number}" : "";
                var reason = entry.Str("reason") is { } why ? $" ({why})" : "";
                var took = entry.Num("ms") is { } ms ? $" in {ms / 1000.0:0.0}s" : "";
                return entry.Str("phase") == "start"
                    ? $"step {entry.Str("name")} start{attempt}"
                    : $"step {entry.Str("name")}{attempt} {(outcome == "ok" ? "ok" : outcome?.ToUpperInvariant())}{reason}{took}";
            case "target":
                var rejected = entry.Json.TryGetProperty("rejected", out var list) && list.ValueKind == JsonValueKind.Array
                    ? "; passed over " + string.Join(", ", list.EnumerateArray().Take(6).Select(r => $"{Hex(r, "id")} {Str(r, "code")} ({Str(r, "filter")})"))
                        + (list.GetArrayLength() > 6 ? $" and {list.GetArrayLength() - 6} more" : "")
                    : "";
                return entry.Num("id") is { } id
                    ? $"target {entry.Str("code")} 0x{id:X} ({entry.Str("reason")}){rejected}"
                    : $"target none ({entry.Str("reason")}){rejected}";
            case "wait":
                return $"wait {entry.Str("state")}: {entry.Str("reason")}";
            case "move":
                var destination = ServerPositions.To(entry) is { X: 0, Y: 0 } ? "(0, 0) (destination not set: the bot asked for (0, 0))" : Point(entry, "to");
                return $"move {entry.Str("kind")} {Point(entry, "from")} -> {destination}{(entry.Str("area") is { } area ? $" in {area}" : "")}: {entry.Str("outcome")}{(entry.Str("fallback") is { } fallback ? $", fallback {fallback}" : "")}";
            case "decision" when GameQuery.IsStopRequest(entry):
                return $"STOP REQUESTED ({entry.Fields()}): walks, paths and steps from here on may fail because the bot is stopping";
            case "decision":
                return $"decision {entry.Str("name")}: {entry.Fields()}";
            case "gap":
                return $"sidecar gap: {entry.Num("n")} events dropped";
            case "settings":
                return $"bot settings: {entry.Fields()}";
            default:
                return $"{entry.Kind} {entry.Json.GetRawText()}";
        }
    }

    private static string Point(SidecarEntry entry, string key)
        => entry.Json.TryGetProperty(key, out var value) ? SidecarEntry.Render(value) : "-";

    private static string Str(JsonElement element, string key)
        => element.TryGetProperty(key, out var value) ? SidecarEntry.Render(value) : "";

    private static string Hex(JsonElement element, string key)
        => element.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) ? $"0x{number:X}" : "?";

    private static string Level(string level) => level switch
    {
        "Information" => "INF",
        "Warning" => "WRN",
        "Error" => "ERR",
        "Fatal" => "FTL",
        "Debug" => "DBG",
        "Verbose" => "VRB",
        _ => level,
    };

    private static string OneLine(string text)
    {
        if (text == null)
        {
            return "";
        }

        var end = text.IndexOfAny(['\r', '\n']);
        var line = end < 0 ? text : text[..end] + " [..]";
        return line.Length > 300 ? line[..300] + ".." : line;
    }
}
