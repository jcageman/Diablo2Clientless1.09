using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using System.Collections.Generic;
using System.Threading;

namespace ConsoleBot.Bots.Types.Cows;

/// <summary>
/// Tracks the monster clusters discovered during a run and hands them out to clients. Hunted
/// clusters are additionally gated on the nearest cow clusters having been done, so the walking
/// party is only sent into ground the sorceresses have already been over.
/// </summary>
public sealed class ClusterRegistry
{
    /// <summary>Monsters within this distance of an existing cluster belong to that cluster.</summary>
    public const double ClusterRadius = 30.0;

    /// <summary>How many of the nearest cow clusters must be done before a hunted cluster opens up.</summary>
    public const int RequiredDoneCowClusters = 4;

    /// <summary>
    /// A cluster this close is taken even when the route says to go elsewhere. Far enough to be
    /// worth the detour, near enough that it does not become a reason to leave the route.
    /// </summary>

    private readonly SpatialGrid<MonsterCluster> _cowGrid = new();
    private readonly SpatialGrid<MonsterCluster> _huntedGrid = new();
    private readonly List<MonsterCluster> _cows = [];
    private readonly List<MonsterCluster> _hunted = [];
    private readonly Lock _lock = new();
    private uint _nextId = 1;
    private double _maxDistance = double.MaxValue;
    private IReadOnlyList<Point> _sweep = [];

    /// <summary>Whether any cluster has been discovered yet, so an empty registry is not finished.</summary>
    public bool AnyDiscovered
    {
        get
        {
            lock (_lock)
            {
                return _cows.Count > 0 || _hunted.Count > 0;
            }
        }
    }

