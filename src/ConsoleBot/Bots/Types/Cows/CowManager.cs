using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Cows;

internal sealed class CowManager
{
    /// <summary>How long the party may spend on one hunted cluster before it is abandoned.</summary>
    public static readonly TimeSpan HuntedClusterTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the hunt goes without a kill before the game is called. A fresh level costs roughly
    /// a minute and a half to reach, so waiting much longer than this to find the next soul loses
    /// more than starting over would. Tune it from tools/cow_kpi.py over a batch of games.
    /// </summary>
    public static readonly TimeSpan HuntPatience = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Overall budget for the hunt, measured from the moment the party first sets off. The hunted
    /// queue keeps growing while the party explores, so this has to outlast discovery rather than
    /// just the clusters known when the hunt started.
    /// </summary>
    public static readonly TimeSpan ActivePhaseBudget = TimeSpan.FromSeconds(480);

    private readonly List<Client> _killingClients;
    private readonly HashSet<Client> _listeners;
    private readonly IMapApiService mapApiService;
    private readonly HashSet<NPCCode> _huntedMonsters;
    private readonly SpatialGrid<AliveMonster> _aliveMonsters = new();
    private readonly ClusterRegistry _clusters = new();
    private readonly Stopwatch _activePhase = new();

    /// <summary>
    /// Runs from the moment the party is in the level, which is when it can start killing. The
    /// active phase starts later, when the first cluster opens, so it is the wrong denominator for
    /// a rate: souls killed while trailing the sorceresses would count with no time against them.
    /// </summary>
    private readonly Stopwatch _timeInLevel = new();
    private readonly Stopwatch _sinceLastKill = new();

    /// <summary>
    /// Entities confirmed dead. Every client in the party receives the same death packet, and an
    /// assign for the same entity can still arrive from another client afterwards, so without this
    /// one monster is counted several times and, worse, is put back into its cluster as alive.
    /// </summary>
    private readonly ConcurrentDictionary<uint, byte> _confirmedDead = new();
    private int _huntedKilled;

    public CowManager(List<Client> killingclients, List<Client> listeningClients, IMapApiService mapApiService,
        IReadOnlyCollection<NPCCode> huntedMonsters)
    {
        _killingClients = killingclients;
        this.mapApiService = mapApiService;
        _huntedMonsters = huntedMonsters == null ? [] : [.. huntedMonsters];
        _listeners = [.. killingclients, .. listeningClients];
    }

    /// <summary>
    /// Whether this client feeds this manager. Packet handlers live for the whole session while a
    /// manager lives for one game, so the handlers ask rather than subscribing per game: there is
    /// no way to unsubscribe, and every game would otherwise leave another manager behind chewing
    /// through the same packets.
    /// </summary>
    public bool Listens(Client client) => _listeners.Contains(client);

    public void OnAssignNpc(D2gsPacket packet) => HandleAssignNPC(new AssignNpcPacket(packet));

    public void OnNpcState(D2gsPacket packet) => HandleNPCStateChange(new NpcStatePacket(packet));

    public void OnNpcMove(Point location, uint entityId) => HandleNPCMove(entityId, location);

    public void OnNpcStop(uint entityId, Point location, double lifePercentage) => HandleNPCMove(entityId, location, lifePercentage);

    public void OnNpcHit(uint entityId, double lifePercentage) => UpdateNPCLife(entityId, lifePercentage);

    /// <summary>
    /// Spacing of the sweep waypoints in game units. A killer standing on one covers the ground
    /// around it, so the spacing is a little under twice the range it clears.
    /// </summary>
    private const int SweepBand = 40;
    private const int SweepStep = 40;

    private readonly Lock _sweepLock = new();
    private bool _sweepBuilt;

