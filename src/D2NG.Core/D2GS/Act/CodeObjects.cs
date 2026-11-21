using D2NG.Core.D2GS.Objects;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace D2NG.Core.D2GS.Act;

/// <summary>
/// The world objects of one entity code, in the order the ConcurrentBag this replaces gave them out.
/// That bag was only ever filled from the receive thread, so it enumerated newest first, and a removal
/// rebuilt it by adding what it enumerated - which turned the order around on every removal. Readers
/// pick objects by that order, so it is kept exactly; only the copying of a bag per removal is gone.
/// </summary>
internal sealed class CodeObjects : IEnumerable<WorldObject>
{
    private readonly Lock _gate = new();

    /// <summary>In the order the bag held them: enumeration runs from the end.</summary>
    private readonly List<WorldObject> _held = [];

    public void Add(WorldObject obj)
    {
        lock (_gate)
        {
            _held.Add(obj);
        }
    }

    /// <summary>What a rebuild of the bag without the unit left: its enumeration order, as the held order.</summary>
    public void Remove(uint id, EntityType type)
    {
        lock (_gate)
        {
            _held.Reverse();
            var kept = 0;
            for (var i = 0; i < _held.Count; i++)
            {
                var obj = _held[i];
                if (obj.Id != id || obj.Type != type)
                {
                    _held[kept++] = obj;
                }
            }

            _held.RemoveRange(kept, _held.Count - kept);
        }
    }

    public IEnumerator<WorldObject> GetEnumerator()
    {
        WorldObject[] snapshot;
        lock (_gate)
        {
            snapshot = new WorldObject[_held.Count];
            for (var i = 0; i < snapshot.Length; i++)
            {
                snapshot[i] = _held[_held.Count - 1 - i];
            }
        }

        return ((IEnumerable<WorldObject>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
