using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// What went wrong in a game, from the replay checks, the sidecar (failed steps, chickens, deaths,
/// strangers, exceptions, errors, the game server's own warnings) and the realm traces (refused
/// creates and joins).
/// </summary>
public sealed class IncidentOptions
{
    /// <summary>"state", "bot" or "all".</summary>
    public string Checks { get; init; } = "all";

    public TraceHistory History { get; init; }

    public MapSource Maps { get; init; }
}

public static class Incidents
{
    private static readonly TimeSpan SameIncident = TimeSpan.FromSeconds(3);

    public static List<Incident> Find(GameQuery query) => Find(query, new IncidentOptions()).Incidents;

    /// <summary>The incidents whose time, or for one that spans time its episode, overlaps <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IEnumerable<Incident> Within(IEnumerable<Incident> incidents, TimeSpan from, TimeSpan to)
        => incidents.Where(i => i.Offset <= to && (i.End is { } end && end > i.Offset ? end : i.Offset) >= from);

    /// <summary>One replay pass per client feeds every check and the activity; then the checks over the whole game, the sidecar and the realm.</summary>
    public static (List<Incident> Incidents, List<string> Notes) Find(GameQuery query, IncidentOptions options)
    {
        var family = options.Checks ?? "all";
        var phase = Profile.Time("incidents: replay all clients (wall)");
        var replayed = query.ReplayAll(query.Game.Labels, TimeSpan.MaxValue,
            _ => (Checks: IncidentCheck.Of(family), Activity: new ActivityObserver(), Combat: family is "all" or "bot" ? new CombatObserver() : null),
            o => [.. o.Checks, o.Activity, .. o.Combat == null ? [] : new IReplayObserver[] { o.Combat }]);
        phase.Dispose();
        phase = Profile.Time("incidents: steps");
        var steps = Steps.Of(query);
        phase.Dispose();
        var contexts = replayed.Select(r => new CheckContext
        {
            Query = query,
            Client = r.Client,
            Activity = r.Observers.Activity,
            Steps = steps,
            History = options.History,
            Maps = options.Maps,
            MapId = r.Observers.Activity.MapId,
            Difficulty = r.Observers.Activity.Difficulty,
            Combat = r.Observers.Combat,
        }).ToList();
        var found = MergeUnits(replayed.SelectMany(r => r.Observers.Checks.SelectMany(c => c.Found)).Where(i => i.Kind != LootLeftBehindCheck.Kind));
        found.AddRange(LootLeftBehindCheck.OnePerItem([.. replayed.SelectMany(r => r.Observers.Checks.OfType<LootLeftBehindCheck>().Select(c => (r.Client, c)))]));
        CompletedCheck[] completed = [new StuckCheck(), new PathFailureCheck(), new NotWalkableCheck(), new SendRateCheck(), new MoveNotConfirmedCheck(), new CombatCheck()];
        var perClient = new (List<Incident> Found, Exception Error)[contexts.Count];
        System.Threading.Tasks.Parallel.For(0, contexts.Count, i =>
        {
            var list = new List<Incident>();
            try
            {
                foreach (var check in completed.Where(c => family == "all" || c.Family == family))
                {
                    using var _ = Profile.Time("incidents: check " + check.GetType().Name);
                    list.AddRange(check.Run(contexts[i]));
                }

                perClient[i] = (list, null);
            }
            catch (Exception e)
            {
                perClient[i] = (list, e);
            }
        });
        foreach (var (client, error) in perClient)
        {
            if (error != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            }

            found.AddRange(client);
        }

        GameCheck[] gameChecks = [new HotLoopCheck(), new PartySplitCheck(), new StepDurationCheck(), new NewWarningCheck()];
        foreach (var check in gameChecks.Where(c => family == "all" || c.Family == family))
        {
            using var _ = Profile.Time("incidents: check " + check.GetType().Name);
            found.AddRange(check.Run(query, contexts));
        }

        using (Profile.Time("incidents: from sidecar"))
        {
            found.AddRange(FromSidecar(query));
        }

        using (Profile.Time("incidents: from realm"))
        {
            found.AddRange(FromRealm(query.RealmAnswers(), query.Game.StartUtc, query.Game.Labels));
        }

        using var merging = Profile.Time("incidents: merge");
        var drops = replayed.ToDictionary(r => r.Client, r => r.Observers.Checks.OfType<DeathCheck>().FirstOrDefault()?.Drops ?? []);
        return (AfterStopRequest(DeathsOverChickens(Merge(found), drops), query.StopRequestedAt()), [.. contexts.SelectMany(c => c.Notes).Distinct()]);
    }

