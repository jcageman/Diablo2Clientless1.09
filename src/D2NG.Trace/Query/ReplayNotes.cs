using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// What every command that replays a game has to say before its answer: which files were not recorded by the bot,
/// what their replay had to assume (a guessed own player, made up game flags), and which files are a second view
/// of a client already read from another source.
/// </summary>
public static class ReplayNotes
{
    /// <param name="client">One client's notes, or null for every client of the game.</param>
    public static List<string> Of(GameTrace game, string client = null)
    {
        var notes = RulesNotes.Of(game, client);
        IReadOnlyList<string> labels = client == null ? game.Labels : [client];
        foreach (var label in labels)
        {
            var prefix = labels.Count > 1 ? $"{label}: " : "";
            foreach (var file in game.FilesOf(label).Where(f => f.Header.Source != TraceSource.Bot))
            {
                notes.Add($"{prefix}source: {file.Header.Source.ToString().ToLowerInvariant()} ({Path.GetFileName(file.Path)}), a capture of a real client: no client actions or digests were recorded, they are derived from what it sent");
                foreach (var note in Assumed(file.Path))
                {
                    notes.Add(prefix + note);
                }
            }

            foreach (var view in game.SecondViews.Where(v => v.Label == label))
            {
                notes.Add($"{prefix}{Path.GetFileName(view.Path)} (source {view.Header.Source.ToString().ToLowerInvariant()}) recorded this client at the same time as the bot's own trace, which is the one read; it is a second view, not a rejoin");
            }
        }

        return [.. notes.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>What a replay of the file assumes at its start: the replayer decides it before the first record is applied.</summary>
    private static IEnumerable<string> Assumed(string path)
    {
        try
        {
            using var replayer = new Replayer(path);
            return [.. replayer.Result.Notes];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
