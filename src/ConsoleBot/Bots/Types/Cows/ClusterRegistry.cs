using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using System.Collections.Generic;
using System.Threading;

using System;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// Tracks the monster clusters discovered during a run and hands them out to clients. One pool for
/// the whole level: everything in it is something to kill, and whether a given character can hurt a
/// given cluster is answered from monster resistances at the point of attack, not by sorting
/// monsters into categories here.
/// </summary>
public sealed class ClusterRegistry
{
    /// <summary>Monsters within this distance of an existing cluster belong to that cluster.</summary>
    public const double ClusterRadius = 30.0;

    private readonly SpatialGrid<MonsterCluster> _grid = new();
    private readonly List<MonsterCluster> _clusters = [];
    private readonly Lock _lock = new();
    private uint _nextId = 1;
    private double _maxDistance = double.MaxValue;
    private Func<MonsterCluster, bool> _canWork;
    private IReadOnlyList<Point> _sweep = [];

    /// <summary>Whether any cluster has been discovered yet, so an empty registry is not finished.</summary>
    public bool AnyDiscovered
    {
        get
        {
            lock (_lock)
            {
                return _clusters.Count > 0;
            }
        }
    }

    /// <summary>
    /// The cluster covering <paramref name="location"/>, creating one when nothing covers it yet.
    /// <paramref name="created"/> reports which happened.
    /// </summary>
    public MonsterCluster FindOrRegister(Point location, out bool created)
    {
        lock (_lock)
        {
            var covering = _grid.Within(location, ClusterRadius, 1);
            if (covering.Count > 0)
            {
                created = false;
                return covering[0];
            }

            var cluster = new MonsterCluster { Id = _nextId++, Location = location, SweepIndex = SweepIndexOf(location) };
            _grid.TryAdd(cluster.Id, location, cluster);
            _clusters.Add(cluster);
            created = true;
            return cluster;
        }
    }

    public void AddMember(MonsterCluster cluster)
    {
        lock (_lock)
        {
            cluster.AliveMembers++;
            cluster.TotalMembers++;
        }
    }

    public void RemoveMember(uint clusterId, bool killed)
    {
        lock (_lock)
        {
            var cluster = _grid.TryGetValue(clusterId, out var found) ? found : null;
            if (cluster == null)
            {
                return;
            }

            if (cluster.AliveMembers > 0)
            {
                cluster.AliveMembers--;
            }

            if (killed)
            {
                cluster.KilledMembers++;
            }
        }
    }

    /// <summary>Whether every monster assigned to this cluster is dead.</summary>
    public bool IsCleared(MonsterCluster cluster)
    {
        lock (_lock)
        {
            return cluster.AliveMembers == 0;
        }
    }

    /// <summary>
    /// Sets the route that orders the work. Clusters already discovered are placed on it, so this
    /// can be set once the level map is available rather than before the first monster is seen.
    /// </summary>
    public void SetSweep(IReadOnlyList<Point> sweep)
    {
        lock (_lock)
        {
            _sweep = sweep ?? [];
            foreach (var cluster in _clusters)
            {
                cluster.SweepIndex = SweepIndexOf(cluster.Location);
            }
        }
    }

    public bool HasSweep
    {
        get
        {
            lock (_lock)
            {
                return _sweep.Count > 0;
            }
        }
    }

