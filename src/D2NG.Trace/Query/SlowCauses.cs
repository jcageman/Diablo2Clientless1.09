using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>One reason a game was slow or a step failed, with the seconds it explains and what shows it.</summary>
public sealed record SlowCause(string Label, double Seconds, string Detail);

/// <summary>
/// Why a client's game ran long or a step failed, from what the incidents, the phases and the item ledger already know.
/// Each cause explains seconds of the window, never more than the window:
/// <list type="bullet">
/// <item>stuck at a wall / skill moves went nowhere: the spans of <c>skill move went nowhere</c>, <c>attack into a wall</c>
/// and, when one of those is there, <c>attacks hitting nothing</c> (alone: "attacks hitting nothing"), merged;</item>
/// <item>death, chicken / out of potions, connection lost (a closed connection, a silent server, a stranger exit), waypoint
/// or portal refused (the bot's own failure lines: "Moving to town failed", taking or creating a waypoint or portal that
/// failed): the rest of the window after the first;</item>
/// <item>records dropped (a trace or sidecar gap), path failure and monster accuracy issue: the spans of their incidents,
/// at least a second each;</item>
/// <item>gambling, muling, vendor trip or long town stay: the town phase over the median game's, told apart by what the
/// item ledger says the client did (gambled, traded, bought, sold or repaired);</item>
/// <item>otherwise the phase most over the median, "slow &lt;phase&gt;".</item>
/// </list>
/// A death comes first whatever the seconds; for a failed step, then whatever ended it.
/// </summary>
public static class SlowCauses
{
    public const string Wall = "stuck at a wall / skill moves went nowhere";
    public const string Death = "death";
    public const string TraceEnds = "trace ends mid-step";

    private static readonly string[] ConnectionKinds = ["connection closed", "server silent", "stranger exit"];
    private static readonly string[] GapKinds = ["trace gap", "sidecar gap"];
    private static readonly string[] PathKinds = ["path failures", "not walkable", "stuck", "position mismatch", "server holds position", "position off its map", "position unconfirmed"];
    private static readonly string[] AccuracyKinds = ["stale unit", StaleUnitCheck.SubmergedKind, "unanswered attacks", AttackingWithoutHitsCheck.Kind];
    private static readonly TimeSpan TownSlack = TimeSpan.FromSeconds(10);

