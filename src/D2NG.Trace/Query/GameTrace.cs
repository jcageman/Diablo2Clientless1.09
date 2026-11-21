using D2NG.Core;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.Trace.Query;

/// <summary>One .d2trace of a client in a game; a rejoin starts a new one (part 2, 3...).</summary>
public sealed class ClientFile
{
    public ClientFile(string path, TraceHeader header, string serverGame = null, DateTime? endUtc = null)
    {
        Path = path;
        Header = header;
        ServerGame = serverGame ?? header.ServerGame;
        EndUtc = endUtc ?? header.StartUtc;
    }

    public string Path { get; }

    public TraceHeader Header { get; }

    public string Label => Header.ClientLabel;

    public int Part { get; internal set; } = 1;

    /// <summary>The game server's id for the game: from the header, or for older files "#token" from the logon packet; null when neither is there.</summary>
    public string ServerGame { get; }

    /// <summary>The start plus the elapsed time of the last record.</summary>
    public DateTime EndUtc { get; }

    /// <summary>A file's header, game server id and end, read in one pass and kept for the process (<see cref="TraceFileSummary"/>).</summary>
    public static ClientFile Read(string path) => Read(new FileInfo(path));

    /// <param name="info">The file as a directory listing found it.</param>
    public static ClientFile Read(FileInfo info)
    {
        var summary = TraceFileSummary.Of(info);
        return new ClientFile(summary.Path, summary.Header, summary.ServerGame, summary.EndUtc);
    }

    /// <summary>The token part of the id, which is all an older file carries.</summary>
    internal static string Token(string serverGame)
    {
        var hash = serverGame.LastIndexOf('#');
        return hash < 0 ? serverGame : serverGame[(hash + 1)..];
    }

    /// <summary>Full ids compare whole; when either side carries only the token, tokens compare.</summary>
    internal static bool SameServerGame(string a, string b)
        => !a.StartsWith('#') && !b.StartsWith('#') ? a == b : Token(a) == Token(b);
}

/// <summary>
/// A game: the files under &lt;root&gt;/&lt;yyyyMMdd&gt;/ that belong to one game on the server - one trace
/// per client (the labels are the file names), a rejoin adding parts - with the process sidecars and
/// realm traces beside it. Offsets are measured from the earliest client start.
///
/// Game names repeat across bot restarts, and folders written before the recorder kept them apart hold
/// several games. <see cref="OpenSet"/> reads a game name's folders together and splits them: files
/// whose game server ids differ are different games; without ids, a file starting after every file of
/// the earlier set has ended starts a new game (a game on the server closes when its last player
/// leaves). Such games are addressed as &lt;folder&gt;#1, #2... in start order across the name's folders.
/// </summary>
public sealed class GameTrace
{
    /// <summary>Joining the same server game id this long after the last file ended is a reused id, not a rejoin.</summary>
    public static readonly TimeSpan SameIdRejoinLimit = TimeSpan.FromMinutes(5);

    /// <summary>Without ids: how long after the last record of a set a file may start and still be a rejoin.</summary>
    public static readonly TimeSpan NoIdRejoinGrace = TimeSpan.FromSeconds(10);