    /// <summary>
    /// Claims the next pending cluster along the sweep, falling back to distance from
    /// <paramref name="from"/> while no route is set or for clusters that share a waypoint. Hunted
    /// clusters are only handed out once eligible.
    /// </summary>
    public MonsterCluster ClaimNearest(
        Point from,
        int fromSweepIndex = 0,
        double maxDistance = double.MaxValue,
        bool nearestFirst = false,
        Func<MonsterCluster, bool> canWork = null)
    {
        lock (_lock)
        {
            _maxDistance = maxDistance;
            _canWork = canWork;
            RetireKilledClusters();
            // Prefer the next cluster ahead on the route. Something left behind is still taken, but
            // only once nothing is left in front - and then the closest one, not the earliest.
            //
            // Picking the earliest sent the party the full width of the level to whichever orphan
            // sat lowest on the route, straight through live cows it was not stopping for, and it
            // then swept forward over ground it had already covered. Hunted clusters become orphans
            // routinely: one is only eligible once the cow clusters around it are done, so packs
            // passed early come up for work long after the group has moved beyond them.
            var best = PickPending(from, fromSweepIndex, nearestFirst)
                ?? PickPending(from, 0, nearestFirst: true);

            if (best != null)
            {
                best.Claimed = true;
                return best;
            }

            // Nothing pending, so join a cluster someone else is already on rather than idling.
            foreach (var cluster in _clusters)
            {
                if (!cluster.Done && cluster.Claimed && from.Distance(cluster.Location) <= _maxDistance)
                {
                    return cluster;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Marks clusters whose members are all confirmed dead as done. Skipping them at claim time was
    /// not enough on its own: nothing else ever set Done, so they stayed pending for the rest of the
    /// game. With two cow clusters stuck like that the sorceresses had nothing left to claim and
    /// stood still, AllDone never came true, and the hunting party stayed capped to its short range
    /// with eligible work sitting outside it - the whole party idle until the patience timer.
    /// </summary>
    private void RetireKilledClusters()
    {
        foreach (var cluster in _clusters)
        {
            if (cluster.Done || cluster.Claimed || cluster.TotalMembers == 0
                || cluster.KilledMembers < cluster.TotalMembers)
            {
                continue;
            }

            cluster.Done = true;
        }
    }

    /// <summary>
    /// Whether any cluster of this kind is still worth working: not done, and for hunted clusters
    /// eligible. Once the cows are finished nothing further can become eligible, so a game with
    /// none of these left has nothing to wait for.
    /// </summary>
    public bool AnyPending()
    {
        lock (_lock)
        {
            foreach (var cluster in _clusters)
            {
                if (!cluster.Done)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Marks a cluster done, whatever the outcome of working it.</summary>
    public void Release(MonsterCluster cluster)
    {
        if (cluster == null)
        {
            return;
        }

        lock (_lock)
        {
            cluster.Claimed = false;
            if (cluster.Done)
            {
                return;
            }

            cluster.Done = true;
            // Monsters we can still see alive, not monsters we did not kill. A member that walked
            // out of view is pruned without being counted as killed, so measuring this against the
            // kill count marked half the cleared clusters abandoned and made the party look far
            // worse than it was.
            cluster.Abandoned = cluster.AliveMembers > 0;
        }
    }

    /// <summary>Returns a cluster to the pool without marking it done, so another client can take it.</summary>
    public void GiveUp(MonsterCluster cluster)
    {
        if (cluster == null)
        {
            return;
        }

        lock (_lock)
        {
            cluster.Claimed = false;
        }
    }

    /// <summary>How many clusters were given up on with monsters still standing.</summary>
    public int AbandonedCount()
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var cluster in _clusters)
            {
                if (cluster.Abandoned)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Whether every discovered cluster of this kind has been done.</summary>
    public bool AllDone()
    {
        lock (_lock)
        {
            foreach (var cluster in _clusters)
            {
                if (!cluster.Done)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            return _clusters.Count;
        }
    }

    public int DoneCount()
    {
        lock (_lock)
        {
            var done = 0;
            foreach (var cluster in _clusters)
            {
                if (cluster.Done)
                {
                    done++;
                }
            }

            return done;
        }
    }

    /// <summary>
    /// The pending cluster earliest on the route at or after <paramref name="minSweepIndex"/>,
    /// tie-broken by distance from <paramref name="from"/>.
    /// </summary>
    private MonsterCluster PickPending(Point from, int minSweepIndex, bool nearestFirst = false)
    {
        MonsterCluster best = null;
        var bestSweep = int.MaxValue;
        var bestDistance = double.MaxValue;

        // Strict route order. Taking the nearest instead walks about a third less on paper, and it
        // was tried twice: both times the party doubled back on over a third of its turns. Taking
        // anything out of order advances the group's own sweep position, which orphans everything
        // behind it, and the orphans then have to be collected in a second pass across the level.
        foreach (var cluster in _clusters)
        {
            if (cluster.Done || cluster.Claimed || cluster.SweepIndex < minSweepIndex)
            {
                continue;
            }

            // Already killed by whoever could reach it first - usually a bow or a whirlwind from
            // the previous cluster. Handing it out anyway sent the party across the level to stand
            // on an empty spot and declare it cleared, which was half of the trips it made.
            //
            // Killed, not merely gone: a monster that leaves sight is removed from its cluster as
            // well, so testing AliveMembers alone threw away every pack the party had not walked up
            // to yet - three quarters of the level in one measured game.
            if (cluster.TotalMembers > 0 && cluster.KilledMembers >= cluster.TotalMembers)
            {
                continue;
            }

            // Nothing here this claimant can hurt. Taking it anyway is what left packs standing: a
            // nova sorceress would claim a pack of lightning immunes, fail to shift it, and hand it
            // back marked done, so the characters who could kill it were never offered it.
            if (_canWork != null && !_canWork(cluster))
            {
                continue;
            }

            var distance = from.Distance(cluster.Location);
            if (distance > _maxDistance)
            {
                continue;
            }

            var better = nearestFirst
                ? distance < bestDistance
                : cluster.SweepIndex < bestSweep || (cluster.SweepIndex == bestSweep && distance < bestDistance);
            if (better)
            {
                bestSweep = cluster.SweepIndex;
                bestDistance = distance;
                best = cluster;
            }
        }

        return best;
    }

    /// <summary>The waypoint this location belongs to, or the end of the route when none is set.</summary>
    private int SweepIndexOf(Point location)
    {
        var best = int.MaxValue;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _sweep.Count; i++)
        {
            var distance = _sweep[i].Distance(location);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }
}
