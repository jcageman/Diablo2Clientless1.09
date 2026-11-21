using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.Trace.Query;

/// <summary>
/// The sidecars and realm traces of date folders. A batch command shares one so every file is read once
/// and each game takes its time window from memory; a single game reads only its window.
/// </summary>
public sealed class TraceSources
{
    private readonly Dictionary<string, RealmTraces> _realm = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public static IEnumerable<string> SidecarFilesIn(string dateFolder)
        => Directory.Exists(dateFolder) ? Directory.GetFiles(dateFolder, TraceSidecar.FilePrefix + "*" + TraceSidecar.Extension) : [];

    public static IEnumerable<string> RealmFilesIn(string dateFolder)
    {
        var realm = Path.Combine(dateFolder, "realm");
        return Directory.Exists(realm) ? Directory.GetFiles(realm, "*" + TraceFormat.Extension) : [];
    }

    public static List<SidecarEntry> Sidecar(IEnumerable<string> files, DateTime fromUtc, DateTime toUtc)
    {
        var entries = new List<SidecarEntry>();
        foreach (var file in files)
        {
            if (!SidecarReader.MayHold(file, fromUtc, toUtc))
            {
                continue;
            }

            var all = SidecarReader.File(file).Sorted();
            for (var i = LowerBound(all, fromUtc); i < all.Count && all[i].Utc <= toUtc; i++)
            {
                entries.Add(all[i]);
            }
        }

        entries.Sort(SidecarReader.ByTime);
        return entries;
    }

    public RealmTraces Realm(IEnumerable<string> files)
    {
        var parts = new List<RealmTraces>();
        foreach (var file in files)
        {
            lock (_gate)
            {
                if (!_realm.TryGetValue(file, out var realm))
                {
                    realm = RealmTraces.Read([file]);
                    _realm[file] = realm;
                }

                parts.Add(realm);
            }
        }

        return RealmTraces.Merge(parts);
    }

    private readonly Dictionary<string, List<GameTrace>> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameQuery> _queries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The games of a date folder, opened once.</summary>
    public List<GameTrace> GamesIn(string dateFolder)
    {
        lock (_gate)
        {
            if (_games.TryGetValue(dateFolder, out var games))
            {
                return games;
            }
        }

        List<GameTrace> found;
        try
        {
            found = GameTrace.IsDateFolder(dateFolder) && Directory.Exists(dateFolder) ? GameTrace.GamesUnder(dateFolder) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            found = [];
        }

        lock (_gate)
        {
            return _games.TryAdd(dateFolder, found) ? found : _games[dateFolder];
        }
    }

    /// <summary>The query of a game sharing these sources, one per game.</summary>
    public GameQuery QueryOf(GameTrace game)
    {
        lock (_gate)
        {
            var key = GameQuery.Identity(game);
            if (!_queries.TryGetValue(key, out var query))
            {
                query = new GameQuery(game, this);
                _queries[key] = query;
            }

            return query;
        }
    }

    private static int LowerBound(List<SidecarEntry> entries, DateTime utc)
    {
        int low = 0, high = entries.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (entries[middle].Utc < utc)
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
}

/// <summary>A game with what the queries share: its sidecar lines, its realm traces, the processes that played it and log attribution.</summary>
public sealed class GameQuery
{
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);

    /// <summary>Realm answers about a game count from this long before one of its files starts...</summary>
    public static readonly TimeSpan RealmBefore = TimeSpan.FromMinutes(2);

    /// <summary>...to this long after.</summary>
    public static readonly TimeSpan RealmAfter = TimeSpan.FromSeconds(30);

    private readonly TraceSources _sources;
    private List<SidecarEntry> _sidecar;
    private RealmTraces _realm;
    private Attribution _attribution;
    private HashSet<string> _processes;
    private List<SidecarEntry> _entries;
    private TraceSources _neighbours;

    public GameQuery(GameTrace game, TraceSources sources = null)
    {
        Game = game;
        _sources = sources;
    }

    public GameTrace Game { get; }

    /// <summary>The shared sources, or for a game opened alone, its own for the games around it.</summary>
    public TraceSources Sources => _sources ?? (_neighbours ??= new TraceSources());

    public static GameQuery Open(string target) => new(GameTrace.Open(target));

    /// <summary>Every sidecar line in the game's time window, from any process.</summary>
    public List<SidecarEntry> Sidecar => _sidecar ??= _sources != null
        ? TraceSources.Sidecar(Game.SidecarFiles(), Game.StartUtc - Margin, Game.EndUtc + Margin)
        : SidecarReader.Read(Game.SidecarFiles(), Game.StartUtc - Margin, Game.EndUtc + Margin);