    private GameTrace(string folder, List<ClientFile> files, List<(string Path, string Reason)> skipped, int index, int count)
    {
        Folder = folder;
        SecondViews = SecondViewsOf(files);
        files = [.. files.Except(SecondViews)];
        Files = files;
        Skipped = skipped;
        Index = index;
        GameCount = count;
        GameName = files.Select(f => f.Header.GameName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? System.IO.Path.GetFileName(folder);
        StartUtc = files.Min(f => f.Header.StartUtc);
        EndUtc = files.Max(f => f.EndUtc);
        if (EndUtc < StartUtc)
        {
            EndUtc = StartUtc;
        }

        Labels = [.. files.GroupBy(f => f.Label, StringComparer.Ordinal).OrderBy(g => g.Min(f => f.Header.StartUtc)).Select(g => g.Key)];
        foreach (var group in files.GroupBy(f => f.Label, StringComparer.Ordinal))
        {
            var part = 1;
            foreach (var file in group.OrderBy(f => f.Header.StartUtc))
            {
                file.Part = part++;
            }
        }
    }

    public string Folder { get; }

    /// <summary>The folder of the game's earliest file.</summary>
    /// <remarks>Its other files may sit in a sibling folder; see <see cref="OpenSet"/>.</remarks>
    /// <summary>Which game of its folder's sibling set this is, from 1 in start order.</summary>
    public int Index { get; }

    /// <summary>How many games the folder's sibling set holds.</summary>
    public int GameCount { get; }

    /// <summary>How to name this game on a command line: the folder, with #N when the folder holds several games.</summary>
    public string Id => GameCount > 1 ? $"{Folder}#{Index}" : Folder;

    public string GameName { get; }

    public DateTime StartUtc { get; }

    /// <summary>When the last record of the game was written.</summary>
    public DateTime EndUtc { get; }

    public IReadOnlyList<ClientFile> Files { get; }

    /// <summary>
    /// Files of a client that another source also recorded at the same time - a sniffer capturing a bot writes the
    /// bot's game a second time - left out of <see cref="Files"/>: they are the same client seen twice, not a rejoin.
    /// </summary>
    public IReadOnlyList<ClientFile> SecondViews { get; }

    /// <summary>
    /// For each client recorded by more than one source, the files of the sources other than the primary one - the
    /// bot's own trace, else the earliest source - that overlap a primary file in time.
    /// </summary>
    private static List<ClientFile> SecondViewsOf(List<ClientFile> files)
    {
        var views = new List<ClientFile>();
        foreach (var client in files.GroupBy(f => f.Label, StringComparer.Ordinal))
        {
            if (client.Select(f => f.Header.Source).Distinct().Count() < 2)
            {
                continue;
            }

            var primary = client.Any(f => f.Header.Source == TraceSource.Bot) ? TraceSource.Bot : client.MinBy(f => f.Header.StartUtc).Header.Source;
            var primaries = client.Where(f => f.Header.Source == primary).ToList();
            views.AddRange(client.Where(f => f.Header.Source != primary
                && primaries.Any(p => f.Header.StartUtc <= p.EndUtc + NoIdRejoinGrace && p.Header.StartUtc <= f.EndUtc + NoIdRejoinGrace)));
        }

        return views;
    }

    public IReadOnlyList<string> Labels { get; }

    /// <summary>Trace files in the folder that could not be read, left out of <see cref="Files"/>.</summary>
    public IReadOnlyList<(string Path, string Reason)> Skipped { get; }

    public string DateFolder => System.IO.Path.GetDirectoryName(Folder);

    public static readonly TimeSpan StillWriting = TimeSpan.FromMinutes(1);

    /// <summary>A game still being recorded: one of its trace files was written within the last minute.</summary>
    public bool InProgress => Files.Any(f => File.Exists(f.Path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(f.Path) < StillWriting);

    public TimeSpan Offset(DateTime utc) => utc - StartUtc;

    public IEnumerable<ClientFile> FilesOf(string label) => Files.Where(f => f.Label == label).OrderBy(f => f.Part);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _replays = new(StringComparer.Ordinal);

    /// <summary>How many times each client's replay was started on this game; a command needs one pass per client.</summary>
    public IReadOnlyDictionary<string, int> Replays => _replays;

    internal void CountReplay(string label) => _replays.AddOrUpdate(label, 1, (_, count) => count + 1);

    public static bool IsGameFolder(string folder)
        => Directory.Exists(folder)
            && !string.Equals(System.IO.Path.GetFileName(folder), "realm", StringComparison.OrdinalIgnoreCase)
            && Directory.EnumerateFiles(folder, "*" + TraceFormat.Extension).Any();

    /// <summary>A folder, or folder#N for one game of a folder holding several.</summary>
    public static (string Folder, int? Index) ParseTarget(string target)
    {
        var full = System.IO.Path.GetFullPath(target);
        if (Directory.Exists(full))
        {
            return (full, null);
        }

        var hash = target.LastIndexOf('#');
        if (hash > 0 && int.TryParse(target.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return (System.IO.Path.GetFullPath(target[..hash]), index);
        }

        return (full, null);
    }

    public static bool IsGameTarget(string target) => IsGameFolder(ParseTarget(target).Folder);

    /// <summary>
    /// One game: a folder whose files belong to exactly one game, or folder#N - the Nth game, in start
    /// order, of the folder's sibling set (see <see cref="OpenSet"/>), whichever sibling is named.
    /// </summary>
    public static GameTrace Open(string target)
    {
        var (folder, index) = ParseTarget(target);
        folder = System.IO.Path.GetFullPath(folder);
        var set = OpenSet(folder);
        if (index != null)
        {
            var chosen = set.FirstOrDefault(g => g.Index == index)
                ?? throw new ArgumentException($"{folder} and its sibling folders hold {set.Count} game{(set.Count == 1 ? "" : "s")} that started that day; there is no #{index}");
            if (!chosen.Files.Any(f => SameFolder(f.Path, folder)))
            {
                var here = set.Where(g => g.Files.Any(f => SameFolder(f.Path, folder))).ToList();
                throw new ArgumentException($"game #{index} of {System.IO.Path.GetFileName(folder)} and its sibling folders has no file in {folder}; "
                    + (here.Count == 0 ? "no game has." : $"the games with files there are:{Environment.NewLine}{string.Join(Environment.NewLine, here.Select(g => $"{Describe(g)}: {System.IO.Path.GetFileName(folder)}#{g.Index}"))}"));
            }

            return chosen;
        }

        var games = set.Where(g => g.Files.Any(f => SameFolder(f.Path, folder))).ToList();
        if (games.Count == 0)
        {
            throw new InvalidDataException($"the files in {folder} are the after-midnight part of a game that started the day before; open it from the same-named folder of the previous date folder");
        }

        if (games.Count > 1)
        {
            throw new ArgumentException($"{folder} holds {games.Count} games of the same name; add #N to the folder to pick one (#N counts the games of the folder and its {System.IO.Path.GetFileName(folder)}~* siblings in start order):{Environment.NewLine}"
                + string.Join(Environment.NewLine, games.Select(Describe)));
        }

        return games[0];
    }

    public static string Describe(GameTrace game)
        => $"  #{game.Index}  started {game.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {(game.EndUtc - game.StartUtc).TotalMinutes:0.0} min, {game.Labels.Count} client{(game.Labels.Count == 1 ? "" : "s")}";

    /// <summary>How close to midnight a game must start or end for its files to be looked for in the date folder beside its own.</summary>
    public static readonly TimeSpan MidnightReach = TimeSpan.FromMinutes(15);

    /// <summary>The games with a file in a folder, oldest first; see <see cref="OpenSet"/>.</summary>
    public static List<GameTrace> OpenAll(string folder)
    {
        folder = System.IO.Path.GetFullPath(folder);
        return [.. OpenSet(folder).Where(g => g.Files.Any(f => SameFolder(f.Path, folder)))];
    }

    /// <summary>
    /// Every game of a folder's sibling set, oldest first, numbered #1, #2... in start order across the
    /// set. The siblings are the folders of one game name in one date folder: &lt;name&gt;, and the
    /// &lt;name&gt;~N the recorder claims for a later game of the same name, or &lt;name&gt;~&lt;8 hex&gt; it
    /// falls back to when a claim fails - so one game's files can sit in two of them. Files are grouped
    /// into games by game server id wherever they landed (see <see cref="Split"/>). A game that ran over
    /// midnight has its later parts in the sibling set of the next date folder (in &lt;name&gt; or
    /// &lt;name&gt;~N, whichever the rejoin claimed): those files join the game here, and the next day
    /// leaves the game out, since it belongs to the day it started. A game's folder is the folder of its
    /// earliest file.
    /// </summary>
    public static List<GameTrace> OpenSet(string folder)
    {
        using var _ = Profile.Time("open: game set");
        folder = System.IO.Path.GetFullPath(folder);
        var dateFolder = System.IO.Path.GetDirectoryName(folder);
        var name = BaseName(System.IO.Path.GetFileName(folder));
        var siblings = dateFolder == null ? [folder] : Siblings(dateFolder, name);
        if (!siblings.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            siblings.Add(folder);
        }

        var skipped = new List<(string Path, string Reason)>();
        var files = new List<ClientFile>();
        var here = new List<(string Path, string Reason)>();
        foreach (var sibling in siblings)
        {
            files.AddRange(ReadFolder(sibling, string.Equals(sibling, folder, StringComparison.OrdinalIgnoreCase) ? here : skipped));
        }

        skipped.AddRange(here);
        if (!files.Any(f => SameFolder(f.Path, folder)))
        {
            throw new InvalidDataException(here.Count == 0
                ? $"{folder} holds no {TraceFormat.Extension} files"
                : $"{folder} holds no readable {TraceFormat.Extension} files ({string.Join("; ", here.Select(s => $"{System.IO.Path.GetFileName(s.Path)}: {s.Reason}"))})");
        }

        var all = new List<ClientFile>(files);
        foreach (var beside in FoldersAcrossMidnight(folder, name, files))
        {
            all.AddRange(ReadFolder(beside, []));
        }

        bool InSet(ClientFile file) => siblings.Any(s => SameFolder(file.Path, s));
        var groups = Split(all).Where(g => g.Any(InSet) && InSet(g.MinBy(f => f.Header.StartUtc))).ToList();
        return [.. groups.Select((g, i) => new GameTrace(System.IO.Path.GetDirectoryName(g.MinBy(f => f.Header.StartUtc).Path), g, skipped, i + 1, groups.Count))];
    }

    /// <summary>The game name a folder is for: its name without a ~N or ~&lt;8 hex&gt; suffix.</summary>
    public static string BaseName(string folderName)
    {
        var tilde = folderName.LastIndexOf('~');
        if (tilde <= 0)
        {
            return folderName;
        }

        var suffix = folderName.AsSpan(tilde + 1);
        var numbered = suffix.Length > 0 && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        var fallback = suffix.Length == 8 && uint.TryParse(suffix, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
        return numbered || fallback ? folderName[..tilde] : folderName;
    }

    /// <summary>The game folders of one game name in a date folder, in name order.</summary>
    public static List<string> Siblings(string dateFolder, string name)
        => Directory.Exists(dateFolder)
            ? [.. Directory.GetDirectories(dateFolder)
                .Where(d => string.Equals(BaseName(System.IO.Path.GetFileName(d)), name, StringComparison.OrdinalIgnoreCase) && IsGameFolder(d))
                .OrderBy(d => d, StringComparer.Ordinal)]
            : [];

    private static bool SameFolder(string path, string folder)
        => string.Equals(System.IO.Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase);

    private static List<ClientFile> ReadFolder(string folder, List<(string Path, string Reason)> skipped)
    {
        var listed = new DirectoryInfo(folder).GetFiles("*" + TraceFormat.Extension).OrderBy(f => f.FullName, StringComparer.Ordinal).ToArray();
        var paths = listed.Select(f => f.FullName).ToArray();
        var read = new (ClientFile File, Exception Error)[paths.Length];
        Parallel.For(0, paths.Length, i =>
        {
            try
            {
                read[i] = (ClientFile.Read(listed[i]), null);
            }
            catch (Exception e)
            {
                read[i] = (null, e);
            }
        });
        var files = new List<ClientFile>();
        for (var i = 0; i < paths.Length; i++)
        {
            if (read[i].File != null)
            {
                files.Add(read[i].File);
            }
            else if (read[i].Error is IOException or UnauthorizedAccessException)
            {
                skipped.Add((paths[i], read[i].Error.Message));
            }
            else
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(read[i].Error);
            }
        }

        return files;
    }

    /// <summary>The sibling folders of the game name in the previous and next date folder, when a file here starts or ends within <see cref="MidnightReach"/> of midnight.</summary>
    private static List<string> FoldersAcrossMidnight(string folder, string name, List<ClientFile> files)
    {
        var dateFolder = System.IO.Path.GetDirectoryName(folder);
        var root = dateFolder == null ? null : System.IO.Path.GetDirectoryName(dateFolder);
        if (root == null || !IsDateFolder(dateFolder))
        {
            return [];
        }

        var day = DateTime.ParseExact(System.IO.Path.GetFileName(dateFolder), "yyyyMMdd", CultureInfo.InvariantCulture);
        var found = new List<string>();
        if (files.Any(f => f.EndUtc.ToLocalTime() >= day.AddDays(1) - MidnightReach))
        {
            found.AddRange(Siblings(System.IO.Path.Combine(root, day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)), name));
        }

        if (files.Any(f => f.Header.StartUtc.ToLocalTime() < day + MidnightReach))
        {
            found.AddRange(Siblings(System.IO.Path.Combine(root, day.AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)), name));
        }

        return found;
    }

    /// <summary>The files grouped into games, in start order.</summary>
    public static List<List<ClientFile>> Split(IEnumerable<ClientFile> files)
    {
        var groups = new List<(List<ClientFile> Files, DateTime End)>();
        foreach (var file in files.OrderBy(f => f.Header.StartUtc))
        {
            var start = file.Header.StartUtc;
            var index = groups.FindLastIndex(g =>
            {
                var known = g.Files.Select(f => f.ServerGame).LastOrDefault(id => id != null);
                return known != null && file.ServerGame != null
                    ? ClientFile.SameServerGame(known, file.ServerGame) && start <= g.End + SameIdRejoinLimit
                    : start <= g.End + NoIdRejoinGrace;
            });
            if (index < 0)
            {
                groups.Add(([file], file.EndUtc));
                continue;
            }

            var group = groups[index];
            group.Files.Add(file);
            groups[index] = (group.Files, file.EndUtc > group.End ? file.EndUtc : group.End);
        }

        return [.. groups.Select(g => g.Files)];
    }

    /// <summary>The games under a game, date or root folder, oldest first.</summary>
    public static List<GameTrace> GamesUnder(string target, Action<string, string> skippedFolder = null)
    {
        var (folder, index) = ParseTarget(target);
        if (IsGameFolder(folder))
        {
            return index == null ? OpenAll(folder) : [Open(target)];
        }

        var opened = new HashSet<(string, string)>();
        var sets = GameFoldersUnder(folder).Where(game => opened.Add((System.IO.Path.GetDirectoryName(game).ToUpperInvariant(), BaseName(System.IO.Path.GetFileName(game)).ToUpperInvariant()))).ToList();
        var results = new (List<GameTrace> Games, Exception Error)[sets.Count];
        Parallel.For(0, sets.Count, i =>
        {
            try
            {
                results[i] = (OpenSet(sets[i]), null);
            }
            catch (Exception e)
            {
                results[i] = (null, e);
            }
        });
        var games = new List<GameTrace>();
        for (var i = 0; i < sets.Count; i++)
        {
            if (results[i].Error is InvalidDataException error)
            {
                skippedFolder?.Invoke(sets[i], error.Message);
            }
            else if (results[i].Error is { } other)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(other);
            }
            else
            {
                games.AddRange(results[i].Games);
            }
        }

        return [.. games.OrderBy(g => g.StartUtc)];
    }

    /// <summary>The game folders under a game, date or root folder, oldest first.</summary>
    public static List<string> GameFoldersUnder(string folder)
    {
        folder = System.IO.Path.GetFullPath(folder);
        if (IsGameFolder(folder))
        {
            return [folder];
        }

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"{folder} does not exist");
        }

        var found = new List<string>();
        foreach (var child in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (IsGameFolder(child))
            {
                found.Add(child);
            }
            else if (IsDateFolder(child))
            {
                found.AddRange(Directory.GetDirectories(child).Where(IsGameFolder).OrderBy(d => d, StringComparer.Ordinal));
            }
        }

        return found;
    }