    /// <summary>
    /// The cluster of this kind covering <paramref name="location"/>, creating one when nothing
    /// covers it yet. <paramref name="created"/> reports which happened.
    /// </summary>
    public MonsterCluster FindOrRegister(Point location, ClusterKind kind, out bool created)
    {
        lock (_lock)
        {
            var grid = kind == ClusterKind.Cow ? _cowGrid : _huntedGrid;
            var covering = grid.Within(location, ClusterRadius, 1);
            if (covering.Count > 0)
            {
                created = false;
                return covering[0];
            }

            var cluster = new MonsterCluster { Id = _nextId++, Location = location, Kind = kind, SweepIndex = SweepIndexOf(location) };
            grid.TryAdd(cluster.Id, location, cluster);
            (kind == ClusterKind.Cow ? _cows : _hunted).Add(cluster);
            RecomputeEligibility();
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
            var cluster = _cowGrid.TryGetValue(clusterId, out var cow) ? cow
                : _huntedGrid.TryGetValue(clusterId, out var hunted) ? hunted
                : null;
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
            foreach (var cluster in _cows)
            {
                cluster.SweepIndex = SweepIndexOf(cluster.Location);
            }

            foreach (var cluster in _hunted)
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
    public MonsterCluster ClaimNearest(Point from, ClusterKind kind, int fromSweepIndex = 0, double maxDistance = double.MaxValue, bool nearestFirst = false)
    {
        lock (_lock)
        {
            _maxDistance = maxDistance;
            RetireKilledClusters(kind);
            // Prefer the next cluster ahead on the route. Something left behind is still taken, but
            // only once nothing is left in front - and then the closest one, not the earliest.
            //
            // Picking the earliest sent the party the full width of the level to whichever orphan
            // sat lowest on the route, straight through live cows it was not stopping for, and it
            // then swept forward over ground it had already covered. Hunted clusters become orphans
            // routinely: one is only eligible once the cow clusters around it are done, so packs
            // passed early come up for work long after the group has moved beyond them.
            var best = PickPending(from, kind, fromSweepIndex, nearestFirst)
                ?? PickPending(from, kind, 0, nearestFirst: true);

            if (best != null)
            {
                best.Claimed = true;
                return best;
            }

            // Nothing pending, so join a cluster someone else is already on rather than idling.
            foreach (var cluster in kind == ClusterKind.Cow ? _cows : _hunted)
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
    private void RetireKilledClusters(ClusterKind kind)
    {
        var retiredCow = false;
        foreach (var cluster in kind == ClusterKind.Cow ? _cows : _hunted)
        {
            if (cluster.Done || cluster.Claimed || cluster.TotalMembers == 0
                || cluster.KilledMembers < cluster.TotalMembers)
            {
                continue;
            }

            cluster.Done = true;
            retiredCow |= kind == ClusterKind.Cow;
        }

        if (retiredCow)
        {
            RecomputeEligibility();
        }
    }

    /// <summary>
    /// Whether any cluster of this kind is still worth working: not done, and for hunted clusters
    /// eligible. Once the cows are finished nothing further can become eligible, so a game with
    /// none of these left has nothing to wait for.
    /// </summary>
    public bool AnyPending(ClusterKind kind)
    {
        lock (_lock)
        {
            foreach (var cluster in kind == ClusterKind.Cow ? _cows : _hunted)
            {
                if (!cluster.Done && (kind == ClusterKind.Cow || cluster.Eligible))
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
            if (cluster.Kind == ClusterKind.Cow)
            {
                RecomputeEligibility();
            }
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

    /// <summary>Whether every discovered cluster of this kind has been done.</summary>
    public bool AllDone(ClusterKind kind)
    {
        lock (_lock)
        {
            foreach (var cluster in kind == ClusterKind.Cow ? _cows : _hunted)
            {
                if (!cluster.Done)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public int CountOf(ClusterKind kind)
    {
        lock (_lock)
        {
            return (kind == ClusterKind.Cow ? _cows : _hunted).Count;
        }
    }

    public int DoneCountOf(ClusterKind kind)
    {
        lock (_lock)
        {
            var done = 0;
            foreach (var cluster in kind == ClusterKind.Cow ? _cows : _hunted)
            {
                if (cluster.Done)
                {
                    done++;
                }
            }

            return done;
        }
    }

    public int EligibleHuntedCount()
    {
        lock (_lock)
        {
            var eligible = 0;
            foreach (var cluster in _hunted)
            {
                if (cluster.Eligible)
                {
                    eligible++;
                }
            }

            return eligible;
        }
    }

    /// <summary>
    /// Grants eligibility to every hunted cluster whose nearest <see cref="RequiredDoneCowClusters"/>
    /// cow clusters are all done. Eligibility is never taken away again, and this only runs when a
    /// cluster is discovered or completed rather than on the client loops.
    /// </summary>
    /// <summary>
    /// The pending cluster earliest on the route at or after <paramref name="minSweepIndex"/>,
    /// tie-broken by distance from <paramref name="from"/>.
    /// </summary>
    private MonsterCluster PickPending(Point from, ClusterKind kind, int minSweepIndex, bool nearestFirst = false)
    {
        MonsterCluster best = null;
        var bestSweep = int.MaxValue;
        var bestDistance = double.MaxValue;

        // Strict route order. Taking the nearest instead walks about a third less on paper, and it
        // was tried twice: both times the party doubled back on over a third of its turns. Taking
        // anything out of order advances the group's own sweep position, which orphans everything
        // behind it, and the orphans then have to be collected in a second pass across the level.
        foreach (var cluster in kind == ClusterKind.Cow ? _cows : _hunted)
        {
            if (cluster.Done || cluster.Claimed || (kind == ClusterKind.Hunted && !cluster.Eligible)
                || cluster.SweepIndex < minSweepIndex)
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

    private void RecomputeEligibility()
    {
        if (_hunted.Count == 0 || _cows.Count < RequiredDoneCowClusters)
        {
            return;
        }

        var nearest = new double[RequiredDoneCowClusters];
        var nearestClusters = new MonsterCluster[RequiredDoneCowClusters];
        foreach (var hunted in _hunted)
        {
            if (hunted.Eligible)
            {
                continue;
            }

            var filled = 0;
            foreach (var cow in _cows)
            {
                var distance = hunted.Location.Distance(cow.Location);
                if (filled == RequiredDoneCowClusters && distance >= nearest[filled - 1])
                {
                    continue;
                }

                var insertAt = filled < RequiredDoneCowClusters ? filled : RequiredDoneCowClusters - 1;
                while (insertAt > 0 && nearest[insertAt - 1] > distance)
                {
                    nearest[insertAt] = nearest[insertAt - 1];
                    nearestClusters[insertAt] = nearestClusters[insertAt - 1];
                    insertAt--;
                }

                nearest[insertAt] = distance;
                nearestClusters[insertAt] = cow;
                if (filled < RequiredDoneCowClusters)
                {
                    filled++;
                }
            }

            var allDone = true;
            for (var i = 0; i < filled; i++)
            {
                if (!nearestClusters[i].Done)
                {
                    allDone = false;
                    break;
                }
            }

            hunted.Eligible = allDone;
        }
    }
}
