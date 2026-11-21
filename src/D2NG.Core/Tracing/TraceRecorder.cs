using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace D2NG.Core.Tracing;

public sealed class TraceRecorderOptions
{
    public string Root { get; init; }

    public TraceSource Source { get; init; } = TraceSource.Bot;

    public int BufferBytes { get; init; } = 256 * 1024;

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan DigestInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxPendingEvents { get; init; } = 100_000;

    internal bool BackgroundWriter { get; init; } = true;
}

/// <summary>
/// Records every packet of every client in the process to trace files under one root, from one
/// background writer thread. Clients find it through <see cref="Current"/>; while that is null nothing
/// is traced.
/// </summary>
public sealed class TraceRecorder : IDisposable
{
    private readonly TraceRecorderOptions _options;
    private readonly List<TraceChannel> _channels = [];
    private readonly object _drainLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _writer;
    private readonly string _realmSuffix;
    private volatile bool _stopping;

    public TraceRecorder(TraceRecorderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options?.Root);
        _options = options;
        Root = Path.GetFullPath(options.Root);
        var started = DateTime.Now;
        _realmSuffix = started.ToString("HHmmss", CultureInfo.InvariantCulture);
        Sidecar = new TraceSidecar(
            Path.Combine(Root, started.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            TraceSidecar.FilePrefix + _realmSuffix,
            options.MaxPendingEvents);
        Events = new TraceEventWriter(this);
        if (options.BackgroundWriter)
        {
            _writer = new Thread(WriteLoop) { Name = "Trace writer", IsBackground = true };
            _writer.Start();
        }
    }

    public static TraceRecorder Current { get; private set; }

    public string Root { get; }

    public TraceSource Source => _options.Source;

    /// <summary>The log lines and typed events of this process.</summary>
    public TraceSidecar Sidecar { get; }

    public TraceEventWriter Events { get; }

    public static TraceRecorder Start(TraceRecorderOptions options)
    {
        var recorder = new TraceRecorder(options);
        Current = recorder;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => recorder.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => recorder.FlushNow();
        return recorder;
    }

    /// <summary>
    /// A client's game server traffic for one game: &lt;root&gt;/&lt;date&gt;/&lt;game&gt;/&lt;label&gt;.d2trace, -2 and
    /// up when rejoining. With the game server's id for the game, a folder already holding another game
    /// of the same name is left alone and the game goes to &lt;game&gt;~2, ~3...
    /// </summary>
    public TraceChannel OpenGame(string gameName, string clientLabel, string serverGame = null)
    {
        var channel = Open(Path.Combine(DateFolder(), Safe(gameName)), Safe(clientLabel), clientLabel, gameName, serverGame);
        if (Settings.Count > 0)
        {
            Events.Settings(clientLabel, gameName, Settings);
        }

        return channel;
    }

    /// <summary>
    /// The settings that change how the bot behaves - its type, the monster motion model, its own switches - as key and
    /// value, written to the sidecar as a "settings" event each time a client enters a game. Empty until the bot sets it;
    /// traces of a bot that never did simply have none.
    /// </summary>
    public IReadOnlyList<(string Key, string Value)> Settings { get; set; } = [];

    /// <summary>A client's chat and realm traffic for this process: &lt;root&gt;/&lt;date&gt;/realm/&lt;label&gt;-&lt;HHmmss&gt;.d2trace.</summary>
    public TraceChannel OpenRealm(string clientLabel)
        => Open(Path.Combine(DateFolder(), "realm"), $"{Safe(clientLabel)}-{_realmSuffix}", clientLabel, "");

    private TraceChannel Open(string directory, string fileName, string clientLabel, string gameName, string serverGame = null)
    {
        var channel = new TraceChannel(this, directory, fileName, clientLabel, gameName, _options.BufferBytes, _options.DigestInterval, serverGame);
        lock (_channels)
        {
            _channels.Add(channel);
        }

        return channel;
    }

    /// <summary>
    /// Synchronous: everything recorded so far is on disk when it returns. A call made from inside a
    /// drain - the writer logging an error - returns at once instead of draining again. It waits on
    /// the writer's disk I/O, so it is for process exit and unhandled exceptions only; anything on a
    /// bot or listener thread wakes the writer instead.
    /// </summary>
    public void FlushNow()
    {
        if (Monitor.IsEntered(_drainLock))
        {
            return;
        }

        lock (_drainLock)
        {
            TraceChannel[] channels;
            lock (_channels)
            {
                channels = [.. _channels];
            }

            foreach (var channel in channels)
            {
                try
                {
                    if (channel.Drain())
                    {
                        lock (_channels)
                        {
                            _channels.Remove(channel);
                        }
                    }
                }
                catch (Exception e)
                {
                    Serilog.Log.Warning(e, "Trace writer failed to drain {Directory}", channel.Directory);
                }
            }

            try
            {
                Sidecar.Drain();
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Trace writer failed to drain the sidecar");
            }
        }
    }

    internal int OpenChannels
    {
        get
        {
            lock (_channels)
            {
                return _channels.Count;
            }
        }
    }

    internal void Wake() => _wake.Set();

    private void WriteLoop()
    {
        while (!_stopping)
        {
            _wake.WaitOne(_options.FlushInterval);
            try
            {
                FlushNow();
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Trace writer failed a pass");
            }
        }
    }

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _wake.Set();
        _writer?.Join(TimeSpan.FromSeconds(2));
        lock (_channels)
        {
            foreach (var channel in _channels)
            {
                channel.Close();
            }
        }