    private static readonly System.Text.RegularExpressions.Regex PortalFailure = new(
        @"Moving to town failed|[Tt]aking (the )?(waypoint|wp|townportal|town portal|lair portal|portal)\b.* failed|Creating townportal failed",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>An error line of the bot's own saying a waypoint or portal could not be taken or made.</summary>
    public static bool IsPortalFailure(Incident incident)
        => incident.Kind == "error" && incident.Detail != null && PortalFailure.IsMatch(incident.Detail);

    /// <param name="incidents">The game's incidents; those of other clients are left out.</param>
    /// <param name="phases">The client's phases in the game, or null - always null for a step, whose window is not the game.</param>
    /// <param name="median">The median game's phases in seconds, or null.</param>
    /// <param name="failed">For a failed step: a death, chicken, lost connection or refused waypoint or portal in its last
    /// <see cref="EndedWithin"/> ended it, and comes first (after a death) whatever the seconds.</param>
    public static List<SlowCause> Of(string client, IEnumerable<Incident> incidents, TimeSpan from, TimeSpan to,
        IReadOnlyDictionary<string, TimeSpan> phases, IReadOnlyDictionary<string, double> median, ItemMoves.Summary items, bool failed = false)
    {
        var window = Math.Max(0, (to - from).TotalSeconds);
        var mine = incidents.Where(i => i.Client != null && i.Client.Split('+').Contains(client) && i.Offset <= to && (i.End ?? i.Offset) >= from).ToList();
        var causes = new List<SlowCause>();
        double Rest(Incident i) => Math.Max(1, (to - (i.Offset > from ? i.Offset : from)).TotalSeconds);
        double Spans(IEnumerable<Incident> list) => Merged(list.Select(i => (Start: i.Offset < from ? from : i.Offset, End: Max(i.End ?? i.Offset, i.Offset + TimeSpan.FromSeconds(1)) > to ? to : Max(i.End ?? i.Offset, i.Offset + TimeSpan.FromSeconds(1)))));

        var nowhere = mine.Where(i => i.Kind is CombatCheck.NowhereKind or CombatCheck.WallKind).ToList();
        var nothing = mine.Where(i => i.Kind == CombatCheck.NothingKind).ToList();
        if (nowhere.Count > 0)
        {
            causes.Add(new SlowCause(Wall, Spans(nowhere.Concat(nothing)), $"{Count(nowhere.Concat(nothing))}; mana {Mana(nowhere.Concat(nothing))}"));
        }
        else if (nothing.Count > 0)
        {
            causes.Add(new SlowCause("attacks hitting nothing", Spans(nothing), Count(nothing)));
        }

        var ended = new List<SlowCause>();
        void First(string label, Func<Incident, bool> match)
        {
            var matching = mine.Where(match).OrderBy(i => i.Offset).ToList();
            if (matching.Count == 0)
            {
                return;
            }

            var last = matching.LastOrDefault(i => to - i.Offset <= EndedWithin);
            if (failed && last != null)
            {
                ended.Add(new SlowCause(label, Rest(last), $"ended it; {last.Kind} at {TraceTime.Format(last.Offset)}: {Short(last.Detail)}"));
                return;
            }

            var first = matching[0];
            causes.Add(new SlowCause(label, Rest(first), $"{first.Kind} at {TraceTime.Format(first.Offset)}: {Short(first.Detail)}"));
        }

        First(Death, i => i.Kind == "death");
        First("chicken / out of potions", i => i.Kind == "chicken");
        First("connection lost", i => ConnectionKinds.Contains(i.Kind));
        First("waypoint or portal refused", IsPortalFailure);
        void Spanned(string label, string[] kinds)
        {
            var list = mine.Where(i => kinds.Contains(i.Kind)).ToList();
            if (list.Count > 0)
            {
                causes.Add(new SlowCause(label, Spans(list), Count(list)));
            }
        }

        Spanned("records dropped", GapKinds);
        Spanned("path failure", PathKinds);
        Spanned("monster accuracy issue", AccuracyKinds);

        if (phases != null && median != null)
        {
            var town = phases.GetValueOrDefault("town") - TimeSpan.FromSeconds(median.GetValueOrDefault("town"));
            if (town > TownSlack)
            {
                var (label, what) = items switch
                {
                    { Gambled: > 0 } => ("gambling", $"gambled {items.Gambled} items"),
                    { TradedAway: > 0 } or { Received: > 0 } => ("muling", $"traded away {items.TradedAway}, received {items.Received}"),
                    { Bought: > 0 } or { Sold: > 0 } or { Repaired: > 0 } => ("vendor trip", $"bought {items.Bought}, sold {items.Sold}, repaired {items.Repaired}"),
                    _ => ("long town stay", "no trade in the item ledger"),
                };
                causes.Add(new SlowCause(label, town.TotalSeconds, $"town {phases["town"].TotalSeconds:0}s against the median game's {median.GetValueOrDefault("town"):0}s; {what}"));
            }

            if (causes.Count == 0 && ended.Count == 0 && phases.Select(p => (Phase: p.Key, Over: p.Value.TotalSeconds - median.GetValueOrDefault(p.Key))).OrderByDescending(p => p.Over).FirstOrDefault() is { Over: >= 1 } worst)
            {
                causes.Add(new SlowCause($"slow {worst.Phase}", worst.Over, $"{phases[worst.Phase].TotalSeconds:0}s against the median game's {median.GetValueOrDefault(worst.Phase):0}s; no incident explains it"));
            }
        }

        return [.. ended.Select(c => (Cause: c, Ended: true)).Concat(causes.Select(c => (Cause: c, Ended: false)))
            .Select(c => (Cause: c.Cause with { Seconds = Math.Min(c.Cause.Seconds, window) }, c.Ended))
            .OrderBy(c => c.Cause.Label == Death ? 0 : 1)
            .ThenBy(c => c.Ended ? 0 : 1)
            .ThenByDescending(c => c.Cause.Seconds)
            .ThenBy(c => c.Cause.Label, StringComparer.Ordinal)
            .Select(c => c.Cause)];
    }

    public static readonly TimeSpan EndedWithin = TimeSpan.FromSeconds(5);

    /// <summary>"cause: gambling 219s (town 229s against ...); also: path failure 3s" - the top cause and the next two.</summary>
    public static string Render(IReadOnlyList<SlowCause> causes)
        => causes.Count == 0
            ? "cause: none found"
            : $"cause: {causes[0].Label} {causes[0].Seconds:0}s ({causes[0].Detail})"
                + (causes.Count > 1 ? $"; also: {string.Join(", ", causes.Skip(1).Take(2).Select(c => $"{c.Label} {c.Seconds:0}s"))}" : "");

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static double Merged(IEnumerable<(TimeSpan Start, TimeSpan End)> spans)
    {
        var total = TimeSpan.Zero;
        TimeSpan? openStart = null, openEnd = null;
        foreach (var (start, end) in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (openEnd is { } e && start <= e)
            {
                openEnd = end > e ? end : e;
                continue;
            }

            if (openStart is { } s0)
            {
                total += openEnd.Value - s0;
            }

            (openStart, openEnd) = (start, end);
        }

        if (openStart is { } last)
        {
            total += openEnd.Value - last;
        }

        return total.TotalSeconds;
    }

    private static string Count(IEnumerable<Incident> incidents)
        => string.Join(", ", incidents.GroupBy(i => i.Kind).Select(g => $"{g.Count()} {g.Key}"));

    private static readonly System.Text.RegularExpressions.Regex SpanMana = new(@"(\d+) (?:mana spent )?in the span", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The most mana one of the incidents spent in its span; the spans overlap, so they are not added up.</summary>
    private static string Mana(IEnumerable<Incident> incidents)
    {
        var most = incidents.Select(i => SpanMana.Match(i.Detail ?? "")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        return $"up to {most} spent in one span";
    }

    private static string Short(string text) => text == null ? "" : text.Length > 100 ? text[..100] + ".." : text;
}
