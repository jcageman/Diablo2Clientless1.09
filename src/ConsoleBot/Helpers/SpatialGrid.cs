using D2NG.Core.D2GS;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ConsoleBot.Helpers;

/// <summary>
/// Uniform hash grid over game coordinates, keyed by entity id. A neighbour query only visits the
/// cells overlapping the search radius instead of scanning every entry, and moving an entity is a
/// bucket swap. All members are safe to call from the packet handler threads.
/// </summary>
public sealed class SpatialGrid<T>
{
    /// <summary>
    /// Width of a cell in game units. Chosen so the common query radii (20 - 35) span at most a
    /// 3x3 block of cells.
    /// </summary>
    public const int CellSize = 32;

    private sealed class Entry
    {
        public T Value { get; set; }
        public Point Location { get; set; }
        public int Cell { get; set; }
    }

    private readonly Dictionary<int, List<uint>> _cells = [];
    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryAdd(uint id, Point location, T value)
    {
        lock (_lock)
        {
            if (_entries.ContainsKey(id))
            {
                return false;
            }

            var cell = CellOf(location);
            _entries[id] = new Entry { Value = value, Location = location, Cell = cell };
            AddToCell(cell, id);
            return true;
        }
    }

    public bool TryGetValue(uint id, out T value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                value = entry.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    public bool TryUpdateLocation(uint id, Point location)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return false;
            }

            entry.Location = location;
            var cell = CellOf(location);
            if (cell != entry.Cell)
            {
                RemoveFromCell(entry.Cell, id);
                AddToCell(cell, id);
                entry.Cell = cell;
            }

            return true;
        }
    }

    public bool TryRemove(uint id, out T value)
    {
        lock (_lock)
        {
            if (!_entries.Remove(id, out var entry))
            {
                value = default;
                return false;
            }

            RemoveFromCell(entry.Cell, id);
            value = entry.Value;
            return true;
        }
    }

    /// <summary>
    /// Returns the entries within <paramref name="radius"/> of <paramref name="center"/>, nearest
    /// first, capped at <paramref name="max"/>.
    /// </summary>
    public List<T> Within(Point center, double radius, int max = int.MaxValue)
    {
        var candidates = new List<(double DistanceSquared, T Value)>();
        var radiusSquared = radius * radius;
        lock (_lock)
        {
            var cellRadius = (int)(radius / CellSize) + 1;
            var centerX = center.X / CellSize;
            var centerY = center.Y / CellSize;
            for (var x = Math.Max(centerX - cellRadius, 0); x <= centerX + cellRadius; x++)
            {
                for (var y = Math.Max(centerY - cellRadius, 0); y <= centerY + cellRadius; y++)
                {
                    if (!_cells.TryGetValue((x << 11) | y, out var ids))
                    {
                        continue;
                    }

                    foreach (var id in ids)
                    {
                        var entry = _entries[id];
                        var distanceSquared = SquaredDistance(center, entry.Location);
                        if (distanceSquared < radiusSquared)
                        {
                            candidates.Add((distanceSquared, entry.Value));
                        }
                    }
                }
            }
        }

        candidates.Sort((left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));
        var result = new List<T>(Math.Min(candidates.Count, max));
        for (var i = 0; i < candidates.Count && i < max; i++)
        {
            result.Add(candidates[i].Value);
        }

        return result;
    }

    /// <summary>
    /// Whether any entry matching <paramref name="predicate"/> lies within
    /// <paramref name="radius"/>. Stops at the first hit rather than materialising a list.
    /// </summary>
    public bool Any(Point center, double radius, Func<T, bool> predicate)
    {
        var radiusSquared = radius * radius;
        lock (_lock)
        {
            var cellRadius = (int)(radius / CellSize) + 1;
            var centerX = center.X / CellSize;
            var centerY = center.Y / CellSize;
            for (var x = Math.Max(centerX - cellRadius, 0); x <= centerX + cellRadius; x++)
            {
                for (var y = Math.Max(centerY - cellRadius, 0); y <= centerY + cellRadius; y++)
                {
                    if (!_cells.TryGetValue((x << 11) | y, out var ids))
                    {
                        continue;
                    }

                    foreach (var id in ids)
                    {
                        var entry = _entries[id];
                        if (SquaredDistance(center, entry.Location) < radiusSquared && predicate(entry.Value))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    public List<T> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<T>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                result.Add(entry.Value);
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _cells.Clear();
        }
    }

    private static int CellOf(Point location) => ((location.X / CellSize) << 11) | (location.Y / CellSize);

    private static double SquaredDistance(Point left, Point right)
    {
        double deltaX = left.X - right.X;
        double deltaY = left.Y - right.Y;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private void AddToCell(int cell, uint id)
    {
        if (!_cells.TryGetValue(cell, out var ids))
        {
            ids = [];
            _cells[cell] = ids;
        }

        ids.Add(id);
    }

    private void RemoveFromCell(int cell, uint id)
    {
        if (!_cells.TryGetValue(cell, out var ids))
        {
            return;
        }

        ids.Remove(id);
        if (ids.Count == 0)
        {
            _cells.Remove(cell);
        }
    }
}
