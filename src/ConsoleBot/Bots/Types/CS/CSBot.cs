using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.MonsterData;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.ObjectData;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.CS;

public class CSBot : MultiClientBotBase
{
    private readonly CsConfiguration _csconfig;

    private readonly ITownManagementService _townManagementService;
    private readonly IAttackService _attackService;
    private readonly IMapApiService _mapApiService;
    private readonly ShrineTable _shrines;
    private readonly List<Client> _clients = [];
    private CsState _state = new();
    private readonly ConcurrentDictionary<uint, byte> _shrinesSeen = new();
    private readonly ConcurrentDictionary<string, byte> _deathsSeen = new();

    public CSBot(
        IOptions<BotConfiguration> config,
        IOptions<CsConfiguration> csconfig,
        IExternalMessagingClient externalMessagingClient,
        IMuleService muleService,
        ITownManagementService townManagementService,
        IPathingService pathingService,
        IMapApiService mapApiService,
        IAttackService attackService,
        ShrineTable shrines) : base(config, csconfig, externalMessagingClient, muleService, pathingService)
    {
        _mapApiService = mapApiService;
        _shrines = shrines;
        _csconfig = csconfig.Value;
        _townManagementService = townManagementService;
        _attackService = attackService;
    }

    private const int PickupSafetyRadius = 15;


    private const int PotionReach = 4;

    private const int TaxiPotionBuffer = 14;

    /// <summary>She drinks one a second under fire; opening a seal with fewer than this in hand ended in a dry chicken at the top seal.</summary>
    private const int TaxiRestockBelow = 4;

    /// <summary>The taxi escapes up to 40 from a spot the boss spawns up to 55 from, so his corpse can lie 90 away when she looks for it.</summary>
    private const double BossDownCheckRadius = 120;

    /// <summary>At twenty the hammerdin, with twelve free cells, got no spares and ran dry mid-fight; six keeps room for the boss drop.</summary>
    private const int FollowerInventoryReserve = 6;

    private const double PaladinEngageDistance = 10;

    private const double PartyEngagedRadius = 12;

    private const double TaxiApproachMinLife = 0.6;

    private const double TaxiEscapeLossPerSecond = 0.20;

    private const double TaxiEscapeLifeFloor = 0.40;

    private const double TaxiApproachRange = 35;

    /// <summary>Static Field's reach at level 20 is about sixteen; twelve leaves room for the target to step.</summary>
    private const double StaticRange = 12;

    /// <summary>A boss this far out is still her target once a follower is on him; at 35 she spent a far De Seis fight on the trash beside her.</summary>
    private const double StaticBossRange = 60;

    private const int StaticBossMaxEscort = 2;

    private static readonly TimeSpan StaticInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Below this share of life a monster is not worth another static; the party finishes it.</summary>
    private const double StaticFloor = 15;

    /// <summary>Nova's ring reaches about seven around her.</summary>
    private const double NovaRange = 6;


    /// <summary>The taxi died at the top seal drinking eight potions in eight seconds from 1240 down to 0, with her own portal ten units away. Under this, with hostiles on her, she takes it. At 0.55 she left twelve fights in thirteen games, since her potions hold her at 55-70% in any fight; 0.45 is below where she sits and above where she died.</summary>
    private const double TaxiRetreatLife = 0.45;

    private const int TaxiRetreatRadius = 20;

    private const double TaxiHealedLife = 0.9;

    private static readonly TimeSpan TaxiHealWait = TimeSpan.FromSeconds(8);

    /// <summary>After an escape she used to hop straight back to the kill spot the pack had overrun; for this long the spot she escaped to is her anchor.</summary>
    private static readonly TimeSpan TaxiEscapeHold = TimeSpan.FromSeconds(4);

    private const int SealAssembleCount = 2;

    private const double SealAssembleRadius = 30;

    private static readonly TimeSpan SealAssembleTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Short: the trash at the kill spot keeps arriving, so at six seconds the cap was hit in seven of seven games.</summary>
    private static readonly TimeSpan SealClearTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long after the taxi starts on a seal the paladin hammers regardless of targets; the boss appears within about two seconds of the last seal.</summary>
    private static readonly TimeSpan SealHammerWindow = TimeSpan.FromSeconds(6);

    private const int SealClearRadius = 20;

    private const int SealClearMaxHostiles = 2;

    private const int SealWaitDangerRadius = 12;

    private const double SorceressSkillRange = 20;

    private const double OrbClusterRadius = 8;

    private const int IronMaidenThreatRadius = 25;

    private const double IronMaidenLeash = 25;

    /// <summary>The longest curse seen lasted 35 seconds; past this the curse is taken to have lifted without the client hearing of it.</summary>
    private static readonly TimeSpan IronMaidenTownWait = TimeSpan.FromSeconds(40);

    /// <summary>At 38 the relocation fired on a spawn 49 away and put the party 27 from De Seis inside ten Doom Knights; far spawns are for the followers closing on him, not for moving the portal.</summary>
    private const double BossRelocateThreshold = 55;

    private const double BossStandoffDistance = 28;

    /// <summary>
    /// Vizier's fights took 16s up to 40 out, 21s from 40 to 59 with few failures, and 28s from 60
    /// with nearly half failing. At 40 half the left seals relocated, for no gain below 60.
    /// </summary>
    private const double VizierRelocateThreshold = 55;

    /// <summary>
    /// De Seis is not moved to at all: his knights make any new spot near him the most dangerous place
    /// in the sanctuary. The party holds the spot and closes on him once his escort thins, which the
    /// boss focus already does.
    /// </summary>
    private const double NeverRelocate = double.MaxValue;

    /// <summary>The point <paramref name="distance"/> from <paramref name="center"/> toward <paramref name="toward"/>, turned by <paramref name="degrees"/> around the center.</summary>
    private static Point PointAround(Point center, Point toward, double distance, double degrees)
    {
        double dx = toward.X - center.X;
        double dy = toward.Y - center.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            return null;
        }