    public static bool IsDateFolder(string folder)
        => DateTime.TryParseExact(System.IO.Path.GetFileName(folder), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>The label of the client a name means: exact, then case-insensitive, then a unique prefix.</summary>
    public string ResolveClient(string name)
    {
        if (name == null)
        {
            return null;
        }

        var exact = Labels.FirstOrDefault(l => l == name) ?? Labels.FirstOrDefault(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var prefixed = Labels.Where(l => l.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefixed.Count == 1
            ? prefixed[0]
            : throw new ArgumentException($"no single client '{name}' in {GameName}; clients: {string.Join(", ", Labels)}");
    }

    /// <summary>Sibling date folders within a day of the game: sidecars and realm traces of a game near midnight sit in either.</summary>
    public IEnumerable<string> NearbyDateFolders()
    {
        var root = System.IO.Path.GetDirectoryName(DateFolder);
        if (root == null || !IsDateFolder(DateFolder))
        {
            yield return DateFolder;
            yield break;
        }

        var date = DateTime.ParseExact(System.IO.Path.GetFileName(DateFolder), "yyyyMMdd", CultureInfo.InvariantCulture);
        foreach (var day in new[] { date.AddDays(-1), date, date.AddDays(1) })
        {
            var candidate = System.IO.Path.Combine(root, day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    public IEnumerable<string> SidecarFiles() => NearbyDateFolders().SelectMany(TraceSources.SidecarFilesIn).OrderBy(p => p, StringComparer.Ordinal);

    public IEnumerable<string> RealmFiles() => NearbyDateFolders().SelectMany(TraceSources.RealmFilesIn).OrderBy(p => p, StringComparer.Ordinal);
}

/// <summary>
/// Where the item tables are, so item packets parse: --datadir, then D2NG_DATADIR, then a data folder
/// holding item_data.txt in the trace folder or any folder above it.
/// </summary>
public static class TraceDataLocation
{
    public const string EnvironmentVariable = "D2NG_DATADIR";

    public static string Find(string explicitDirectory, string startFolder)
    {
        if (!string.IsNullOrEmpty(explicitDirectory))
        {
            return explicitDirectory;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var folder = startFolder == null ? null : new DirectoryInfo(GameTrace.ParseTarget(startFolder).Folder);
        while (folder != null)
        {
            var candidate = System.IO.Path.Combine(folder.FullName, "data");
            if (File.Exists(System.IO.Path.Combine(candidate, GameDataLocation.ItemDataFile)))
            {
                return candidate;
            }

            folder = folder.Parent;
        }

        return null;
    }

    /// <summary>Points the item parser at the data; false when none was found and the default is all there is.</summary>
    public static bool Use(string explicitDirectory, string startFolder)
    {
        var directory = Find(explicitDirectory, startFolder);
        if (directory != null)
        {
            GameDataLocation.Use(directory);
        }

        return GameDataLocation.MissingRequiredFiles().Count == 0;
    }
}