    /// <summary>The incidents a stopping bot causes by itself: walks, paths and steps that fail once a stop was asked for.</summary>
    public static readonly string[] StopFailures = ["path failures", "step failed", "step timeout", "quick step failure", MoveNotConfirmedCheck.Kind];

    /// <summary>A walk, path or step failure at or after a stop request says so, since the stop is the likely cause.</summary>
    public static List<Incident> AfterStopRequest(List<Incident> incidents, TimeSpan? stopAt)
        => stopAt is not { } stop
            ? incidents
            : [.. incidents.Select(i => StopFailures.Contains(i.Kind) && i.Offset >= stop ? i with { Detail = $"{i.Detail}; after a stop request at {TraceTime.Format(stop)}" } : i)];

    private static readonly TimeSpan ChickenBeforeDeath = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AfterDeath = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A chicken or life drop of a client that died within moments is that death: the death keeps them as
    /// details (the bot leaving right after), never the other way round. A chicken that stands alone says
    /// which hit set it off.
    /// </summary>
    public static List<Incident> DeathsOverChickens(List<Incident> incidents, IReadOnlyDictionary<string, IReadOnlyList<LifeDrop>> drops)
    {
        var deaths = incidents.Where(i => i.Kind == "death" && i.Source == "replay").ToList();
        bool Within(Incident death, Incident other) => other.Client == death.Client && other.Offset >= death.Offset - ChickenBeforeDeath && other.Offset <= death.Offset + AfterDeath;
        var result = new List<Incident>();
        foreach (var incident in incidents)
        {
            if (incident.Kind is "chicken" or "life drop" && deaths.Any(d => Within(d, incident)))
            {
                continue;
            }

            if (incident.Kind == "death" && incident.Source == "replay")
            {
                var chicken = incidents.FirstOrDefault(c => c.Kind == "chicken" && Within(incident, c));
                result.Add(chicken == null ? incident : incident with { Detail = incident.Detail + $"; the bot logged a chicken {(chicken.Offset - incident.Offset).TotalMilliseconds:0} ms after dying: {chicken.Detail}" });
                continue;
            }

            if (incident.Kind == "chicken" && incident.Client != null && drops.TryGetValue(incident.Client, out var clientDrops)
                && clientDrops.Where(d => d.At >= incident.Offset - TimeSpan.FromSeconds(3) && d.At <= incident.Offset + TimeSpan.FromSeconds(0.3)).MaxBy(d => d.Lost) is { } hit)
            {
                result.Add(incident with { Detail = incident.Detail + $"; the biggest hit in the 3s before: {hit.Describe()}" });
                continue;
            }

            result.Add(incident);
        }

        return result;
    }

    /// <summary>
    /// Reports of one unit by several clients at about the same time are one line naming the clients,
    /// with how many packets each sent at it; a report about a unit by one client gets its count too.
    /// </summary>
    public static List<Incident> MergeUnits(IEnumerable<Incident> incidents)
    {
        var merged = new List<Incident>();
        var groups = new List<List<Incident>>();
        var byKindAndSubject = new Dictionary<(string, string), List<List<Incident>>>();
        foreach (var incident in incidents)
        {
            if (incident.Subject == null)
            {
                merged.Add(incident);
                continue;
            }

            var key = (incident.Kind, incident.Subject);
            if (!byKindAndSubject.TryGetValue(key, out var candidates))
            {
                byKindAndSubject[key] = candidates = [];
            }

            var group = candidates.FirstOrDefault(g => (g[0].Offset - incident.Offset).Duration() <= SameIncident);
            if (group == null)
            {
                groups.Add([incident]);
                candidates.Add(groups[^1]);
            }
            else
            {
                group.Add(incident);
            }
        }

        foreach (var group in groups)
        {
            var first = group.OrderBy(i => i.Offset).First();
            var clients = group.Select(i => i.Client).Distinct().ToList();
            var counts = group.GroupBy(i => i.Client).Select(g => (Client: g.Key, Count: g.Sum(i => i.Count ?? 0))).ToList();
            var sent = group.Any(i => i.PerClient != null)
                ? "; " + string.Join("; ", group.OrderBy(i => i.Offset).Select(i => $"{i.Client} {i.PerClient}"))
                : counts.Count == 1
                ? $"; {counts[0].Count} packets sent at it"
                : $"; {counts.Sum(c => c.Count)} packets sent at it by {string.Join(", ", counts.Select(c => $"{c.Client} {c.Count}"))}";
            merged.Add(first with { Client = string.Join("+", clients), Detail = first.Detail + sent, End = group.Max(i => i.End), Count = counts.Sum(c => c.Count) });
        }

        return merged;
    }

