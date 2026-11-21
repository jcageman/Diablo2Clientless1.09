using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace D2NG.Trace.Query;

/// <summary>A step of a game. Client null is a step of the whole party; Parent is the smallest step around it.</summary>
public sealed record StepSpan(string Client, string Name, int Attempt, TimeSpan Start, TimeSpan End, string Outcome, string Source)
{
    public TimeSpan Duration => End - Start;

    public StepSpan Parent { get; set; }

    public bool Leaf { get; set; } = true;

    /// <summary>The start was not reported and is taken from the step before it.</summary>
    public bool StartInferred { get; init; }

    /// <summary>The start was not reported and no step came before it: the step is shown at its end, with no length.</summary>
    public bool StartUnknown { get; init; }

    public bool Covers(string client) => Client == null || Client == client;
}

/// <summary>
/// The steps of a game, from typed step events and, where a bot reports none, from its "TIMING ..." log
/// lines: the template up to {Outcome} names the step with its properties filled in, Seconds before the
/// line is its start, and a Client/Member/Character/Rushee property naming a client of the game makes it
/// that client's step (otherwise it is the party's).
/// </summary>
public static class Steps
{
    private static readonly string[] ClientProperties = ["Client", "Member", "Character", "Rushee"];
    private static readonly string[] NameEnds = [" {Outcome}", " finished", " in {Seconds"];

    public static List<StepSpan> Of(GameQuery query)
    {
        var steps = new List<StepSpan>();
        var starts = new Dictionary<(string, string, int), TimeSpan>();
        var lastEnd = new Dictionary<string, TimeSpan>();
        foreach (var entry in query.GameEntries())
        {
            var at = query.Offset(entry);
            if (entry.Kind == "step")
            {
                var name = entry.Str("name") ?? "?";
                var attempt = (int)(entry.Num("attempt") ?? 1);
                var key = (entry.Client, name, attempt);
                if (entry.Str("phase") == "start")
                {
                    starts[key] = at;
                    continue;
                }

                var inferred = false;
                var unknown = false;
                TimeSpan start;
                if (starts.Remove(key, out var started))
                {
                    start = started;
                }
                else if (entry.Num("ms") is { } ms)
                {
                    start = at - TimeSpan.FromMilliseconds(ms);
                }
                else
                {
                    var known = lastEnd.TryGetValue(entry.Client ?? "", out var previous);
                    start = known ? previous : at;
                    var open = starts.Where(s => s.Key.Item1 == entry.Client && s.Value < at).Select(s => s.Value).DefaultIfEmpty(TimeSpan.MinValue).Max();
                    if (open > start || (!known && open != TimeSpan.MinValue))
                    {
                        start = open;
                        known = true;
                    }

                    inferred = true;
                    unknown = !known;
                }

                lastEnd[entry.Client ?? ""] = at;
                steps.Add(new StepSpan(entry.Client, name, attempt, start, at, entry.Str("outcome") ?? "?", "typed") { StartInferred = inferred, StartUnknown = unknown });
            }
            else if (entry.IsLog && FromTiming(entry, query.Game.Labels) is { } timing)
            {
                var start = at - timing.Seconds;
                steps.Add(new StepSpan(timing.Client, timing.Name, 1, start, at, timing.Outcome, "log"));
            }
        }

        var length = query.Game.EndUtc - query.Game.StartUtc;
        foreach (var ((client, name, attempt), start) in starts)
        {
            steps.Add(new StepSpan(client, name, attempt, start, length, "unfinished", "typed"));
        }

        steps.RemoveAll(s => !Overlaps(s, length));
        Nest(steps);
        return [.. steps.OrderBy(s => s.Start)];
    }

    /// <summary>Whether a step lies at least partly within the game, [0, length]; a step logged by the process after it finished is another game's.</summary>
    public static bool Overlaps(StepSpan step, TimeSpan length)
        => step.Duration > TimeSpan.Zero ? step.End > TimeSpan.Zero && step.Start < length : step.Start >= TimeSpan.Zero && step.Start <= length;

    /// <summary>
    /// A step is a leaf when no other step of the same client (or of the party) lies inside it; its parent
    /// is the smallest step of its client or of the party around it. Two steps with the same start and end
    /// are not nested. A step whose start was inferred does not make the step around it a non-leaf: its
    /// time is a guess, and the step around it is the one that was measured. One sweep per client, over
    /// its steps and the party's, in start order.
    /// </summary>
    public static void Nest(List<StepSpan> steps)
    {
        foreach (var step in steps)
        {
            step.Leaf = true;
            step.Parent = null;
        }

        foreach (var key in steps.Select(s => s.Client).Distinct().Append(null).Distinct())
        {
            var list = steps.Where(s => s.Client == key || s.Client == null).OrderBy(s => s.Start).ThenByDescending(s => s.End).ToList();
            var open = new List<StepSpan>();
            foreach (var step in list)
            {
                while (open.Count > 0 && !(Inside(step, open[^1]) && !Same(step, open[^1])))
                {
                    open.RemoveAt(open.Count - 1);
                }

                if (open.Count > 0)
                {
                    if (!step.StartInferred)
                    {
                        open[^1].Leaf = false;
                    }

                    if (step.Client == key)
                    {
                        step.Parent = open[^1];
                    }
                }

                open.Add(step);
            }
        }
    }

    private static bool Same(StepSpan a, StepSpan b) => a.Start == b.Start && a.End == b.End;

    private static bool Inside(StepSpan inner, StepSpan outer) => inner.Start >= outer.Start && inner.End <= outer.End;

    public sealed record Timing(string Name, string Outcome, TimeSpan Seconds, string Client);

    public static Timing FromTiming(SidecarEntry entry, IReadOnlyList<string> labels)
    {
        var template = entry.Template ?? entry.Message;
        if (template == null || !template.StartsWith("TIMING ", StringComparison.Ordinal)
            || !entry.Json.TryGetProperty("p", out var properties) || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("Seconds", out var seconds) || seconds.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var body = template["TIMING ".Length..];
        var cut = NameEnds.Select(m => body.IndexOf(m, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(body.Length).Min();
        var name = Fill(body[..cut], properties);
        if (name.StartsWith("step ", StringComparison.Ordinal))
        {
            name = name["step ".Length..];
        }

        string client = null;
        foreach (var property in ClientProperties)
        {
            if (properties.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && labels.Contains(value.GetString()))
            {
                client = value.GetString();
                break;
            }
        }

        var outcome = properties.TryGetProperty("Outcome", out var said) ? SidecarEntry.Render(said) : "ok";
        return new Timing(name, outcome.ToLowerInvariant(), TimeSpan.FromSeconds(seconds.GetDouble()), client);
    }

    /// <summary>A message template with its {Name} and {Name:format} holes filled from the properties.</summary>
    public static string Fill(string template, JsonElement properties)
    {
        var text = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                text.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i);
            if (close < 0)
            {
                text.Append(template[i..]);
                break;
            }

            var hole = template[(i + 1)..close];
            var colon = hole.IndexOf(':');
            var key = colon < 0 ? hole : hole[..colon];
            text.Append(properties.TryGetProperty(key, out var value) ? SidecarEntry.Render(value) : "{" + hole + "}");
            i = close;
        }

        return text.ToString();
    }

    /// <summary>A sorted sample's median and 90th percentile, nearest rank.</summary>
    public static (double Median, double P90) Percentiles(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (0, 0);
        }

        var sorted = values.OrderBy(v => v).ToList();
        double Rank(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        return (Rank(0.5), Rank(0.9));
    }
}