        var radians = degrees * Math.PI / 180;
        var ux = dx / length;
        var uy = dy / length;
        var rx = ux * Math.Cos(radians) - uy * Math.Sin(radians);
        var ry = ux * Math.Sin(radians) + uy * Math.Cos(radians);
        return new Point((ushort)Math.Round(center.X + rx * distance), (ushort)Math.Round(center.Y + ry * distance));
    }

    private static readonly TimeSpan BossSpawnWait = TimeSpan.FromSeconds(5);

    /// <summary>Vizier once stood inside a wall, 23 from the spot, untouched for the whole seal timeout. A boss whose life and position have not moved for this long, on a tile nobody can walk, is unreachable and the game is lost; lava alone is not proof: Vizier, alone among the three, crosses it, and arrows and orbs reach him there, so his life keeps moving and this stays quiet.</summary>
    private static readonly TimeSpan UnreachableBossTimeout = TimeSpan.FromSeconds(12);

    private static readonly TimeSpan BossScoutWait = TimeSpan.FromSeconds(3);

    /// <summary>With this many or fewer hostiles left beside the boss, and the spot itself quiet, the followers stop fighting around the amazon and close on him.</summary>
    private const int BossFocusMaxEscort = 3;

    private const double BossFocusEscortRadius = 12;

    private const int BossFocusMaxCrowd = 8;

    private const double BossFocusCrowdRadius = 40;

    private const double BossFocusSearchRadius = 60;

    /// <summary>With this few hostiles left at the spot there is nothing else worth staying for; at three the party stood dormant with five around it while De Seis sat out of the amazon's range.</summary>
    private const int BossFocusQuietSpot = 6;

    private const double BossFocusSpotRadius = 20;

    /// <summary>A boss this far out does not come to the spot; his trash trickled in for forty seconds while he stood untouched, and the seal timed out.</summary>
    /// <summary>Anything past this is "not on the spot": at 35 the party sat defensive for half a minute while the amazon shot De Seis from range with his escort intact.</summary>
    private const double BossFocusFarDistance = 22;

    /// <summary>Going to a far boss is for when the trash that was coming has come and been thinned, not into the whole seal's population: the paladin walked into forty-four and lost 1400 life in three seconds.</summary>
    private const int BossFocusFarMaxCrowd = 25;


    private static readonly TimeSpan BossFocusFarDelay = TimeSpan.FromSeconds(5);

    private const double TaxiEscapeMinDistance = 20;

    /// <summary>Orb, hop, orb, hop: standing between orbs is what got her hit, and a moving target dodges the ranged attacks.</summary>
    private static readonly TimeSpan TaxiHopInterval = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan TaxiIdleHopInterval = TimeSpan.FromSeconds(0.8);

    private const double TaxiStepMin = 5;

    private const double TaxiStepMax = 10;

    /// <summary>How far from her anchor the quiet spots may lie, so moving about never walks her off the fight.</summary>
    private const double TaxiStepLeash = 12;

    /// <summary>Further than this from the kill spot with nothing to fight, she goes back to it.</summary>
    private const double TaxiStrayDistance = 25;

    /// <summary>
    /// The spot five to ten from her with the fewest monsters about it - those within four count
    /// triple - on walkable ground and within the leash of her anchor; null if there is none.
    /// </summary>
    private async Task<Point> QuietSpotNear(Client client, IEnumerable<WorldObject> enemies, Point anchor, double minDistance, double maxDistance)
    {
        var me = client.Game.Me.Location;
        var threats = enemies.Select(e => e.Location).ToList();
        Point best = null;
        var bestScore = int.MaxValue;
        for (var direction = 0; direction < 12; direction++)
        {
            var radians = direction * Math.PI / 6;
            foreach (var distance in new[] { minDistance, (minDistance + maxDistance) / 2, maxDistance })
            {
                var candidate = new Point((ushort)Math.Round(me.X + Math.Cos(radians) * distance), (ushort)Math.Round(me.Y + Math.Sin(radians) * distance));
                if (anchor != null && candidate.Distance(anchor) > TaxiStepLeash)
                {
                    continue;
                }

                var score = threats.Count(t => t.Distance(candidate) < 8) + 2 * threats.Count(t => t.Distance(candidate) < 4);
                if (score >= bestScore
                    || !await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, candidate))
                {
                    continue;
                }

                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static readonly TimeSpan CastLogInterval = TimeSpan.FromSeconds(3);

    private const double TaxiEscapeMaxDistance = 40;

    private static IEnumerable<Point> RetreatPoints(Point from, IReadOnlyList<Point> threats)
    {
        double dx = 0;
        double dy = 0;
        foreach (var threat in threats)
        {
            dx += from.X - threat.X;
            dy += from.Y - threat.Y;
        }

        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001)
        {
            dx = 1;
            dy = 0;
            length = 1;
        }

        var ux = dx / length;
        var uy = dy / length;
        foreach (var (angle, hop) in new[] { (0.0, 20.0), (0.9, 20.0), (-0.9, 20.0), (0.0, 12.0) })
        {
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var rx = ux * cos - uy * sin;
            var ry = ux * sin + uy * cos;
            yield return from.Add((short)(rx * hop), (short)(ry * hop));
        }
    }

    private static bool NoEnemiesNearby(Client client)
    {
        return !NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, PickupSafetyRadius).Any();
    }

    /// <summary>
    /// Whether a seal boss near this character is dead or dying. Diablo does not count: his death
    /// ends the game, and the loop that follows him is the taxi's to end.
    /// </summary>
    private bool SealBossIsDown(Client client)
    {
        return NPCHelpers.GetNearbySuperUniques(client, BossDownCheckRadius)
            .Any(w => w.NPCCode != NPCCode.Diablo && (w.State == EntityState.Dead || w.State == EntityState.Dieing) && !_state.IsEarlierBoss(w));
    }

    /// <summary>
    /// Every super unique any client knows to be dead when a seal starts is an earlier seal's boss.
    /// Vizier died 76 from the top seal's spot, inside the 120 the death check looks over, and De
    /// Seis' seal was called done three seconds after he spawned; Diablo then never came.
    /// </summary>
    private void RememberEarlierBosses()
    {
        foreach (var dead in _clients
            .Where(c => c.Game.IsInGame())
            .SelectMany(c => c.Game.WorldObjects.Values)
            .Where(w => w.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique) && (w.State == EntityState.Dead || w.State == EntityState.Dieing)))
        {
            _state.EarlierBosses[dead.Id] = dead.NPCCode;
        }
    }

    /// <summary>The seal boss of the current fight as this client sees him, or null once he is down or before he spawned.</summary>
    private WorldObject LiveBoss(Client client)
    {
        if (_state.BossId is not uint id)
        {
            return null;
        }

        var boss = client.Game.WorldObjects.GetValueOrDefault((id, EntityType.NPC));
        return boss == null || boss.State == EntityState.Dead || boss.State == EntityState.Dieing ? null : boss;
    }

    private const double FollowerRetreatLife = 0.5;

    /// <summary>She has 1041 life; at half of it the retreat fired at 446 and she was at 159 before the portal was up.</summary>
    private const double AmazonRetreatLife = 0.65;


    /// <summary>Higher for the barbarian: one Concentrate hit reflected by Iron Maiden costs him up to 1200.</summary>
    private const double BarbarianRetreatLife = 0.5;

    private const int FollowerRetreatRadius = 15;

    /// <summary>Followers that portalled out hurt and have not yet healed in town; they go back into the same fight once healed.</summary>
    private readonly ConcurrentDictionary<string, byte> _retreated = new();

    /// <summary>Followers that chickened and rejoined: they wait in town for the Diablo portal.</summary>
    private readonly ConcurrentDictionary<string, byte> _benchedUntilDiablo = new();

    /// <summary>
    /// A follower at half life with enemies on it leaves the fight, not the game: it portals to town,
    /// heals at Jamella and comes back through the taxi's current portal. Waiting for her next portal
    /// instead kept a barbarian with a full belt out of a whole seal after one Iron Maiden.
    /// The chicken at a fifth was a leave request that the server processed after the death in
    /// three cases last night; at half there is time, and the game keeps its other four.
    /// </summary>
    private async Task<bool> RetreatToTownIfHurt(Client client, double lifeFraction)
    {
        var me = client.Game.Me;
        if (me.MaxLife <= 0 || me.Life >= me.MaxLife * lifeFraction || client.Game.IsInTown())
        {
            return false;
        }

        if (!NPCHelpers.GetNearbyNPCs(client, me.Location, 1, FollowerRetreatRadius).Any())
        {
            return false;
        }

        Log.Warning("{Character} retreating to town at {Life} of {MaxLife} life", me.Name, me.Life, me.MaxLife);
        if (await _townManagementService.TakeTownPortalToTown(client))
        {
            _retreated[me.Name] = 0;
        }

        return true;
    }

    public override string GetName()
    {
        return "cs";
    }

    protected override void ResetForNextRun()
    {
        _state = new CsState();
        _shrinesSeen.Clear();
        _deathsSeen.Clear();
        _retreated.Clear();
        _benchedUntilDiablo.Clear();
    }

    private static void LogExperience(Client client, string moment)
    {
        if (!client.Game.IsInGame() || client.Game.Me == null)
        {
            return;
        }

        Log.Information("Experience {Character} {Experience} in {Area} at {Moment}",
            client.Game.Me.Name, client.Game.Me.Experience, client.Game.Area, moment);
    }

    private void LogDeath(Client client)
    {
        if (!client.Game.IsInGame() || client.Game.Me == null)
        {
            return;
        }

        if (client.Game.Me.MaxLife > 0
            && client.Game.Me.Life <= 0
            && _deathsSeen.TryAdd(client.Game.Me.Name, 0))
        {
            Log.Warning("{Character} died at {Location} in {Area}",
                client.Game.Me.Name, client.Game.Me.Location, client.Game.Area);
            RequestStop($"{client.Game.Me.Name} died at {client.Game.Me.Location} in {client.Game.Area}");
        }
    }

    private static async Task<bool> Phase(Client client, string phase, Func<Task<bool>> step)
    {
        var watch = Stopwatch.StartNew();
        LogExperience(client, $"{phase} start");
        var succeeded = await step();
        LogExperience(client, $"{phase} end");
        Log.Information("CS phase {Phase} {Result} in {Seconds:F1}s for {Character}",
            phase, succeeded ? "done" : "failed", watch.Elapsed.TotalSeconds, client.Game.Me.Name);
        return succeeded;
    }

    /// <summary>
    /// Every shrine any client sees outside town, once each. Without it a game with no experience
    /// shrine cannot be told apart from a game with no shrines at all.
    /// </summary>
    private void LogShrinesInView(Client client)
    {
        if (_shrines.Count == 0 || client.Game.IsInTown())
        {
            return;
        }

        foreach (var shrine in ShrineHelpers.VisibleShrines(client.Game, _shrines))
        {
            if (_shrinesSeen.TryAdd(shrine.Id, 0))
            {
                Log.Information("Shrine seen: {Type} at {Location} in {Area} by {Character}",
                    ShrineHelpers.TypeOf(shrine), shrine.Location, client.Game.Area, client.Game.Me.Name);
            }
        }
    }

    protected override Task PostInitializeAllJoined(List<Client> clients)
    {
        _clients.Clear();
        _clients.AddRange(clients);
        return base.PostInitializeAllJoined(clients);
    }

    /// <summary>
    /// Notes an experience shrine the moment any client has one in view. The kind comes with the
    /// assign object packet, so no click is needed; the location is what a client that never saw the
    /// object needs to go and take it later.
    /// </summary>
    private void RememberExperienceShrine(Client client)
    {
        LogShrinesInView(client);
        if (_state.ExperienceShrineLocation != null || _shrines.Count == 0 || client.Game.Area != Area.ChaosSanctuary)
        {
            return;
        }

        var shrine = ShrineHelpers.FindShrine(client.Game, _shrines, ShrineType.ExperienceBoost);
        if (shrine != null)
        {
            _state.ExperienceShrineLocation = shrine.Location;
            Log.Information($"Client {client.Game.Me.Name} spotted an experience shrine at {shrine.Location}");
        }
    }

    /// <summary>
    /// The experience shrine goes to the highest level character once the seals are done, right
    /// before Diablo: experience is level wide, so the boost is worth the most on the character whose
    /// share of it is largest, and Diablo is the biggest kill of the run. Claims the shrine before
    /// heading out so a second client does not make the same detour.
    /// </summary>
    private async Task TryTakeExperienceShrine(Client client)
    {
        if (!_state.SealsDone
            || _state.ExperienceShrineTaken
            || _state.ExperienceShrineLocation == null
            || client.Game.Area != Area.ChaosSanctuary
            || !client.Game.Me.Name.Equals(_state.ExperienceShrineTaker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _state.ExperienceShrineTaken = true;
        var location = _state.ExperienceShrineLocation;
        Log.Information($"Client {client.Game.Me.Name} is the highest level and goes for the experience shrine at {location}");
        await ShrineHelpers.TakeShrine(client.Game, _pathingService, _mapApiService, _shrines, ShrineType.ExperienceBoost, location, EntityEffect.ShrineExperience);
    }

    protected override async Task<bool> PrepareForRun(Client client, AccountConfig account)
    {
        StopIfDied(client);
        if (StopBatch)
        {
            return false;
        }

        var townManagementOptions = new TownManagementOptions(account, Act.Act4) { RejuvenationsToKeep = RejuvenationsToKeep };

        var beltShortfall = account.HealthPotionTarget(client.Game.Belt.Height)
            - client.Game.Belt.GetHealthPotionsInSlots(account.HealthSlots).Count;
        var carried = client.Game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion);
        // A follower's spare potions come out of the cells it picks loot into. The amazon carried
        // fourteen and had two cells left, so every arrow quiver she picked up stuck on her cursor.
        var roomForPotions = client.Game.Inventory.FreeCellCount() + carried - FollowerInventoryReserve;
        // The hammerdin and the necromancer fight on mana: eight belt potions, drunk only under ten
        // percent, left the paladin dry for most of every fight. They carry spares and the health
        // buffer gives up the cells.
        var manaSpares = DrinksMana(client) ? Math.Clamp(roomForPotions / 3, 0, FollowerManaSpares) : 0;
        var buffer = IsTeleportClient(client) ? TaxiPotionBuffer : Math.Clamp(roomForPotions - manaSpares, 0, TaxiPotionBuffer);
        townManagementOptions.HealthPotionsToBuy = Math.Max(0, Math.Max(0, beltShortfall) + buffer - carried);
        if (manaSpares > 0)
        {
            townManagementOptions.ManaPotionsToBuy = Math.Max(0, account.ManaPotionTarget(client.Game.Belt.Height) + manaSpares - InventoryHelpers.GetTotalManaPotions(client.Game));
        }

        await Phase(client, "town", () => GeneralHelpers.TryWithTimeout(
            async (_) =>
            {
                var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
                if (townTaskResult.ShouldMule)
                {
                    ClientsNeedingMule.Add(client.LoggedInUserName());
                }
                if (!townTaskResult.Succes)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }
                return townTaskResult.Succes;
            },
            TimeSpan.FromSeconds(20)));

        if (IsTeleportClient(client))
        {
            return await Phase(client, "approach", async () =>
            {
            var approachClock = Stopwatch.StartNew();
            Log.Debug($"Client {client.Game.Me.Name} Taking waypoint to {Waypoint.RiverOfFlame}");
            if (!await _townManagementService.TakeWaypoint(client, Waypoint.RiverOfFlame))
            {
                Log.Debug($"Client {client.Game.Me.Name} Taking waypoint failed at location {client.Game.Me.Location}");
                return false;
            }

            Log.Information("{Character} approach: waypoint taken at {Seconds:F1}s", client.Game.Me.Name, approachClock.Elapsed.TotalSeconds);
            Log.Debug($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary}");
            var pathToChaos = await _pathingService.GetPathToObjectWithOffset(client.Game.MapId, Difficulty.Normal, Area.RiverOfFlame, client.Game.Me.Location, EntityCode.WaypointAct4Levels, -6, -319, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToChaos, MovementMode.Teleport))
            {
                Log.Debug($"Client {client.Game.Me.Name} Teleporting to {Area.ChaosSanctuary} warp failed at location {client.Game.Me.Location}");
                return false;
            }

            Log.Information("{Character} approach: sanctuary warp at {Seconds:F1}s", client.Game.Me.Name, approachClock.Elapsed.TotalSeconds);
            var goalLocation = client.Game.Me.Location.Add(0, -20);
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(goalLocation);
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Debug($"Client {client.Game.Me.Name} Teleporting to location within {Area.ChaosSanctuary} failed at location {client.Game.Me.Location}");
                return false;
            }

            var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
            Log.Information("{Character} approach: star path of {Hops} hops ready at {Seconds:F1}s", client.Game.Me.Name, pathToDiabloStar.Count, approachClock.Elapsed.TotalSeconds);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
            {
                Log.Debug($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
                return false;
            }

            // She stays at the star. The round trip to town and back through her own portal that
            // used to end the approach bought nothing: the followers take her portal from town
            // whether she is standing in town or beside it, and the entry phase creates it.
            Log.Information("{Character} approach: at the star at {Seconds:F1}s", client.Game.Me.Name, approachClock.Elapsed.TotalSeconds);
            return true;
            });
        }

        return true;
    }

    private readonly ConcurrentDictionary<string, long> _lastExperience = new();

    private static readonly HashSet<EntityEffect> PlayerCurses =
        [EntityEffect.Amplifydamage, EntityEffect.Decrepify, EntityEffect.Lowerresist, EntityEffect.Weaken, EntityEffect.Ironmaiden];

    /// <summary>A loss of this share of maximum life between two life packets is logged with what stood around the character and which curses were on it.</summary>
    private const double BurstShare = 0.12;

    protected override void PostInitializeClient(Client client, AccountConfig accountCharacter)
    {
        var lastLife = 0;
        var lastAt = DateTime.MinValue;
        var lastLogged = DateTime.MinValue;
        client.OnReceivedPacketEvent(InComingPacket.LifeManaUpdate, _ => NoteBurst(client, ref lastLife, ref lastAt, ref lastLogged));
        client.OnReceivedPacketEvent(InComingPacket.LifeManaUpdatePot, _ => NoteBurst(client, ref lastLife, ref lastAt, ref lastLogged));
    }

    private static void NoteBurst(Client client, ref int lastLife, ref DateTime lastAt, ref DateTime lastLogged)
    {
        var me = client.Game.Me;
        if (me == null || !client.Game.IsInGame() || client.Game.Area != Area.ChaosSanctuary)
        {
            return;
        }

        var life = me.Life;
        var now = DateTime.Now;
        var loss = lastLife - life;
        if (lastLife > 0 && me.MaxLife > 0 && loss >= me.MaxLife * BurstShare && now - lastLogged > TimeSpan.FromSeconds(1))
        {
            var curses = string.Join(",", me.Effects.Keys.Where(PlayerCurses.Contains));
            var around = NPCHelpers.GetNearbyNPCs(client, me.Location, 3, 30)
                .Select(e => $"{e.NPCCode}@{(int)e.Location.Distance(me.Location)}");
            var close = NPCHelpers.GetNearbyNPCs(client, me.Location, 50, 10).Count();
            Log.Information("{Character} burst: lost {Loss} of {MaxLife} in {Ms}ms at {Location}, {Close} within ten, nearest {Around}, curses [{Curses}]",
                me.Name, loss, me.MaxLife, (int)(now - lastAt).TotalMilliseconds, me.Location, close, string.Join(" ", around), curses);
            lastLogged = now;
        }

        lastLife = life;
        lastAt = now;
    }

    private const long DeathExperienceFloor = 1_000_000;

    private void RememberExperience(Client client)
    {
        if (client.Game.IsInGame() && client.Game.Me != null && client.Game.Area == Area.ChaosSanctuary)
        {
            _lastExperience[client.Game.Me.Name] = client.Game.Me.Experience;
        }
    }

    /// <summary>
    /// Lifetime experience only ever rises, so a town start below the last sanctuary sample is a
    /// death - the one signal that cannot be faked. The chicken's own Life reading is not: three of
    /// the overnight deaths were logged as leaves at 0, 19 and 795 life, and the in-game check never
    /// sees a character that died after its leave request went out. A reading that has fallen by
    /// more than a fifth is the stale value a client reports while leaving, not a death.
    /// </summary>
    private void StopIfDied(Client client)
    {
        if (client.Game.Me == null || !_lastExperience.TryGetValue(client.Game.Me.Name, out var last))
        {
            return;
        }

        var lost = last - client.Game.Me.Experience;
        if (lost > DeathExperienceFloor && lost < last / 5)
        {
            Log.Fatal("{Character} died: experience fell from {Last} to {Now}, {Lost} lost",
                client.Game.Me.Name, last, client.Game.Me.Experience, lost);
            RequestStop($"{client.Game.Me.Name} died, {lost:N0} experience lost");
        }
    }

    protected override async Task<bool> PerformRun(Client client, AccountConfig account)
    {
        using var sampler = new ExecuteAtInterval((_, _) =>
        {
            LogExperience(client, "sample");
            RememberExperience(client);
            LogDeath(client);
        }, TimeSpan.FromSeconds(5));
        sampler.Start();
        LogExperience(client, "run start");
        try
        {
            return await PerformCsRun(client, account);
        }
        finally
        {
            sampler.Stop();
            LogExperience(client, "run end");
        }
    }

    private async Task<bool> PerformCsRun(Client client, AccountConfig account)
    {
        if (IsTeleportClient(client))
        {
            var result = await TaxiCs(client, account);
            NextGame.TrySetResult(true);

            return result;
        }
        else
        {
            var action = GetKillActionForClass(client, account);

            var movementMode = MovementHelpers.PreferredMovement(client.Game);
            var pathToTpLocation = await _pathingService.GetPathToLocation(client.Game, new Point(5042, 5036), movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTpLocation, movementMode))
            {
                Log.Warning($"Client {client.Game.Me.Name} {movementMode} to portal area failed at {client.Game.Me.Location}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                if (client.Game.IsInTown() && _state.TeleportId != null)
                {
                    Log.Debug($"Client {client.Game.Me.Name} taking town portal to chaos");
                    var teleportPlayer = client.Game.Players.FirstOrDefault(p => p.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.OrdinalIgnoreCase));
                    if (teleportPlayer == null || !await _townManagementService.TakeTownPortalToArea(client, teleportPlayer, Area.ChaosSanctuary))
                    {
                        return false;
                    }

                    return true;
                }

                return false;
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Debug($"Client {client.Game.Me.Name} Taking townportal to {Area.ChaosSanctuary} failed");
                return false;
            }

            if (!await WaitForBo(client, account, action))
            {
                return false;
            }

            return await BaseCsBot(client, account, action);
        }
    }

    private static bool OutOfHealthPotions(Client client)
    {
        // Before the last one, not after it: a follower that only leaves when dry has already spent
        // the fight disengaged.
        // Belt plus inventory, and a rejuvenation is a potion: a paladin with fifteen full
        // rejuvenations in his inventory left fights as "dry".
        return client.Game.Belt.NumOfHealthPotions() < 3
            && !client.Game.Inventory.Items.Any(i => i.Classification == ClassificationType.HealthPotion || i.Classification == ClassificationType.RejuvenationPotion)
            && client.Game.Belt.NumOfRejuvenationPotions() == 0;
    }

    /// <summary>
    /// A follower that has drunk its last potion goes to town, buys, and comes back through the
    /// taxi's current portal. The hammerdin drinks ten a game and carries four spares, so without
    /// this he spent the second half of most games disengaged, and he is the boss killer.
    /// </summary>
    private async Task RestockFollower(Client client, AccountConfig account, CsState ownState)
    {
        Log.Warning("{Character} is out of health potions, restocking in town", client.Game.Me.Name);
        if (!await _townManagementService.TakeTownPortalToTown(client))
        {
            return;
        }

        await ResupplyAndRejoin(client, account, ownState);
    }

    /// <summary>
    /// Heals at Jamella and tops up potions, then clears the follower's portal so the main loop takes
    /// the taxi's current one back into the fight. Jamella sells the potions too, so a hurt follower
    /// is healed on the same visit.
    /// </summary>
    private async Task ResupplyAndRejoin(Client client, AccountConfig account, CsState ownState)
    {
        var options = new TownManagementOptions(account, Act.Act4)
        {
            HealthPotionsToBuy = account.HealthPotionTarget(client.Game.Belt.Height) + FollowerRestockSpares,
        };
        if (DrinksMana(client))
        {
            options.ManaPotionsToBuy = Math.Max(0, account.ManaPotionTarget(client.Game.Belt.Height) + FollowerManaSpares - InventoryHelpers.GetTotalManaPotions(client.Game));
        }

        await _townManagementService.PerformTownTasks(client, options);
        ownState.TeleportId = null;
    }

    private static bool DrinksMana(Client client)
    {
        return client.Game.Me.Class == CharacterClass.Paladin || client.Game.Me.Class == CharacterClass.Necromancer;
    }

    private const int FollowerRestockSpares = 6;

    private const int FollowerManaSpares = 4;

    private async Task<bool> BaseCsBot(Client client, AccountConfig account, Func<Task> action)
    {
        var followed = await FollowTaxi(client, account, action);

        // A chicken leaves the game, not the run. Back in at once, it has the seals' time to heal and
        // shop and is ready at the Diablo portal; it stays in town until then, since the seals are
        // where it was in danger.
        if (!await IsNextGame() && !client.Game.IsInGame() && !IsTeleportClient(client)
            && await RejoinCurrentGame(client, account))
        {
            _retreated[client.Game.Me.Name] = 0;
            _benchedUntilDiablo[client.Game.Me.Name] = 0;
            return await FollowTaxi(client, account, action) && (await IsNextGame() || client.Game.IsInGame());
        }

        // Checked after the rejoin, not before it: leaving the game is exactly what makes the fight
        // loop report failure, and returning on that skipped the rejoin for every chicken.
        if (!followed || (!await IsNextGame() && !client.Game.IsInGame()))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> FollowTaxi(Client client, AccountConfig account, Func<Task> action)
    {
        var ownState = new CsState();
        DateTime? cursedInTownSince = null;
        while (!await IsNextGame() && client.Game.IsInGame())
        {
            await Task.Delay(100);
            if (!client.Game.IsInTown() && OutOfHealthPotions(client))
            {
                await RestockFollower(client, account, ownState);
                continue;
            }

            if (client.Game.IsInTown() && client.Game.Me.Effects.ContainsKey(EntityEffect.Ironmaiden))
            {
                cursedInTownSince ??= DateTime.Now;
                if (DateTime.Now - cursedInTownSince < IronMaidenTownWait)
                {
                    continue;
                }
            }
            else
            {
                cursedInTownSince = null;
            }

            if (client.Game.IsInTown() && _retreated.TryRemove(client.Game.Me.Name, out _))
            {
                await ResupplyAndRejoin(client, account, ownState);
                Log.Information("{Character} healed in town at {Life} of {MaxLife} life, rejoining", client.Game.Me.Name, client.Game.Me.Life, client.Game.Me.MaxLife);
                continue;
            }

            // A rejoined chicken is to wait in town for the Diablo portal; one was back in the top seal
            // six seconds after healing, by a way the log did not show, and chickened again.
            if (_benchedUntilDiablo.ContainsKey(client.Game.Me.Name) && !_state.DiabloPortalUp && !client.Game.IsInTown())
            {
                Log.Warning("{Character} is benched until Diablo but in {Area} at {Location}, going back to town", client.Game.Me.Name, client.Game.Area, client.Game.Me.Location);
                await _townManagementService.TakeTownPortalToTown(client);
                continue;
            }

            if (_state.TeleportHasChanged(ownState) && !client.Game.IsInTown())
            {
                Log.Debug($"Client {client.Game.Me.Name} Taking town portal to town");
                if (!await _townManagementService.TakeTownPortalToTown(client))
                {
                    continue;
                }
            }

            var newTeleportId = _state.TeleportId;
            if (client.Game.IsInTown() && newTeleportId != null && newTeleportId != ownState.TeleportId
                && (!_benchedUntilDiablo.ContainsKey(client.Game.Me.Name) || _state.DiabloPortalUp))
            {
                Log.Information("{Character} following the taxi through portal {Portal}, benched {Benched}, Diablo portal up {DiabloUp}",
                    client.Game.Me.Name, newTeleportId, _benchedUntilDiablo.ContainsKey(client.Game.Me.Name), _state.DiabloPortalUp);
                var teleportPlayer = client.Game.Players.FirstOrDefault(p => p.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.OrdinalIgnoreCase));
                if (teleportPlayer == null || !await _townManagementService.TakeTownPortalToArea(client, teleportPlayer, Area.ChaosSanctuary))
                {
                    Log.Warning($"Client {client.Game.Me.Name} failed to take town portal");
                    continue;
                }

                ownState.TeleportId = newTeleportId;
            }

            if (!client.Game.IsInTown())
            {
                RememberExperienceShrine(client);
                await TryTakeExperienceShrine(client);
                if (!await KillBosses(client, account, null, action, ownState, _state, true))
                {
                    return false;
                }

                // The taxi leaves the moment the boss drops; his minions stay, and they are a third of
                // the run's experience: the baseline party, which fought on until her next portal
                // appeared, made 830 experience a second at a seal against 500 for the party that
                // left with her. They fight on for the seconds her transit takes, under the same
                // retreat and whirl rules as in the fight, then follow.
                if (!client.Game.IsInTown() && SealBossIsDown(client) && !_state.TeleportHasChanged(ownState) && !await IsNextGame())
                {
                    await FinishLeftovers(client, action, ownState);
                    if (client.Game.Me.Class == CharacterClass.Paladin)
                    {
                        // He is the one still here when the boss drops, so the boss's loot is his
                        // to collect: a sweep around the corpse, not around himself. Vizier died 37
                        // from the spot and three rares lay outside a sweep centred on the paladin.
                        var corpse = NPCHelpers.GetNearbySuperUniques(client, BossDownCheckRadius)
                            .FirstOrDefault(w => w.NPCCode != NPCCode.Diablo && (w.State == EntityState.Dead || w.State == EntityState.Dieing));
                        var lootAt = corpse?.Location ?? client.Game.Me.Location;
                        if (!NPCHelpers.GetNearbyNPCs(client, lootAt, 1, PaladinSweepClearance).Any())
                        {
                            await PickupItemsAndPotions(client, account, PaladinSweepRadius, lootAt);
                        }
                    }
                    else
                    {
                        Log.Information("{Character} leaving the seal, the boss is down", client.Game.Me.Name);
                        await _townManagementService.TakeTownPortalToTown(client);
                    }
                }
            }
        }

        return true;
    }

    private int _sealAttempts;

    /// <summary>
    /// Telekinesis and a plain interaction in turn. The interaction has the server walk her onto the
    /// seal, which a pack standing on it blocks - a left seal stayed shut through five seconds of tries
    /// with the room full; Telekinesis works from where she stands. Alternating keeps the old way going
    /// should the server not take one.
    /// </summary>
    private void OperateSeal(Client client, WorldObject seal)
    {
        if (Interlocked.Increment(ref _sealAttempts) % 2 == 1
            && client.Game.Me.HasSkill(Skill.Telekinesis)
            && client.Game.Me.Mana > 10)
        {
            client.Game.UseRightHandSkillOnEntity(Skill.Telekinesis, seal);
            return;
        }

        client.Game.InteractWithEntity(seal);
    }

    private bool IsTeleportClient(Client client)
    {
        return client.Game.Me.Name.Equals(_csconfig.TeleportCharacterName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keeps a character fighting at the kill spot after the boss has dropped, until the pack there
    /// is gone, the taxi's next portal is up, or the time is spent. Unlike KillBosses this loop has
    /// no seal timeout: running out of time here is normal, not a failed game.
    /// </summary>
    private async Task FinishLeftovers(Client client, Func<Task> action, CsState ownState)
    {
        var timer = Stopwatch.StartNew();
        var fought = false;
        while (timer.Elapsed < LeftoverTimeout
            && !_state.TeleportHasChanged(ownState)
            && !await IsNextGame()
            && client.Game.IsInGame()
            && !client.Game.IsInTown()
            && !OutOfHealthPotions(client)
            && NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, LeftoverRadius).Any())
        {
            fought = true;
            await action.Invoke();
            await Task.Delay(100);
        }

        if (fought)
        {
            Log.Information("{Character} fought the leftovers for {Seconds:F1}s", client.Game.Me.Name, timer.Elapsed.TotalSeconds);
        }
    }

    private static readonly TimeSpan LeftoverTimeout = TimeSpan.FromSeconds(12);



    /// <summary>Around the character, not the spot: by the time the boss drops the followers have closed on him and the pack is where they are.</summary>
    private const int LeftoverRadius = 25;


    private async Task<bool> TaxiCs(Client client, AccountConfig account)
    {
        if (!await Phase(client, "entry", async () =>
        {
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                if (client.Game.Area == Area.ChaosSanctuary)
                {
                    return true;
                }
                return await _townManagementService.TakeTownPortalToArea(client, client.Game.Me, Area.ChaosSanctuary);
            }, TimeSpan.FromSeconds(15)))
            {
                Log.Warning($"Client {client.Game.Me.Name} Taking townportal to {Area.ChaosSanctuary} failed");
                return false;
            }

            return await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await _townManagementService.CreateTownPortal(client);
            }, TimeSpan.FromSeconds(5));
        }))
        {
            Log.Warning($"Client {client.Game.Me.Name} Creating townportal failed");
            return false;
        }

        var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);

        _state.TeleportId = myPortal.Id;
        _state.KillLocation = client.Game.Me.Location;
        var action = GetSorceressKillAction(client, account);
        if (!await Phase(client, "bo", () => WaitForBo(client, account, action)))
        {
            return false;
        }

        if (await IsNextGame())
        {
            return true;
        }

        _state.TeleportId = null;
        _state.BossId = null;
        _state.PaladinPost = null;
        RememberEarlierBosses();

        if (!await Phase(client, "leftseal", () => KillLeftSeal(client, account, _state)))
        {
            return false;
        }

        RememberExperienceShrine(client);
        if (await IsNextGame())
        {
            return true;
        }

        _state.TeleportId = null;
        _state.BossId = null;
        _state.PaladinPost = null;
        RememberEarlierBosses();

        if (!await Phase(client, "topseal", () => KillTopSeal(client, account, _state)))
        {
            return false;
        }

        RememberExperienceShrine(client);
        if (await IsNextGame())
        {
            return true;
        }

        _state.TeleportId = null;
        _state.BossId = null;
        _state.PaladinPost = null;
        RememberEarlierBosses();

        if (!await Phase(client, "rightseal", () => KillRightSeal(client, account, _state)))
        {
            return false;
        }

        RememberExperienceShrine(client);
        _state.SealsDone = true;
        _state.TeleportId = null;
        _state.BossId = null;
        _state.PaladinPost = null;
        RememberEarlierBosses();

        // The portal at the star first, so the party is in place when Diablo comes; she spends the
        // rest of his fifteen seconds on the loot the party saw and left, and is back before he is.
        if (!await Phase(client, "diabloportal", () => OpenDiabloPortal(client, _state)))
        {
            return false;
        }

        // The whole sweep goes here, not after him: the party is at the star through her portal and
        // starts on Diablo without her, while after his death everyone waits on what she still fetches.
        await Phase(client, "sweep", () => SweepLeftovers(client, account, SweepRadius, SweepBudget, "before Diablo"));
        _state.ExperienceShrineTaker = ShrineHelpers.PickHighestLevel(_clients.Select(c => c.Game));
        if (_state.ExperienceShrineLocation != null)
        {
            Log.Information($"Seals done, experience shrine at {_state.ExperienceShrineLocation} goes to {_state.ExperienceShrineTaker}");
        }
        else
        {
            Log.Information("Seals done, no experience shrine found this game, {Count} shrines seen in total", _shrinesSeen.Count);
        }

        if (await IsNextGame())
        {
            return true;
        }

        await Phase(client, "shrine", async () =>
        {
            await TryTakeExperienceShrine(client);
            return true;
        });
        return await Phase(client, "diablo", () => KillDiablo(client, account, _state));
    }

    private async Task<bool> WaitForBo(Client client, AccountConfig account, Func<Task> action)
    {
        var initialLocation = client.Game.Me.Location;
        var random = new Random();
        var stopWatch = new Stopwatch();
        stopWatch.Start();
        while (stopWatch.Elapsed < TimeSpan.FromSeconds(30) && StillWaitingForShouts(client, stopWatch.Elapsed) && !await IsNextGame())
        {
            if (client.Game.Me.Class == CharacterClass.Barbarian)
            {
                await ClassHelpers.CastAllShouts(client);
            }
            else
            {
                // Everyone arrives through the same portal, which is where the barbarian shouts, so
                // there is nowhere to go: before the shouts a follower has half its life, and every
                // step away from the portal is a Storm Caster met for nothing. A shuffle of a couple
                // of units keeps the character from standing on one tile without leaving the spot.
                await client.Game.MoveToAsync(initialLocation.Add((short)random.Next(-2, 3), (short)random.Next(-2, 3)));
                await Task.Delay(300);
                continue;
            }

            var movementMode = MovementHelpers.PreferredMovement(client.Game);
            var pathToInitialLocation = await _pathingService.GetPathToLocation(client.Game, initialLocation, movementMode);
            var movementCancellation = new CancellationTokenSource();
            movementCancellation.CancelAfter(500);
            await MovementHelpers.TakePathOfLocations(client.Game, pathToInitialLocation, movementMode, movementCancellation.Token);
        }

        if (stopWatch.Elapsed >= TimeSpan.FromSeconds(30))
        {
            // Another player's effects can stay "missing" in this client's view for the whole wait
            // while the character itself is buffed; that used to abort the game. Only a character
            // that is itself without the shouts has anything to lose by going on.
            var missing = string.Join(",", client.Game.Players.Where(ClassHelpers.IsMissingShouts).Select(p => p.Name));
            Log.Warning("{Character} gave up waiting for shouts after 30s at {Location}, still missing on: {Missing}",
                client.Game.Me.Name, client.Game.Me.Location, missing);
            return !ClassHelpers.IsMissingShouts(client.Game.Me);
        }

        return true;
    }

    /// <summary>At ten the amazon, nine to thirteen seconds in town, arrived after the shouts were done and fought a whole seal on 526 life; she went to 176.</summary>
    private static readonly TimeSpan PartyShoutGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The barbarian waits until everyone he can see has the shouts, since he is the one casting
    /// them. Everyone else waits for their own, and gives the rest of the party ten seconds on top.
    /// </summary>
    /// <summary>
    /// The shouts go up in town, where the whole party stands for the ten seconds of the taxi's
    /// approach anyway. At the star the same wait cost seven seconds a game, half-life, with Storm
    /// Casters around; the wait there is now only a fallback for a shout that ran out.
    /// </summary>
    private async Task<bool> ShoutsInTown(Client client)
    {
        var me = client.Game.Me;
        if (!client.Game.IsInTown())
        {
            return true;
        }

        var timer = Stopwatch.StartNew();
        var random = new Random();
        while (timer.Elapsed < TownShoutTimeout && !await IsNextGame() && client.Game.IsInGame())
        {
            var inTown = client.Game.Players.Where(p => p.Id != me.Id && p.Area == client.Game.Area && p.Location != null).ToList();
            if (me.Class == CharacterClass.Barbarian)
            {
                var near = inTown.Where(p => p.Location.Distance(me.Location) < TownShoutRange).ToList();
                if (near.Count == inTown.Count || timer.Elapsed > TownShoutGather)
                {
                    await ClassHelpers.CastAllShouts(client);
                    if (!ClassHelpers.IsMissingShouts(me) && !near.Any(ClassHelpers.IsMissingShouts))
                    {
                        Log.Information("{Character} shouted in town for {Count} after {Seconds:F1}s", me.Name, near.Count, timer.Elapsed.TotalSeconds);
                        return true;
                    }
                }

                await Task.Delay(200);
                continue;
            }

            if (!ClassHelpers.IsMissingShouts(me))
            {
                return true;
            }

            var barbarian = inTown.FirstOrDefault(p => p.Class == CharacterClass.Barbarian);
            if (barbarian != null && barbarian.Location.Distance(me.Location) > TownShoutRange / 2)
            {
                var beside = barbarian.Location.Add((short)random.Next(-2, 3), (short)random.Next(-2, 3));
                await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, beside, MovementMode.Walking);
            }

            await Task.Delay(300);
        }

        Log.Information("{Character} leaves town without full shouts after {Seconds:F1}s", me.Name, timer.Elapsed.TotalSeconds);
        return true;
    }

    private static readonly TimeSpan TownShoutTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The barbarian shouts for whoever has gathered once this has passed, so one slow shopper does not hold the taxi.</summary>
    private static readonly TimeSpan TownShoutGather = TimeSpan.FromSeconds(4);

    private const double TownShoutRange = 10;

    private static bool StillWaitingForShouts(Client client, TimeSpan waited)
    {
        if (client.Game.Me.Class == CharacterClass.Barbarian)
        {
            // Only for whoever is actually beside him, and no longer than the others wait: he used
            // to stand alone at the star for a player whose shouts his client never saw, after the
            // rest had long moved on to the first seal.
            return waited < PartyShoutGrace
                && client.Game.Players.Any(p => p.Id != client.Game.Me.Id
                    && p.Location != null
                    && p.Location.Distance(client.Game.Me.Location) < 15
                    && ClassHelpers.IsMissingShouts(p));
        }

        if (ClassHelpers.IsMissingShouts(client.Game.Me))
        {
            return true;
        }

        return waited < PartyShoutGrace && ClassHelpers.AnyPlayerIsMissingShouts(client);
    }


    private async Task<bool> SweepLeftovers(Client client, AccountConfig account, double radius, TimeSpan budget, string when, Func<bool> stopWhen = null, Func<Item, bool> worthIt = null)
    {
        var clock = Stopwatch.StartNew();
        var picked = 0;
        while (clock.Elapsed < budget && !await IsNextGame() && client.Game.IsInGame() && !client.Game.IsInTown() && !(stopWhen?.Invoke() ?? false))
        {
            var me = client.Game.Me.Location;
            var item = PeekPickitList(client, radius, me)
                .Where(i => i.Ground && client.Game.Inventory.FindFreeSpace(i) != null && (worthIt?.Invoke(i) ?? true))
                .Where(i => !NPCHelpers.GetNearbyNPCs(client, i.Location, 1, SweepClearance).Any())
                .OrderBy(i => i.Location.Distance(me))
                .FirstOrDefault();
            if (item == null)
            {
                break;
            }

            if (item.Location.Distance(me) > SweepPickupReach)
            {
                var path = await _pathingService.GetPathToLocation(client.Game, item.Location, MovementMode.Teleport);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Teleport))
                {
                    break;
                }
            }

            await PickupItemsAndPotions(client, account, SweepPickupReach);
            picked++;
        }

        if (picked > 0)
        {
            Log.Information("{Character} swept {Count} leftover items in {Seconds:F1}s {When}", client.Game.Me.Name, picked, clock.Elapsed.TotalSeconds, when);
        }

        foreach (var left in PeekPickitList(client, radius, client.Game.Me.Location).Where(i => i.Ground && (worthIt?.Invoke(i) ?? true)))
        {
            var why = client.Game.Inventory.FindFreeSpace(left) == null ? "no room"
                : NPCHelpers.GetNearbyNPCs(client, left.Location, 1, SweepClearance).Any() ? "monsters beside it"
                : "budget spent";
            Log.Warning("{Character} left {Item} at {Location}, {Distance} away: {Why}", client.Game.Me.Name, left.Name, left.Location, (int)left.Location.Distance(client.Game.Me.Location), why);
        }

        return true;
    }

    /// <summary>Twelve left two rings and an amulet 125 to 180 out in one game; at about two seconds a far item, rings are worth the wait.</summary>
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(20);

    /// <summary>From the right seal the left seal room is about 270 away; a Grim Shield by Vizier's corpse sat outside a 250 sweep.</summary>
    private const double SweepRadius = 400;

    private const int SweepClearance = 6;

    /// <summary>
    /// A teleport counts as arrived anywhere within ten of its target, and the next hop to the same
    /// item does not move her at all. At six she stood eight from a rare ring at the left seal and
    /// left without it; the pickup walks the rest.
    /// </summary>
    private const double SweepPickupReach = 12;

    private async Task<bool> OpenDiabloPortal(Client client, CsState csState)
    {
        var pathToDiabloStar = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.DiabloStar, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToDiabloStar, MovementMode.Teleport))
        {
            Log.Warning($"Client {client.Game.Me.Name} Teleporting to {EntityCode.DiabloStar} failed at location {client.Game.Me.Location}");
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(client))
        {
            return false;
        }

        var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
        csState.TeleportId = myPortal.Id;
        csState.KillLocation = client.Game.Me.Location;
        csState.DiabloPortalUp = true;
        return true;
    }

    /// <summary>
    /// After a relocation the followers come through town, seven to ten seconds and sometimes over
    /// twenty; standing alone at the new spot she had to escape in six relocations of nine, twice
    /// into a chicken. She holds near the portal, teleporting away from whatever closes in, until two of
    /// them are there.
    /// </summary>
    private async Task HoldUntilPartyArrives(Client client, Point spot)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < RelocationHold && client.Game.IsInGame() && !client.Game.IsInTown() && !await IsNextGame())
        {
            var arrived = client.Game.Players.Count(p => p.Id != client.Game.Me.Id && p.Location != null && p.Location.Distance(spot) < RelocationArrivedRadius);
            if (arrived >= 2)
            {
                Log.Information("{Character} held the relocated spot {Seconds:F1}s until {Arrived} followers arrived", client.Game.Me.Name, clock.Elapsed.TotalSeconds, arrived);
                return;
            }

            var threats = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 20, RelocationThreatRadius).Select(e => e.Location).ToList();
            if (threats.Count > 0)
            {
                await _attackService.MoveToNearbySafeSpot(client, threats, spot, MovementMode.Teleport, 8, 20);
            }

            await Task.Delay(200);
        }

        Log.Information("{Character} held the relocated spot {Seconds:F1}s, followers still on their way", client.Game.Me.Name, clock.Elapsed.TotalSeconds);
    }

    private static readonly TimeSpan RelocationHold = TimeSpan.FromSeconds(12);

    private const int RelocationArrivedRadius = 15;

    private const int RelocationThreatRadius = 15;

    private async Task<bool> KillDiablo(Client client, AccountConfig account, CsState csState)
    {
        if (csState.KillLocation != null && client.Game.Me.Location.Distance(csState.KillLocation) > 15)
        {
            var back = await _pathingService.GetPathToLocation(client.Game, csState.KillLocation, MovementMode.Teleport);
            await MovementHelpers.TakePathOfLocations(client.Game, back, MovementMode.Teleport);
        }

        var action = GetSorceressKillAction(client, account);
        if (!await KillBosses(client, account, null, action, csState, csState, true))
        {
            return false;
        }

        // His loot lands at the end of the death animation, seconds after the kill; the pickup in
        // KillBosses runs at once and found nothing, and the game ended half a second after he died.
        // His drop lands with his death, and the pickup in KillBosses has usually taken the best of it
        // by now; waiting for a further item to land stood her idle 3.6 seconds a game with the whole
        // party waiting. Only when nothing at all lies by the corpse is a moment's wait worth it.
        var corpse = NPCHelpers.GetNearbySuperUniques(client, BossDownCheckRadius).FirstOrDefault(w => w.NPCCode == NPCCode.Diablo)?.Location ?? client.Game.Me.Location;
        var dropWait = Stopwatch.StartNew();
        while (dropWait.Elapsed < DiabloDropWait && client.Game.IsInGame()
            && !client.Game.Items.Values.Any(i => i.Ground && i.Location.Distance(corpse) < DiabloLootRadius))
        {
            await Task.Delay(100);
        }

        // Only his own drop: the whole party waits on this, and two items across the sanctuary cost
        // eight seconds a game. The rest of the sanctuary is swept before him.
        await SweepLeftovers(client, account, DiabloLootRadius, DiabloLootBudget, "after Diablo");
        return true;
    }

    private static readonly TimeSpan DiabloDropWait = TimeSpan.FromSeconds(1);

    private const double DiabloLootRadius = 40;

    private static readonly TimeSpan DiabloLootBudget = TimeSpan.FromSeconds(10);

    private async Task<bool> KillRightSeal(Client client, AccountConfig account, CsState csState)
    {
        Log.Information($"Teleporting to {EntityCode.RightSeal1}");

        var seal1 = await GetSeal(client, EntityCode.RightSeal1);
        var seal2 = await GetSeal(client, EntityCode.RightSeal2);
        var killLocation = seal1.X < seal2.X ? seal1.Add(30, -10) : seal1.Add(12, -38);

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var pathToSeal = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
            return await MovementHelpers.TakePathOfLocations(client.Game, pathToSeal, MovementMode.Teleport);
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Teleporting to killing location of {EntityCode.RightSeal1} failed at location {client.Game.Me.Location}");
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(client))
        {
            return false;
        }

        var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
        csState.TeleportId = myPortal.Id;
        csState.KillLocation = killLocation;

        if (!await RestockIfDry(client, account))
        {
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            return await client.Game.TeleportToLocationAsync(seal1);
        }, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        csState.SealOpeningAt = DateTime.Now;
        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var rightseal1Entity = client.Game.GetEntityByCode(EntityCode.RightSeal1).First();
            if (rightseal1Entity.State == EntityState.Activating || rightseal1Entity.State == EntityState.Activated)
            {
                return true;
            }
            OperateSeal(client, rightseal1Entity);
            await Task.Delay(100);
            return false;
        }, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        // Both seals before the fight: Infector comes out of the first, and Diablo's clock runs
        // from his death only once the second is open too. Opened after the fight it added the
        // trip to the seal on top of the fifteen seconds he takes to appear.
        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            return await client.Game.TeleportToLocationAsync(seal2);
        }, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var rightseal2Entity = client.Game.GetEntityByCode(EntityCode.RightSeal2).First();
            if (rightseal2Entity.State == EntityState.Activating || rightseal2Entity.State == EntityState.Activated)
            {
                return true;
            }
            OperateSeal(client, rightseal2Entity);
            await Task.Delay(100);
            return false;
        }, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            return await client.Game.TeleportToLocationAsync(killLocation);
        }, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        var action = GetSorceressKillAction(client, account);
        if (!await KillBosses(client, account, null, action, csState, csState, true))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> KillTopSeal(Client client, AccountConfig account, CsState csState)
    {
        Log.Information($"Teleporting to {EntityCode.TopSeal}");

        Point topSeal = await GetSeal(client, EntityCode.TopSeal);
        var toLeftOfSealIsValid = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, topSeal.Add(-20, 0));
        // Below variant: De Seis spawns at y 5203-5240, and 31 of 180 spawns were within 20 of the
        // old spot at y 5254; at y 5260 none were, and the room stays open to y 5261.
        // The below spot sits east of the wall corner at the room's south-west, so nothing the party
        // sees is on the wall's far side.
        // Thirty off the spawn. On it (02:16-15:35, 14 Sept) De Seis appeared eight units from the
        // party with twenty to thirty-six hostiles within ten of the sorceress and the amazon.
        var killLocation = toLeftOfSealIsValid ? topSeal.Add(-14, 39) : topSeal.Add(9, 103);
        Log.Information("TopSeal at {Seal}, variant {Variant}, kill location {Kill}", topSeal, toLeftOfSealIsValid ? "left" : "below", killLocation);
        var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport))
        {
            Log.Warning($"Teleporting to kill location {killLocation} failed at location {client.Game.Me.Location}");
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(client))
        {
            return false;
        }

        var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
        csState.TeleportId = myPortal.Id;
        csState.KillLocation = killLocation;

        if (!await RestockIfDry(client, account))
        {
            return false;
        }

        // De Seis spawns within a few units of the same point per layout (180 games: median seal +
        // (1, 67) below, (-42, 23) left). The paladin goes to stand there before the seal opens and
        // meets the pack with hammers already spinning; the others stay at the kill spot. He sets
        // off as soon as he is through the portal, so his walk overlaps the party's assembly.
        // Not sent to the spawn: alone on De Seis' post with the party thirty units back, his fights
        // ran 33 seconds median and seven of nine seals timed out. Together at the kill spot the
        // overnight arm did the same fight in 14.
        await WaitForPartyBeforeSeal(client, killLocation);

        var pathToTopSeal2 = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, client.Game.Me.Location, EntityCode.TopSeal, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTopSeal2, MovementMode.Teleport))
        {
            Log.Warning($"Teleporting to {EntityCode.TopSeal} failed at location {client.Game.Me.Location}");
            return false;
        }

        csState.SealOpeningAt = DateTime.Now;
        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var topSealEntity = client.Game.GetEntityByCode(EntityCode.TopSeal).First();
            if (topSealEntity.State == EntityState.Activating || topSealEntity.State == EntityState.Activated)
            {
                return true;
            }
            OperateSeal(client, topSealEntity);
            await Task.Delay(100);
            return false;
        }, TimeSpan.FromSeconds(5)))
        {
            Log.Warning($"Opening {EntityCode.TopSeal} failed at location {client.Game.Me.Location} with state {client.Game.GetEntityByCode(EntityCode.TopSeal).FirstOrDefault()?.State}");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var pathToKillingLocation = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
            return await MovementHelpers.TakePathOfLocations(client.Game, pathToKillingLocation, MovementMode.Teleport);
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Teleporting back to kill location failed at location {client.Game.Me.Location}");
            return false;
        }

        await ReturnToKillLocation(client, csState.KillLocation);

        if (!await MoveKillingLocationIfFar(client, csState, killLocation.GetPointBeforePointInSameDirection(topSeal, 30), NeverRelocate))
        {
            Log.Warning($"Bosses are far from usual location, moving location {client.Game.Me.Name}");
            return false;
        }

        var knight = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 50, 80)
            .FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
        Log.Information("Killing top seal bosses {Character} at {Location}, boss {Distance} away",
            client.Game.Me.Name,
            client.Game.Me.Location,
            knight == null ? -1 : (int)knight.Location.Distance(client.Game.Me.Location));
        var action = GetSorceressKillAction(client, account);
        if (!await KillBosses(client, account, null, action, csState, csState, true))
        {
            return false;
        }

        return true;
    }

    private const double KillLocationTolerance = 8;

    /// <summary>
    /// The path back from a seal ends short in about a quarter of left-variant games: it runs
    /// through the corridor the boss spawns in and the last hops fail into his pack. A fight that
    /// starts with the taxi six units from him produced three chickens in one batch, so this hops
    /// out of his reach first and then tries for the kill location once more.
    /// </summary>
    private async Task ReturnToKillLocation(Client client, Point killLocation)
    {
        if (client.Game.Me.Location.Distance(killLocation) <= KillLocationTolerance)
        {
            return;
        }

        var boss = NPCHelpers.GetNearbySuperUniques(client, BossStandoffDistance).FirstOrDefault();
        Log.Warning("{Character} is at {Location}, {Distance} from the kill location{Boss}",
            client.Game.Me.Name,
            client.Game.Me.Location,
            (int)client.Game.Me.Location.Distance(killLocation),
            boss == null ? string.Empty : $", boss {(int)boss.Location.Distance(client.Game.Me.Location)} away");

        if (boss != null)
        {
            var threats = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 20, (int)TaxiEscapeMaxDistance).Select(e => e.Location).ToList();
            await _attackService.MoveToNearbySafeSpot(client, threats, client.Game.Me.Location, MovementMode.Teleport, TaxiEscapeMinDistance, TaxiEscapeMaxDistance);
        }

        await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var path = await _pathingService.GetPathToLocation(client.Game, killLocation, MovementMode.Teleport);
            await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Teleport);
            return client.Game.Me.Location.Distance(killLocation) <= KillLocationTolerance;
        }, TimeSpan.FromSeconds(4));
    }

    private async Task<bool> RestockIfDry(Client client, AccountConfig account)
    {
        var healthPotions = client.Game.Belt.NumOfHealthPotions()
            + client.Game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion);
        if (healthPotions >= TaxiRestockBelow)
        {
            return true;
        }

        Log.Warning("{Character} has {Count} health potions before opening the seal, restocking", client.Game.Me.Name, healthPotions);
        if (!await _townManagementService.TakeTownPortalToTown(client))
        {
            Log.Warning("{Character} could not take a portal to restock", client.Game.Me.Name);
            return true;
        }

        var options = new TownManagementOptions(account, Act.Act4);
        var beltShortfall = account.HealthPotionTarget(client.Game.Belt.Height)
            - client.Game.Belt.GetHealthPotionsInSlots(account.HealthSlots).Count;
        options.HealthPotionsToBuy = Math.Max(0, Math.Max(0, beltShortfall) + TaxiPotionBuffer);
        await _townManagementService.PerformTownTasks(client, options);

        return await _townManagementService.TakeTownPortalToArea(client, client.Game.Me, Area.ChaosSanctuary);
    }

    private async Task WaitForPartyBeforeSeal(Client client, Point killLocation)
    {
        var waited = Stopwatch.StartNew();
        var assembled = 0;
        while (waited.Elapsed < SealAssembleTimeout)
        {
            assembled = client.Game.Players.Count(p => p.Id != client.Game.Me.Id
                && p.Location != null
                && p.Location.Distance(killLocation) < SealAssembleRadius);
            if (assembled >= SealAssembleCount)
            {
                break;
            }

            await StayMobile(client, killLocation);
        }

        var assembledAt = waited.Elapsed.TotalSeconds;
        var timedOut = assembled < SealAssembleCount;
        // The pack spawns onto whatever is at the kill spot. A trash mass there took the taxi from
        // 1017 to 327 life in half a second once, so the party clears the spot before the seal opens,
        // within a cap: the boss is the run, the trash is not worth more than a few seconds.
        var hostiles = HostilesAt(client, killLocation);
        if (!timedOut)
        {
            var dwell = Stopwatch.StartNew();
            while (dwell.Elapsed < SealClearTimeout && hostiles > SealClearMaxHostiles)
            {
                await StayMobile(client, killLocation);
                hostiles = HostilesAt(client, killLocation);
            }
        }

        Log.Information("Assembled {Assembled} followers within {Radius} after {AssembledAt:F1}s ({Outcome}), opened after {Waited:F1}s with {Hostiles} hostiles within {ClearRadius} of the kill spot",
            assembled, SealAssembleRadius, assembledAt, timedOut ? "timeout" : "met", waited.Elapsed.TotalSeconds, hostiles, SealClearRadius);
    }

    private static int HostilesAt(Client client, Point killLocation)
    {
        return NPCHelpers.GetNearbyNPCs(client, killLocation, 30, SealClearRadius).Count();
    }

    private static readonly TimeSpan PaladinPostTimeout = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan PaladinHammerLead = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The seal opens only once the paladin stands on the post and has had two seconds to get his
    /// hammers up; a pack that spawns onto spinning hammers is half dead before it acts.
    /// </summary>
    private async Task WaitForPaladinOnPost(Client client, Point post, Point killLocation)
    {
        var waited = Stopwatch.StartNew();
        bool OnPost() => client.Game.Players.Any(p => p.Class == CharacterClass.Paladin
            && p.Location != null
            && p.Location.Distance(post) <= PaladinPostTolerance + 2);
        while (waited.Elapsed < PaladinPostTimeout && !OnPost())
        {
            await StayMobile(client, killLocation);
        }

        var arrived = OnPost();
        if (arrived)
        {
            var lead = Stopwatch.StartNew();
            while (lead.Elapsed < PaladinHammerLead)
            {
                await StayMobile(client, killLocation);
            }
        }

        Log.Information("Paladin {Outcome} the post after {Seconds:F1}s, opening the seal", arrived ? "on" : "not on", waited.Elapsed.TotalSeconds);
    }

    private async Task StayMobile(Client client, Point anchor)
    {
        var threats = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 20, SealWaitDangerRadius).ToList();
        if (threats.Count > 0)
        {
            await _attackService.MoveToNearbySafeSpot(client,
                threats.Select(e => e.Location).ToList(),
                anchor,
                MovementMode.Teleport,
                TaxiEscapeMinDistance,
                TaxiEscapeMaxDistance);
        }

        await Task.Delay(100);
    }

    private static async Task<WorldObject> WaitForBoss(Client client, Point around, TimeSpan timeout)
    {
        WorldObject boss = null;
        await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            // Not the capped GetNearbyNPCs: at the left seal more than fifty hostiles stand within
            // eighty of the spot and the boss was not among the nearest fifty.
            boss = NPCHelpers.GetNearbySuperUniques(client, around, 80)
                .FirstOrDefault(e => e.State != EntityState.Dead && e.State != EntityState.Dieing);
            if (boss == null)
            {
                await Task.Delay(100);
            }
            return boss != null;
        }, timeout);
        return boss;
    }

    private async Task<bool> MoveKillingLocationIfFar(Client client, CsState csState, Point scoutPoint, double relocateThreshold = BossRelocateThreshold)
    {
        Log.Debug($"Kill location {csState.KillLocation}");
        var boss = await WaitForBoss(client, csState.KillLocation, BossSpawnWait);
        if (boss == null)
        {
            // The server only sends what is within sight of her. From the corridor spot of the
            // seal1-above layout Vizier's far-east spawn is out of it, and six games in fifty were
            // thrown away as "no spawn" with him alive in the room. A hop into the room finds him.
            Log.Warning("{Character} sees no boss from {Kill} after {Wait}s, scouting from {Scout}", client.Game.Me.Name, csState.KillLocation, BossSpawnWait.TotalSeconds, scoutPoint);
            var pathToScout = await _pathingService.GetPathToLocation(client.Game, scoutPoint, MovementMode.Teleport);
            if (await MovementHelpers.TakePathOfLocations(client.Game, pathToScout, MovementMode.Teleport))
            {
                boss = await WaitForBoss(client, client.Game.Me.Location, BossScoutWait);
            }

            if (boss == null)
            {
                Log.Warning($"Waiting for bosses to spawn failed at {client.Game.Me.Location}");
                return false;
            }

            if (boss.Location.Distance(csState.KillLocation) <= relocateThreshold)
            {
                await ReturnToKillLocation(client, csState.KillLocation);
            }
        }

        Log.Information("Boss {Code} spawned at {Boss}, kill location {Kill}, {Distance:F0} apart",
            boss.NPCCode, boss.Location, csState.KillLocation, boss.Location.Distance(csState.KillLocation));
        csState.BossId = boss.Id;

        // Straight-line: the walking path from the left-variant spot to De Seis runs round a wall
        // and read 45+ for a boss 40 away, which relocated the portal and the whole party onto his
        // pack.
        if (boss.Location.Distance(csState.KillLocation) > relocateThreshold)
        {
            // In sight of him, not only walkable: a spot behind a wall corner from the boss has the
            // party walk round it before anyone can shoot. Off the straight line too, where it runs
            // into the wall; the first walkable spot on the line is kept only for when none is in sight.
            Point standoff = null;
            Point walkableFallback = null;
            foreach (var distance in new[] { BossStandoffDistance, BossStandoffDistance - 6, BossStandoffDistance - 12 })
            {
                foreach (var degrees in new[] { 0, 25, -25, 50, -50 })
                {
                    var candidate = PointAround(boss.Location, client.Game.Me.Location, distance, degrees);
                    if (candidate == null
                        || !await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, candidate))
                    {
                        continue;
                    }

                    if (degrees == 0)
                    {
                        walkableFallback ??= candidate;
                    }

                    if (await _attackService.IsInLineOfSight(client, candidate, boss.Location))
                    {
                        standoff = candidate;
                        break;
                    }
                }

                if (standoff != null)
                {
                    break;
                }
            }

            if (standoff == null && walkableFallback != null)
            {
                Log.Warning("{Character} found no standoff spot in sight of the boss at {Boss}, using {Fallback}", client.Game.Me.Name, boss.Location, walkableFallback);
                standoff = walkableFallback;
            }

            if (standoff == null)
            {
                Log.Warning("{Character} found no standoff spot toward the boss at {Boss}, staying at {Kill}", client.Game.Me.Name, boss.Location, csState.KillLocation);
                return true;
            }

            Log.Information("{Character} relocating the party to {Standoff}, {Distance:F0} from the boss", client.Game.Me.Name, standoff, standoff.Distance(boss.Location));
            csState.RelocationPending = true;

            var pathToBosses = await _pathingService.GetPathToLocation(client.Game, standoff, MovementMode.Teleport);
            if (await MovementHelpers.TakePathOfLocations(client.Game, pathToBosses, MovementMode.Teleport))
            {
                if (!await _townManagementService.CreateTownPortal(client))
                {
                    csState.RelocationPending = false;
                    return false;
                }

                var newPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
                csState.TeleportId = newPortal.Id;
                csState.KillLocation = standoff;
                csState.RelocationPending = false;
                await HoldUntilPartyArrives(client, standoff);
                return true;
            }

            csState.RelocationPending = false;
            return false;
        }

        return true;
    }

    private async Task<bool> KillLeftSeal(Client client, AccountConfig account, CsState csState)
    {
        Log.Information($"Teleporting to {EntityCode.LeftSeal1}");

        Point leftSeal1 = await GetSeal(client, EntityCode.LeftSeal1);
        Point leftSeal2 = await GetSeal(client, EntityCode.LeftSeal2);
        // Vizier spawns a median 6 (seal1 below seal2) or 13 units (seal1 above) from the old spots,
        // i.e. on the party. With seal1 below he spawns spread over the room's middle, so the spot
        // is the north-west corner by seal2, 30-45 off. With seal1 above he spawns along the south
        // wall (x 7662-7698, y 5321) or, one game in three, far east; the spot is the corridor north
        // of the room by seal1, 32+ from every spawn seen, walled on the east. The seal nearer the
        // spot is opened last so the taxi ends her round beside it and never crosses the spawn.
        var seal1Below = leftSeal1.Y > leftSeal2.Y;
        var leftSealKillLocation = seal1Below ? leftSeal1.Add(5, -43) : leftSeal1.Add(-2, 15);
        Log.Information("LeftSeal at {Seal1} and {Seal2}, variant {Variant}, kill location {Kill}",
            leftSeal1, leftSeal2, seal1Below ? "seal1 below" : "seal1 above", leftSealKillLocation);
        var sealOrder = new[] { (leftSeal1, EntityCode.LeftSeal1), (leftSeal2, EntityCode.LeftSeal2) }
            .OrderByDescending(s => s.Item1.Distance(leftSealKillLocation))
            .ToList();

        var pathToLeftSeal = await _pathingService.GetPathToLocation(client.Game, leftSealKillLocation, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToLeftSeal, MovementMode.Teleport))
        {
            Log.Debug($"Teleporting to {EntityCode.LeftSeal1} failed at location {client.Game.Me.Location}");
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(client))
        {
            return false;
        }

        var myPortal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
        csState.TeleportId = myPortal.Id;
        csState.KillLocation = leftSealKillLocation;
        if (!await RestockIfDry(client, account))
        {
            return false;
        }

        // No assembly wait here: the left seal's kill spots sit in trash, twenty to thirty hostiles at
        // the open, and the taxi waiting alone on them drank eight to twelve potions before Vizier
        // ever spawned. Her seal round takes the five seconds the followers need to arrive, and she
        // ends it beside the kill spot, thirty off the spawn, not on it.
        csState.SealOpeningAt = DateTime.Now;
        foreach (var (sealLocation, sealCode) in sealOrder)
        {
            // Beside the seal, not onto it: the object's own tile is not a landing spot, and two
            // rounds in ten timed out silently trying to land there from the corridor.
            var besideSeal = client.Game.Me.Location.GetPointBeforePointInSameDirection(sealLocation, 4);
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.TeleportToLocationAsync(besideSeal);
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning("{Character} could not teleport to {Seal} at {Location} from {From}", client.Game.Me.Name, sealCode, sealLocation, client.Game.Me.Location);
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                var sealEntity = client.Game.GetEntityByCode(sealCode).First();
                if (sealEntity.State == EntityState.Activating || sealEntity.State == EntityState.Activated)
                {
                    return true;
                }

                OperateSeal(client, sealEntity);
                await Task.Delay(100);
                return false;
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Warning("{Character} could not open {Seal} at {Location}", client.Game.Me.Name, sealCode, sealLocation);
                return false;
            }
        }

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            return await client.Game.TeleportToLocationAsync(leftSealKillLocation);
        }, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        await ReturnToKillLocation(client, leftSealKillLocation);

        // The room lies east of both spots; from thirty-five in she sees to the far wall.
        if (!await MoveKillingLocationIfFar(client, csState, leftSealKillLocation.Add(35, 15), VizierRelocateThreshold))
        {
            return false;
        }

        var action = GetSorceressKillAction(client, account);
        if (!await KillBosses(client, account, null, action, csState, csState, true))
        {
            return false;
        }

        return true;
    }

    private Func<Task> GetKillActionForClass(Client client, AccountConfig account)
    {
        return client.Game.Me.Class switch
        {
            CharacterClass.Barbarian => GetBarbarianKillAction(client, account),
            CharacterClass.Sorceress => GetSorceressKillAction(client, account),
            CharacterClass.Paladin => GetPaladinKillAction(client, account),
            CharacterClass.Necromancer => GetNecromancerKillAction(client, account),
            CharacterClass.Amazon => GetAmazonKillAction(client, account),
            _ => new Func<Task>(() =>
            {
                return Task.CompletedTask;
            }),
        };
    }

    private Func<Task> GetAmazonKillAction(Client client, AccountConfig account)
    {
        var bossFocus = new BossFocusTracker();
        Func<Task> action = (async () =>
        {
            var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 50);
            if (!enemies.Any(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique)))
            {
                if (NoEnemiesNearby(client))
                {
                    await PickupItemsAndPotions(client, account, 10);
                }
                else
                {
                    // Only potions within reach while hostiles are near: the amazon walked to a Full
                    // Rejuvenation in De Seis' pack and went 649 to 164 in the seconds it took.
                    await PickupPotionsOnly(client, account, PotionReach);
                }
            }

            // No range check against her own position: De Seis spawned 48 from the spot, 63 from her,
            // and returning here whenever the boss was beyond 50 of her kept her from shooting or
            // closing on him for the 25 seconds the others spent getting to him.
            if (!enemies.Any())
            {
                return;
            }

            if (await RetreatToTownIfHurt(client, AmazonRetreatLife))
            {
                return;
            }

            // She stays with the barbarian. Standing off at the kill spot (01:48-02:30, 14 Sept) made
            // him and the paladin, who anchor on her, fight the pack's edge from the spot instead of
            // pushing into it: fights went from 12s to 25s medians and everyone bled longer.
            // The barbarian first: he holds the kill spot while the paladin is forward on the spawn,
            // and she is not to follow the paladin into the pack.
            var nearbyPlayer = client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Barbarian)
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            nearbyPlayer ??= client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Paladin)
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            nearbyPlayer ??= client.Game.Me;
            if (nearbyPlayer != null)
            {
                await _attackService.AssistPlayer(client, nearbyPlayer, null, bossFocus.Update(client, _state), _state.KillLocation);
            }
        });
        return action;
    }

    private Func<Task> GetBarbarianKillAction(Client client, AccountConfig account)
    {
        var random = new Random();
        var bossFocus = new BossFocusTracker();
        Func<Task> action = (async () =>
        {
            var anyPlayersWithoutShouts = ClassHelpers.AnyPlayerIsMissingShouts(client);
            if (anyPlayersWithoutShouts && client.Game.Me.Class == CharacterClass.Barbarian)
            {
                await ClassHelpers.CastAllShouts(client);
            }

            if (await RetreatToTownIfHurt(client, BarbarianRetreatLife))
            {
                return;
            }

            if (client.Game.Me.Effects.ContainsKey(EntityEffect.Ironmaiden))
            {
                // Cursed, he cannot whirl, and a 35 second curse at the top seal had him stepping
                // away from threats until he was 80 out at the right seal with the amazon who anchors
                // on him in tow. He waits it out in town instead and rejoins once it has lifted.
                Log.Information("{Character} is under Iron Maiden at {Life} of {MaxLife} life, waiting it out in town", client.Game.Me.Name, client.Game.Me.Life, client.Game.Me.MaxLife);
                if (await _townManagementService.TakeTownPortalToTown(client))
                {
                    _retreated[client.Game.Me.Name] = 0;
                    return;
                }

                // No portal: keep moving, since the positions this client has for the monsters lag
                // and standing still is how he gets hit, but leashed to the kill spot.
                var me = client.Game.Me.Location;
                var home = _state.KillLocation ?? me;
                var threats = NPCHelpers.GetNearbyNPCs(client, me, 20, IronMaidenThreatRadius).Select(e => e.Location).ToList();
                var candidates = threats.Count > 0
                    ? RetreatPoints(me, threats)
                    : [me.Add((short)random.Next(-12, 13), (short)random.Next(-12, 13))];
                var retreat = me.Distance(home) > IronMaidenLeash
                    ? home
                    : candidates.FirstOrDefault(p => p.Distance(home) <= IronMaidenLeash) ?? home;
                if (client.Game.Me.HasSkill(Skill.Leap))
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.Leap, retreat);
                    await Task.Delay(150);
                }
                else
                {
                    var pathAway = await _pathingService.GetPathToLocation(client.Game, retreat, MovementMode.Walking);
                    if (pathAway.Count > 0)
                    {
                        var cancel = new CancellationTokenSource();
                        cancel.CancelAfter(600);
                        await MovementHelpers.TakePathOfLocations(client.Game, pathAway, MovementMode.Walking, cancel.Token);
                    }
                }

                return;
            }

            var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 20);
            var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
            if (nearest == null)
            {
                if (NoEnemiesNearby(client))
                {
                    await PickupItemsAndPotions(client, account, 10);
                }
                else
                {
                    // Only potions within reach while hostiles are near: the amazon walked to a Full
                    // Rejuvenation in De Seis' pack and went 649 to 164 in the seconds it took.
                    await PickupPotionsOnly(client, account, PotionReach);
                }

                nearest = enemies.FirstOrDefault();
            }
            else if(nearest.State == EntityState.Dead || nearest.State == EntityState.Dieing)
            {
                await ClassHelpers.FindItemOnDeadEnemy(client.Game, _pathingService, _mapApiService, nearest);
            }

            var nearbyPlayer = client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Amazon))
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            nearbyPlayer ??= client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Paladin))
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            nearbyPlayer ??= client.Game.Me;
            if (nearbyPlayer != null)
            {
                await _attackService.AssistPlayer(client, nearbyPlayer, null, bossFocus.Update(client, _state));
            }

        });
        return action;
    }

    private Func<Task> GetPaladinKillAction(Client client, AccountConfig account)
    {
        var bossFocus = new BossFocusTracker();
        async Task action()
        {
            if (await RetreatToTownIfHurt(client, FollowerRetreatLife))
            {
                return;
            }

            // Hammers up before the seal opens, monsters or none: the spiral is already spinning
            // when the spawn arrives instead of starting after it.
            if (DateTime.Now - _state.SealOpeningAt < SealHammerWindow && client.Game.Me.Skills.GetValueOrDefault(Skill.BlessedHammer) >= 20)
            {
                if (client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var aura) && aura == Skill.Vigor && client.Game.Me.HasSkill(Skill.Fanaticism))
                {
                    client.Game.ChangeSkill(Skill.Fanaticism, Hand.Right);
                }

                client.Game.ShiftHoldLeftHandSkillOnLocation(Skill.BlessedHammer, client.Game.Me.Location);
                await Task.Delay(100);
                return;
            }

            var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 50);
            var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
            if (nearest == null && NoEnemiesNearby(client))
            {
                await PickupItemsAndPotions(client, account, 10);
            }
            else
            {
                // Only potions within reach while hostiles are near: the amazon walked to a Full
                // Rejuvenation in De Seis' pack and went 649 to 164 in the seconds it took.
                await PickupPotionsOnly(client, account, PotionReach);
            }

            var post = _state.PaladinPost;
            if (post != null && client.Game.Belt.NumOfHealthPotions() > 0)
            {
                if (client.Game.Me.Location.Distance(post) > PaladinPostTolerance)
                {
                    var cancel = new CancellationTokenSource();
                    cancel.CancelAfter(1500);
                    var path = await _pathingService.GetPathToLocation(client.Game, post, MovementMode.Walking);
                    await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking, cancel.Token);
                }

                // Out on the post his targets are whatever stands around him; the party is thirty away.
                await _attackService.AssistPlayer(client, client.Game.Me);
                return;
            }

            var nearbyPlayer = client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Amazon))
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            nearbyPlayer ??= client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && (p.Class == CharacterClass.Barbarian))
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            nearbyPlayer ??= client.Game.Me;
            await _attackService.AssistPlayer(client, nearbyPlayer, null, bossFocus.Update(client, _state));
        }
        return action;
    }

    /// <summary>
    /// Where the followers should fight when the boss has been left nearly alone: his position, or
    /// null while the pack around him or the spot is still worth their targeting around the amazon.
    /// Logs each switch, so the log shows when a fight turned into a chase.
    /// </summary>
    private sealed class BossFocusTracker
    {
        private bool _focused;

        private uint _bossId;

        private DateTime _bossSeen;

        public Point Update(Client client, CsState state)
        {
            var killLocation = state.KillLocation;
            Point focus = null;
            if (killLocation != null)
            {
                var boss = NPCHelpers.GetNearbySuperUniques(client, killLocation, BossFocusSearchRadius)
                    .FirstOrDefault(e => e.NPCCode != NPCCode.Diablo && e.State != EntityState.Dead && e.State != EntityState.Dieing);
                if (boss != null)
                {
                    if (boss.Id != _bossId)
                    {
                        _bossId = boss.Id;
                        _bossSeen = DateTime.Now;
                    }

                    var escort = NPCHelpers.GetNearbyNPCs(client, boss.Location, 50, (int)BossFocusEscortRadius).Count(e => e.Id != boss.Id);
                    var crowd = NPCHelpers.GetNearbyNPCs(client, killLocation, 50, (int)BossFocusCrowdRadius).Count(e => e.Id != boss.Id);
                    var atSpot = NPCHelpers.GetNearbyNPCs(client, killLocation, 50, (int)BossFocusSpotRadius).Count(e => e.Id != boss.Id);
                    // Once on him, stay on him: the go decision flapped every few seconds as the
                    // count at the spot crossed three, the followers walked back and forth between
                    // the boss and the amazon, and De Seis outlived the seal timeout with twenty
                    // beside him. Only a pack arriving at the spot after all calls them back.
                    var committed = _focused && boss.Id == _bossId && atSpot <= BossFocusQuietSpot * 3;
                    var thin = escort <= BossFocusMaxEscort && (crowd <= BossFocusMaxCrowd || atSpot <= BossFocusQuietSpot);
                    // No escort cap: a quiet spot five seconds after the spawn means the pack is
                    // not coming, and a Vizier 28 out with thirteen around him stood untouched for
                    // the whole timeout under one. The crowd cap only excludes the whole seal's
                    // population still being on its way.
                    var far = boss.Location.Distance(killLocation) > BossFocusFarDistance
                        && crowd <= BossFocusFarMaxCrowd
                        && atSpot <= BossFocusQuietSpot
                        && DateTime.Now - _bossSeen > BossFocusFarDelay;
                    // Each client counts the pack from where it stands, and they disagree: at a De Seis
                    // 37 out the paladin's count let him go while the barbarian's and the amazon's
                    // kept them at the spot with nothing in reach for forty seconds. The first to
                    // commit takes the others with him.
                    var party = state.FocusedBossId == boss.Id && atSpot <= BossFocusQuietSpot * 3;
                    if (thin || far || committed || party)
                    {
                        focus = boss.Location;
                        state.FocusedBossId = boss.Id;
                        if (!_focused)
                        {
                            Log.Information("{Character} closing on {Boss} at {Location} ({Why}), {Escort} beside him, {AtSpot} at the spot, {Crowd} around it",
                                client.Game.Me.Name, boss.NPCCode, boss.Location, thin ? "escort thin" : far ? "far spawn" : committed ? "committed" : "party", escort, atSpot, crowd);
                        }
                    }
                }
            }

            _focused = focus != null;
            return focus;
        }
    }

    private const double PaladinPostTolerance = 5;

    private const int PaladinSweepRadius = 25;

    private const int PaladinSweepClearance = 8;

    private Func<Task> GetSorceressKillAction(Client client, AccountConfig account)
    {
        var moveTimer = new Stopwatch();
        moveTimer.Start();
        var orbTimer = new Stopwatch();
        orbTimer.Start();
        var lifeTimer = new Stopwatch();
        lifeTimer.Start();
        var lastLife = client.Game.Me.Life;
        var lastMaxLife = client.Game.Me.MaxLife;
        var townTimer = new Stopwatch();
        var escapedAt = DateTime.MinValue;
        var lastCastLog = DateTime.MinValue;
        var orbCasts = 0;
        var staticRefused = 0;
        var staticTimer = Stopwatch.StartNew();
        var novaTimer = Stopwatch.StartNew();
        Log.Information("{Character} skills: {Skills}; item skills: {ItemSkills}", client.Game.Me.Name,
            string.Join(" ", client.Game.Me.Skills.Where(k => k.Value > 0).Select(k => $"{k.Key}:{k.Value}")),
            string.Join(" ", client.Game.Me.ItemSkills.Where(k => k.Value > 0).Select(k => $"{k.Key}:{k.Value}")));
        Func<Task> action = (async () =>
        {
            if (client.Game.IsInTown())
            {
                if (client.Game.Me.Life < client.Game.Me.MaxLife * TaxiHealedLife && townTimer.Elapsed < TaxiHealWait)
                {
                    client.Game.UseHealthPotions();
                    await Task.Delay(700);
                    return;
                }

                Log.Information("{Character} returning from town at {Life} of {MaxLife} life after {Seconds:F1}s", client.Game.Me.Name, client.Game.Me.Life, client.Game.Me.MaxLife, townTimer.Elapsed.TotalSeconds);
                townTimer.Reset();
                await _townManagementService.TakeTownPortalToArea(client, client.Game.Me, Area.ChaosSanctuary);
                return;
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                await Task.Delay(TimeSpan.FromSeconds(0.1));
            }

            var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 50, 50);
            var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
            if (nearest == null)
            {
                if (NoEnemiesNearby(client))
                {
                    await PickupItemsAndPotions(client, account, 30);
                }
                else
                {
                    // Only potions within reach while hostiles are near: the amazon walked to a Full
                    // Rejuvenation in De Seis' pack and went 649 to 164 in the seconds it took.
                    await PickupPotionsOnly(client, account, PotionReach);
                }

                nearest = enemies.FirstOrDefault();
            }

            if (nearest == null || nearest.Location.Distance(client.Game.Me.Location) > 50)
            {
                // Out past the fight, nothing here moves her: an escape left her 51 from the spot, every
                // tick returned before the back-up move, and she stood there until the seal timed out
                // with Vizier long dead and the followers in town.
                if (_state.KillLocation != null && client.Game.Me.Location.Distance(_state.KillLocation) > TaxiStrayDistance)
                {
                    Log.Warning("{Character} is {Distance} from the kill spot with nothing in reach, going back", client.Game.Me.Name, (int)client.Game.Me.Location.Distance(_state.KillLocation));
                    var back = await _pathingService.GetPathToLocation(client.Game, _state.KillLocation, MovementMode.Teleport);
                    await MovementHelpers.TakePathOfLocations(client.Game, back, MovementMode.Teleport);
                }

                return;
            }

            var distanceToNearest = nearest.Location.Distance(client.Game.Me.Location);
            var life = client.Game.Me.Life;
            var maxLife = client.Game.Me.MaxLife;
            var since = lifeTimer.Elapsed.TotalSeconds;
            var bleeding = false;
            if (since > 0.25)
            {
                var lossPerSecond = (lastLife - life) / since;
                bleeding = maxLife > 0 && maxLife == lastMaxLife && lossPerSecond > maxLife * TaxiEscapeLossPerSecond;
                lastLife = life;
                lastMaxLife = maxLife;
                lifeTimer.Restart();
            }

            if (maxLife > 0 && life <= maxLife && life < maxLife * TaxiRetreatLife
                && enemies.Any(e => e.Location.Distance(client.Game.Me.Location) < TaxiRetreatRadius))
            {
                Log.Warning("{Character} retreating to town at {Life} of {MaxLife} life", client.Game.Me.Name, life, maxLife);
                townTimer.Restart();
                if (await _townManagementService.TakeTownPortalToTown(client))
                {
                    return;
                }
            }

            if (maxLife > 0 && life <= maxLife && (bleeding || life < maxLife * TaxiEscapeLifeFloor))
            {
                Log.Information("{Character} escaping at {Life} of {MaxLife} life", client.Game.Me.Name, life, maxLife);
                moveTimer.Restart();
                escapedAt = DateTime.Now;
                if (await _attackService.MoveToNearbySafeSpot(client, enemies.Select(e => e.Location).ToList(), client.Game.Me.Location, MovementMode.Teleport, TaxiEscapeMinDistance, TaxiEscapeMaxDistance))
                {
                    return;
                }

                var crowd = enemies.Where(e => e.Location.Distance(client.Game.Me.Location) < TaxiEscapeMaxDistance).ToList();
                if (crowd.Count > 0)
                {
                    foreach (var away in RetreatPoints(client.Game.Me.Location, crowd.Select(e => e.Location).ToList()))
                    {
                        if (await client.Game.TeleportToLocationAsync(away))
                        {
                            break;
                        }
                    }
                }

                return;
            }

            var monster = client.Game.WorldObjects.GetValueOrDefault((nearest.Id, EntityType.NPC));
            if (monster == null)
            {
                return;
            }

            var coldImmune = _attackService.IsImmuneTo(client, monster, ResistType.Cold);
            var lightningImmune = _attackService.IsImmuneTo(client, monster, ResistType.Lightning);
            var orbTarget = monster;
            if (coldImmune)
            {
                var reachable = enemies
                    .Where(e => e.Location.Distance(client.Game.Me.Location) < TaxiApproachRange
                        && !_attackService.IsImmuneTo(client, e, ResistType.Cold))
                    .ToList();
                orbTarget = reachable
                    .OrderByDescending(c => reachable.Count(o => o.Location.Distance(c.Location) < OrbClusterRadius))
                    .ThenBy(c => c.Location.Distance(client.Game.Me.Location))
                    .FirstOrDefault();
            }

            if (orbTarget == null && lightningImmune)
            {
                var away = enemies.Select(e => e.Location).ToList();
                await _attackService.MoveToNearbySafeSpot(client, away, client.Game.Me.Location, MovementMode.Teleport, TaxiEscapeMinDistance, TaxiEscapeMaxDistance);
                return;
            }

            // One anchor for every hop, so the hops never fight each other: two branches that each
            // moved her and returned, one toward the fight and one back to the spot, had her
            // teleporting in place for a whole game without a single orb. Fresh from an escape the
            // anchor is where she landed; with the fight twenty to thirty-five out and her life
            // whole enough, it is seventeen off the target; otherwise the kill spot.
            // The taxi sorceress has no Frozen Orb: Static Field 20, Nova 20, Lightning Mastery 20. On 1.09 Static
            // has no life floor, so her job is to static the boss down to nothing while the party
            // finishes him. That needs her within twelve of him, which she only closes to once a
            // follower is on him or her life is whole; lightning immunes get nothing from her.
            var staticMode = !client.Game.Me.HasSkill(Skill.FrozenOrb) && client.Game.Me.HasSkill(Skill.StaticField);
            WorldObject staticTarget = null;
            if (staticMode)
            {
                staticTarget = enemies
                    .Where(e => !_attackService.IsImmuneTo(client, e, ResistType.Lightning) && e.LifePercentage > StaticFloor)
                    .OrderBy(e => (e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique) || e.NPCCode == NPCCode.Diablo) ? 0 : 1)
                    .ThenBy(e => e.Location.Distance(client.Game.Me.Location))
                    .FirstOrDefault(e => e.Location.Distance(client.Game.Me.Location) < ((e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique) || e.NPCCode == NPCCode.Diablo) ? StaticBossRange : TaxiApproachRange));
            }

            var targetDistance = orbTarget?.Location.Distance(client.Game.Me.Location) ?? double.MaxValue;
            var staticDistance = staticTarget?.Location.Distance(client.Game.Me.Location) ?? double.MaxValue;
            var staticTargetIsBoss = staticTarget != null && (staticTarget.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique) || staticTarget.NPCCode == NPCCode.Diablo);
            var followersOnTarget = staticTarget == null ? 0 : client.Game.Players.Count(p => p.Id != client.Game.Me.Id && p.Location != null && p.Location.Distance(staticTarget.Location) < 10);
            // De Seis usually carries an aura and his pack hits hard beside him; she waits until the
            // pack around a boss is down to a couple before going in to static him.
            var bossEscort = staticTarget == null ? 0 : enemies.Count(e => e.Id != staticTarget.Id && e.Location.Distance(staticTarget.Location) < BossFocusEscortRadius);
            Point anchor;
            if (DateTime.Now - escapedAt < TaxiEscapeHold)
            {
                anchor = client.Game.Me.Location;
            }
            else if (staticMode && staticTarget != null && staticDistance >= StaticRange
                && (staticTargetIsBoss
                    ? followersOnTarget >= 1 && bossEscort <= StaticBossMaxEscort
                    : life > maxLife * 0.5))
            {
                anchor = client.Game.Me.Location.GetPointBeforePointInSameDirection(staticTarget.Location, StaticRange - 2);
            }
            else if (!staticMode && orbTarget != null && targetDistance >= SorceressSkillRange && targetDistance < TaxiApproachRange && life > maxLife * TaxiApproachMinLife)
            {
                anchor = client.Game.Me.Location.GetPointBeforePointInSameDirection(orbTarget.Location, SorceressSkillRange - 3);
            }
            else
            {
                anchor = _state.KillLocation
                    ?? client.Game.GetEntityByCode(EntityCode.TownPortal)
                        .OrderBy(t => t.Location.Distance(client.Game.Me.Location))
                        .Select(t => t.Location)
                        .FirstOrDefault();
            }

            // Static is instant, so it goes before any hop: with the cast behind the movement she
            // repositioned every tick a monster stood within five and cast nothing at all.
            // The hold-and-repeat packet, as the Mephisto bot statics: twelve single casts in three
            // seconds cost her seventeen mana, so the server took two of them. Spaced to the cast rate.
            // Static hits everything around her, so anything worth it within reach is reason enough to
            // cast: holding it for Infector 14 to 20 out, she cast nothing for thirteen seconds with
            // Doom Knights three away.
            var castNow = false;
            var staticWorthIt = staticMode && enemies.Any(e => e.Location.Distance(client.Game.Me.Location) < StaticRange
                && e.LifePercentage > StaticFloor
                && !_attackService.IsImmuneTo(client, e, ResistType.Lightning));
            if (staticWorthIt && staticTimer.Elapsed > StaticInterval)
            {
                if (client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location))
                {
                    orbCasts++;
                    castNow = true;
                }
                else
                {
                    staticRefused++;
                }

                staticTimer.Restart();
            }

            // Once everything around her is below the static floor, Static does nothing more and Nova
            // finishes them with the party; she stood beside a 15% Diablo casting nothing. Diablo
            // below the floor is also worth stepping in for.
            var novaOnDiablo = staticMode && nearest.NPCCode == NPCCode.Diablo && nearest.LifePercentage <= StaticFloor && client.Game.Me.HasSkill(Skill.Nova);
            var novaWorthIt = staticMode && !staticWorthIt && client.Game.Me.HasSkill(Skill.Nova)
                && enemies.Any(e => e.Location.Distance(client.Game.Me.Location) < NovaRange && !_attackService.IsImmuneTo(client, e, ResistType.Lightning));
            if (novaWorthIt && novaTimer.Elapsed > StaticInterval)
            {
                if (client.Game.RepeatRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location))
                {
                    orbCasts++;
                    castNow = true;
                }
                else
                {
                    staticRefused++;
                }

                novaTimer.Restart();
            }

            if (nearest.NPCCode == NPCCode.Diablo)
            {
                if (novaOnDiablo && distanceToNearest >= NovaRange)
                {
                    await client.Game.TeleportToLocationAsync(client.Game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, NovaRange - 2));
                }
                else if (distanceToNearest > 15)
                {
                    await client.Game.TeleportToLocationAsync(nearest.Location);
                }
            }
            else if (distanceToNearest < (staticMode ? 3 : 5) || (!castNow && moveTimer.Elapsed > TaxiIdleHopInterval))
            {
                // With nothing to cast she does not stand and wait either: a hop to the quietest
                // ground five to ten out, the old safe spot near the anchor when there is none.
                var hopTo = await QuietSpotNear(client, enemies, anchor, TaxiStepMin, TaxiStepMax);
                if (hopTo != null && await client.Game.TeleportToLocationAsync(hopTo))
                {
                    moveTimer.Restart();
                }
                else if (anchor != null && await _attackService.MoveToNearbySafeSpot(client, enemies.Select(e => e.Location).ToList(), anchor, MovementMode.Teleport, 0, 8))
                {
                    moveTimer.Restart();
                }
                else if (anchor != null && anchor.Distance(client.Game.Me.Location) > 12)
                {
                    // An escape can leave her 45 from the spot, past a single teleport's reach; one
                    // game she sat there stranded on rejuvenations until the seal timed out.
                    Log.Warning("Back-up move for {Character} at {Location}, {Distance} from the anchor", client.Game.Me.Name, client.Game.Me.Location, (int)client.Game.Me.Location.Distance(anchor));
                    var path = await _pathingService.GetPathToLocation(client.Game, anchor, MovementMode.Teleport);
                    await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Teleport);
                    moveTimer.Restart();
                }
                else
                {
                    moveTimer.Restart();
                }

                return;
            }

            if (DateTime.Now - lastCastLog > CastLogInterval)
            {
                // Why she is or is not casting, every few seconds: a whole game without an orb was
                // reported from the outside and nothing in the log said which gate held her.
                Log.Information("{Character} at {Location}: orb skill {HasOrb}, target {Target} at {Distance}, chilled {Chilled}, static skill {HasStatic}, lightning immune {LI}, anchor {Anchor}, life {Life}",
                    client.Game.Me.Name, client.Game.Me.Location, client.Game.Me.HasSkill(Skill.FrozenOrb), orbTarget?.NPCCode.ToString() ?? "none",
                    orbTarget == null ? -1 : (int)orbTarget.Location.Distance(client.Game.Me.Location), orbTarget?.Effects.Contains(EntityEffect.Cold) ?? false,
                    client.Game.Me.HasSkill(Skill.StaticField), lightningImmune, anchor, life);
                Log.Information("{Character} casts so far this fight: {Casts} ({Refused} refused), static target {StaticTarget} at {StaticDistance}, mana {Mana}, right hand {Right}",
                    client.Game.Me.Name, orbCasts, staticRefused, staticTarget?.NPCCode.ToString() ?? "none", staticTarget == null ? -1 : (int)staticDistance, client.Game.Me.Mana,
                    client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var rightNow) ? rightNow.ToString() : "?");
                lastCastLog = DateTime.Now;
            }

            if (staticMode)
            {
                await Task.Delay(TimeSpan.FromSeconds(0.1));
                return;
            }

            if (client.Game.Me.HasSkill(Skill.FrozenOrb)
            && orbTarget != null
            && orbTimer.Elapsed > TimeSpan.FromSeconds(1)
            && orbTarget.Location.Distance(client.Game.Me.Location) < SorceressSkillRange
            && (!orbTarget.Effects.Contains(EntityEffect.Cold) || !ClassHelpers.CanStaticEntity(client, orbTarget.LifePercentage)))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, orbTarget.Location);
                orbCasts++;
                await Task.Delay(TimeSpan.FromSeconds(0.25));
                orbTimer.Restart();
            }
            else if (!client.Game.Me.HasSkill(Skill.FrozenOrb) && client.Game.Me.HasSkill(Skill.FrostNova) && distanceToNearest < 10 && !monster.Effects.Contains(EntityEffect.Cold))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.FrostNova, client.Game.Me.Location);
                await Task.Delay(TimeSpan.FromSeconds(0.25));
            }
            else if (client.Game.Me.HasSkill(Skill.StaticField) && !lightningImmune && distanceToNearest < SorceressSkillRange)
            {
                client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
            }
            await Task.Delay(TimeSpan.FromSeconds(0.1));
        });
        return action;
    }

    private const double KiteDistance = 12;

    private const int KiteCrowd = 3;

    /// <summary>Life Tap on what stands beside the barbarian, Amplify on what does not, as in the necromancer's assist.</summary>
    private static async Task CurseWhileKiting(Client client)
    {
        var me = client.Game.Me;
        var barbarian = client.Game.Players.FirstOrDefault(p => p.Class == CharacterClass.Barbarian && p.Location != null && p.Id != me.Id);
        var target = NPCHelpers.GetNearbyNPCs(client, me.Location, 10, (int)KiteCurseRange)
            .FirstOrDefault(e => !e.Effects.Contains(EntityEffect.Lifetap) && !e.Effects.Contains(EntityEffect.Amplifydamage));
        if (target == null)
        {
            return;
        }

        var besideBarbarian = barbarian != null && target.Location.Distance(barbarian.Location) < KiteAmplifyKeepOut;
        var skill = besideBarbarian || target.NPCCode == NPCCode.OblivionKnight || target.NPCCode == NPCCode.AbyssKnight ? Skill.LifeTap : Skill.AmplifyDamage;
        if (!me.HasSkill(skill) || me.Mana < 15)
        {
            return;
        }

        Log.Information("{Character} casting {Skill} on {NPCCode} while kiting", me.Name, skill, target.NPCCode);
        client.Game.UseRightHandSkillOnEntity(skill, target);
        await Task.Delay(150);
    }

    private const double KiteCurseRange = 25;

    private const double KiteAmplifyKeepOut = 15;

    private const double DiabloCurseRange = 25;

    /// <summary>
    /// Keeps a ranged follower out of melee. Curses and arrows work from twenty units; the necromancer
    /// died at 2424 life standing where the hammerdin stood, and the amazon has 1041. A step back to
    /// the party's side of the nearest enemy, and only then the attack.
    /// </summary>
    private async Task<bool> KiteIfCrowded(Client client)
    {
        var me = client.Game.Me.Location;
        var anchor = _state.KillLocation ?? me;
        // A crowd, not one monster: kiting from any single one in reach kept him walking through the
        // whole Diablo fight without a curse cast.
        var close = NPCHelpers.GetNearbyNPCs(client, me, KiteCrowd, (int)KiteDistance);
        if (close.Count() < KiteCrowd)
        {
            // Nothing on him: if his kiting has carried him off, he walks back. Step after step
            // away from threats with no anchor left him forty off the spot, looting a ring alone
            // while De Seis spawned on the others.
            if (me.Distance(anchor) > KiteLeash)
            {
                var back = await _pathingService.GetPathToLocation(client.Game, anchor, MovementMode.Walking);
                var leashCancel = new CancellationTokenSource();
                leashCancel.CancelAfter(800);
                await MovementHelpers.TakePathOfLocations(client.Game, back, MovementMode.Walking, leashCancel.Token);
                return true;
            }

            return false;
        }

        var threats = NPCHelpers.GetNearbyNPCs(client, me, 20, 25).Select(e => e.Location).ToList();
        // A safe spot beside the kill spot first, so kiting keeps him with the party; straight away
        // from the threats only when there is none.
        if (await _attackService.MoveToNearbySafeSpot(client, threats, anchor, MovementMode.Walking, 6, 18))
        {
            return true;
        }

        var retreat = RetreatPoints(me, threats).First();
        var path = await _pathingService.GetPathToLocation(client.Game, retreat, MovementMode.Walking);
        if (path.Count > 0)
        {
            var cancel = new CancellationTokenSource();
            cancel.CancelAfter(600);
            await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking, cancel.Token);
        }

        return true;
    }

    private const double KiteLeash = 30;

    private Func<Task> GetNecromancerKillAction(Client client, AccountConfig account)
    {
        var bossFocus = new BossFocusTracker();
        async Task action()
        {
            if (await RetreatToTownIfHurt(client, FollowerRetreatLife))
            {
                return;
            }

            // Before kiting: Diablo within twelve kept him walking away for the whole fight, and the
            // Amplify that doubles the party's physical damage on him never went on.
            var diablo = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, (int)DiabloCurseRange)
                .FirstOrDefault(e => e.NPCCode == NPCCode.Diablo && !e.Effects.Contains(EntityEffect.Amplifydamage));
            if (diablo != null && client.Game.Me.HasSkill(Skill.AmplifyDamage) && client.Game.Me.Mana > 5)
            {
                Log.Information("{Character} casting {Skill} on {NPCCode}", client.Game.Me.Name, Skill.AmplifyDamage, diablo.NPCCode);
                client.Game.UseRightHandSkillOnEntity(Skill.AmplifyDamage, diablo);
                await Task.Delay(200);
                return;
            }

            if (NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, KiteCrowd, (int)KiteDistance).Count() >= KiteCrowd)
            {
                // Crowded, he steps away, but a curse goes out on every step: kiting ticks that only
                // moved left whole busy fights without a cast from him.
                await CurseWhileKiting(client);
            }

            if (await KiteIfCrowded(client))
            {
                return;
            }

            // The barbarian is the one who needs the curses where he fights: Life Tap on his pack
            // heals him through Iron Maiden's reflection. The paladin is the anchor only without him.
            var nearbyPlayer = client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Barbarian)
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            nearbyPlayer ??= client.Game.Players
            .Where(p => p.Id != client.Game.Me.Id && p.Location != null && p.Class == CharacterClass.Paladin)
            .OrderBy(p => p.Location.Distance(client.Game.Me.Location)).FirstOrDefault();
            if (nearbyPlayer != null)
            {
                await _attackService.AssistPlayer(client, nearbyPlayer, null, bossFocus.Update(client, _state));
            }

            var enemies = NPCHelpers.GetNearbyNPCs(client, _state.KillLocation, 5, 20);
            var nearest = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
            if (nearest == null && NoEnemiesNearby(client))
            {
                await PickupItemsAndPotions(client, account, 20);
            }
            else
            {
                // Only potions within reach while hostiles are near: the amazon walked to a Full
                // Rejuvenation in De Seis' pack and went 649 to 164 in the seconds it took.
                await PickupPotionsOnly(client, account, PotionReach);
            }

        }
        return action;
    }

    private static readonly TimeSpan PositionLogInterval = TimeSpan.FromSeconds(3);

    private const int QuietRadius = 40;

    private static readonly TimeSpan QuietSealDone = TimeSpan.FromSeconds(8);

    private void LogPosition(Client client)
    {
        var me = client.Game.Me;
        var nearest = NPCHelpers.GetNearbyNPCs(client, me.Location, 1, 60).FirstOrDefault();
        Log.Information("{Character} position {Location}, {FromSpot} from the kill spot, life {Life} of {MaxLife}, nearest {Nearest} at {Distance}, {Within10} within ten",
            me.Name, me.Location, _state.KillLocation == null ? -1 : (int)me.Location.Distance(_state.KillLocation), me.Life, me.MaxLife,
            nearest?.NPCCode.ToString() ?? "none", nearest == null ? -1 : (int)nearest.Location.Distance(me.Location),
            NPCHelpers.GetNearbyNPCs(client, me.Location, 50, 10).Count());
    }

    private async Task<bool> KillBosses(Client client,
                            AccountConfig account,
                            CancellationTokenSource taskCancellation,
                            Func<Task> action,
                            CsState ownState,
                            CsState newState,
                            bool checkBossDead = false)
    {
        var sealMaxTimeout = new Stopwatch();
        sealMaxTimeout.Start();
        var bossStill = Stopwatch.StartNew();
        double bossLife = -1;
        Point bossAt = null;
        var positionLog = Stopwatch.StartNew();
        var quietFor = Stopwatch.StartNew();
        var bossSeen = false;
        var lastChange = new Dictionary<uint, (Point At, double Life, DateTime Since)>();
        var fightActive = true;
        do
        {
            await Task.Delay(100);
            if (sealMaxTimeout.Elapsed > TimeSpan.FromSeconds(50))
            {
                // Only the taxi's timeout ends the game. A follower out of sight of the corpse never
                // receives the boss's death and ran its fifty seconds while the taxi was already at
                // the next seal; its timeout then threw away a game that was going fine.
                if (!IsTeleportClient(client))
                {
                    Log.Warning("{Character} reached the seal timeout, following the portal", client.Game.Me.Name);
                    return true;
                }

                Log.Warning($" {client.Game.Me.Name} reached seal timeout");
                NextGame.TrySetResult(true);
                return false;
            }

            if(checkBossDead && NPCHelpers.GetNearbySuperUniques(client, BossDownCheckRadius).Any(w => (w.State == EntityState.Dead || w.State == EntityState.Dieing) && !_state.IsEarlierBoss(w)))
            {
                break;
            }

            if (checkBossDead && IsTeleportClient(client) && ownState.BossId is uint bossId)
            {
                var boss = client.Game.WorldObjects.GetValueOrDefault((bossId, EntityType.NPC));
                if (boss != null && boss.State != EntityState.Dead && boss.State != EntityState.Dieing)
                {
                    if (boss.LifePercentage != bossLife || boss.Location != bossAt)
                    {
                        bossLife = boss.LifePercentage;
                        bossAt = boss.Location;
                        bossStill.Restart();
                    }
                    // Not while the fight goes on: on the seal1-below layout the map calls ground beside
                    // the seal unwalkable, and Vizier in his pack went twelve seconds without a hit with
                    // the experience still climbing - the game was given up mid-fight.
                    else if (bossStill.Elapsed > UnreachableBossTimeout
                        && !fightActive
                        && !await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary, boss.Location))
                    {
                        Log.Warning("{Character}: {Boss} at {Location} untouched for {Seconds}s on unwalkable ground, unreachable, giving up the game", client.Game.Me.Name, boss.NPCCode, boss.Location, (int)bossStill.Elapsed.TotalSeconds);
                        NextGame.TrySetResult(true);
                        return false;
                    }
                }
            }

            // A dry follower used to stand "disengaged" in the pack until the boss dropped, because
            // the restock check lives outside this loop; the amazon went to 177 life that way. Dry
            // means dry: at "fewer than three" the paladin, with no cells for spares, left seven
            // fights in nine after his fifth drink and the pack outlived everyone.
            if (!IsTeleportClient(client)
                && client.Game.Belt.NumOfHealthPotions() == 0
                && client.Game.Belt.NumOfRejuvenationPotions() == 0
                && !client.Game.Inventory.Items.Any(i => i.Classification == ClassificationType.HealthPotion || i.Classification == ClassificationType.RejuvenationPotion))
            {
                Log.Warning("{Character} is dry, leaving the fight to restock", client.Game.Me.Name);
                break;
            }

            // Off to town the moment she decides to move the spot: the trip through town took the
            // followers seven to ten seconds after her new portal was up, with her alone there.
            if (!IsTeleportClient(client) && _state.RelocationPending && !client.Game.IsInTown())
            {
                Log.Information("{Character} heading to town ahead of the relocated portal", client.Game.Me.Name);
                await _townManagementService.TakeTownPortalToTown(client);
            }

            // A follower that portalled out has to leave this loop for the heal and the way back,
            // which live in the loop around it: staying here kept a retreated barbarian in town for
            // the rest of the seal, five of ten failed fights in one batch.
            if (!IsTeleportClient(client) && client.Game.IsInTown())
            {
                return true;
            }

            if (checkBossDead && IsTeleportClient(client))
            {
                // Only monsters that are doing something count. A Storm Caster on unwalkable ground took
                // twenty whirls and sixty Life Taps without a mark while Vizier lay dead out of sight,
                // and kept the room from ever looking quiet.
                var near = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 50, QuietRadius);
                if (ownState.KillLocation != null)
                {
                    near = near.Concat(NPCHelpers.GetNearbyNPCs(client, ownState.KillLocation, 50, QuietRadius));
                }

                var hostileNear = false;
                foreach (var monster in near)
                {
                    if (!lastChange.TryGetValue(monster.Id, out var seen) || seen.At != monster.Location || seen.Life != monster.LifePercentage)
                    {
                        lastChange[monster.Id] = (new Point(monster.Location.X, monster.Location.Y), monster.LifePercentage, DateTime.Now);
                        hostileNear = true;
                    }
                    else if (DateTime.Now - seen.Since < QuietSealDone)
                    {
                        hostileNear = true;
                    }
                }
                fightActive = hostileNear;
                if (hostileNear || !bossSeen || client.Game.IsInTown())
                {
                    quietFor.Restart();
                }

                // Armed by a fight, not by seeing the boss: Infector died at the right seal with this
                // client not counting him as seen, and the party stood forty seconds in an empty room.
                bossSeen |= hostileNear;
                if (bossSeen && quietFor.Elapsed > QuietSealDone)
                {
                    // Bosses were killed and nobody noticed: the experience jumped, then the party
                    // stood in an empty room until the timeout. What this client knows of the bosses
                    // says why the death check missed it.
                    var known = string.Join("; ", client.Game.WorldObjects.Values
                        .Where(w => w.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique))
                        .Select(w => $"{w.NPCCode} {w.Id} {w.State} at {(int)w.Location.Distance(client.Game.Me.Location)}{(_state.IsEarlierBoss(w) ? " earlier" : string.Empty)}"));
                    Log.Warning("{Character}: nothing within {Radius} for {Seconds}s after the boss was seen, taking the fight as won; bosses known: {Known}",
                        client.Game.Me.Name, QuietRadius, (int)QuietSealDone.TotalSeconds, known);
                    break;
                }
            }

            if (positionLog.Elapsed > PositionLogInterval)
            {
                // Where everyone stands, so a fight watched in game can be matched to the log.
                LogPosition(client);
                positionLog.Restart();
            }

            await action.Invoke();

        } while (
        (taskCancellation == null || !taskCancellation.IsCancellationRequested)
        && !await IsNextGame()
        && client.Game.IsInGame()
        && !ownState.TeleportHasChanged(newState));

        var nearest = NPCHelpers.GetNearbySuperUniques(client, BossDownCheckRadius).FirstOrDefault(w => w.State == EntityState.Dead || w.State == EntityState.Dieing);
        if (nearest != null)
        {
            if (client.Game.Me.HasSkill(Skill.FindItem)
                && await ClassHelpers.FindItemOnDeadEnemy(client.Game, _pathingService, _mapApiService, nearest))
            {
                await Task.Delay(300);
            }
        }

        if (client.Game.IsInGame())
        {
            if (NoEnemiesNearby(client))
            {
                await PickupItemsAndPotions(client, account, 15);
            }
            else
            {
                // Only potions within reach while hostiles are near: the amazon walked to a Full
                // Rejuvenation in De Seis' pack and went 649 to 164 in the seconds it took.
                await PickupPotionsOnly(client, account, PotionReach);
            }
        }

        return client.Game.IsInGame();
    }

    private async Task<Point> GetSeal(Client client, EntityCode entityCode)
    {
        var map = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary);
        if (!map.Objects.TryGetValue((int)entityCode, out var objectPoints) || objectPoints.Count == 0)
        {
            Log.Error($"Did not find a {entityCode} in the mapdata");
            return null;
        }

        return objectPoints.First();
    }
}
