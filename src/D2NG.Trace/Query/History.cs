using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace D2NG.Trace.Query;

/// <summary>
/// What earlier games of the same bot looked like, for the checks that compare across runs: step
/// durations and the warning templates each game logged. The earlier games are read from the trace
/// folders themselves - games of the same bot (the game name without its trailing number, sharing a
/// client) that started before this one, at most <see cref="Days"/> days before - so the answer never
/// depends on which games the tool happened to look at before. The file is only a cache of what each
/// game's record is, keyed by the game's identity: deleting it changes no answer, a corrupt one is
/// ignored, and it is written whole to a temporary file and renamed into place.
/// </summary>
public sealed class TraceHistory
{
    public const string FileName = "trace-history.json";

    /// <summary>How far back the earlier games of a bot are looked for.</summary>
    public const int Days = 7;

    /// <summary>How many earlier games the slow-step and new-warning checks need before they say anything.</summary>
    public const int MinimumGames = 10;

    /// <summary>Changes whenever what a record holds or how it is computed changes, so an older cache is not reused.</summary>
    private const int Version = 4;

    /// <summary>A game this recent may still be written to, or have a next game that moves its window: it is not cached.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;
    private readonly bool _corrupt;
    private readonly ConcurrentDictionary<string, GameRecord> _cached;
    private readonly ConcurrentDictionary<string, GameRecord> _computed = new(StringComparer.Ordinal);
    private bool _changed;

    private TraceHistory(string path, Dictionary<string, GameRecord> games, bool corrupt)
    {
        _path = path;
        _corrupt = corrupt;
        _cached = new ConcurrentDictionary<string, GameRecord>(games, StringComparer.Ordinal);
    }

    public sealed class GameRecord
    {
        public DateTime Start { get; set; }

        public string Bot { get; set; }

        public List<string> Clients { get; set; } = [];

        public Dictionary<string, List<double>> Steps { get; set; } = [];

        public List<string> Warnings { get; set; } = [];

        /// <summary>Per outgoing packet type ("0x4B"), the median sends per minute of the client that sent it most (<see cref="Query.SendRates.OfGame"/>).</summary>
        public Dictionary<string, double> SendRates { get; set; } = [];
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }

        public Dictionary<string, GameRecord> Games { get; set; } = [];
    }

    public string Path => _path;

    /// <summary>Set when the cache file could not be read; it is then left alone.</summary>
    public string Problem { get; private set; }

    /// <summary>A history with no cache file.</summary>
    public static TraceHistory InMemory() => new(null, [], false);

    public static TraceHistory Load(string path)
    {
        if (path == null || !File.Exists(path))
        {
            return new TraceHistory(path, [], false);
        }

        try
        {
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), Options);
            return new TraceHistory(path, file?.Version == Version ? file.Games ?? [] : [], false);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new TraceHistory(path, [], true) { Problem = $"the history cache {path} could not be read ({e.Message.Split('\n')[0].Trim()}); ignored and left as it is - delete it to rebuild it" };
        }
    }

    /// <summary>The per-user cache: %LOCALAPPDATA%\D2NG\Trace\trace-history.json, or null when there is no such folder.</summary>
    public static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : System.IO.Path.Combine(local, "D2NG", "Trace", FileName);
    }

    public static string BotOf(string gameName) => (gameName ?? "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    public static string Key(GameTrace game) => GameQuery.Identity(game);

    /// <summary>What a game contributes to later comparisons: the seconds of its steps that finished ok with a measured start, its warning templates and its send rates.</summary>
    public static GameRecord Compute(GameQuery query, IReadOnlyList<StepSpan> steps = null)
    {
        steps ??= Query.Steps.Of(query);
        return new GameRecord
        {
            Start = query.Game.StartUtc,
            Bot = BotOf(query.Game.GameName),
            Clients = [.. query.Game.Labels],
            Steps = steps.Where(s => s.Outcome == "ok" && !s.StartInferred).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Select(s => Math.Round(s.Duration.TotalSeconds, 1)).ToList()),
            Warnings = [.. query.GameEntries().Where(e => e.IsLog && e.Level is "Warning" or "Error" or "Fatal").Select(HotLoopCheck.Template).Distinct()],
            SendRates = Query.SendRates.OfGame(query.Game),
        };
    }

    /// <summary>
    /// The games of the same bot under the same trace root that started before this one, within
    /// <see cref="Days"/> days: same name without its number, at least one client in common.
    /// </summary>
    public static List<GameTrace> EarlierGames(GameQuery query)
    {
        var game = query.Game;
        var bot = BotOf(game.GameName);
        var folders = new List<string>();
        var dateFolder = game.DateFolder;
        var root = System.IO.Path.GetDirectoryName(dateFolder);
        if (root != null && GameTrace.IsDateFolder(dateFolder) && Directory.Exists(root))
        {
            var day = DateTime.ParseExact(System.IO.Path.GetFileName(dateFolder), "yyyyMMdd", CultureInfo.InvariantCulture);
            for (var back = Days; back >= -1; back--)
            {
                var candidate = System.IO.Path.Combine(root, day.AddDays(-back).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                if (Directory.Exists(candidate))
                {
                    folders.Add(candidate);
                }
            }
        }

        var self = GameQuery.Identity(game);
        return [.. folders.SelectMany(query.Sources.GamesIn)
            .Where(g => g.StartUtc < game.StartUtc && g.StartUtc >= game.StartUtc.AddDays(-Days) && BotOf(g.GameName) == bot
                && g.Labels.Intersect(game.Labels, StringComparer.Ordinal).Any() && GameQuery.Identity(g) != self)
            .GroupBy(GameQuery.Identity).Select(g => g.First())
            .OrderBy(g => g.StartUtc)];
    }

    /// <summary>The records of the earlier games, from the cache where it holds them.</summary>
    public List<GameRecord> Earlier(GameQuery query)
    {
        var records = new List<GameRecord>();
        foreach (var game in EarlierGames(query))
        {
            var key = Key(game);
            if (_computed.TryGetValue(key, out var record) || _cached.TryGetValue(key, out record))
            {
                records.Add(record);
                continue;
            }

            record = Compute(query.Sources.QueryOf(game));
            _computed[key] = record;
            if (game.EndUtc < DateTime.UtcNow - Settled)
            {
                _cached[key] = record;
                _changed = true;
            }

            records.Add(record);
        }

        return records;
    }

    public void Save()
    {
        if (_path == null || !_changed || _corrupt)
        {
            return;
        }

        var temporary = _path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CacheFile { Version = Version, Games = new Dictionary<string, GameRecord>(_cached) }, Options));
            File.Move(temporary, _path, overwrite: true);
            _changed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not write the history cache to {_path}: {e.Message}");
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
