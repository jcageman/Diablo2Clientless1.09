using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Query;

/// <summary>
/// Loot nobody took: an item of magical quality or better (magical, set, rare, unique, crafted) that the server dropped
/// fresh within <see cref="CheckThresholds.SightRange"/> of the client - 0x9C AddToGround, a monster's death or an opened
/// chest; a player's drop is DropToGround and left out - and that no client of the game ever held afterwards, while the
/// client went to town, left the area, lost sight of it or its trace ended. A refused pick-up leaves it there: the server
/// takes the item off the ground (0x0A) and drops it back on its spot (DropToGround) to every client. Item ids are game-wide,
/// so a party sees one drop alike: <see cref="OnePerItem"/> makes it one incident, from the client nearest to it when it
/// dropped, and leaves it out when any client took it. Evidence: Diablo's five rare and magic drops of 20 Nov 2026 lay
/// untouched while the leveling party went to town; in another game a rare hatchet came back after the paladin's pick-up.
/// </summary>
public sealed class LootLeftBehindCheck : IncidentCheck, IFamily
{
    public const string Kind = "loot left behind";

    /// <summary>
    /// A drop this client saw, and how and when it lost it: <paramref name="RemovedInSight"/> when the server took it off the
    /// ground within sight - someone picked it up; <paramref name="PutBack"/> when the server then dropped it back on its spot,
    /// as it does when it refuses a pick-up.
    /// </summary>
    public sealed record Sighting(uint Id, string Item, string Quality, Point Where, TimeSpan At, double Distance, TimeSpan LostAt, string Lost, bool RemovedInSight, bool PutBack = false);

    private readonly Dictionary<uint, (Sighting Seen, Area? Area)> _open = [];
    private readonly List<Sighting> _sightings = [];
    private readonly Dictionary<uint, TimeSpan> _taken = [];
    private readonly Dictionary<uint, int> _asked = [];
    private bool _left;

    public string Family => "bot";

    public IReadOnlyList<Sighting> Sightings => _sightings;

    /// <summary>Every item this client saw held by someone - itself, or a player it saw equip one - and when first.</summary>
    public IReadOnlyDictionary<uint, TimeSpan> Taken => _taken;

    /// <summary>How often this client asked to pick each item up (0x16).</summary>
    public IReadOnlyDictionary<uint, int> Asked => _asked;

    public static bool IsLoot(QualityType quality) => quality is QualityType.Magical or QualityType.Set or QualityType.Rare or QualityType.Unique or QualityType.Crafted;

    public override void After(ReplayContext context, TraceRecord record)
    {
        var p = record.Payload;
        if (p.Length == 0)
        {
            return;
        }

        if (record.Kind == TraceRecordKind.D2gsSent)
        {
            if (p[0] == 0x16 && p.Length >= 9)
            {
                var id = PacketDecoder.U32(p, 5);
                _asked[id] = _asked.GetValueOrDefault(id) + 1;
            }
            else if (p[0] == (byte)OutGoingPacket.LeaveGame)
            {
                _left = true;
            }

            return;
        }

        if (record.Kind != TraceRecordKind.D2gsReceived || context.Game.Me == null)
        {
            return;
        }

        if (_open.Count > 0 && context.Game.KnownArea.Area is { } area)
        {
            foreach (var id in _open.Where(o => o.Value.Area is { } at && at != area).Select(o => o.Key).ToList())
            {
                Close(context, id, ActivityObserver.IsTown(area) ? $"went to town ({Names.Area(area)})" : $"left for {Names.Area(area)}");
            }
        }

        switch (p[0])
        {
            case 0x9C or 0x9D when p.Length >= 8:
                Item(context, p);
                break;
            case 0x0A when p.Length >= 6 && p[1] == (byte)EntityType.Item && _open.TryGetValue(PacketDecoder.U32(p, 2), out var removed):
                var distance = context.DistanceTo(removed.Seen.Where);
                Close(context, removed.Seen.Id, distance <= CheckThresholds.SightRange ? $"saw it taken off the ground {distance:0} away" : $"lost sight of it {distance:0} away", distance <= CheckThresholds.SightRange);
                break;
        }
    }

    private void Item(ReplayContext context, byte[] p)
    {
        var action = (Action)p[1];
        var id = PacketDecoder.U32(p, 4);
        if (action is not (Action.AddToGround or Action.OnGround or Action.DropToGround or Action.AddToShop or Action.RemoveFromShop))
        {
            _taken.TryAdd(id, context.Now);
            if (_open.ContainsKey(id))
            {
                Close(context, id, "took it");
            }

            return;
        }

        if (action is Action.OnGround or Action.DropToGround)
        {
            Reappeared(context, id);
            return;
        }

        if (action != Action.AddToGround || _open.ContainsKey(id))
        {
            return;
        }

        Item item;
        try
        {
            item = new ParseItemPacket(new D2gsPacket(p)).Item;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return;
        }

        var distance = context.DistanceTo(item.Location);
        if (!IsLoot(item.Quality) || item.IsGold || item.Location == null || distance > CheckThresholds.SightRange)
        {
            return;
        }

        _open[id] = (new Sighting(id, item.Name.ToString(), item.Quality.ToString(), item.Location, context.Now, distance, default, null, false), context.Game.KnownArea.Area);
    }

