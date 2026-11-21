using D2NG.Trace.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceTool;

/// <summary>activity --area-spells: what each client's Static Field, Nova and Frost Nova took off the monsters around it.</summary>
internal sealed partial class Commands
{
    private int AreaSpellsOf(List<GameTrace> games)
    {
        var only = Value("--client");
        var sources = Shared;
        var totals = new Dictionary<(string Client, string Skill), (int Casts, int Effect, double Life, int Kills, TimeSpan Time)>();
        var profile = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var profileDrops = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var query = sources.QueryOf(game);
            var labels = only == null ? game.Labels.ToList() : game.Labels.Where(l => string.Equals(l, only, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var (client, o) in query.ReplayAll(labels, TimeSpan.MaxValue,
                () => (Combat: new CombatObserver(), Life: new MonsterLifeObserver()), o => [o.Combat, o.Life]))
            {
                if (Flag("--by-distance"))
                {
                    foreach (var name in AreaSpells.Radius.Keys)
                    {
                        var bins = AreaSpells.ByDistance(o.Combat.Casts, o.Life.Drops, name);
                        var life = profile.TryGetValue(name, out var l) ? l : profile[name] = new double[bins.Count];
                        var count = profileDrops.TryGetValue(name, out var c) ? c : profileDrops[name] = new int[bins.Count];
                        for (var b = 0; b < bins.Count; b++)
                        {
                            life[b] += bins[b].Life;
                            count[b] += bins[b].Drops;
                        }
                    }
                }

                foreach (var skill in AreaSpells.Measure(o.Combat.Casts, o.Life.Drops))
                {
                    var key = (client, skill.Skill);
                    var t = totals.GetValueOrDefault(key);
                    totals[key] = (t.Casts + skill.Casts, t.Effect + skill.CastsWithEffect, t.Life + skill.LifeRemoved, t.Kills + skill.Kills, t.Time + skill.CastTime);
                }
            }
        }

        if (AsJson)
        {
            foreach (var ((client, skill), t) in totals.OrderBy(t => t.Key.Client, StringComparer.Ordinal))
            {
                output.WriteLine(JsonSerializer.Serialize(new { client, skill, casts = t.Casts, withEffect = t.Effect, lifeRemoved = Math.Round(t.Life), kills = t.Kills, seconds = Math.Round(t.Time.TotalSeconds, 1) }, Json));
            }

            return 0;
        }

        output.WriteLine($"{games.Count} games under {Path.GetFullPath(GameTrace.ParseTarget(_typed).Folder)}{FilterText()}; each area cast credited with the monster life (in % of a monster) that fell within its radius of the caster in the {AreaSpells.Window.TotalSeconds:0.0}s after it, until the next cast");
        if (totals.Count == 0)
        {
            output.WriteLine("no Static Field, Nova or Frost Nova casts in these games");
            return 0;
        }

        foreach (var client in totals.Keys.Select(k => k.Client).Distinct().OrderBy(c => c, StringComparer.Ordinal))
        {
            output.WriteLine($"  {client}:");
            foreach (var ((_, skill), t) in totals.Where(t => t.Key.Client == client).OrderByDescending(t => t.Value.Casts))
            {
                output.WriteLine($"    {skill,-12} {t.Casts,6} casts, {100.0 * t.Effect / Math.Max(1, t.Casts),3:0}% took life, {t.Life / Math.Max(1, t.Casts),6:0.0}% a cast, {t.Life / Math.Max(0.001, t.Time.TotalSeconds),6:0}% a second, {t.Kills,5} kills");
            }
        }

        foreach (var (name, life) in profile)
        {
            var drops = profileDrops[name];
            output.WriteLine($"  {name} by distance from the caster (life drops{(name == "StaticField" ? " of a quarter of the monster's life" : "")}, per 2.5 units):");
            output.WriteLine("    " + string.Join("  ", life.Select((l, b) => $"{b * 2.5:0}-{(b + 1) * 2.5:0}: {drops[b]}")));
        }

        output.WriteLine($"note: radii {string.Join(", ", AreaSpells.Radius.Select(r => $"{r.Key} {r.Value:0}"))}; a party member's damage in the same window and radius counts too, alike for every skill, so compare skills rather than read one alone");
        return 0;
    }
}