    public static IEnumerable<Incident> FromSidecar(GameQuery query)
    {
        var entries = query.GameEntries().ToList();
        var recovered = entries.Where(e => e.Kind == "step" && e.Str("outcome") == "ok").Select(e => (e.Client, Name: e.Str("name"), e.Utc)).ToList();
        foreach (var entry in entries)
        {
            var offset = query.Offset(entry);
            switch (entry.Kind)
            {
                case "step" when entry.Str("outcome") is "failed" or "timeout":
                    var name = entry.Str("name");
                    if (recovered.Any(r => r.Client == entry.Client && r.Name == name && r.Utc > entry.Utc))
                    {
                        continue;
                    }

                    yield return new Incident(offset, entry.Client, $"step {entry.Str("outcome")}",
                        $"{name}{(entry.Num("attempt") is > 1 and var attempt ? $" attempt {attempt}" : "")}{(entry.Str("reason") is { } reason ? $": {reason}" : "")}{(entry.Num("ms") is { } ms ? $" after {ms / 1000.0:0.0}s" : "")}", "sidecar");
                    break;
                case "decision" when entry.Str("name") is "chicken":
                    yield return new Incident(offset, entry.Client, "chicken", $"{entry.Field("reason")} (life {entry.Field("life")}/{entry.Field("maxLife")})", "sidecar");
                    break;
                case "decision" when entry.Str("name") is "death" && entry.Field("damagedRecently") == "true":
                    yield return new Incident(offset, entry.Client, "death", $"the bot saw its corpse assigned at {entry.Field("at")} in {entry.Field("area")}", "sidecar");
                    break;
                case "decision" when entry.Str("name") is "stranger":
                    yield return new Incident(offset, entry.Client, "stranger exit", entry.Field("what"), "sidecar");
                    break;
                case "decision" when entry.Str("name") is "exception":
                    yield return new Incident(offset, entry.Client, "exception", $"{entry.Field("where")}: {entry.Field("error")}", "sidecar");
                    break;
                case "gap":
                    yield return new Incident(offset, null, "sidecar gap", $"{entry.Num("n")} log lines and events dropped on a full queue", "sidecar");
                    break;
                case "log" when entry.Level is "Error" or "Fatal":
                    yield return new Incident(offset, Client(query, entry), entry.Level == "Fatal" ? "fatal" : "error", FirstLine(entry.Message) + ExceptionType(entry), "sidecar");
                    break;
                case "log" when entry.Level == "Warning" && entry.Message != null && entry.Message.Contains("closed the connection mid-game", StringComparison.Ordinal):
                    yield return new Incident(offset, Client(query, entry), "connection closed", FirstLine(entry.Message), "sidecar");
                    break;
                case "log" when entry.Level == "Warning" && entry.Message != null && entry.Message.Contains("was silent for", StringComparison.Ordinal):
                    yield return new Incident(offset, Client(query, entry), "server silent", FirstLine(entry.Message), "sidecar");
                    break;
            }
        }
    }

    public static IEnumerable<Incident> FromRealm(IEnumerable<RealmAnswer> answers, DateTime gameStart, IReadOnlyList<string> labels)
        => answers.Where(a => a.Refused).Select(a => new Incident(
            a.Utc - gameStart,
            a.Character ?? a.Instance,
            $"{a.Request} refused",
            $"game {a.Game}: result 0x{a.Result:X2} {a.Meaning}",
            "realm"));

    private static string Client(GameQuery query, SidecarEntry entry)
    {
        var clients = query.Attribution.ClientsOf(entry);
        return clients.Count == 0 ? null : string.Join("+", clients);
    }

    private static string FirstLine(string text)
    {
        if (text == null)
        {
            return "";
        }

        var end = text.IndexOfAny(['\r', '\n']);
        var line = end < 0 ? text : text[..end];
        return line.Length > 240 ? line[..240] + ".." : line;
    }