    /// <summary>
    /// Lays a serpentine route over the level and orders all cluster work along it, so the killers
    /// sweep in one direction and the walking party can trail them. Safe to call from every client;
    /// only the first call does the work.
    /// </summary>
    public async Task EnsureSweep(Game game, Point entry)
    {
        lock (_sweepLock)
        {
            if (_sweepBuilt)
            {
                return;
            }

            _sweepBuilt = true;
        }

        var areaMap = await mapApiService.GetArea(game.MapId, Difficulty.Normal, D2NG.Core.D2GS.Act.Area.CowLevel);
        var walkable = new bool[areaMap.Map.Length][];
        for (var y = 0; y < areaMap.Map.Length; y++)
        {
            var row = areaMap.Map[y];
            walkable[y] = new bool[row.Length];
            for (var x = 0; x < row.Length; x++)
            {
                walkable[y][x] = AreaMapExtensions.IsMovable(row[x]);
            }
        }

        var route = SweepRoute.Build(walkable, SweepBand, SweepStep);
        var points = new List<Point>(route.Count);
        foreach (var (x, y) in route)
        {
            points.Add(areaMap.MapToPoint(x, y));
        }

        // Run the route from whichever end the party entered at, so it sweeps away from the portal
        // rather than crossing the level to start.
        if (points.Count > 1 && entry.Distance(points[^1]) < entry.Distance(points[0]))
        {
            points.Reverse();
        }

        _timeInLevel.Restart();
        _clusters.SetSweep(points);
        Log.Information($"Sweep route over {D2NG.Core.D2GS.Act.Area.CowLevel} has {points.Count} waypoints, starting at {(points.Count > 0 ? points[0].ToString() : "nowhere")}");
    }

    /// <summary>Whether the party hunts monsters of its own on top of the sorceresses clearing cows.</summary>
    public bool ActiveMode => _huntedMonsters.Count > 0;

    public IReadOnlyCollection<NPCCode> HuntedMonsters => _huntedMonsters;

    public async Task<List<Point>> GetPossibleStartingLocations(Game game)
    {
        var result = new List<Point>();
        var areaMap = await mapApiService.GetArea(game.MapId, Difficulty.Normal, D2NG.Core.D2GS.Act.Area.CowLevel);
        var cowKing = areaMap.Npcs[(int)NPCCode.CowKing][0];
        var rows = areaMap.Map.GetLength(0);
        for (var i = 0; i < rows; i += rows / 5)
        {
            var edge1 = GetNearestLocationToEdge(areaMap.Map[i], true);
            if(edge1.HasValue)
            {
                var option1 = areaMap.MapToPoint(i, edge1.Value);
                if (option1.Distance(cowKing) > 150)
                {
                    result.Add(option1);
                }
            }

            var edge2 = GetNearestLocationToEdge(areaMap.Map[i], false);
            if(edge2.HasValue)
            {
                var option2 = areaMap.MapToPoint(i, edge2.Value);
                if (option2.Distance(cowKing) > 150)
                {
                    result.Add(option2);
                }
            }
        }

        return result.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
    }

    private static int? GetNearestLocationToEdge(int[] locations, bool leftToRight)
    {
        var startX = leftToRight ? 0 : locations.Length - 1;
        int x = startX;
        int count = 0;
        while (true)
        {
            if (AreaMapExtensions.IsMovable(locations[x]))
            {
                count++;
                if(count > 5)
                {
                    return x;
                }
            }
            else
            {
                count = 0;
            }

            if(leftToRight)
            {
                x++;
                if(x >= locations.Length)
                {
                    break;
                }
            }
            else
            {
                x--;
                if (x < 0)
                {
                    break;
                }
            }
        }

        return null;
    }

    private void UpdateNPCLife(uint entityId, double lifePercentage)
    {
        if(lifePercentage == 0)
        {
            RemoveMonster(entityId);
        }
        else if (_aliveMonsters.TryGetValue(entityId, out var monster))
        {
            monster.LifePercentage = lifePercentage;
        }
    }

    private void HandleNPCMove(uint entityId, Point location, double? lifePercentage = null)
    {
        if (_aliveMonsters.TryGetValue(entityId, out var monster))
        {
            monster.Location = location;
            _aliveMonsters.TryUpdateLocation(entityId, location);
            if(lifePercentage.HasValue)
            {
                monster.LifePercentage = lifePercentage.Value;
            }
        }
    }

