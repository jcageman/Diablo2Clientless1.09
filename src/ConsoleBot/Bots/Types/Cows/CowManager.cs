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
    /// The budget of a game that clears the level: a party with no hunt budget configured
    /// (<see cref="CowConfiguration.HuntBudgetSeconds"/>), and a game that ends with a farmer muling - its identify
    /// trip to Cain runs while the rest of the party is still in the game, which is only safe in a cleared level.
    /// </summary>
    public static readonly TimeSpan ClearingBudget = TimeSpan.FromSeconds(480);

    /// <summary>Overall budget for the hunt, measured from the moment the party first sets off; see <see cref="CowConfiguration.HuntBudgetSeconds"/>.</summary>
    public TimeSpan HuntBudget { get; init; } = ClearingBudget;

    /// <summary>Asked once, when the party sets off: whether this game has to clear the level, see <see cref="ClearingBudget"/>.</summary>
    public Func<bool> ClearsTheLevel { get; init; } = () => false;

    /// <summary>Fixed when the party sets off: a budget that grew later turned a hunt already over back into one running.</summary>
    private TimeSpan _budget = ClearingBudget;

    private readonly List<Client> _killingClients;
    private readonly HashSet<Client> _listeners;
    private readonly IMapApiService mapApiService;
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

    /// <summary>How far from the Cow King a boss minion counts as one of his guards.</summary>
    private const double KingGuardRadius = 20;
    private Point _cowKingLocation;
    private int _killed;
    private readonly Lock _killLock = new();

    public CowManager(List<Client> killingclients, List<Client> listeningClients, IMapApiService mapApiService, bool activeMode)
    {
        _killingClients = killingclients;
        this.mapApiService = mapApiService;
        _listeners = [.. killingclients, .. listeningClients];
        ActiveMode = activeMode;
    }

    /// <summary>
    /// Whether this client feeds this manager. Packet handlers live for the whole session while a
    /// manager lives for one game, so the handlers ask rather than subscribing per game: there is
    /// no way to unsubscribe, and every game would otherwise leave another manager behind chewing
    /// through the same packets.
    /// </summary>
    public bool Listens(Client client) => _listeners.Contains(client);

    public void OnAssignNpc(D2gsPacket packet) => Guarded(() => HandleAssignNPC(new AssignNpcPacket(packet)));

    public void OnNpcState(D2gsPacket packet) => Guarded(() => HandleNPCStateChange(new NpcStatePacket(packet)));

    public void OnNpcMove(Point location, uint entityId) => Guarded(() => HandleNPCMove(entityId, location));

    public void OnNpcStop(uint entityId, Point location, double lifePercentage) => Guarded(() => HandleNPCMove(entityId, location, lifePercentage));

    public void OnNpcHit(uint entityId, double lifePercentage) => Guarded(() => UpdateNPCLife(entityId, lifePercentage));

    private static readonly TimeSpan HandlerWarningInterval = TimeSpan.FromSeconds(10);
    private long _lastHandlerWarning;

    /// <summary>
    /// These run in the clients' packet handlers and read the other clients' games, and an exception
    /// escaping a packet handler drops that client's connection. A missed update costs one position.
    /// </summary>
    private void Guarded(Action handle)
    {
        try
        {
            handle();
        }
        catch (Exception e)
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastHandlerWarning);
            if (Stopwatch.GetElapsedTime(last, now) >= HandlerWarningInterval
                && Interlocked.CompareExchange(ref _lastHandlerWarning, now, last) == last)
            {
                Log.Warning(e, "Cow manager could not apply a monster packet");
            }
        }
    }

    /// <summary>
    /// Spacing of the sweep waypoints in game units. A killer standing on one covers the ground
    /// around it, so the spacing is a little under twice the range it clears.
    /// </summary>
    private const int SweepBand = 40;
    private const int SweepStep = 40;

    private readonly Lock _sweepLock = new();
    private bool _sweepBuilt;
    private AreaMap _levelMap;

    /// <summary>
    /// Whether a point lies inside the cow level, or null before its map is loaded. Tristram's drops
    /// stay known for the whole game and lie some 700 units from the cows, close enough that a
    /// distance test took them for leftovers here and sent the party looking for a path to them.
    /// </summary>
    public bool? InLevel(Point point) => _levelMap?.TryMapToPointInMap(point, out _);

    /// <summary>The cow level's map, or null before the sweep has loaded it.</summary>
    public AreaMap LevelMap => _levelMap;

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
        _levelMap = areaMap;
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
    public bool ActiveMode { get; }

    public async Task<List<Point>> GetPossibleStartingLocations(Game game)
    {
        var areaMap = await mapApiService.GetArea(game.MapId, Difficulty.Normal, D2NG.Core.D2GS.Act.Area.CowLevel);
        return StartLocations.Candidates(areaMap.Map, areaMap.LevelOrigin, areaMap.Npcs[(int)NPCCode.CowKing][0]);
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
            // A move packet's destination is where the monster is going, not where it is: the clients
            // place it along its path, and that is what every range check here should see.
            var at = SightingOf(entityId).Location ?? location;
            monster.Location = at;
            _aliveMonsters.TryUpdateLocation(entityId, at);
            if(lifePercentage.HasValue)
            {
                monster.LifePercentage = lifePercentage.Value;
            }
        }
    }

    /// <summary>
    /// Whether this is the Cow King, who arrives as an ordinary Hell Bovine with the boss flag set.
    /// </summary>
    /// <remarks>
    /// superuniques.bin record 39, "The Cow King", gives his base class as 391 - Hell Bovine - and
    /// he is the only Hell Bovine super unique, so the class and the super unique marker identify
    /// him between them. The boss flag alone does not: the level is full of randomly uniqued cow
    /// packs that carry it too. <see cref="NPCCode.CowKing"/> is no use here either - 651 is a DS1
    /// preset id, which is the map API's key for a placed unit, and the server never sends it.
    /// </remarks>
    private static bool IsCowKing(AssignNpcPacket packet)
        => CowKing.IsCowKing(packet.UniqueCode, packet.MonsterEnchantments, packet.IsBossMinion);

    /// <summary>
    /// Whether this NPC is one of the party's mercenaries or a friendly summon.
    /// </summary>
    /// <remarks>
    /// Mercenaries are recognised by owner rather than class: the 0x81 each client gets on joining
    /// names its own merc, and NPCCode's hireling entries do not line up with 1.09's monstats.
    /// </remarks>
    private bool IsOnOurSide(AssignNpcPacket packet)
        => NPCHelpers.IsFriendly(packet.UniqueCode)
            || _listeners.Any(c => c.Game.Me?.MercId == packet.EntityId);

    private void HandleAssignNPC(AssignNpcPacket packet)
    {
        if (IsCowKing(packet))
        {
            // His lightning immunity needs no special case: he arrives as a Hell Bovine, which is
            // zero across the board in Hell, carrying LightningEnchanted and MagicResistant, and
            // 0 + 75 + 40 clears the immunity threshold on its own. A nova sorceress is told he is
            // beyond her by the ordinary resistance check.
            Log.Information("Cow King assigned at {Location}, enchantments {Enchantments}, active mode {ActiveMode}",
                packet.Location, string.Join(", ", packet.MonsterEnchantments), ActiveMode);

            if (!ActiveMode)
            {
                // Nobody here can finish him when only the nova sorceresses are killing, so the
                // party stays clear rather than walking over and stalling on him. His guards go
                // with him: they share his lightning enchantment, and every sorceress kept
                // claiming them, backing off and claiming them again until the game ran out -
                // with the drops of the last packs still on the ground.
                _cowKingLocation = packet.Location;
                var guards = _aliveMonsters.Within(packet.Location, KingGuardRadius, int.MaxValue).Where(m => m.IsBossMinion).ToList();
                foreach (var guard in guards)
                {
                    RemoveMonster(guard.Id, killed: false);
                }

                if (guards.Count > 0)
                {
                    Log.Information("Leaving the Cow King's {Count} guards alone", guards.Count);
                }

                return;
            }
        }

        if (!ActiveMode && packet.IsBossMinion && _cowKingLocation != null
            && packet.Location.Distance(_cowKingLocation) <= KingGuardRadius)
        {
            return;
        }

        // Everything in the level counts. Which character can hurt a given monster is decided from
        // its resistances when the attack is chosen, so there is nothing to configure here and
        // nothing to leave out - a monster type nobody thought to list used to be invisible.
        var tracked = _aliveMonsters.TryGetValue(packet.EntityId, out var known);
        switch (MonsterTracking.OnAssign(_confirmedDead.ContainsKey(packet.EntityId), IsOnOurSide(packet), tracked, packet.LifePercentage))
        {
            case AssignOutcome.Ignore:
                return;
            case AssignOutcome.RemoveDead:
                RemoveMonster(packet.EntityId);
                return;
            case AssignOutcome.Move:
                // Assigned again on coming back into view, where it stands now.
                known.Location = packet.Location;
                known.LifePercentage = packet.LifePercentage;
                _aliveMonsters.TryUpdateLocation(packet.EntityId, packet.Location);
                return;
        }

        var cluster = _clusters.FindOrRegister(packet.Location, out var created);
        if (created)
        {
            Log.Information($"Adding new cluster at {packet.Location}");
        }

        var added = _aliveMonsters.TryAdd(packet.EntityId, packet.Location, new AliveMonster
        {
            Id = packet.EntityId,
            Location = packet.Location,
            NPCCode = packet.UniqueCode,
            MonsterEnchantments = packet.MonsterEnchantments,
            IsBossMinion = packet.IsBossMinion,
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
        RefreshPositions();
        return _aliveMonsters.Within(location, distance, numberOfMonsters);
    }

    /// <summary>How often tracked positions are read again from the clients' world objects.</summary>
    private static readonly TimeSpan PositionRefreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly Stopwatch _sinceRefresh = Stopwatch.StartNew();
    private int _refreshing;

    /// <summary>
    /// Moves every tracked monster to where the clients place it now, and its cluster with it, and drops
    /// the ones a client has seen die. The positions used to be the destinations of move packets,
    /// 4.5 units off at the median against 1.0 for the clients' own simulated positions.
    /// </summary>
    private void RefreshPositions()
    {
        if (_sinceRefresh.Elapsed < PositionRefreshInterval || Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            _sinceRefresh.Restart();
            var members = new List<(uint ClusterId, Point Location)>();
            foreach (var monster in _aliveMonsters.Snapshot())
            {
                var (location, dead) = SightingOf(monster.Id);
                if (dead)
                {
                    RemoveMonster(monster.Id);
                    continue;
                }

                if (location != null && location != monster.Location)
                {
                    monster.Location = location;
                    _aliveMonsters.TryUpdateLocation(monster.Id, location);
                }

                members.Add((monster.ClusterId, monster.Location));
            }

            _clusters.Follow(members);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>Where the clients that see this monster place it, the freshest first, and whether any saw it die.</summary>
    private (Point Location, bool Dead) SightingOf(uint entityId)
    {
        var sightings = new List<(Point, TimeSpan)>(2);
        var dead = false;
        foreach (var client in _listeners)
        {
            if (client.Game.WorldObjects.TryGetValue((entityId, EntityType.NPC), out var seen))
            {
                dead |= MonsterTracking.IsDead(seen.State);
                sightings.Add((seen.Location, seen.LocationAge));
            }
        }

        return (MonsterTracking.Freshest(sightings), dead);
    }



    /// <summary>Whether the level has been picked over and a fresh one is worth more than staying.</summary>
    private bool IsProducingTooSlowly()
    {
        return _sinceLastKill.Elapsed > HuntPatience;
    }

    /// <summary>Souls per minute over the hunt so far, the number the whole run is judged on.</summary>
    public double KilledPerMinute => _timeInLevel.Elapsed.TotalMinutes > 0
        ? _killed / _timeInLevel.Elapsed.TotalMinutes
        : 0;

    public int Killed => _killed;

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
        RefreshPositions();
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
        RefreshPositions();
        foreach (var monster in _aliveMonsters.Within(client.Game.Me.Location, distance, int.MaxValue))
        {
            var inWorld = client.Game.WorldObjects.TryGetValue((monster.Id, EntityType.NPC), out var seen);
            switch (MonsterTracking.OnPrune(inWorld, seen?.State ?? EntityState.Alive))
            {
                case PruneOutcome.OutOfSight:
                    RemoveMonster(monster.Id, killed: false);
                    break;
                case PruneOutcome.Dead:
                    RemoveMonster(monster.Id);
                    break;
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

        // Packet handlers and the position refresh both get here; the id counts once whoever does.
        if (!killed || !_confirmedDead.TryAdd(entityId, 0))
        {
            return;
        }

        Interlocked.Increment(ref _killed);
        lock (_killLock)
        {
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
    /// Claims the next cluster for a killer. <paramref name="anchor"/> is where the clearing should
    /// happen - the walking party - rather than where the killer happens to be standing, so the
    /// group works one area together and everything that opens up is reachable.
    /// </summary>
    /// <summary>
    /// Takes the next cluster this client should work, optionally skipping the ones it cannot hurt.
    /// </summary>
    /// <param name="canHarm">
    /// Whether this client can damage a given monster. A claimant that takes a pack it cannot shift
    /// holds it until it gives up and then hands it back marked done, so the characters who could
    /// have killed it are never offered it. Clusters with no monster currently tracked are still
    /// claimable: not knowing what is there is not a reason to leave it, and treating unknown as
    /// unworkable would leave clusters nobody ever takes and a level that never finishes.
    /// </param>
    public MonsterCluster ClaimNextCluster(
        Client client,
        int fromSweepIndex = 0,
        Point anchor = null,
        double maxDistance = double.MaxValue,
        Func<AliveMonster, bool> canHarm = null)
    {
        RefreshPositions();
        var from = anchor ?? client.Game.Me.Location;
        Func<MonsterCluster, bool> canWork = null;
        if (canHarm != null)
        {
            // Bucketed once per claim rather than per candidate cluster: the claim walks every
            // cluster, and asking the monster grid inside that walk would turn one pass into two
            // nested ones.
            var known = new HashSet<uint>();
            var harmable = new HashSet<uint>();
            foreach (var monster in _aliveMonsters.Snapshot())
            {
                known.Add(monster.ClusterId);
                if (canHarm(monster))
                {
                    harmable.Add(monster.ClusterId);
                }
            }

            canWork = cluster => !known.Contains(cluster.Id) || harmable.Contains(cluster.Id);
        }

        return _clusters.ClaimNearest(from, fromSweepIndex, maxDistance, nearestFirst: anchor != null, canWork);
    }

    /// <summary>
    /// What is left of the active phase's budget; null outside active mode or before the hunt has set
    /// off, when nothing bounds it. Not the patience for the next kill: the party goes on killing
    /// while a member is away.
    /// </summary>
    public TimeSpan? HuntTimeLeft => ActiveMode && _activePhase.IsRunning ? _budget - _activePhase.Elapsed : null;

    /// <summary>Whether the hunt's budget has run out, so cows may still stand around the party.</summary>
    public bool OutOfTime => ActiveMode && _activePhase.IsRunning && _activePhase.Elapsed > _budget;

    /// <summary>
    /// The party's own hunt budget ran out, not <see cref="ClearingBudget"/>: the level was cut short on purpose, with packs
    /// standing, so the party leaves at once. A game that clears the level ends at its budget as it always did, with the
    /// loot sweep and the muling farmer's Cain trip after it.
    /// </summary>
    public bool CutShort => OutOfTime && _budget != ClearingBudget;

    /// <summary>Starts the overall hunt budget, on the first cluster the party sets off towards.</summary>
    public void NotifyHuntStarted()
    {
        if (!_activePhase.IsRunning)
        {
            _budget = ClearsTheLevel() ? ClearingBudget : HuntBudget;
            Log.Information($"Hunting party setting off, active phase budget is {_budget}");
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

        if (OutOfTime || (ActiveMode && _activePhase.IsRunning && IsProducingTooSlowly()))
        {
            return true;
        }

        // Nothing left that can be claimed, rather than every cluster marked done: one that cannot
        // be worked will never become workable, and waiting on it only burned the patience timer at
        // the end of every game.
        return !_clusters.AnyPending();
    }

    public string DescribeProgress()
    {
        var abandoned = _clusters.AbandonedCount();
        var abandonedNote = abandoned > 0 ? $" ({abandoned} abandoned)" : string.Empty;
        return $"clusters {_clusters.DoneCount()}/{_clusters.Count()}{abandonedNote}, {_killed} killed at {KilledPerMinute:F1}/min";
    }
}