    /// <summary>The lines of <see cref="Sidecar"/> in no particular order, for answers that are sets; read without sorting when the sidecar was not needed yet.</summary>
    private List<SidecarEntry> SidecarUnordered()
    {
        if (_sidecar != null)
        {
            return _sidecar;
        }

        var entries = new List<SidecarEntry>();
        foreach (var file in Game.SidecarFiles())
        {
            if (SidecarReader.MayHold(file, Game.StartUtc - Margin, Game.EndUtc + Margin))
            {
                SidecarReader.File(file).AddWindow(entries, Game.StartUtc - Margin, Game.EndUtc + Margin);
            }
        }

        return entries;
    }

    public RealmTraces Realm => _realm ??= _sources != null ? _sources.Realm(Game.RealmFiles()) : RealmTraces.Read(Game.RealmFiles());

    public Attribution Attribution => _attribution ??= new Attribution(Game.Labels, Realm.Characters);

    /// <summary>Whether a realm answer falls in the window around one of the game's file starts.</summary>
    public bool NearJoin(DateTime utc) => Game.Files.Any(f => utc >= f.Header.StartUtc - RealmBefore && utc <= f.Header.StartUtc + RealmAfter);

    /// <summary>
    /// The processes (sidecar and realm file suffix, HHmmss) that played this game: their typed events
    /// name it in its time window, or their realm traces joined a game of its name just before one of its
    /// files started.
    /// </summary>
    public IReadOnlySet<string> Processes => _processes ??= FindProcesses();