    /// <summary>An item this client saw drop and lose, on the ground again: back in sight, or dropped back after a refused pick-up.</summary>
    private void Reappeared(ReplayContext context, uint id)
    {
        if (_open.ContainsKey(id) || _taken.ContainsKey(id))
        {
            return;
        }

        var last = _sightings.FindLastIndex(s => s.Id == id);
        if (last < _judged)
        {
            return;
        }

        var seen = _sightings[last];
        _sightings.RemoveAt(last);
        _open[id] = (seen with { LostAt = default, Lost = null, RemovedInSight = false, PutBack = seen.PutBack || seen.RemovedInSight }, context.Game.KnownArea.Area);
    }

    private void Close(ReplayContext context, uint id, string lost, bool removedInSight = false)
    {
        if (_open.Remove(id, out var open))
        {
            _sightings.Add(open.Seen with { LostAt = context.Now, Lost = lost, RemovedInSight = removedInSight });
        }
    }

    /// <summary>
    /// Alone, a client reports what it saw left: not taken by itself and not taken off the ground in its sight. In a game
    /// <see cref="OnePerItem"/> replaces these with what every client saw.
    /// </summary>
    public override void Finished(ReplayContext context)
    {
        foreach (var id in _open.Keys.ToList())
        {
            Close(context, id, _left ? "left the game" : "its trace ended");
        }

        foreach (var seen in _sightings.Skip(_judged).Where(s => !_taken.ContainsKey(s.Id) && !s.RemovedInSight))
        {
            AddFound(Describe(context.Client, seen, 1, _asked.TryGetValue(seen.Id, out var asks) ? [(context.Client, asks)] : []));
        }

        _judged = _sightings.Count;
        _left = false;
    }

    private int _judged;

    public override void Save(System.IO.BinaryWriter w)
    {
        base.Save(w);
        w.WriteList(_sightings, (x, s) =>
        {
            x.Write(s.Id);
            x.WriteText(s.Item);
            x.WriteText(s.Quality);
            x.Write(s.Where);
            x.Write(s.At);
            x.Write(s.Distance);
            x.Write(s.LostAt);
            x.WriteText(s.Lost);
            x.Write(s.RemovedInSight);
            x.Write(s.PutBack);
        });
        w.WriteList(_taken, (x, t) => { x.Write(t.Key); x.Write(t.Value); });
        w.WriteList(_asked, (x, a) => { x.Write(a.Key); x.Write(a.Value); });
    }

    public override void Load(System.IO.BinaryReader r)
    {
        base.Load(r);
        r.ReadList(_sightings, x => new Sighting(x.ReadUInt32(), x.ReadText(), x.ReadText(), x.ReadPoint(), x.ReadTime(), x.ReadDouble(), x.ReadTime(), x.ReadText(), x.ReadBoolean(), x.ReadBoolean()));
        var taken = new List<(uint, TimeSpan)>();
        r.ReadList(taken, x => (x.ReadUInt32(), x.ReadTime()));
        foreach (var (id, at) in taken)
        {
            _taken[id] = at;
        }

        var asked = new List<(uint, int)>();
        r.ReadList(asked, x => (x.ReadUInt32(), x.ReadInt32()));
        foreach (var (id, count) in asked)
        {
            _asked[id] = count;
        }
    }

    /// <summary>
    /// The game's loot incidents: one per item that some client saw drop and no client took, from the client that was nearest
    /// to it then, with every client's pick-up requests for it.
    /// </summary>
    public static IEnumerable<Incident> OnePerItem(IReadOnlyList<(string Client, LootLeftBehindCheck Check)> clients)
    {
        var taken = clients.SelectMany(c => c.Check.Taken.Keys).ToHashSet();
        return clients.SelectMany(c => c.Check.Sightings.Select(s => (c.Client, Seen: s)))
            .Where(s => !taken.Contains(s.Seen.Id))
            .GroupBy(s => s.Seen.Id)
            .Select(g =>
            {
                var nearest = g.OrderBy(s => s.Seen.Distance).First();
                var asks = clients.Where(c => c.Check.Asked.ContainsKey(g.Key)).Select(c => (c.Client, c.Check.Asked[g.Key])).ToList();
                return Describe(nearest.Client, nearest.Seen, g.Select(s => s.Client).Distinct().Count(), asks);
            })
            .OrderBy(i => i.Offset);
    }

    private static Incident Describe(string client, Sighting seen, int clientsSaw, List<(string Client, int Count)> asks)
    {
        var asked = asks.Count == 0 ? "no client asked to pick it up" : $"pick-up asked by {string.Join(", ", asks.Select(a => $"{a.Client} x{a.Count}"))}";
        return new Incident(seen.At, client, Kind,
            $"{seen.Item} ({seen.Quality}) {Names.Id(seen.Id)} dropped at {Names.Point(seen.Where)}, {seen.Distance:0} from {client}{(clientsSaw > 1 ? $", the nearest of the {clientsSaw} clients that saw it drop" : "")};"
            + $" nobody took it and {asked}{(seen.PutBack ? ", and the server dropped it back on its spot after a pick-up" : "")}; {client} {seen.Lost} at {TraceTime.Format(seen.LostAt)}")
        { End = seen.LostAt };
    }
}
