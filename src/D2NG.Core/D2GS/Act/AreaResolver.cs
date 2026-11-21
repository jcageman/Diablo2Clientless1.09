using D2NG.Core.D2GS.Packet;
using System;
using System.Collections.Generic;

namespace D2NG.Core.D2GS.Act;

public enum AreaState
{
    Unknown,
    Resolved,
}

/// <summary>Where an area answer comes from.</summary>
public enum AreaSource
{
    Unknown,

    /// <summary>The position the client believes it holds.</summary>
    Believed,

    /// <summary>The last position the server stated, because the believed one is outside the client's revealed map.</summary>
    ServerConfirmed,

    /// <summary>
    /// The town the last act load named, held until a tile holds a position set after the load or the server states one.
    /// The load names the act's town wherever the character arrives, so this is no evidence of being in town.
    /// </summary>
    ActLoad,
}

/// <summary>
/// The server kept stating the own position within <see cref="AreaResolver.HoldRadius"/> subtiles of
/// <paramref name="Location"/> while the client held itself far from it just before: the server is refusing the
/// client's movement.
/// </summary>
/// <remarks>
/// Only statements that find the client more than <see cref="AreaResolver.HoldMoving"/> away count, and they form a run
/// while each comes less than <see cref="AreaResolver.HoldSpan"/> after the one before; a longer silence or a statement
/// elsewhere starts a new one, so an old divergence is never carried into a later stay. A statement finding the client
/// in sync neither counts nor breaks the run: most of them follow a statement that has just put the client back, and a
/// held client walks off again at once. One run is one episode, and every record of it carries the same
/// <paramref name="Since"/>, which tells it from any other.
/// </remarks>
/// <param name="Since">Clock timestamp of the first statement of the run; the episode's identity.</param>
/// <param name="Statements">Statements of the run.</param>
/// <param name="LargestBelievedDistance">The farthest the client held itself from <paramref name="Location"/> just before one of them.</param>
public sealed record ServerHeldPosition(Point Location, long Since, int Statements, double LargestBelievedDistance)
{
    /// <summary>Clock timestamp from which, without a further statement, the run has ended: <see cref="AreaResolver.HoldSpan"/> after its last.</summary>
    internal long Until { get; init; }

    internal Func<Point> Believed { get; init; }

    /// <summary>Subtiles from where the client believes it is now to <see cref="Location"/>; null while it is not placed.</summary>
    public double? BelievedDistance
        => Believed?.Invoke() is { } believed && (believed.X != 0 || believed.Y != 0) ? believed.Distance(Location) : null;

    /// <summary>
    /// The client believes itself within <see cref="AreaResolver.HoldMoving"/> of <see cref="Location"/> right now, as it
    /// does after every snap back; a held client walks off again at once, so this does not end the episode.
    /// </summary>
    public bool ClientIsOnHeldPoint => BelievedDistance <= AreaResolver.HoldMoving;

    internal bool HoldsAt(long now) => now < Until;
}

/// <summary>What <see cref="AreaResolver"/> last decided; replaced whole, so a reader never sees half an update.</summary>
/// <param name="ChangedAt">Clock timestamp of the last change of <paramref name="Area"/>, to or from unknown included.</param>
/// <param name="ServerArea">The revealed tile's area holding the last position the server stated since the act load; null without one.</param>
/// <param name="Held"><paramref name="Area"/> is the act load's town, held (<see cref="AreaSource.ActLoad"/>), not a tile's.</param>
/// <param name="Confirmation">The last position the server stated since the act load; null without one.</param>
/// <param name="ServerHold">Set while the server holds the character in place against its movement; see <see cref="ServerHeldPosition"/>.</param>
internal sealed record AreaResolution(Area? Area, Area? LastKnown, long ChangedAt, Area? ServerArea, bool Held = false,
    PositionConfirmation Confirmation = null, ServerHeldPosition ServerHold = null);

/// <summary>
/// The area the character stands in, from the map tiles the server revealed (0x07) and the act loads (0x03).
/// A position inside a revealed tile is in that tile's area. After an act load the area the load named is
/// held until a tile holds a position set after the load, or the server states a position. Otherwise the area
/// is unknown - never the act's town, which is what <see cref="ActData.Area"/> falls back to - and the last
/// resolved area is kept. The last position the server stated since the act load is resolved by the same
/// tiles, without the load: the server reveals the rooms around its own idea of the position, so a believed
/// position off the revealed map is a client that has lost sync, while the server's position is still on it.
/// </summary>
/// <remarks>
/// Decided when the position, the tiles or the act change rather than when read, so the answer does not
/// depend on how often a bot looks and a read is a single reference load. Every change reads the believed
/// position from the position source inside the lock, so whichever change resolves last sees the position
/// the client holds last; the source must take no lock. Of several tiles holding a position the one that
/// held the last position wins, then the first revealed.
/// </remarks>
internal sealed class AreaResolver
{
    /// <summary>Subtiles: statements this close to the first of them are the server holding one position.</summary>
    public const double HoldRadius = 3;

    /// <summary>Statements in a run at one position before it counts as held against the client.</summary>
    public const int HoldStatements = 5;

    /// <summary>How long a run must have lasted to count, and the longest silence within one.</summary>
    public static readonly TimeSpan HoldSpan = TimeSpan.FromSeconds(5);

    /// <summary>Subtiles the client must have held itself from the held position, just before one statement of the run, for it to count.</summary>
    public const double HoldBelievedDistance = 30;

