using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Act;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.Trace.Query;

/// <summary>A map that cannot be had; WholeService when the service itself cannot be reached, so no uncached area can be.</summary>
public sealed class MapUnavailableException(string message, bool wholeService = false) : Exception(message)
{
    public bool WholeService { get; } = wholeService;
}

/// <summary>What the map says about a point: walkable, not walkable but with ground close by, or no ground within reach.</summary>
public enum GroundAt
{
    Walkable,
    NearGround,
    NoGround,
}

/// <summary>
/// What a subtile's collision value - Diablo 2's collision bits, as the map service gives them - blocks. Bit 0x1 blocks
/// walking (odd values; <see cref="AreaMapExtensions.IsMovable"/>), bit 0x2 blocks line of sight and so missiles: the
/// Travincal temple's edge is one row of 7 (walk, sight, wall) with gaps, then rows of 1 (walk only) that missiles cross;
/// the Chaos Sanctuary star is 17 (0x10 and walk only) that missiles cross, and the void around it is 1.
/// </summary>
public static class MapCollision
{
    public static bool BlocksWalk(int value) => value >= 0 && !AreaMapExtensions.IsMovable(value);

    public static bool BlocksSight(int value) => value >= 0 && (value & 2) != 0;
}

/// <summary>An area's collision grid kept as three bits per subtile - inside the area, walkable, blocks sight - instead of the service's int per subtile.</summary>
public sealed class GroundGrid
{
    private readonly ulong[] _inside;
    private readonly ulong[] _walkable;
    private readonly ulong[] _sight;