        FlushNow();
        FlushNow();
        lock (_drainLock)
        {
            Sidecar.Close();
        }

        if (Current == this)
        {
            Current = null;
        }
    }

    /// <summary>
    /// Oldest game folder first, by last write time. Only what sits inside date folders (yyyyMMdd) is
    /// counted or deleted, whatever else the root holds. A date's sidecars and realm folder count
    /// towards the total and go with its last game folder; a date with no game folder left is deleted
    /// as one unit.
    /// </summary>
    public static int EnforceCap(string root, long maxBytes)
        => EnforceCap(root, maxBytes, folder => Directory.Delete(folder, recursive: true));

    internal static int EnforceCap(string root, long maxBytes, Action<string> delete)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var dates = Directory.GetDirectories(root).Where(IsDateFolder).ToList();
        var games = dates
            .SelectMany(date => Vanishing(() => Directory.GetDirectories(date), []).Where(folder => !IsRealmFolder(folder)))
            .Select(folder => (Folder: folder, Bytes: FolderBytes(folder), Written: Directory.GetLastWriteTimeUtc(folder), DateOnly: false))
            .ToList();
        var gamesLeft = dates.ToDictionary(date => date, date => games.Count(g => Path.GetDirectoryName(g.Folder) == date));
        var leftovers = dates.Select(date => (Folder: date, Bytes: LeftoverBytes(date), Written: Directory.GetLastWriteTimeUtc(date), DateOnly: true)).ToList();
        var total = games.Sum(g => g.Bytes) + leftovers.Sum(l => l.Bytes);
        var queue = games.Concat(leftovers.Where(l => gamesLeft[l.Folder] == 0)).OrderBy(f => f.Written).ToList();
        var deleted = 0;
        foreach (var (folder, bytes, _, dateOnly) in queue)
        {
            if (total <= maxBytes)
            {
                break;
            }

            if (dateOnly)
            {
                total -= DeleteLeftovers(folder, delete);
                continue;
            }

            try
            {
                delete(folder);
                total -= bytes;
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(folder))
                {
                    Serilog.Log.Warning("Could not delete old trace folder {Folder}: {Error}", folder, e.Message);
                    continue;
                }

                total -= bytes;
            }

            var date = Path.GetDirectoryName(folder);
            if (--gamesLeft[date] == 0)
            {
                total -= DeleteLeftovers(date, delete);
            }
        }

        foreach (var date in dates)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(date).Any())
                {
                    Directory.Delete(date);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    private static bool IsRealmFolder(string folder) => string.Equals(Path.GetFileName(folder), "realm", StringComparison.OrdinalIgnoreCase);

    private static List<FileInfo> Sidecars(string date)
        => Vanishing(() => new DirectoryInfo(date).EnumerateFiles(TraceSidecar.FilePrefix + "*" + TraceSidecar.Extension).ToList(), []);

    /// <summary>Another bot trimming the same root can delete a folder between listing and reading it: gone counts as empty.</summary>
    private static T Vanishing<T>(Func<T> read, T gone)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException)
        {
            return gone;
        }
    }

    private static long LeftoverBytes(string date)
    {
        var realm = Path.Combine(date, "realm");
        return Sidecars(date).Sum(f => Vanishing(() => f.Length, 0L)) + FolderBytes(realm);
    }

    /// <returns>The bytes that are gone: a file still open in another process stays and still counts.</returns>
    private static long DeleteLeftovers(string date, Action<string> delete)
    {
        var removed = 0L;
        foreach (var sidecar in Sidecars(date))
        {
            var bytes = Vanishing(() => sidecar.Length, 0L);
            try
            {
                sidecar.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            if (!File.Exists(sidecar.FullName))
            {
                removed += bytes;
            }
        }

        var realm = Path.Combine(date, "realm");
        if (Directory.Exists(realm))
        {
            var bytes = FolderBytes(realm);
            try
            {
                delete(realm);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            removed += Directory.Exists(realm) ? bytes - FolderBytes(realm) : bytes;
        }

        return removed;
    }

    private static bool IsDateFolder(string folder)
    {
        var name = Path.GetFileName(folder);
        return name.Length == 8 && name.All(char.IsAsciiDigit);
    }

    private static long FolderBytes(string folder)
        => Vanishing(() => new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => Vanishing(() => f.Length, 0L)), 0L);

    private string DateFolder() => Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

    /// <summary>
    /// What Windows refuses in a file name, on every system: Linux's own list is only '/' and NUL, so a trace named there
    /// with a ':' could not be copied to the Windows machines that read the traces.
    /// </summary>
    private static readonly HashSet<char> WindowsInvalid = [.. "\"<>|:*?\\/", .. Enumerable.Range(0, 32).Select(c => (char)c)];

    internal static string Safe(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var safe = name.Select(c => WindowsInvalid.Contains(c) ? '_' : c).ToArray();
        for (var i = safe.Length - 1; i >= 0 && safe[i] is '.' or ' '; i--)
        {
            safe[i] = '_';
        }

        var text = new string(safe);
        var dot = text.IndexOf('.');
        var stem = dot < 0 ? text : text[..dot];
        return IsDeviceName(stem.TrimEnd(' ')) ? stem + "_" + (dot < 0 ? "" : text[dot..]) : text;
    }

    /// <summary>
    /// Windows opens a device instead of a file for these names, whatever extension follows and in any case: a game
    /// named "con" would have its folder and every file in it fail to open.
    /// </summary>
    private static bool IsDeviceName(string stem)
        => stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
}