    private HashSet<string> FindProcesses()
    {
        var found = SidecarUnordered().Where(e => !e.IsLog && e.Kind is not ("gap" or "trace-failed") && e.Process != null && string.Equals(e.Game, Game.GameName, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Process).ToHashSet(StringComparer.Ordinal);
        foreach (var answer in Realm.AnswersFor(Game.GameName))
        {
            if (!answer.Refused && answer.Request == "join" && NearJoin(answer.Utc))
            {
                found.Add(answer.Process);
            }
        }

        return found;
    }

    /// <summary>The game's realm answers: its name, near one of its file starts, and from one of its processes when any is known.</summary>
    public IEnumerable<RealmAnswer> RealmAnswers()
        => Realm.AnswersFor(Game.GameName).Where(a => NearJoin(a.Utc) && (Processes.Count == 0 || Processes.Contains(a.Process)));

    /// <summary>
    /// Typed events of this game, and the log and gap lines of its processes, each within its process's
    /// window: the game's time span widened by a 10 s margin on both sides, but never back past the end of
    /// that process's previous game nor on past the end of this one when its next game starts within the
    /// margin, so no line belongs to two games. A typed event that names this game is kept up to the end
    /// of the margin or the next game's start, whichever comes first (the sidecar is read no further): the
    /// bot ends its run step after the game's last packet (a connection the server closed, a long town
    /// trip). When the next game has the same name, events naming it stop at this game's end, so one
    /// event cannot land in both.
    /// </summary>
    public IReadOnlyList<SidecarEntry> GameEntries()
    {
        if (_entries != null)
        {
            return _entries;
        }

        using var phase = Profile.Time("sidecar: game entries (incl. read)");
        Prefetch(
            () => Parallel.ForEach(Game.NearbyDateFolders().ToList(), folder => Sources.GamesIn(folder)),
            () => SidecarReader.Prefetch(Game.SidecarFiles(), Game.StartUtc - Margin, Game.EndUtc + Margin),
            () => _ = Realm);
        var processes = Processes;
        var windows = processes.ToDictionary(p => p, Window, StringComparer.Ordinal);
        bool InWindow(SidecarEntry e) => e.Process == null || !windows.TryGetValue(e.Process, out var w) || (e.Utc >= w.From && e.Utc < w.To);
        bool InNamedWindow(SidecarEntry e) => e.Process == null || !windows.TryGetValue(e.Process, out var w) || (e.Utc >= w.From && e.Utc < w.NamedTo);
        return _entries = [.. Sidecar.Where(e => e.IsLog || e.Kind is "gap" or "trace-failed"
            ? e.Process != null && processes.Contains(e.Process) && InWindow(e)
            : string.Equals(e.Game, Game.GameName, StringComparison.OrdinalIgnoreCase)
                ? InNamedWindow(e)
                : ((e.Game == null && e.Client != null && Game.Labels.Contains(e.Client) && processes.Contains(e.Process))
                    || (e.Game == null && e.Client == null && e.Process != null && processes.Contains(e.Process) && IsStopRequest(e))) && InWindow(e))];
    }

    public const string StopRequest = "stop requested";

    /// <summary>The bot's typed event that a stop was asked for (a stop file, a stranger, the game limit); it names no game or client, only its process.</summary>
    public static bool IsStopRequest(SidecarEntry entry) => entry.Kind == "decision" && entry.Str("name") == StopRequest;

    /// <summary>When a stop was first asked for during this game, or null.</summary>
    public TimeSpan? StopRequestedAt()
        => GameEntries().FirstOrDefault(IsStopRequest) is { } stop ? Offset(stop) : null;

    /// <summary>Reads ahead side by side; a failure is left for the read that needs the data to raise, as it would have without this.</summary>
    private static void Prefetch(params Action[] reads)
        => Parallel.ForEach(reads, read =>
        {
            try
            {
                read();
            }
            catch (Exception)
            {
            }
        });

    /// <summary>A game's identity across commands: its name and the start of its first file.</summary>
    public static string Identity(GameTrace game) => $"{game.GameName}@{game.StartUtc:yyyy-MM-ddTHH:mm:ss.fffZ}";

    /// <summary>The span of a process's lines that belong to this game, and how far a typed event naming it may come; see <see cref="GameEntries"/>.</summary>
    public (DateTime From, DateTime To, DateTime NamedTo) Window(string process)
    {
        var from = Game.StartUtc - Margin;
        var to = Game.EndUtc + Margin;
        var namedTo = to;
        var sources = Sources;
        var self = Identity(Game);
        foreach (var other in Game.NearbyDateFolders().SelectMany(sources.GamesIn))
        {
            var before = other.StartUtc < Game.StartUtc;
            var clips = before ? other.EndUtc > from : other.StartUtc < to;
            if (!clips || Identity(other) == self || (other.StartUtc == Game.StartUtc && other.Folder == Game.Folder))
            {
                continue;
            }

            if (!sources.QueryOf(other).Processes.Contains(process))
            {
                continue;
            }

            if (before)
            {
                var cut = other.EndUtc < Game.StartUtc ? other.EndUtc : Game.StartUtc;
                from = cut > from ? cut : from;
            }
            else
            {
                var cut = Game.EndUtc < other.StartUtc ? Game.EndUtc : other.StartUtc;
                to = cut < to ? cut : to;
                var namedCut = string.Equals(other.GameName, Game.GameName, StringComparison.OrdinalIgnoreCase) ? cut : other.StartUtc;
                namedTo = namedCut < namedTo ? namedCut : namedTo;
            }
        }

        return (from, to, namedTo);
    }

    public TimeSpan Offset(SidecarEntry entry) => entry.Utc - Game.StartUtc;

    /// <summary>Replays the clients side by side, each with its own observers; returns them per client in label order.</summary>
    public List<(string Client, T Observers)> ReplayAll<T>(IEnumerable<string> clients, TimeSpan until, Func<T> observers, Func<T, IReadOnlyList<IReplayObserver>> list)
        => ReplayAll(clients, until, _ => observers(), list);

    public List<(string Client, T Observers)> ReplayAll<T>(IEnumerable<string> clients, TimeSpan until, Func<string, T> observers, Func<T, IReadOnlyList<IReplayObserver>> list)
    {
        var labels = clients.ToList();
        var sidecar = _entries == null ? Task.Run(GameEntries) : Task.CompletedTask;
        var results = new (string, T)[labels.Count];
        Parallel.For(0, labels.Count, i =>
        {
            var created = observers(labels[i]);
            var fed = list(created);
            var whole = until == TimeSpan.MaxValue;
            var spoiled = false;
            if (!(whole && DerivedCache.TryLoad(Game, labels[i], fed, out spoiled)))
            {
                if (spoiled)
                {
                    created = observers(labels[i]);
                    fed = list(created);
                }

                var context = ClientReplay.Run(Game, labels[i], until, fed);
                context?.Replayer.Dispose();
                if (whole)
                {
                    DerivedCache.Store(Game, labels[i], fed);
                }
            }

            results[i] = (labels[i], created);
        });
        sidecar.GetAwaiter().GetResult();
        return [.. results];
    }
}

/// <summary>Writes at most a set number of list lines and then says how many were left out and how to see them.</summary>
public sealed class CappedOutput(TextWriter writer, int limit, string narrowHint)
{
    private int _written;
    private int _left;

    public void Line(string text)
    {
        if (limit > 0 && _written >= limit)
        {
            _left++;
            return;
        }

        writer.WriteLine(text);
        _written++;
    }

    public void Finish()
    {
        if (_left > 0)
        {
            writer.WriteLine($"... {_left} more lines left out (limit {limit}); {narrowHint}, or --limit 0 for all");
        }
    }
}
