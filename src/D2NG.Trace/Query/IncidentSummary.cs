using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace D2NG.Trace.Query;

/// <summary>
/// The worst of a party's incidents first: deaths, chickens (least life left first), the longest stalls,
/// failed steps and lost connections, each with the game and time of the worst three, so the first lines
/// answer "what was the worst thing today".
/// </summary>
public static class IncidentSummary
{
    private const int Shown = 3;
    private static readonly Regex LifeLeft = new(@"\(life (\d+)/(\d+)\)", RegexOptions.Compiled);

    public static List<string> Lines(IEnumerable<(string Game, Incident Incident)> incidents)
    {
        var all = incidents.ToList();
        var lines = new List<string>();
        void Add(string title, IEnumerable<(string Game, Incident Incident)> rows, Func<(string Game, Incident Incident), double> worst, Func<Incident, string> what)
        {
            var list = rows.ToList();
            if (list.Count == 0)
            {
                lines.Add($"{title}: none");
                return;
            }

            var top = list.OrderByDescending(worst).ThenBy(r => r.Incident.Offset).Take(Shown)
                .Select(r => $"{r.Game} {TraceTime.Format(r.Incident.Offset)} {r.Incident.Client ?? "party"} {what(r.Incident)}");
            lines.Add($"{title}: {list.Count}{(list.Count > Shown ? $", worst {Shown}" : "")}: {string.Join("; ", top)}");
        }

        Add("deaths", all.Where(r => r.Incident.Kind == "death"), _ => 0, i => Short(i.Detail));
        Add("chickens", all.Where(r => r.Incident.Kind == "chicken"), r => -LifeShare(r.Incident), i => LifeShare(i) is var share && share < 1 ? $"at {share:P0} life" : Short(i.Detail));
        Add("longest stalls", all.Where(r => r.Incident.Kind is "stuck" or "party split" && r.Incident.End != null), r => (r.Incident.End.Value - r.Incident.Offset).TotalSeconds,
            i => $"{i.Kind} {(i.End.Value - i.Offset).TotalSeconds:0}s");
        Add("failed steps", all.Where(r => r.Incident.Kind is "step failed" or "step timeout"), _ => 0, i => Short(i.Detail));
        Add("connection losses", all.Where(r => r.Incident.Kind is "connection closed" or "server silent"), _ => 0, i => i.Kind);
        return lines;
    }

    private static double LifeShare(Incident incident)
    {
        var match = LifeLeft.Match(incident.Detail ?? "");
        return match.Success && double.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / max
            : 1;
    }

    private static string Short(string text)
    {
        var cut = text.IndexOf(';');
        var first = cut < 0 ? text : text[..cut];
        return first.Length > 90 ? first[..90] + ".." : first;
    }
}