    /// <summary>Subtiles away from the held position that count as the client still moving away; closer, the client is in sync and not held.</summary>
    public const double HoldMoving = 10;

    private const int CellShift = 3;

    private readonly object _gate = new();
    private readonly Dictionary<Act, Dictionary<int, List<Tile>>> _cells = [];
    private readonly TimeProvider _clock;
    private readonly Func<Point> _position;
    private Act _act;
    private Area? _loaded;
    private bool _positionSinceLoad;
    private Tile _tile;
    private Point _server;
    private Area? _serverArea;
    private PositionConfirmation _confirmation;
    private Point _holdAt;
    private long _holdSince;
    private long _holdLast;
    private int _holdStatements;
    private double _holdWidest;
    private ServerHeldPosition _hold;
    private volatile AreaResolution _current;

    /// <param name="position">Where the client believes the character is, null before it is placed; read under the resolver's lock, so it must take no lock.</param>
    public AreaResolver(TimeProvider clock = null, Func<Point> position = null)
    {
        _clock = clock ?? TimeProvider.System;
        _position = position ?? (static () => null);
        _current = new AreaResolution(null, null, _clock.GetTimestamp(), null);
    }

    public TimeProvider Clock => _clock;

    public AreaResolution Current => _current;

    public void ActLoaded(Act act, Area area)
    {
        lock (_gate)
        {
            _act = act;
            _loaded = area;
            _positionSinceLoad = false;
            _tile = null;
            _server = null;
            _serverArea = null;
            _confirmation = null;
            ResetHold(null);
            Resolve();
        }
    }

    public void TileRevealed(Act act, Tile tile)
    {
        lock (_gate)
        {
            if (!_cells.TryGetValue(act, out var cells))
            {
                _cells[act] = cells = [];
            }

            for (var x = tile.X >> CellShift; x <= (tile.X + tile.Size.X - 1) >> CellShift; x++)
            {
                for (var y = tile.Y >> CellShift; y <= (tile.Y + tile.Size.Y - 1) >> CellShift; y++)
                {
                    var key = Cell(x, y);
                    if (!cells.TryGetValue(key, out var list))
                    {
                        cells[key] = list = [];
                    }

                    list.Add(tile);
                }
            }

            if (_server != null)
            {
                _serverArea = Find(_server)?.Area;
            }

            Resolve();
        }
    }

    /// <summary>The believed position was set.</summary>
    public void Moved()
    {
        lock (_gate)
        {
            _positionSinceLoad = true;
            Resolve();
        }
    }

    /// <summary>A position the server stated for the character; ends an act load's hold.</summary>
    public void Confirmed(Point server, InComingPacket packet)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            var believed = _position();
            _server = server;
            _serverArea = Find(server)?.Area;
            _loaded = null;
            _confirmation = new PositionConfirmation(server, now, packet, believed);
            TrackHold(server, believed, now);
            Resolve();
        }
    }

    private static int Cell(int x, int y) => (x << 16) | y;

    private static bool Placed(Point position) => position != null && (position.X != 0 || position.Y != 0);

    private void TrackHold(Point server, Point believed, long now)
    {
        if (_holdAt == null || server.Distance(_holdAt) > HoldRadius)
        {
            ResetHold(server);
        }

        var distance = Placed(believed) ? believed.Distance(_holdAt) : 0;
        if (distance <= HoldMoving)
        {
            return;
        }

        var span = (long)(HoldSpan.TotalSeconds * _clock.TimestampFrequency);
        if (_holdStatements == 0 || now - _holdLast >= span)
        {
            _holdSince = now;
            _holdStatements = 0;
            _holdWidest = 0;
        }

        _holdLast = now;
        _holdStatements++;
        _holdWidest = Math.Max(_holdWidest, distance);
        _hold = _holdStatements >= HoldStatements && _holdWidest >= HoldBelievedDistance && now - _holdSince >= span
            ? new ServerHeldPosition(_holdAt, _holdSince, _holdStatements, _holdWidest) { Until = now + span, Believed = _position }
            : null;
    }

    private void ResetHold(Point at)
    {
        _holdAt = at;
        _holdStatements = 0;
        _hold = null;
    }

    private void Resolve()
    {
        var position = _positionSinceLoad ? _position() : null;
        var area = _loaded;
        var held = area != null;
        if (Placed(position))
        {
            if (_tile != null && _tile.Contains(position))
            {
                area = _tile.Area;
                held = false;
            }
            else if (Find(position) is { } tile)
            {
                _tile = tile;
                _loaded = null;
                area = tile.Area;
                held = false;
            }
        }

        var serverArea = _serverArea;
        var current = _current;
        var lastKnown = area ?? current.LastKnown;
        if (area != current.Area || lastKnown != current.LastKnown || serverArea != current.ServerArea || held != current.Held
            || !ReferenceEquals(_confirmation, current.Confirmation) || !ReferenceEquals(_hold, current.ServerHold))
        {
            _current = new AreaResolution(area, lastKnown, area != current.Area ? _clock.GetTimestamp() : current.ChangedAt, serverArea, held, _confirmation, _hold);
        }
    }

    private Tile Find(Point position)
    {
        if (!_cells.TryGetValue(_act, out var cells)
            || !cells.TryGetValue(Cell(position.X / Tile.Scale >> CellShift, position.Y / Tile.Scale >> CellShift), out var tiles))
        {
            return null;
        }

        foreach (var tile in tiles)
        {
            if (tile.Contains(position))
            {
                return tile;
            }
        }

        return null;
    }
}
