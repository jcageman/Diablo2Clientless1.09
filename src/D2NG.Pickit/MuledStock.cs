using D2NG.Core.D2GS.Items;
using D2NG.Pickit.Nip;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace D2NG.Pickit;

/// <summary>
/// The items on the mules as of one load, counted against a rule's '[maxquantity]'. A load is never
/// changed, only replaced by the next, so how many of its items count towards a rule is worked out
/// the first time that rule asks and kept for as long as this load is current.
/// </summary>
public sealed class MuledStock(IReadOnlyCollection<Item> items)
{
    public static MuledStock Empty { get; } = new([]);

    private readonly ConcurrentDictionary<NipRule, int> _counts = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyCollection<Item> Items { get; } = items;

    internal int CountTowards(NipRule rule, Func<NipRule, IReadOnlyCollection<Item>, int> count)
    {
        return _counts.GetOrAdd(rule, r => count(r, Items));
    }
}
