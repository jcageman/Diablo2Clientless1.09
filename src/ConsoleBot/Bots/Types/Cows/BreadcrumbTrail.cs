using D2NG.Core.D2GS;
using System.Collections.Generic;
using System.Threading;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// The trail of positions the hunting party passed through while nothing was on them. When the
/// party has to disengage it walks back to the most recent point far enough behind it, which is
/// ground it already knows is clear.
/// </summary>
public sealed class BreadcrumbTrail
{
    public const int Capacity = 64;

    private readonly List<Point> _points = new(Capacity);
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _points.Count;
            }
        }
    }

    public void Record(Point location)
    {
        lock (_lock)
        {
            if (_points.Count > 0 && _points[^1] == location)
            {
                return;
            }

            _points.Add(location);
            if (_points.Count > Capacity)
            {
                _points.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// The most recently recorded point at least <paramref name="minDistance"/> away from
    /// <paramref name="current"/>, or <see langword="null"/> when the party has not travelled
    /// that far yet.
    /// </summary>
    public Point FindRetreatPoint(Point current, double minDistance)
    {
        lock (_lock)
        {
            for (var i = _points.Count - 1; i >= 0; i--)
            {
                if (_points[i].Distance(current) >= minDistance)
                {
                    return _points[i];
                }
            }

            return null;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _points.Clear();
        }
    }
}
