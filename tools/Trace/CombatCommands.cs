using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceTool;

/// <summary>find, mana and activity --skill-moves.</summary>
internal sealed partial class Commands
{
    private int Mana(string target)
    {
        var query = Open(target);
        var client = Client(query, required: true);
        var combat = new CombatObserver();
        var mana = new ManaObserver(From, To, combat);
        var context = ClientReplay.Run(query.Game, client, To, [combat, mana]);
        context?.Replayer.Dispose();
        ReplayNotesHeader(query.Game, client);
        if (AsJson)
        {
            mana.Attribute();
            foreach (var change in mana.Changes)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    t = TraceTime.Format(change.At),
                    client,
                    before = change.Before,
                    after = change.After,
                    max = change.Max,
                    change = change.Delta,
                    causes = change.Causes.Select(c => new { cause = c.Cause, detail = c.Detail, amount = c.Amount }),
                    doing = change.Doing,
                    at = change.Position == null ? null : new[] { change.Position.X, change.Position.Y },
                    nearestBurner = change.NearestBurner,
                    burnerDistance = change.BurnerDistance is { } d ? Math.Round(d, 1) : (double?)null,
                }, Json));
            }

            return 0;
        }

        output.WriteLine(Header(query.Game));
        var capped = new CappedOutput(output, Limit, "narrow with --from/--to");
        foreach (var line in mana.Report(client, Flag("--all")))
        {
            capped.Line(line);
        }

        capped.Finish();
        return 0;
    }

    private int SkillMoves(GameQuery query)
    {
        var only = Client(query, required: false);
        var (from, to) = (From, To);
        var labels = only == null ? query.Game.Labels.ToList() : [only];
        var replayed = query.ReplayAll(labels, TimeSpan.MaxValue, () => new CombatObserver(), o => [o]);
        if (!AsJson)
        {
            output.WriteLine(Header(query.Game));
        }

        ReplayNotesHeader(query.Game, only);
        var capped = new CappedOutput(output, Limit, "narrow with --from/--to or --client");
        foreach (var (client, combat) in replayed)
        {
            var views = combat.Moves.Where(m => m.Start >= from && m.Start <= to).Select(combat.View).ToList();
            if (!AsJson)
            {
                var nowhere = views.Count(CombatCheck.WentNowhere);
                capped.Line($"{client}: {views.Count} skill moves{string.Concat(views.GroupBy(v => v.Move.Skill).Select(g => $", {g.Count()} {g.Key}"))}; {nowhere} went nowhere (moved under {CombatCheck.Nowhere:0}, hit nothing, asked {CombatCheck.NowhereAsked:0} or more away); "
                    + $"{views.Sum(v => Math.Max(0, v.ManaSpent ?? 0))} mana spent on them, {views.Where(CombatCheck.WentNowhere).Sum(v => Math.Max(0, v.ManaSpent ?? 0))} on those");
            }

            foreach (var view in views)
            {
                var move = view.Move;
                if (AsJson)
                {
                    capped.Line(JsonSerializer.Serialize(new
                    {
                        client,
                        t = TraceTime.Format(move.Start),
                        end = TraceTime.Format(move.End),
                        skill = move.Skill,
                        asked = move.Asked == null ? null : new[] { move.Asked.X, move.Asked.Y },
                        askedAt = TraceTime.Format(move.AskedAt),
                        asks = move.Asks,
                        from = view.From == null ? null : new[] { view.From.X, view.From.Y },
                        to = view.To == null ? null : new[] { view.To.X, view.To.Y },
                        moved = Math.Round(view.Moved, 1),
                        hits = view.Hits.Count,
                        units = view.Hits.Select(h => D2NG.Trace.Query.Names.Id(h.Id)).Distinct(),
                        manaBefore = view.ManaBefore,
                        manaAfter = view.ManaAfter,
                    }, Json));
                    continue;
                }

                var units = view.Hits.GroupBy(h => h.Id).Select(g => $"{D2NG.Trace.Query.Names.Id(g.Key)}{(g.Count() > 1 ? $" x{g.Count()}" : "")}{(g.Any(h => h.Death) ? " died" : "")}").ToList();
                var nowhere = CombatCheck.WentNowhere(view) ? "  WENT NOWHERE" : "";
                capped.Line($"  {TraceTime.Format(move.Start)} {move.Skill,-12} {(move.End - move.Start).TotalSeconds,5:0.00}s asked {D2NG.Trace.Query.Names.Point(move.Asked)}"
                    + $" ({move.Asks} cast{(move.Asks == 1 ? "" : "s")} since {TraceTime.Format(move.AskedAt)}{(move.Asked != null && view.From != null ? $", {view.From.Distance(move.Asked):0.0} away" : "")});"
                    + $" server {D2NG.Trace.Query.Names.Point(view.From)} -> {D2NG.Trace.Query.Names.Point(view.To)} moved {view.Moved:0.0};"
                    + $" hits {view.Hits.Count}{(units.Count > 0 ? $" ({string.Join(", ", units.Take(5))}{(units.Count > 5 ? $" and {units.Count - 5} more" : "")})" : "")};"
                    + $" mana {view.ManaBefore?.ToString(CultureInfo.InvariantCulture) ?? "?"} -> {view.ManaAfter?.ToString(CultureInfo.InvariantCulture) ?? "?"}{(view.ManaSpent is { } spent ? $" ({-spent:+0;-0;0})" : "")}{nowhere}");
            }
        }

        capped.Finish();
        if (!AsJson)
        {
            output.WriteLine("note: positions are the server's (life updates, reassigns); hits are NPCHit or deaths within 5 of the path from the start to 0.3 s after the end; mana before is the previous move's mana after while a fall charged to that one is still to come; mana after is that less the falls charged to the move - each fall once, to the earliest move ended at most 0.8 s before its packet that has none yet, split over them when it is a lump (packets come every 0.4 s and lag the casts)");
        }

        return 0;
    }

    /// <summary>
    /// activity --skill-moves over a folder: per client, its moves per skill, how many went nowhere and the time they took.
    /// A teleport cast that reaches the server after the move is carried out too, as a teleport onto the spot just
    /// reached, and shows here as a hop that went nowhere: its share swung from 0.5% to 11% between hours of one build.
    /// </summary>
    private int SkillMovesOfBatch(List<GameTrace> games)
    {
        var sources = Shared;
        var totals = new SortedDictionary<string, SkillMoveTotals>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            var query = sources.QueryOf(game);
            var only = Value("--client") is { } name ? game.ResolveClient(name) : null;
            var labels = only == null ? query.Game.Labels.ToList() : [only];
            foreach (var (client, combat) in query.ReplayAll(labels, TimeSpan.MaxValue, () => new CombatObserver(), o => [o]))
            {
                var total = totals.TryGetValue(client, out var known) ? known : totals[client] = new SkillMoveTotals();
                total.Games++;
                foreach (var view in combat.Moves.Where(m => m.Start >= From && m.Start <= To).Select(combat.View))
                {
                    var took = view.Move.End - view.Move.Start;
                    total.Moves++;
                    total.Time += took;
                    total.BySkill[view.Move.Skill.ToString()] = total.BySkill.GetValueOrDefault(view.Move.Skill.ToString()) + 1;
                    if (CombatCheck.WentNowhere(view))
                    {
                        total.Nowhere++;
                        total.NowhereTime += took;
                    }
                }
            }
        }

        if (!AsJson)
        {
            output.WriteLine($"{games.Count} games under {Path.GetFullPath(GameTrace.ParseTarget(_typed).Folder)}{FilterText()}; skill moves per client");
        }

        foreach (var (client, t) in totals.Where(t => t.Value.Moves > 0))
        {
            if (AsJson)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    client,
                    games = t.Games,
                    moves = t.Moves,
                    bySkill = t.BySkill,
                    nowhere = t.Nowhere,
                    seconds = Math.Round(t.Time.TotalSeconds, 1),
                    nowhereSeconds = Math.Round(t.NowhereTime.TotalSeconds, 1),
                    meanMs = Math.Round(t.Time.TotalMilliseconds / t.Moves),
                }, Json));
                continue;
            }

            output.WriteLine($"  {client}: {t.Games} games, {t.Moves} moves ({string.Join(", ", t.BySkill.Select(s => $"{s.Value} {s.Key}"))}), "
                + $"{t.Nowhere} went nowhere ({100.0 * t.Nowhere / t.Moves:0.0}%); {t.Time.TotalSeconds / t.Games:0.0}s moving a game, "
                + $"{t.NowhereTime.TotalSeconds / t.Games:0.0}s of it going nowhere; {t.Time.TotalMilliseconds / t.Moves:0} ms a move");
        }

        if (!AsJson)
        {
            output.WriteLine($"note: a move went nowhere when it moved under {CombatCheck.Nowhere:0} and hit nothing, asked {CombatCheck.NowhereAsked:0} or more away; per game it lists each move (activity <game> --skill-moves)");
        }

        return 0;
    }

    private sealed class SkillMoveTotals
    {
        public int Games;
        public int Moves;
        public int Nowhere;
        public TimeSpan Time;
        public TimeSpan NowhereTime;
        public SortedDictionary<string, int> BySkill { get; } = new(StringComparer.Ordinal);
    }

    private int Find(string target)
    {
        var games = Games(target);
        if (Value("--game") is { } name)
        {
            games = [.. games.Where(g => string.Equals(g.GameName, name, StringComparison.OrdinalIgnoreCase))];
        }

        if (games.Count == 0)
        {
            output.WriteLine($"no game{(Value("--game") is { } n ? $" named {n}" : "")} under {target}{FilterText()}");
            return 1;
        }

        var at = Value("--at");
        var folder = Path.GetFullPath(GameTrace.ParseTarget(target).Folder);
        if (at != null && !GameFinder.HasDate(at) && !GameTrace.IsDateFolder(folder) && !GameTrace.IsGameFolder(folder))
        {
            throw new ArgumentException($"--at {at} is a clock time without a date, which over {target} could be any day; give --at \"yyyy-MM-dd hh:mm:ss\" or a date folder");
        }

        var rows = games.Select(g => (Game: g, Distance: at == null ? TimeSpan.Zero : GameFinder.DistanceTo(g, at, TimeZoneInfo.Local))).ToList();
        var matched = rows.Where(r => r.Distance == TimeSpan.Zero).ToList();
        var nearest = matched.Count == 0;
        if (nearest)
        {
            matched = [.. rows.OrderBy(r => r.Distance).Take(3)];
            if (!AsJson)
            {
                output.WriteLine($"no game's window holds {at}; the nearest:");
            }
        }

        foreach (var (game, distance) in matched)
        {
            var reference = Ref(game);
            var clients = GameFinder.Clients(game);
            if (AsJson)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    game = reference,
                    name = game.GameName,
                    start = game.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    end = game.EndUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    seconds = Math.Round((game.EndUtc - game.StartUtc).TotalSeconds, 1),
                    clients = game.Labels,
                    files = game.Files.Select(f => Path.GetFileName(f.Path)),
                    away = nearest ? Math.Round(distance.TotalSeconds) : (double?)null,
                }, Json));
                continue;
            }

            output.WriteLine($"{reference,-32} {game.GameName,-16} {game.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}-{game.EndUtc.ToLocalTime():HH:mm:ss} {(game.EndUtc - game.StartUtc).TotalMinutes,5:0.0} min  {string.Join(", ", clients)}"
                + (nearest && at != null ? $"  ({distance.TotalSeconds:0}s from {at})" : "")
                + (game.InProgress ? "  IN PROGRESS" : ""));
        }

        if (!AsJson && matched.Count > 0)
        {
            output.WriteLine($"next: Trace activity {D2NG.Trace.Query.Incidents.Quote(Ref(matched[0].Game))} --phases");
        }

        return 0;
    }

    private int Export(string target)
    {
        var query = Open(target);
        var client = Client(query, required: true);
        var at = Value("--at") != null ? Time("--at", TimeSpan.Zero) : throw new ArgumentException("--at is required");
        var file = Value("--out") ?? throw new ArgumentException("--out <file.json> is required");
        var radius = Value("--radius") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : 40;
        var json = MomentExport.Export(query, client, at, radius, Maps());
        File.WriteAllText(file, json);
        output.WriteLine($"{file}: {new FileInfo(file).Length:N0} bytes, {client} as self at {TraceTime.Format(at)} with the grid {radius} around");
        return 0;
    }
}