    private void HandleAssignNPC(AssignNpcPacket packet)
    {
        var isHunted = _huntedMonsters.Contains(packet.UniqueCode);
        if (packet.UniqueCode != NPCCode.HellBovine && !isHunted)
        {
            return;
        }

        if (_confirmedDead.ContainsKey(packet.EntityId))
        {
            return;
        }

        var kind = isHunted ? ClusterKind.Hunted : ClusterKind.Cow;
        var cluster = _clusters.FindOrRegister(packet.Location, kind, out var created);
        if (created)
        {
            Log.Information($"Adding new {kind} cluster at {packet.Location}");
        }

        var added = _aliveMonsters.TryAdd(packet.EntityId, packet.Location, new AliveMonster
        {
            Id = packet.EntityId,
            Location = packet.Location,
            NPCCode = packet.UniqueCode,
            MonsterEnchantments = packet.MonsterEnchantments,
            IsHunted = isHunted,
            ClusterId = cluster.Id
        });

        if (added)
        {
            _clusters.AddMember(cluster);
        }
    }

    private void HandleNPCStateChange(NpcStatePacket packet)
    {
        if(packet.EntityState == EntityState.Dead || packet.EntityState == EntityState.Dieing)
        {
            RemoveMonster(packet.EntityId);
        }
        else if (_aliveMonsters.TryGetValue(packet.EntityId, out var monster))
        {
            monster.LifePercentage = packet.LifePercentage;
        }
    }

    public List<AliveMonster> GetNearbyAliveMonsters(Client client, double distance, int numberOfCows)
    {
        return GetNearbyAliveMonsters(client.Game.Me.Location, distance, numberOfCows);
    }

    public List<AliveMonster> GetNearbyAliveMonsters(Point location, double distance, int numberOfMonsters)
    {
        return _aliveMonsters.Within(location, distance, numberOfMonsters);
    }

    /// <summary>
    /// Bovines only. The hunted monsters are lightning immune, so a nova sorceress that treats one
    /// as a target stands there doing nothing to it until something else kills it.
    /// </summary>
    public List<AliveMonster> GetNearbyAliveCows(Client client, double distance, int numberOfCows)
    {
        return GetNearbyAliveCows(client.Game.Me.Location, distance, numberOfCows);
    }

    public List<AliveMonster> GetNearbyAliveCows(Point location, double distance, int numberOfCows)
    {
        var nearby = _aliveMonsters.Within(location, distance, int.MaxValue);
        var cows = new List<AliveMonster>(Math.Min(nearby.Count, numberOfCows));
        foreach (var monster in nearby)
        {
            if (monster.IsHunted)
            {
                continue;
            }

            cows.Add(monster);
            if (cows.Count == numberOfCows)
            {
                break;
            }
        }

        return cows;
    }

    public bool HasNearbyHuntedMonsters(Point location, double distance)
    {
        return _aliveMonsters.Any(location, distance, m => m.IsHunted);
    }

    /// <summary>Whether the level has been picked over and a fresh one is worth more than staying.</summary>
    private bool IsProducingTooSlowly()
    {
        return _sinceLastKill.Elapsed > HuntPatience;
    }

    /// <summary>Souls per minute over the hunt so far, the number the whole run is judged on.</summary>
    public double HuntedPerMinute => _timeInLevel.Elapsed.TotalMinutes > 0
        ? _huntedKilled / _timeInLevel.Elapsed.TotalMinutes
        : 0;

    public int HuntedKilled => _huntedKilled;

    /// <summary>Whether every monster that belonged to this cluster is dead.</summary>
    public bool IsClusterCleared(MonsterCluster cluster)
    {
        return _clusters.IsCleared(cluster);
    }