    private GroundGrid(Point origin, IReadOnlyList<IReadOnlyList<int>> rows)
    {
        Origin = origin;
        Height = rows.Count;
        Width = Height == 0 ? 0 : rows.Max(r => r.Count);
        _inside = new ulong[((long)Width * Height + 63) / 64];
        _walkable = new ulong[_inside.Length];
        _sight = new ulong[_inside.Length];
        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < rows[row].Count; column++)
            {
                var value = rows[row][column];
                if (value < 0)
                {
                    continue;
                }

                var bit = (long)row * Width + column;
                _inside[bit >> 6] |= 1UL << (int)(bit & 63);
                if (AreaMapExtensions.IsMovable(value))
                {
                    _walkable[bit >> 6] |= 1UL << (int)(bit & 63);
                }

                if (MapCollision.BlocksSight(value))
                {
                    _sight[bit >> 6] |= 1UL << (int)(bit & 63);
                }
            }
        }
    }

    /// <summary>
    /// Reads only the origin and the rows from the map service's JSON, as it streams by - the rows go
    /// straight to bits (a row of bytes at a time), never into the int lists the full map is made of.
    /// </summary>
    public static GroundGrid Parse(byte[] json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        Point origin = null;
        List<IReadOnlyList<int>> rows = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("not a JSON object");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "levelOrigin", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartObject)
            {
                int x = 0, y = 0;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var key = reader.GetString();
                    reader.Read();
                    if (string.Equals(key, "x", StringComparison.OrdinalIgnoreCase))
                    {
                        x = reader.GetInt32();
                    }
                    else if (string.Equals(key, "y", StringComparison.OrdinalIgnoreCase))
                    {
                        y = reader.GetInt32();
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                origin = new Point((ushort)x, (ushort)y);
            }
            else if (string.Equals(name, "mapRows", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartArray)
            {
                rows = [];
                var row = new List<int>();
                while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    row.Clear();
                    while (reader.Read() && reader.TokenType == JsonTokenType.Number)
                    {
                        row.Add(reader.GetInt32());
                    }

                    rows.Add(new PackedRow(row));
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return origin == null || rows == null ? throw new JsonException("no levelOrigin or mapRows") : new GroundGrid(origin, rows);
    }

    /// <summary>A row kept as one byte per subtile (0 outside, 1 blocks walking only, 2 walkable, 3 blocks sight) until the grid packs it.</summary>
    private sealed class PackedRow(List<int> values) : IReadOnlyList<int>
    {
        private readonly byte[] _cells = [.. values.Select(v => (byte)(v < 0 ? 0 : MapCollision.BlocksSight(v) ? 3 : AreaMapExtensions.IsMovable(v) ? 2 : 1))];

        public int this[int index] => _cells[index] switch { 0 => -1, 1 => 1, 2 => 0, _ => 3 };

        public int Count => _cells.Length;

        public IEnumerator<int> GetEnumerator()
        {
            for (var i = 0; i < _cells.Length; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public Point Origin { get; }

    public int Width { get; }

    public int Height { get; }

    public long Bytes => (_inside.Length + _walkable.Length + _sight.Length) * 8L;

    /// <summary>Whether the subtile blocks line of sight, and so missiles (<see cref="MapCollision.BlocksSight"/>).</summary>
    public bool BlocksSight(int x, int y)
    {
        var column = x - Origin.X;
        var row = y - Origin.Y;
        if (row < 0 || row >= Height || column < 0 || column >= Width)
        {
            return false;
        }

        var bit = (long)row * Width + column;
        return (_sight[bit >> 6] & (1UL << (int)(bit & 63))) != 0;
    }

    /// <summary>'.' walkable, '#' blocks walking and sight (a wall), '~' blocks walking only (water, a ledge: missiles cross it), ' ' outside the area - as <see cref="TileGrid.Ground"/>.</summary>
    public char Ground(int x, int y)
    {
        var column = x - Origin.X;
        var row = y - Origin.Y;
        if (row < 0 || row >= Height || column < 0 || column >= Width)
        {
            return ' ';
        }

        var bit = (long)row * Width + column;
        var mask = 1UL << (int)(bit & 63);
        return (_inside[bit >> 6] & mask) == 0 ? ' ' : (_walkable[bit >> 6] & mask) != 0 ? '.' : (_sight[bit >> 6] & mask) != 0 ? '#' : '~';
    }

    public GroundAt At(Point point, int reach)
    {
        if (Ground(point.X, point.Y) == '.')
        {
            return GroundAt.Walkable;
        }

        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (Ground(point.X + dx, point.Y + dy) == '.')
                {
                    return GroundAt.NearGround;
                }
            }
        }

        return GroundAt.NoGround;
    }
}

/// <summary>
/// Area maps from the map service the bots use (D2NG.Navigation's request: /maps?mapid=&amp;area=&amp;difficulty=),
/// cached as the service's JSON under a folder so a repeated query does not ask again. A cached file is
/// written to a temporary name and renamed into place, and one that does not parse is deleted and fetched
/// again. The service may not be running: a failure to connect makes every later fetch fail at once with
/// the same one-line message; an error answer for one area marks only that area unavailable. The checks
/// read areas as <see cref="GroundGrid"/>s, of which only the most recently used few are kept in memory -
/// every game has its own seed, so a batch would otherwise keep every map of every game.
/// </summary>
public sealed class MapSource
{
    public const string UrlVariable = "D2NG_MAP_URL";

    public const string DefaultUrl = "https://localhost:8080";

    /// <summary>How many area grids stay in memory.</summary>
    public const int KeptGrids = 16;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _url;
    private readonly string _cache;
    private readonly Func<string, string> _fetch;
    private readonly object _gate = new();
    private readonly LinkedList<((uint, Difficulty, Area) Key, GroundGrid Grid)> _grids = new();
    private readonly ConcurrentDictionary<(uint, Difficulty, Area), string> _areaUnavailable = new();
    private string _unavailable;

    public MapSource(string url, string cacheFolder, Func<string, string> fetch = null)
    {
        _url = (url ?? Environment.GetEnvironmentVariable(UrlVariable) ?? DefaultUrl).TrimEnd('/');
        _cache = cacheFolder;
        _fetch = fetch ?? Fetch;
    }

    public string Url => _url;

    /// <summary>How many times an area was parsed from JSON; a grid evicted and needed again counts again.</summary>
    public int Parsed => Volatile.Read(ref _parsed);

    private int _parsed;

    /// <summary>The full map, with objects, read from the cache or the service each time; for rendering, not for the checks.</summary>
    public AreaMap Get(uint mapId, Difficulty difficulty, Area area) => Load(mapId, difficulty, area, ParseMap);

    private readonly Dictionary<(uint, Difficulty, Area), Lazy<GroundGrid>> _loading = [];
    private readonly object _fetchGate = new();

    /// <summary>An area's grid; several threads may ask at once, each area is loaded once and different areas side by side.</summary>
    public GroundGrid Grid(uint mapId, Difficulty difficulty, Area area)
    {
        var key = (mapId, difficulty, area);
        Lazy<GroundGrid> pending;
        lock (_gate)
        {
            if (Kept(key) is { } kept)
            {
                return kept;
            }

            if (!_loading.TryGetValue(key, out pending))
            {
                pending = new Lazy<GroundGrid>(() => Load(mapId, difficulty, area, ParseGrid));
                _loading[key] = pending;
            }
        }

        GroundGrid grid;
        try
        {
            grid = pending.Value;
        }
        finally
        {
            lock (_gate)
            {
                if (_loading.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                {
                    _loading.Remove(key);
                }
            }
        }

        lock (_gate)
        {
            if (Kept(key) is { } kept)
            {
                return kept;
            }

            _grids.AddFirst((key, grid));
            while (_grids.Count > KeptGrids)
            {
                _grids.RemoveLast();
            }

            return grid;
        }
    }

    private GroundGrid Kept((uint, Difficulty, Area) key)
    {
        for (var node = _grids.First; node != null; node = node.Next)
        {
            if (node.Value.Key == key)
            {
                _grids.Remove(node);
                _grids.AddFirst(node);
                return node.Value.Grid;
            }
        }

        return null;
    }

    public bool IsWalkable(uint mapId, Difficulty difficulty, Area area, Point point) => Grid(mapId, difficulty, area).Ground(point.X, point.Y) == '.';

    public GroundAt GroundAt(uint mapId, Difficulty difficulty, Area area, Point point, int reach) => Grid(mapId, difficulty, area).At(point, reach);

    private T Load<T>(uint mapId, Difficulty difficulty, Area area, Func<byte[], T> parse)
    {
        var key = (mapId, difficulty, area);
        if (_areaUnavailable.TryGetValue(key, out var reason))
        {
            throw new MapUnavailableException(reason);
        }

        var file = _cache == null ? null : Path.Combine(_cache, $"map-{mapId:X8}-{difficulty}-{(int)area}.json");
        if (file != null && File.Exists(file))
        {
            byte[] cached = null;
            try
            {
                cached = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
            }

            if (cached != null && TryParse(cached, parse, out var map, out _))
            {
                return map;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        lock (_fetchGate)
        {
            return Fetch(mapId, difficulty, area, parse, key, file);
        }
    }

    private T Fetch<T>(uint mapId, Difficulty difficulty, Area area, Func<byte[], T> parse, (uint, Difficulty, Area) key, string file)
    {
        string reason;
        if (_unavailable != null)
        {
            throw new MapUnavailableException(_unavailable, wholeService: true);
        }

        var request = $"{_url}/maps?mapid={mapId}&area={(int)area}&difficulty={difficulty}";
        string text;
        try
        {
            text = _fetch(request);
        }
        catch (HttpRequestException e) when (e.StatusCode != null)
        {
            reason = $"map service at {_url} refused {area} ({e.Message.Split('\n')[0].Trim()})";
            _areaUnavailable[key] = reason;
            throw new MapUnavailableException(reason);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or OperationCanceledException)
        {
            _unavailable = $"map service at {_url} is unreachable ({e.Message.Split('\n')[0].Trim()}); start it or pass --map-url";
            throw new MapUnavailableException(_unavailable, wholeService: true);
        }

        if (!TryParse(System.Text.Encoding.UTF8.GetBytes(text), parse, out var fetched, out var error))
        {
            reason = $"map service at {_url} answered {area} with something that is not a map: {error}";
            _areaUnavailable[key] = reason;
            throw new MapUnavailableException(reason);
        }

        if (file != null)
        {
            var temporary = file + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                Directory.CreateDirectory(_cache);
                File.WriteAllText(temporary, text);
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return fetched;
    }

    private static AreaMap ParseMap(byte[] json)
        => JsonSerializer.Deserialize<AreaMapDto>(json, Json).MapFromDto() is { Map: not null } map ? map : throw new JsonException("no map rows");

    private static GroundGrid ParseGrid(byte[] json) => GroundGrid.Parse(json);

    private bool TryParse<T>(byte[] json, Func<byte[], T> parse, out T map, out string error)
    {
        using var _ = Profile.Time("maps: parse " + typeof(T).Name);
        try
        {
            map = parse(json);
            Interlocked.Increment(ref _parsed);
            error = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or NullReferenceException or FormatException or ArgumentException or InvalidOperationException)
        {
            map = default;
            error = e.Message.Split('\n')[0].Trim();
            return false;
        }
    }

    private static string Fetch(string url)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || (request.RequestUri != null && request.RequestUri.IsLoopback),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = client.GetAsync(new Uri(url)).GetAwaiter().GetResult();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// An area's collision grid around a point as text, one character per subtile, north (smaller y) up:
/// '.' walkable, '#' blocks walking and sight (a wall), '~' blocks walking only (missiles cross it), ' ' outside the area,
/// 'D' a door the map places, 'o' another object
/// the map places, and the marks given (the client '@', monsters 'm', players 'p', portals 'P'...),
/// which win over the ground.
/// </summary>
public static class TileGrid
{
    public static List<string> Render(AreaMap map, Point center, int radius, IReadOnlyList<(Point At, char Mark)> marks)
    {
        var placed = new Dictionary<(int, int), char>();
        foreach (var (code, points) in map.Objects ?? [])
        {
            foreach (var point in points)
            {
                placed[(point.X, point.Y)] = AreaMapExtensions.DoorObjectCodes.Contains(code) ? 'D' : 'o';
            }
        }

        foreach (var (at, mark) in marks)
        {
            if (at != null)
            {
                placed[(at.X, at.Y)] = mark;
            }
        }

        var lines = new List<string>
        {
            $"x {center.X - radius}..{center.X + radius} left to right, y {center.Y - radius}..{center.Y + radius} top to bottom; area origin {Names.Point(map.LevelOrigin)}, {(map.Map.Length == 0 ? 0 : map.Map[0].Length)}x{map.Map.Length} subtiles",
        };
        var ruler = new StringBuilder("".PadLeft(7));
        for (var x = center.X - radius; x <= center.X + radius; x++)
        {
            ruler.Append(x % 10 == 0 ? '|' : ' ');
        }

        lines.Add(ruler.ToString().TrimEnd() + "   (| marks x divisible by 10; y is labelled every 10 rows)");
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            var row = new StringBuilder(radius * 2 + 8);
            row.Append((y % 10 == 0 ? y.ToString(System.Globalization.CultureInfo.InvariantCulture) : "").PadLeft(6)).Append(' ');
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                if (placed.TryGetValue((x, y), out var mark))
                {
                    row.Append(mark);
                    continue;
                }

                row.Append(Ground(map, x, y));
            }

            lines.Add(row.ToString().TrimEnd());
        }

        return lines;
    }

    public static char Ground(AreaMap map, int x, int y)
    {
        var column = x - map.LevelOrigin.X;
        var row = y - map.LevelOrigin.Y;
        if (row < 0 || row >= map.Map.Length || column < 0 || column >= map.Map[row].Length)
        {
            return ' ';
        }

        var value = map.Map[row][column];
        return value < 0 ? ' ' : AreaMapExtensions.IsMovable(value) ? '.' : MapCollision.BlocksSight(value) ? '#' : '~';
    }
}