    private static string ExceptionType(SidecarEntry entry)
        => entry.Exception == null ? "" : $" [{FirstLine(entry.Exception)}]";

    /// <summary>
    /// Only true duplicates are one incident: the same kind for the same client within a few seconds,
    /// seen by two sources (the replay and the bot's own report of one death, chicken or silence) or with
    /// the same detail. The earlier sources in the list win - the replay checks before the sidecar -
    /// since they carry more of what happened.
    /// </summary>
    public static List<Incident> Merge(IEnumerable<Incident> incidents)
    {
        var merged = new List<Incident>();
        var byKindAndClient = new Dictionary<(string, string), List<Incident>>();
        foreach (var incident in incidents)
        {
            var key = (incident.Kind, incident.Client);
            if (!byKindAndClient.TryGetValue(key, out var same))
            {
                byKindAndClient[key] = same = [];
            }

            if (AnyWithin(same, incident.Offset, m => m.Source != incident.Source || m.Detail == incident.Detail))
            {
                continue;
            }

            Insert(same, incident);
            merged.Add(incident);
        }

        return [.. merged.OrderBy(i => i.Offset)];
    }

    /// <summary>Whether an incident of the list, kept in offset order, lies within <see cref="SameIncident"/> of <paramref name="offset"/> and matches.</summary>
    private static bool AnyWithin(List<Incident> sorted, TimeSpan offset, Func<Incident, bool> match)
    {
        for (var i = FirstFrom(sorted, offset - SameIncident); i < sorted.Count && sorted[i].Offset <= offset + SameIncident; i++)
        {
            if (match(sorted[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static int FirstFrom(List<Incident> sorted, TimeSpan from)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle].Offset < from)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static void Insert(List<Incident> sorted, Incident incident)
    {
        var at = FirstFrom(sorted, incident.Offset);
        while (at < sorted.Count && sorted[at].Offset == incident.Offset)
        {
            at++;
        }

        sorted.Insert(at, incident);
    }

    /// <summary>The timeline around an incident: 20 s before it to 2 s after its end, never --to before --from.</summary>
    public static string NextCommand(string game, Incident incident)
    {
        var from = incident.Offset - TimeSpan.FromSeconds(20);
        if (from < TimeSpan.Zero)
        {
            from = TimeSpan.Zero;
        }

        var to = (incident.End ?? incident.Offset) + TimeSpan.FromSeconds(2);
        if (to < from)
        {
            to = from + TimeSpan.FromSeconds(22);
        }

        var client = incident.Client != null && !incident.Client.Contains('+') ? $" --client {Quote(incident.Client)}" : "";
        switch (incident.Kind)
        {
            case "not walkable" when client.Length > 0:
                return $"Trace tiles {Quote(game)}{client} --at {TraceTime.Format(incident.Offset + TimeSpan.FromSeconds(1))}";
            case CombatCheck.WallKind when client.Length > 0:
                return $"Trace tiles {Quote(game)}{client} --at {TraceTime.Format(incident.Offset + TimeSpan.FromMilliseconds(100))}";
            case CombatCheck.NowhereKind when client.Length > 0:
                return $"Trace activity {Quote(game)}{client} --skill-moves --from {TraceTime.Format(Max(TimeSpan.Zero, incident.Offset - TimeSpan.FromSeconds(2)))} --to {TraceTime.Format((incident.End ?? incident.Offset) + TimeSpan.FromSeconds(2))}";
            case "stuck" or "party split" when incident.End is { } end:
                return $"Trace activity {Quote(game)}{client} --from {TraceTime.Format(from)} --to {TraceTime.Format(end + TimeSpan.FromSeconds(2))}";
            case "stale unit" or "unanswered attacks" or StaleUnitCheck.SubmergedKind or AttackingWithoutHitsCheck.Kind when incident.Client != null:
                return $"Trace state {Quote(game)} --client {Quote(incident.Client.Split('+')[0])} --at {TraceTime.Format(incident.End ?? incident.Offset)} --select self,units:45";
        }

        return $"Trace timeline {Quote(game)}{client} --from {TraceTime.Format(from)} --to {TraceTime.Format(to)}";
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public static string Quote(string text) => text.Contains(' ') ? $"\"{text}\"" : text;
}
