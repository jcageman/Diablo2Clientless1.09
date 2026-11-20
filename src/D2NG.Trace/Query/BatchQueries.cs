using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace D2NG.Trace.Query;

/// <summary>
/// Which events a batch state query stops at: kind[:name[:outcome]], each part a glob (*). For step
/// and decision events the name is their name; for move events it is matched against the move's kind
/// or its outcome (move:stuck); wait matches its reason; incident:kind stops at incidents.
/// </summary>
public sealed class EventFilter
{
    private readonly string _kind;
    private readonly Regex _name;
    private readonly Regex _outcome;

    public EventFilter(string spec)
    {
        var parts = spec.Split(':');
        _kind = parts[0].ToLowerInvariant();
        _name = parts.Length > 1 ? Glob(parts[1]) : null;
        _outcome = parts.Length > 2 ? Glob(parts[2]) : null;
    }

    public bool WantsIncidents => _kind == "incident";

    private static Regex Glob(string pattern)
        => new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool Matches(SidecarEntry entry)
    {
        if (entry.IsLog || !string.Equals(entry.Kind, _kind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_name != null)
        {
            var names = entry.Kind switch
            {
                "move" => new[] { entry.Str("kind"), entry.Str("outcome") },
                "wait" => [entry.Str("reason")],
                "target" => [entry.Str("code"), entry.Str("reason")],
                _ => [entry.Str("name")],
            };
            if (!names.Any(n => n != null && _name.IsMatch(n)))
            {
                return false;
            }
        }

        if (_outcome != null && entry.Kind == "step" && entry.Str("phase") == "start")
        {
            return false;
        }

        return _outcome == null || (entry.Str("outcome") is { } outcome && _outcome.IsMatch(outcome));
    }

    public bool Matches(Incident incident) => WantsIncidents && (_name == null || _name.IsMatch(incident.Kind));

    /// <summary>A step known only from a TIMING log line (<see cref="Steps"/>), matched as a step event: name, then outcome.</summary>
    public bool Matches(StepSpan step)
        => _kind == "step" && step.Source == "log" && (_name == null || _name.IsMatch(step.Name)) && (_outcome == null || _outcome.IsMatch(step.Outcome));

    /// <summary>
    /// The sidecar events of a game this filter matches - typed events, and for step:..., also the steps
    /// known only from TIMING log lines, as activity --by-step finds them - with whose they are (null for
    /// the party), when, and what to call them.
    /// </summary>
    public IEnumerable<(string Client, TimeSpan At, string Event)> Events(GameQuery query)
    {
        foreach (var entry in query.GameEntries().Where(Matches))
        {
            yield return (entry.Client, query.Offset(entry), Timeline.Describe(entry));
        }

        if (_kind == "step")
        {
            foreach (var step in Steps.Of(query).Where(Matches))
            {
                yield return (step.Client, step.End, $"step {step.Name} {step.Outcome} after {step.Duration.TotalSeconds:0.0}s (TIMING log line)");
            }
        }
    }

    /// <summary>
    /// Whose state an event is shown in: its client; every client of the game for a party event (no
    /// client); each named client for a combined "a+b". Names that are not clients of the game come back
    /// apart, so the caller can say what it skipped.
    /// </summary>
    public static (List<string> Clients, List<string> NotInGame) ClientsOf(string client, IReadOnlyList<string> labels)
    {
        var named = client == null ? [.. labels] : client.Split('+').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        return ([.. named.Where(labels.Contains)], [.. named.Where(c => !labels.Contains(c))]);
    }
}

/// <summary>Games grouped into parties: one group per set of clients, so two bots recording on the same day are reported apart.</summary>
public static class Parties
{
    /// <summary>The groups in the order each first played, games in start order; the key is the clients sorted, joined with ", ".</summary>
    public static List<(string Clients, List<GameTrace> Games)> Of(IEnumerable<GameTrace> games)
        => [.. games.GroupBy(g => string.Join(", ", g.Labels.OrderBy(l => l, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(x => x.StartUtc)).Select(g => (g.Key, g.OrderBy(x => x.StartUtc).ToList()))];
}

/// <summary>Takes a state snapshot of one client at each of a sorted list of instants, in one replay pass: before the first record past each instant.</summary>
public sealed class SnapshotObserver(IReadOnlyList<(TimeSpan At, IEnumerable<(string Key, object Value)> Leading)> stops, IReadOnlyList<(string Name, double? Radius)> parts, bool fullItems = false) : IReplayObserver
{
    private readonly AreaTracker _areas = new();
    private int _next;

    public List<string> Lines { get; } = [];

    public void Before(ReplayContext context, TraceRecord record)
    {
        var offset = context.Offset(record);
        while (_next < stops.Count && stops[_next].At < offset)
        {
            Take(context);
        }
    }

    public void After(ReplayContext context, TraceRecord record) => _areas.Observe(context, record);

    public void Finished(ReplayContext context)
    {
        while (_next < stops.Count && stops[_next].At <= context.Now + TimeSpan.FromSeconds(1))
        {
            Take(context);
        }
    }

    private void Take(ReplayContext context)
    {
        Lines.Add(StateQuery.OneLine(context, parts, stops[_next].Leading, _areas, fullItems));
        _next++;
    }
}

/// <summary>
/// Where units and objects of one code were seen: NPC assignments (0x55/0x56) and object assignments
/// (0x51), with the client's area at the time. NPC and object codes are separate tables and a name can
/// be in both, so each sighting carries its type ("npc" or "object"); a code given as npc:Name or
/// object:Name looks in that table only.
/// </summary>
public sealed class SeenObserver : IReplayObserver
{
    private readonly string _code;
    private readonly string _type;
    private readonly Area? _area;
    private readonly AreaTracker _areas = new();

    public SeenObserver(string code, Area? area)
    {
        (_type, _code) = Split(code);
        _area = area;
    }

    /// <summary>"npc:Name" -> ("npc", "Name"); a bare name -> (null, name).</summary>
    public static (string Type, string Code) Split(string code)
    {
        var colon = code.IndexOf(':');
        if (colon > 0)
        {
            var type = code[..colon].ToLowerInvariant();
            if (type is "npc" or "object")
            {
                return (type, code[(colon + 1)..]);
            }
        }

        return (null, code);
    }

    public List<(Area Area, Point At, string Code, TimeSpan When, string Type)> Sightings { get; } = [];

    public void After(ReplayContext context, TraceRecord record)
    {
        _areas.Observe(context, record);
        var p = record.Payload;
        if (record.Kind != TraceRecordKind.D2gsReceived || p.Length == 0)
        {
            return;
        }

        string name;
        string type;
        Point at;
        if (p[0] is 0x55 or 0x56 && p.Length >= 11)
        {
            name = Names.Npc((NPCCode)PacketDecoder.U16(p, 5));
            type = "npc";
            at = new Point(PacketDecoder.U16(p, 7), PacketDecoder.U16(p, 9));
        }
        else if (p[0] == 0x51 && p.Length >= 12)
        {
            name = ((EntityCode)PacketDecoder.U16(p, 6)).ToString();
            type = "object";
            at = new Point(PacketDecoder.U16(p, 8), PacketDecoder.U16(p, 10));
        }
        else
        {
            return;
        }

        if (!string.Equals(name, _code, StringComparison.OrdinalIgnoreCase) || (_type != null && _type != type))
        {
            return;
        }

        var area = _area;
        var here = context.Game.Me == null ? Area.None : _areas.Current(context) ?? Area.None;
        if (area == null || area == here)
        {
            Sightings.Add((here, at, name, context.Now, type));
        }
    }
}

/// <summary>
/// A character's lifetime experience as the packets give it, whatever rules the replay runs under: 0x1C carries the
/// total - on joining and every few minutes after - and 0x1A and 0x1B add a gain to it. Game under rules before 5 adds all
/// three, so its running total grew by the whole lifetime total at every 0x1C after the first, and a batch whose last game
/// had more than the joining one read billions an hour.
/// </summary>
public sealed class ExperienceTotal
{
    public long? Total { get; private set; }

    /// <summary>True when the record is an experience packet, which <see cref="Total"/> now includes.</summary>
    public bool Apply(ReplayContext context, TraceRecord record)
    {
        var payload = record.Payload;
        if (record.Kind != TraceRecordKind.D2gsReceived || payload.Length == 0)
        {
            return false;
        }

        long? value = payload[0] switch
        {
            0x1A when payload.Length >= 2 => payload[1],
            0x1B when payload.Length >= 3 => BitConverter.ToUInt16(payload, 1),
            0x1C when payload.Length >= 5 => BitConverter.ToUInt32(payload, 1),
            _ => null,
        };
        if (value is not { } amount)
        {
            return false;
        }

        // A gain before any total: Game's figure, which already holds it.
        Total = payload[0] == 0x1C ? amount : Total is { } total ? total + amount : context.Game.Me?.Experience ?? amount;
        return true;
    }

    public void Reset() => Total = null;
}

/// <summary>
/// A character's experience over one trace file, from the first experience packet after joining (<see cref="ExperienceTotal"/>);
/// a death is the server assigning the character's own corpse right after it took damage (<see cref="DeathCheck"/>).
/// </summary>
public sealed class ExperienceObserver : IReplayObserver
{
    private readonly DeathCheck _deaths = new();

    public sealed record FileExperience(string Character, string Game, DateTime StartUtc, DateTime EndUtc, long? First, long? Last, int Deaths);

    public List<FileExperience> Files { get; } = [];

    private long? _first;
    private readonly ExperienceTotal _experience = new();

    public void After(ReplayContext context, TraceRecord record)
    {
        _deaths.After(context, record);
        if (_experience.Apply(context, record))
        {
            _first ??= _experience.Total;
        }
    }

    public void Finished(ReplayContext context)
    {
        _deaths.Finished(context);
        var start = context.File.Header.StartUtc;
        Files.Add(new FileExperience(context.Client, TraceHistory.Key(context.GameTrace), start, context.GameTrace.StartUtc + context.Now,
            _first, _experience.Total, _deaths.Found.Count(f => f.Kind == "death" && f.Offset >= start - context.GameTrace.StartUtc)));
        _first = null;
        _experience.Reset();
    }
}

/// <summary>Experience per minute per character over many games, deaths included, with long gaps between games left out of the clock.</summary>
public static class ExperienceKpi
{
    public static readonly TimeSpan GapLimit = TimeSpan.FromMinutes(5);

    public sealed record Row(string Character, int Games, int Files, long Gained, TimeSpan Clock, TimeSpan Excluded, int ExcludedGaps, int Deaths, long? Start, long? End, long Lost)
    {
        public double PerMinute => Clock.TotalMinutes > 0 ? Gained / Clock.TotalMinutes : 0;

        /// <summary>Drops between files after a file with no death in it: a death's cost, or the running total over-counting; not counted as deaths.</summary>
        public int UnexplainedFalls { get; init; }
    }

    /// <summary>
    /// Net gain = the last total seen minus the first, so experience lost on death or between games counts
    /// against it; Lost is the drop between one file's end and the next file's first total. The clock runs
    /// from each file's start to its last record, plus the gaps between files up to <see cref="GapLimit"/>.
    /// </summary>
    public static List<Row> Compute(IEnumerable<ExperienceObserver.FileExperience> files)
    {
        var rows = new List<Row>();
        foreach (var character in files.GroupBy(f => f.Character))
        {
            var ordered = character.OrderBy(f => f.StartUtc).ToList();
            var clock = TimeSpan.Zero;
            var excluded = TimeSpan.Zero;
            var gaps = 0;
            long lost = 0;
            DateTime? lastEnd = null;
            long? previousLast = null;
            var previousDied = false;
            var unseenDeaths = 0;
            foreach (var file in ordered)
            {
                var span = file.EndUtc - file.StartUtc;
                if (lastEnd is { } end)
                {
                    var gap = file.StartUtc - end;
                    if (gap > GapLimit)
                    {
                        excluded += gap;
                        gaps++;
                    }
                    else if (gap > TimeSpan.Zero)
                    {
                        clock += gap;
                    }
                    else
                    {
                        span = file.EndUtc - end;
                    }
                }

                if (span > TimeSpan.Zero)
                {
                    clock += span;
                }

                lastEnd = lastEnd == null || file.EndUtc > lastEnd ? file.EndUtc : lastEnd;
                if (previousLast is { } before && file.First is { } after && after < before)
                {
                    lost += before - after;
                    if (!previousDied)
                    {
                        unseenDeaths++;
                    }
                }

                previousLast = file.Last ?? previousLast;
                previousDied = file.Deaths > 0;
            }

            var first = ordered.Select(f => f.First).FirstOrDefault(v => v != null);
            var last = ordered.Select(f => f.Last).LastOrDefault(v => v != null);
            rows.Add(new Row(character.Key, ordered.Select(f => f.Game).Distinct().Count(), ordered.Count,
                first != null && last != null ? last.Value - first.Value : 0, clock, excluded, gaps, ordered.Sum(f => f.Deaths), first, last, lost) { UnexplainedFalls = unseenDeaths });
        }

        return [.. rows.OrderBy(r => r.Character, StringComparer.Ordinal)];
    }
}