    /// <summary>
    /// Where the nearest surviving member of this cluster is. The pack walks away from the spot it
    /// was first seen at, so standing on that spot waiting for it to die never finishes.
    /// </summary>
    public Point GetNearestClusterMember(MonsterCluster cluster, Point from)
    {
        Point nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var monster in _aliveMonsters.Snapshot())
        {
            if (monster.ClusterId != cluster.Id)
            {
                continue;
            }

            var distance = monster.Location.Distance(from);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = monster.Location;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Drops tracked monsters the game no longer has near this client. The server removes an entity
    /// once it is out of range and re-sends it on the way back, so tracking alone never expires:
    /// a pack that wandered off would keep its cluster open forever. Standing next to where it was
    /// and not seeing it is what settles the difference between out of sight and dead.
    /// </summary>
    public void PruneMonstersOutOfSight(Client client, double distance)
    {
        foreach (var monster in _aliveMonsters.Within(client.Game.Me.Location, distance, int.MaxValue))
        {
            if (!client.Game.WorldObjects.ContainsKey((monster.Id, EntityType.NPC)))
            {
                RemoveMonster(monster.Id, killed: false);
            }
        }
    }

    private void RemoveMonster(uint entityId, bool killed = true)
    {
        if (!_aliveMonsters.TryRemove(entityId, out var monster))
        {
            return;
        }

        _clusters.RemoveMember(monster.ClusterId, killed);
        if (!killed || !_confirmedDead.TryAdd(entityId, 0))
        {
            return;
        }

        if (monster.IsHunted)
        {
            _huntedKilled++;
            _sinceLastKill.Restart();
        }
    }

    /// <summary>Returns a cluster to the pool so another client can pick it up.</summary>
    public void GiveUpCluster(MonsterCluster cluster)
    {
        _clusters.GiveUp(cluster);
    }

    /// <summary>Marks a cluster done, whatever the outcome of working it.</summary>
    public void ReleaseCluster(MonsterCluster cluster)
    {
        _clusters.Release(cluster);
    }

    /// <summary>
    /// Claims a cow cluster for a killer. <paramref name="anchor"/> is where the clearing should
    /// happen - the walking party - rather than where the killer happens to be standing.
    ///
    /// Nearest to that anchor rather than in route order, so the two of them clear one area
    /// together and the soul packs that open up are ones the party can reach. Working the route
    /// independently spread them across the level, and the party was then sent to whichever distant
    /// pack happened to be eligible while nearer ones were still undiscovered.
    /// </summary>
    public MonsterCluster ClaimNextCowCluster(Client client, int fromSweepIndex = 0, Point anchor = null)
    {
        var from = anchor ?? client.Game.Me.Location;
        return _clusters.ClaimNearest(from, ClusterKind.Cow, fromSweepIndex, double.MaxValue, nearestFirst: anchor != null);
    }

    public MonsterCluster ClaimNextHuntedCluster(Client client, int fromSweepIndex = 0, double maxDistance = double.MaxValue)
    {
        return _clusters.ClaimNearest(client.Game.Me.Location, ClusterKind.Hunted, fromSweepIndex, maxDistance);
    }

    /// <summary>Whether the killers have finished, so nothing new will open up near the party.</summary>
    public bool CowsAllDone => _clusters.AllDone(ClusterKind.Cow);

    /// <summary>Starts the overall hunt budget, on the first cluster the party sets off towards.</summary>
    public void NotifyHuntStarted()
    {
        if (!_activePhase.IsRunning)
        {
            Log.Information($"Hunting party setting off, active phase budget is {ActivePhaseBudget}");
            _activePhase.Start();
            _sinceLastKill.Restart();
        }
    }

    public bool IsFinished()
    {
        if(_killingClients.All(c => !c.Game.IsInGame()))
        {
            return true;
        }

        if (!_clusters.AnyDiscovered)
        {
            return false;
        }

        if (ActiveMode && _activePhase.IsRunning
            && (_activePhase.Elapsed > ActivePhaseBudget || IsProducingTooSlowly()))
        {
            return true;
        }

        if (!_clusters.AllDone(ClusterKind.Cow))
        {
            return false;
        }

        // Nothing claimable rather than everything done. A cluster that never became eligible never
        // will once the cows are finished, so waiting on it only burned the ninety second patience
        // timer at the end of every game - a fifth of the run spent standing in a cleared level.
        return !ActiveMode || !_clusters.AnyPending(ClusterKind.Hunted);
    }

    public string DescribeProgress()
    {
        var cows = $"cow clusters {_clusters.DoneCountOf(ClusterKind.Cow)}/{_clusters.CountOf(ClusterKind.Cow)}";
        if (!ActiveMode)
        {
            return cows;
        }

        return $"{cows}, hunted clusters {_clusters.DoneCountOf(ClusterKind.Hunted)}/{_clusters.CountOf(ClusterKind.Hunted)} ({_clusters.EligibleHuntedCount()} eligible), {_huntedKilled} killed at {HuntedPerMinute:F1}/min";
    }
}
