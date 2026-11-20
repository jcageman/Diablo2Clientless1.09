using D2NG.Core.Tracing;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// Which build recorded a game and under which client rules, and which rules its replay uses: the first thing to know
/// before reading replayed state, since a replay shows the state the recording bot held only under its own rules.
/// </summary>
public static class RulesNotes
{
    /// <param name="client">One client's files, or null for every client of the game.</param>
    public static List<string> Of(GameTrace game, string client = null)
        => Of(game.Files.Where(f => client == null || f.Label == client).Select(f => (f.Label, f.Header, f.Path)));

    /// <summary>The notes for trace files by path; a file whose header cannot be read is left out.</summary>
    public static List<string> OfFiles(IEnumerable<string> paths)
    {
        var headers = new List<(string, TraceHeader, string)>();
        foreach (var path in paths)
        {
            try
            {
                using var reader = new TraceReader(path);
                headers.Add((reader.Header.ClientLabel, reader.Header, path));
            }
            catch (System.Exception e) when (e is System.IO.IOException or System.UnauthorizedAccessException)
            {
            }
        }

        return Of(headers);
    }

    public static List<string> Of(IEnumerable<(string Label, TraceHeader Header)> files) => Of(files.Select(f => (f.Label, f.Header, (string)null)));

    /// <param name="files">Each file's label, header and path; the path lets an unstamped header's estimate read the file's records.</param>
    public static List<string> Of(IEnumerable<(string Label, TraceHeader Header, string Path)> files)
    {
        var list = files.ToList();
        var notes = new List<string>();
        if (list.Count == 0)
        {
            return notes;
        }

        var overridden = ReplayRules.OverrideText is { } text ? $" (--rules {text})" : "";
        var recorded = list.GroupBy(f => (Recorded: TraceHeaderIdentity.Describe(f.Header), Replayed: Replayed(f.Header, f.Path))).ToList();
        if (recorded.Count == 1)
        {
            notes.Add($"recorded by {recorded[0].Key.Recorded}; replayed under rules {recorded[0].Key.Replayed}{overridden}");
        }
        else
        {
            var each = string.Join("; ", recorded.Select(g => $"{string.Join(", ", g.Select(f => f.Label).Distinct())}: {g.Key.Recorded}, replayed under rules {g.Key.Replayed}"));
            notes.Add($"the clients of this game were recorded by different builds or rules - {each}{overridden}");
        }

        foreach (var version in list.Where(f => ReplayRules.NewerThanKnown(f.Header)).Select(f => f.Header.RulesVersion.Value).Distinct())
        {
            notes.Add(ReplayRules.NewerRulesNote(version));
        }

        return notes;
    }

    /// <summary>"2", or for an unstamped trace replayed under its estimate "1, an estimate - ...".</summary>
    private static string Replayed(TraceHeader header, string path)
    {
        if (ReplayRules.Override is { } forced)
        {
            return forced.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var choice = ReplayRules.Resolve(header, path);
        return choice.Estimated ? $"{choice.Rules.Version}, an estimate - {choice.Basis}" : choice.Rules.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
