using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace D2NG.Trace.Query;

/// <summary>
/// The bot settings a game was played under: the "settings" events its clients wrote on entering it (bot type,
/// monsterMotion as configured, monsterMotionEffective, the bot's own switches). A game recorded before those events
/// existed takes monsterMotion and monsterMotionEffective from its process's startup log line ("Monster motion:
/// configured X, effective Y") - of every sidecar of the process key, so two bots started in the same second give "a|b" when they
/// differ - and has no other setting. Clients that disagree give "a|b".
/// </summary>
public static class GameSettings
{
    public const string NotRecorded = "(not recorded)";

    private const int StartupLines = 400;

    private static readonly ConcurrentDictionary<string, (string Configured, string Effective)?> Startup = new(StringComparer.OrdinalIgnoreCase);

    public static SortedDictionary<string, string> Of(GameQuery query)
    {
        var values = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string value) => (values.TryGetValue(key, out var set) ? set : values[key] = new SortedSet<string>(StringComparer.Ordinal)).Add(value ?? "");
        foreach (var entry in query.GameEntries().Where(e => e.Kind == "settings"))
        {
            foreach (var (key, value) in entry.FieldPairs())
            {
                Add(key, value);
            }
        }

        if (!values.ContainsKey("monsterMotion"))
        {
            foreach (var process in query.Processes.OrderBy(p => p, StringComparer.Ordinal))
            {
                foreach (var motion in StartupMotions(query.Game, process))
                {
                    Add("monsterMotion", motion.Configured);
                    Add("monsterMotionEffective", motion.Effective);
                }
            }
        }

        return new SortedDictionary<string, string>(values.ToDictionary(v => v.Key, v => string.Join("|", v.Value)), StringComparer.OrdinalIgnoreCase);
    }

    public static string ValueOf(GameQuery query, string key) => Of(query).GetValueOrDefault(key) ?? NotRecorded;

    /// <summary>key=value[,key=value]: a game matches when every key has that value (case ignored; "(not recorded)" or "none" for a game without it).</summary>
    public static Func<GameQuery, bool> Filter(string where)
    {
        var wanted = where.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => p.Length == 2 ? (Key: p[0].Trim(), Value: p[1].Trim()) : throw new ArgumentException($"--where {where}: give key=value[,key=value]"))
            .ToList();
        return query =>
        {
            var settings = Of(query);
            return wanted.All(w => settings.TryGetValue(w.Key, out var value)
                ? string.Equals(value, w.Value, StringComparison.OrdinalIgnoreCase)
                : w.Value is NotRecorded or "none");
        };
    }

    /// <summary>
    /// The startup lines of every sidecar of the process key: events-X.jsonl and, for bots started in the same second,
    /// events-X-2.jsonl and on - the key cannot tell them apart, so all of them count. The game's own date folder is read
    /// first; the day before or after only when it has none.
    /// </summary>
    private static List<(string Configured, string Effective)> StartupMotions(GameTrace game, string process)
    {
        var prefix = Core.Tracing.TraceSidecar.FilePrefix + process;
        var folders = new[] { game.DateFolder }.Concat(game.NearbyDateFolders().Where(f => !string.Equals(f, game.DateFolder, StringComparison.OrdinalIgnoreCase)));
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var files = Directory.GetFiles(folder, prefix + "*" + Core.Tracing.TraceSidecar.Extension)
                .Where(f => SidecarReader.ProcessOf(f) == process)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (files.Count > 0)
            {
                return [.. files.Select(f => Startup.GetOrAdd(f, ReadStartup)).OfType<(string, string)>()];
            }
        }

        return [];
    }

    private static (string Configured, string Effective)? ReadStartup(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (var i = 0; i < StartupLines && reader.ReadLine() is { } line; i++)
            {
                if (!line.Contains("Monster motion: configured", StringComparison.Ordinal))
                {
                    continue;
                }

                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("p", out var p) && p.TryGetProperty("Configured", out var configured) && p.TryGetProperty("Effective", out var effective))
                {
                    return (configured.GetString(), effective.GetString());
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return null;
    }
}
