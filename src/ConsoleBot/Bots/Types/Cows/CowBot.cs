using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
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
using System.Timers;

namespace ConsoleBot.Bots.Types.Cows;

public class CowBot : MultiClientBotBase
{
    private readonly ITownManagementService _townManagementService;
    private readonly IAttackService _attackService;
    private readonly IMapApiService _mapApiService;
    private readonly CowConfiguration _cowconfig;
    private uint? BoClientPlayerId;
    private readonly ConcurrentDictionary<string, bool> ShouldFollow = new();
    private readonly ConcurrentDictionary<string, (Point, CancellationTokenSource)> FollowTasks = new();
    private CowManager _cowManager;
    private readonly BreadcrumbTrail _huntTrail = new();
    private List<Client> _huntingParty = [];
    private List<Client> _killingClients = [];
    private volatile bool _partyRetreating;

    private const double HuntingPartyPickupRadius = 25;

    /// <summary>
    /// Clients that can teleport range much further for loot: they rejoin the group in one cast, so
    /// there is no cost to letting them sweep a wide area while the walkers stay tight.
    /// </summary>
    private const double TeleportPickupRadius = 100;
    /// <summary>
    /// Life the party has to be back to before it re-engages. Taken from the level at which the
    /// chicken service stops drinking: asking for more than that waits for life that potions will
    /// never deliver, and the retreat would never resolve.
    /// </summary>
    private double ResumeLifeFraction => _config.Chicken?.UseHealthPotionPercent ?? 0.7;

    /// <summary>
    /// Life at which the party breaks off, set midway between leaving the game and topping up, so
    /// there is room to disengage before the chicken threshold and room to heal back to the resume
    /// bar afterwards.
    /// </summary>
    private double RetreatLifeFraction => ((_config.Chicken?.LifeChickenPercent ?? 0.2) + ResumeLifeFraction) / 2;
    private const double RetreatDistance = 50;
    private const double StragglerDistance = 30;

    /// <summary>
    /// How far behind a cow killer the party trails before the gate opens. Far enough to stay out
    /// of the pack it is nova-ing, close enough to be on cleared ground when a cluster opens up.
    /// </summary>
    private const double KillerTrailDistance = 25;
    private static readonly TimeSpan StragglerWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the party will hold waiting to heal before giving up and carrying on. Without a
    /// bound a character that cannot reach the resume threshold, because it is out of potions or
    /// simply thin, holds the whole group in place for the rest of the game.
    /// </summary>
    private static readonly TimeSpan RetreatTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Monsters this close are hitting us, so they get hit back even while disengaging.</summary>
    private const double SelfDefenceRange = 10;

    /// <summary>
    /// How far from a party member something can be and still mean that member is fighting. Self
    /// defence range was used here, and at ten units it never described the amazon: she kills at
    /// bow range, so nothing is ever that close to her, the party read her as idle and walked off
    /// while she was shooting. She then had to run the gap back, and was the straggler the group
    /// waited for in twenty one of thirty four holds.
    ///
    /// Set to the same distance the lead already uses to decide it is itself engaged, rather than
    /// to bow range: a wider radius would hold the party still whenever anything was alive anywhere
    /// near any of six characters, which trades one stall for a worse one.
    /// </summary>
    private const double EscortEngagementRange = 20;

    /// <summary>
    /// Health potions bought beyond what the belt holds, kept in the inventory as a reserve. Sized
    /// from measured use: the nova sorceresses stand in the packs they are killing and get through
    /// thirteen or so a game, and running dry is what ends games early.
    /// </summary>
    private const int ActiveModePotionBuffer = 14;

    /// <summary>
    /// How far from the walking party a sorceress will look for leftover cows once no cluster is
    /// left to claim. Kept close to the group so the help lands where the group actually is.
    /// </summary>
    private const double StraySearchRadius = 100;

    /// <summary>
    /// How close a sorceress has to be before nova and static reach. Both are cast on her own
    /// position and hit a radius around it, so standing at teleport range and casting hits nothing.
    /// The main killing loop uses the same distance to decide it is in range.
    /// </summary>
    private const double NovaRange = 10;

    /// <summary>Nova's radius is a fixed nine yards, which is what <see cref="NovaRange"/> is.</summary>
    private const double NovaYards = 9;

    /// <summary>
    /// How far static reaches for this character. The skill covers 3.3 yards at level 1 and grows
    /// two thirds of a yard per level, so it is worth reading off the character rather than
    /// guessing a constant: a well levelled static is twice the reach of nova, and everything it
    /// halves dies to one volley from the party.
    /// </summary>
    private static double StaticRangeOf(Client client)
    {
        var level = client.Game.Me.Skills.GetValueOrDefault(Skill.StaticField);
        if (level <= 0)
        {
            return 0;
        }

        var yards = 2.633 + (0.667 * level);
        return yards * (NovaRange / NovaYards);
    }

    /// <summary>
    /// How long the party will wait for the ground where someone died to be cleared. Nothing brings
    /// the character back while its killers are still standing on it, so past this the game is worth
    /// less than a fresh one.
    /// </summary>
    private static readonly TimeSpan DeathRecoveryTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BreadcrumbInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How close the party has to be to a tracked monster before its absence from the game means it
    /// is gone rather than just out of range.
    /// </summary>
    private const double OutOfSightPruneDistance = 20;

    /// <summary>
    /// How far the party will go per surviving monster before writing a cluster off, clamped to the
    /// range below. Running fifty units for one straggler earns less than the party's usual rate;
    /// for a handful of them it earns a good deal more, so the distance follows the prize.
    /// </summary>
    /// <summary>
    /// How far away a soul cluster may be before the party would rather stay with the sorceresses
    /// and wait for a nearer one to open. Only applies while they are still clearing; once they
    /// are done nothing new will open and distance stops mattering.
    /// </summary>
    private const double FarClusterDistance = 110;

    private const double SurvivorChasePerMonster = 20;
    private const double MinSurvivorChase = 40;
    private const double MaxSurvivorChase = 110;

    /// <summary>How long a killer waits for shouts to come back before giving up on the game.</summary>
    private static readonly TimeSpan MissingBoTimeout = TimeSpan.FromSeconds(60);
    public CowBot(IOptions<BotConfiguration> config, IOptions<CowConfiguration> cowconfig,
        IExternalMessagingClient externalMessagingClient, IPathingService pathingService,
        ITownManagementService townManagementService,
        IAttackService attackService,
        IMapApiService mapApiService,
        IMuleService muleService
        )
        : base(config, cowconfig, externalMessagingClient, muleService, pathingService)
    {
        _townManagementService = townManagementService;
        _attackService = attackService;
        _mapApiService = mapApiService;
        _cowconfig = cowconfig.Value;
    }

    public override string GetName()
    {
        return "cows";
    }

    protected override void PostInitializeClient(Client client, AccountConfig accountCharacter)
    {
        ShouldFollow.TryAdd(accountCharacter.Character.ToLower(), false);
        FollowTasks.TryAdd(accountCharacter.Character.ToLower(), (null, new CancellationTokenSource()));
        // Subscribed once for the life of the client and dispatched to whichever manager is running
        // the current game; Client has no way to unsubscribe, so per-game handlers would pile up.
        client.OnReceivedPacketEvent(InComingPacket.AssignNPC2, p => CurrentManagerFor(client)?.OnAssignNpc(p));
        client.OnReceivedPacketEvent(InComingPacket.AssignNPC1, p => CurrentManagerFor(client)?.OnAssignNpc(p));
        client.OnReceivedPacketEvent(InComingPacket.NPCState, p => CurrentManagerFor(client)?.OnNpcState(p));
        client.OnReceivedPacketEvent(InComingPacket.NPCMove, p => { var m = new NPCMovePacket(p); CurrentManagerFor(client)?.OnNpcMove(m.Location, m.EntityId); });
        client.OnReceivedPacketEvent(InComingPacket.NPCStop, p => { var m = new NPCStopPacket(p); CurrentManagerFor(client)?.OnNpcStop(m.EntityId, m.Location, m.LifePercentage); });
        client.OnReceivedPacketEvent(InComingPacket.NPCMoveToTarget, p => { var m = new NPCMoveToTargetPacket(p); CurrentManagerFor(client)?.OnNpcMove(m.Location, m.EntityId); });
        client.OnReceivedPacketEvent(InComingPacket.NPCHit, p => { var m = new NpcHitPacket(p); CurrentManagerFor(client)?.OnNpcHit(m.EntityId, m.LifePercentage); });

        client.OnReceivedPacketEvent(InComingPacket.EntityMove, async (packet) =>
        {
            var entityMovePacket = new EntityMovePacket(packet);
            if (entityMovePacket.UnitType == EntityType.Player && entityMovePacket.UnitId == BoClientPlayerId && ShouldFollowLeadClient(client))
            {
                await FollowToLocation(client, entityMovePacket.MoveToLocation);
            }
        });

        client.OnReceivedPacketEvent(InComingPacket.ReassignPlayer, async (packet) =>
        {
            var reassignPlayerPacket = new ReassignPlayerPacket(packet);
            if (reassignPlayerPacket.UnitType == EntityType.Player && reassignPlayerPacket.UnitId == BoClientPlayerId && ShouldFollowLeadClient(client))
            {
                await FollowToLocation(client, reassignPlayerPacket.Location);
            }
        });
        client.OnReceivedPacketEvent(InComingPacket.PartyAutomapInfo, async (packet) =>
        {
            var partyAutomapInfoPacket = new PartyAutomapInfoPacket(packet);
            if (partyAutomapInfoPacket.Id == BoClientPlayerId && ShouldFollowLeadClient(client))
            {
                await FollowToLocation(client, partyAutomapInfoPacket.Location);
            }
        });
    }

    protected override async Task<bool> PrepareForRun(Client client, AccountConfig account)
    {
        var townManagementOptions = new TownManagementOptions(account, Act.Act1);

        if (_cowconfig.ActiveMode)
        {
            // Everyone fights in active mode, so everyone drinks. Buy past what the belt holds: the
            // surplus sits in the inventory and refills the belt as columns empty, which is the
            // difference between running dry mid level and finishing the game.
            var beltShortfall = (client.Game.Belt.Height * account.HealthSlots.Count)
                - client.Game.Belt.GetHealthPotionsInSlots(account.HealthSlots).Count;
            // Minus what is already carried. Buying the buffer afresh every visit ratcheted the
            // inventory up to 62 potions of its 80 cells, and a full inventory is what stops loot
            // being picked up and makes the next purchase fail with nowhere to put it.
            var carried = client.Game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion);

            // The nova sorceresses stand in the pack they are killing and drink about fifteen a game
            // against two for everyone else. One buffer for all of them sent the two who need it
            // into the level with twenty potions and had them finish on nothing, at half life, with
            // sixty free cells they could have been carrying more in.
            var buffer = client.Game.Me.Class == CharacterClass.Sorceress
                && client.Game.Me.Skills.GetValueOrDefault(Skill.Nova) >= 20
                    ? ActiveModePotionBuffer * 2
                    : ActiveModePotionBuffer;
            townManagementOptions.HealthPotionsToBuy = Math.Max(0, Math.Max(0, beltShortfall) + buffer - carried);
        }

        var isPortalCharacter = _cowconfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase);
        if (isPortalCharacter)
        {
            // Only the portal character needs the leg, so only it reserves one.
            D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.WirtsLeg);

            var tomesOfTp = client.Game.Inventory.Items.Count(i => i.Name == ItemName.TomeOfTownPortal);
            if (tomesOfTp < 2)
            {
                townManagementOptions.ItemsToBuy = new Dictionary<ItemName, int>()
                               {
                                   { ItemName.TomeOfTownPortal, 2 - tomesOfTp }
                               };
            }
        }

        // The result is worth checking, and the budget has to cover the work. A character with a
        // full stash to sell takes well past twenty seconds, and the old budget expired part way
        // through shopping - before the town portal tomes were bought. Carrying on regardless sent
        // the portal character to Tristram with no way back and cost the whole game.
        var townTasksDone = await GeneralHelpers.TryWithTimeout(
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
            TimeSpan.FromSeconds(90));

        if (!townTasksDone)
        {
            Log.Warning($"Client {client.Game.Me.Name} did not finish its town tasks, taking a new game rather than starting one short");
            return false;
        }

        if (isPortalCharacter)
        {
            if (!await CreateCowLevel(client))
            {
                return false;
            }

            if (!await OpenPortalAtCata2(client))
            {
                return false;
            }

            if (!await WaitForShoutsAndReturnToTown(client))
            {
                return false;
            }
        }
        else
        {
            var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
            var pathTownPortalArea = await _pathingService.GetPathToObjectWithOffset(client.Game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(client.Game.Act), client.Game.Me.Location, EntityCode.Stash, 24, 29, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathTownPortalArea, movementMode))
            {
                Log.Warning($"Client {client.Game.Me.Name} {movementMode} to town portal area failed at {client.Game.Me.Location}");
                return false;
            }

            var portalPlayer = client.Game.Players.FirstOrDefault(p => _cowconfig.PortalCharacterName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (portalPlayer == null)
            {
                Log.Warning($"Client {client.Game.Me.Name} did not find portal character {_cowconfig.PortalCharacterName} in game");
                return false;
            }

            if (!await GetBoAtCata2(client, portalPlayer))
            {
                return false;
            }
        }

        return true;
    }

    protected override Task PostInitializeAllJoined(List<Client> clients)
    {
        var boClient = clients.Aggregate((agg, client) =>
        {
            var boClient = client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0);
            var boAgg = agg?.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) ?? 0;
            if (boClient > 0 && boClient > boAgg)
            {
                return client;
            }

            return agg;
        });

        if (boClient == null)
        {
            Log.Error($"Expected at least bo barb in game");
            return Task.CompletedTask;
        }

        BoClientPlayerId = boClient.Game.Me.Id;
        var killingClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && c.Game.Me.Skills.GetValueOrDefault(Skill.Nova) >= 20).ToList();
        Log.Information($"Selected {string.Join(",", killingClients.Select(c => c.Game.Me.Name))} for cow manager");

        // The lead has to be able to walk the party into fights, so a sorceress leading means there
        // is no bo barb in the game and the aggregate above just fell back to the first client.
        var canHunt = _cowconfig.ActiveMode
            && boClient.Game.Me.Class != CharacterClass.Sorceress
            && clients.Exists(c => c.Game.Me.Class != CharacterClass.Sorceress && c.Game.Me.Id != boClient.Game.Me.Id);
        if (_cowconfig.ActiveMode && !canHunt)
        {
            Log.Warning($"Active mode is configured but the lead is {boClient.Game.Me.Name} ({boClient.Game.Me.Class}) with no other non-sorceress in the game, running without it");
        }

        List<NPCCode> huntedMonsters = [];
        List<Client> listeningClients;
        if (canHunt)
        {
            huntedMonsters = _cowconfig.HuntedMonsters;
            // Everything that is not already killing listens, so packs the hunting party walks into
            // are seen even where no sorceress ever goes.
            listeningClients = clients.Where(c => !killingClients.Contains(c)).ToList();
            _huntingParty = clients.Where(c => c.Game.Me.Class != CharacterClass.Sorceress).ToList();
            Log.Information($"Active mode on, hunting party {string.Join(",", _huntingParty.Select(c => c.Game.Me.Name))} looking for {string.Join(",", huntedMonsters)}");
        }
        else
        {
            listeningClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && !killingClients.Contains(c)).ToList();
            listeningClients.Add(boClient);
            _huntingParty = [];
        }

        _killingClients = killingClients;
        _partyRetreating = false;
        _huntTrail.Clear();
        _cowManager = new CowManager(killingClients, listeningClients, _mapApiService, huntedMonsters);
        return Task.CompletedTask;
    }

    protected override async Task<bool> PerformRun(Client client, AccountConfig account)
    {
        var isPortalCharacter = _cowconfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase);
        if (isPortalCharacter)
        {
            if (!await ArrangeStartingPosition(client, _cowManager))
            {
                NextGame.TrySetResult(true);
                return false;
            }

            Log.Information($"Client {client.Game.Me.Name} arranged start position");
        }
        else
        {
            var portalPlayer = client.Game.Players.Single(p => _cowconfig.PortalCharacterName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] <= 80)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(0.2));
                var portal = client.Game.GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(t => t.TownPortalArea == Area.CowLevel && t.TownPortalOwnerId == portalPlayer.Id);
                return portal != null;
            }, TimeSpan.FromSeconds(30)))
            {
                Log.Warning($"Client {client.Game.Me.Name} stopped waiting for cow level tp to open");
                NextGame.TrySetResult(true);
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(0.5));

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(0.2));
                return await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.CowLevel);
            }, TimeSpan.FromSeconds(10)))
            {
                Log.Warning($"Client {client.Game.Me.Name} stopped trying to get into the cow portal from {client.Game.Me.Location}");
                NextGame.TrySetResult(true);
                return false;
            }

            Log.Information($"Client {client.Game.Me.Name} ín cow level");
        }

        await GetTaskForClient(client, account, _cowManager);
        return true;
    }

    private async Task<bool> GetBoAtCata2(Client client, Player portalPlayer)
    {
        // Clients arrive here on their own schedule, so the portal character may still be cubing the
        // leg and its portal may not exist yet. Long enough to cover that, short enough that a
        // client which cannot get through gives the game up rather than holding five others for
        // three minutes and then failing anyway.
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            return await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.CatacombsLevel2);
        }, TimeSpan.FromSeconds(70)))
        {
            Log.Warning($"Client {client.Game.Me.Name} taking portal to cata2 failed");
            return false;
        }

        return await WaitForShoutsAndReturnToTown(client);
    }

    /// <summary>
    /// Waits at the catacombs for shouts, then goes back to town. Clients arrive here whenever
    /// their own town tasks finished, so the shout caster is the only one that waits for the whole
    /// party; everyone else leaves as soon as they personally have the buff.
    /// </summary>
    private async Task<bool> WaitForShoutsAndReturnToTown(Client client)
    {
        var isShoutCaster = client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) > 0;
        var random = new Random();
        await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (isShoutCaster)
            {
                if (retryCount % 5 == 0)
                {
                    foreach (var player in client.Game.Players.Where(p => p.Location?.Distance(client.Game.Me.Location) < 10))
                    {
                        client.Game.RequestUpdate(player.Id);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(0.1));
                await ClassHelpers.CastAllShouts(client);
                return !ClassHelpers.AnyPlayerIsMissingShouts(client);
            }

            var caster = client.Game.Players.FirstOrDefault(p => p.Class == CharacterClass.Barbarian && p.Area == client.Game.Area);
            if (caster?.Location != null)
            {
                var randomPointNear = caster.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                await client.Game.MoveToAsync(randomPointNear);
            }

            await Task.Delay(TimeSpan.FromSeconds(0.1));
            var me = client.Game.Players.FirstOrDefault(p => p.Id == client.Game.Me.Id);
            return me != null && !ClassHelpers.IsMissingShouts(me);
        }, TimeSpan.FromSeconds(90));

        var gotShouts = !ClassHelpers.IsMissingShouts(client.Game.Players.FirstOrDefault(p => p.Id == client.Game.Me.Id));
        if (!gotShouts)
        {
            Log.Warning($"Client {client.Game.Me.Name} gave up waiting for shouts at {Waypoint.CatacombsLevel2}");
        }

        // The portal the party came through is standing right here and leads home, so use it rather
        // than spending a scroll on a second one. Only the portal character is ever restocked with
        // tomes, so for everyone else creating one is what fails once their scrolls run out.
        if (!await TakeNearestPortalToTown(client)
            && !await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(0.5));
                return await _townManagementService.TakeTownPortalToTown(client);
            }, TimeSpan.FromSeconds(20)))
        {
            Log.Warning($"Client {client.Game.Me.Name} taking portal to town failed");
            return false;
        }

        Log.Information($"Client {client.Game.Me.Name} is back in town{(gotShouts ? " with shouts" : " without shouts")}");
        return true;
    }

    private async Task<bool> ArrangeStartingPosition(Client client, CowManager cowManager)
    {
        if (!await MoveToCowLevel(client))
        {
            Log.Information($"{client.Game.Me.Name}, couldn't move to the cow level, next game");
            NextGame.TrySetResult(true);
            return false;
        }

        var startLocations = await cowManager.GetPossibleStartingLocations(client.Game);
        foreach (var location in startLocations)
        {
            Log.Information($"Client {client.Game.Me.Name} starting location {location}");

            var teleportPath = await _pathingService.GetPathToLocation(client.Game, location, MovementMode.Teleport);
            if (teleportPath.Count > 0)
            {
                Log.Information($"Client {client.Game.Me.Name} teleporting to starting location {location}");
                await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.ToList(), MovementMode.Teleport);
                var nearbyAliveCows = cowManager.GetNearbyAliveMonsters(client, 35.0, 100);
                if (!nearbyAliveCows.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)))
                {
                    if (!await _townManagementService.CreateTownPortal(client))
                    {
                        continue;
                    }
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Steps into whichever town portal is already open here, whoever made it. Returns false when
    /// there is none to take, leaving the caller to fall back on making its own.
    /// </summary>
    private static async Task<bool> TakeNearestPortalToTown(Client client)
    {
        var townArea = WayPointHelpers.MapTownArea(client.Game.Act);
        var portal = client.Game.GetEntityByCode(EntityCode.TownPortal)
            .OrderBy(t => t.Location.Distance(client.Game.Me.Location))
            .FirstOrDefault();
        if (portal == null)
        {
            return false;
        }

        return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (!await client.Game.MoveToAsync(portal))
            {
                return false;
            }

            client.Game.InteractWithEntity(portal);
            return await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(50);
                return client.Game.Area == townArea;
            }, TimeSpan.FromSeconds(0.5));
        }, TimeSpan.FromSeconds(5));
    }

    private async Task<bool> OpenPortalAtCata2(Client client)
    {
        Log.Information($"Client {client.Game.Me.Name} taking waypoint to {Waypoint.CatacombsLevel2}");
        if (!await _townManagementService.TakeWaypoint(client, Waypoint.CatacombsLevel2))
        {
            return false;
        }

        Log.Information($"Client {client.Game.Me.Name} creating town portal at {Waypoint.CatacombsLevel2}");
        if (!await _townManagementService.CreateTownPortal(client))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> CreateCowLevel(Client client)
    {
        var game = client.Game;
        var movementMode = game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;

        // Before the trip, not after. Tristram is a one-way walk without a tome to portal back
        // from, and the rest of the party then waits out its whole budget for a cow portal that
        // cannot arrive - one missing tome costs the entire game, not just this character.
        var tomesOfTp = game.Inventory.Items.Where(i => i.Name == ItemName.TomeOfTownPortal);
        if (tomesOfTp.Count() < 2)
        {
            // Which character, how many tomes, and whether the inventory looks loaded at all. This
            // has fired three times today and each time the reading was ambiguous: an inventory
            // that has not arrived yet is indistinguishable from one that genuinely has no tome.
            Log.Error("Client {ClientName} has {Tomes} town portal tomes, needs 2, {Items} items in inventory, {FreeCells} free cells, in {Area} at {Location}",
                game.Me.Name, tomesOfTp.Count(), game.Inventory.Items.Count,
                game.Inventory.FreeCellCount(), game.Area, game.Me.Location);
            return false;
        }

        if (!game.Inventory.Items.Any(i => i.Name == ItemName.WirtsLeg))
        {
            if (!await GetWirtsLeg(client))
            {
                return false;
            }
        }

        var wirtsleg = game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg);
        if (wirtsleg == null)
        {
            Log.Error($"Wirts leg not found");
            return false;
        }

        var lowestQuantity = tomesOfTp.OrderBy(i => i.Amount).First();
        var droppedItems = new List<Item>();
        if (game.Cube.Items.Count != 0)
        {
            if (!InventoryHelpers.MoveCubeItemsToInventory(game))
            {
                Log.Warning($"Couldn't move all items out of cube, dropping cube items for now");
                foreach (var item in game.Cube.Items)
                {
                    if (InventoryHelpers.DropItemFromCube(game, item) != MoveItemResult.Succes)
                    {
                        Log.Error($"Failed to drop item out of cube");
                        return false;
                    }
                    droppedItems.Add(item);
                }
            }
        }

        var freeSpaceTownPortal = game.Cube.FindFreeSpace(lowestQuantity);
        if (game.Cube.Items.Count != 0 || freeSpaceTownPortal == null)
        {
            Log.Error($"Something wrong with cube for transmute town portal");
            return false;
        }

        if (InventoryHelpers.PutInventoryItemInCube(game, lowestQuantity, freeSpaceTownPortal) != MoveItemResult.Succes)
        {
            Log.Error($"Moving tome of town portal to cube failed");
            return false;
        }

        var freeSpaceLeg = game.Cube.FindFreeSpace(wirtsleg);
        if (freeSpaceLeg == null)
        {
            Log.Error($"No space found for leg, which is weird");
            return false;
        }

        if (InventoryHelpers.PutInventoryItemInCube(game, wirtsleg, freeSpaceLeg) != MoveItemResult.Succes)
        {
            Log.Error($"Moving wirts leg to cube failed");
            return false;
        }

        if (!InventoryHelpers.TransmuteItemsInCube(game, false))
        {
            Log.Error($"Transmuting leg and tome failed");
            return false;
        }

        // The transmute reporting success is not the same as the portal being there. When it is
        // missing the whole party waits half a minute for a portal that never opens, so fail here
        // and take a fresh game instead.
        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(100);
            return client.Game.GetEntityByCode(EntityCode.RedTownPortal).Any(t => t.TownPortalArea == Area.CowLevel);
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Error($"Client {game.Me.Name} transmuted the leg but no cow portal appeared, abandoning the game");
            return false;
        }

        Log.Information($"Client {game.Me.Name} opened the cow level");

        foreach (var droppedItem in droppedItems)
        {
            if (!await GeneralHelpers.TryWithTimeout((async retryCount =>
            {
                await client.Game.MoveToAsync(droppedItem);
                client.Game.PickupItem(droppedItem);
                if (!GeneralHelpers.TryWithTimeout((retryCount =>
                {
                    if (client.Game.Inventory.FindItemById(droppedItem.Id) == null)
                    {
                        return false;
                    }

                    return true;
                }), TimeSpan.FromSeconds(0.5)))
                {
                    return false;
                }

                return true;
            }), TimeSpan.FromSeconds(3)))
            {
                Log.Warning($"Picking up item {droppedItem.GetFullDescription()} at location {droppedItem.Location} from location {client.Game.Me.Location} failed");
            }
        }

        return true;
    }

    private async Task<bool> GetWirtsLeg(Client client)
    {
        var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTownWayPoint, MovementMode.Teleport))
        {
            Log.Information($"Teleporting to {client.Game.Act} waypoint failed");
            return false;
        }

        var townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).Single();
        Log.Information("Taking waypoint to StonyFields");
        if (!GeneralHelpers.TryWithTimeout((_) =>
        {
            client.Game.TakeWaypoint(townWaypoint, Waypoint.StonyFields);
            return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == Waypoint.StonyFields.ToArea(), TimeSpan.FromSeconds(2));
        }, TimeSpan.FromSeconds(5)))
        {
            Log.Error($"Taking waypoint to to {Waypoint.StonyFields} waypoint failed");
            return false;
        }
        var pathToPortal = await _pathingService.GetPathToObject(client.Game, EntityCode.TristamPortal, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPortal, MovementMode.Teleport))
        {
            Log.Error($"Teleporting to {EntityCode.TristamPortal}  failed");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (client.Game.Area == Area.Tristram)
            {
                return true;
            }

            var tristamPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).FirstOrDefault(t => t.TownPortalArea == Area.Tristram);
            if (tristamPortal == null)
            {
                return false;
            }
            await client.Game.MoveToAsync(tristamPortal);

            client.Game.InteractWithEntity(tristamPortal);
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                return client.Game.Area == Area.Tristram;
            }, TimeSpan.FromSeconds(0.2)))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                return false;
            }

            return true;

        }, TimeSpan.FromSeconds(15)))
        {
            Log.Error($"Moving to Tristam failed");
            return false;
        }

        client.Game.RequestUpdate(client.Game.Me.Id);

        Log.Information("Arrived in Tristam, teleporting to leg");

        var pathToLeg = await _pathingService.GetPathToObject(client.Game, EntityCode.WirtsBody, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToLeg, MovementMode.Teleport))
        {
            Log.Error($"Teleporting to leg failed");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (retryCount % 4 == 0)
            {
                if (client.Game.Me.HasSkill(Skill.Nova))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                }
                else if (client.Game.Me.HasSkill(Skill.FrozenOrb))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, client.Game.Me.Location);
                }
            }
            var wirtsBody = client.Game.GetEntityByCode(EntityCode.WirtsBody).FirstOrDefault();
            if (wirtsBody == null)
            {
                return false;
            }

            var wirtsLegItem = client.Game.Items.Values.FirstOrDefault(i => i.Name == ItemName.WirtsLeg && i.Ground);
            if (client.Game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg) != null)
            {
                return true;
            }

            if (wirtsLegItem == null)
            {
                client.Game.MoveTo(wirtsBody);
                await Task.Delay(100);
                client.Game.InteractWithEntity(wirtsBody);
                return false;
            }


            if (client.Game.Inventory.FindFreeSpace(wirtsLegItem) == null)
            {
                InventoryHelpers.MoveInventoryItemsToCube(client.Game);
            }

            client.Game.PickupItem(wirtsLegItem);


            return client.Game.Inventory.Items.FirstOrDefault(i => i.Name == ItemName.WirtsLeg) != null;
        }, TimeSpan.FromSeconds(15)))
        {
            Log.Error($"Getting leg failed, while it's at location: {client.Game.Items.Values.FirstOrDefault(i => i.Name == ItemName.WirtsLeg)?.Location} and i'm at location {client.Game.Me.Location}");
            return false;
        }

        if (!await _townManagementService.TakeTownPortalToTown(client))
        {
            return false;
        }

        Log.Information("Got leg and in town again");
        return true;
    }

    private async Task FollowToLocation(Client client, Point location)
    {
        if (!client.Game.IsInGame())
        {
            return;
        }

        var (targetLocation, tokenSource) = FollowTasks[client.Game.Me.Name.ToLower()];
        if (targetLocation == null || (targetLocation.Distance(location) > 10 && client.Game.Me.Location.Distance(location) < 1000))
        {
            var newSource = new CancellationTokenSource();
            if(FollowTasks.TryUpdate(client.Game.Me.Name.ToLower(), (location, newSource), (targetLocation, tokenSource)))
            {
                tokenSource?.Cancel();
                await MoveToLocation(client, location, newSource.Token);
            }
        }
    }

    /// <summary>The manager for the game in progress, when this client is one of its listeners.</summary>
    private CowManager CurrentManagerFor(Client client)
    {
        var manager = _cowManager;
        return manager != null && manager.Listens(client) ? manager : null;
    }

    private bool ShouldFollowLeadClient(Client client)
    {
        if (ShouldFollow.TryGetValue(client.Game.Me.Name.ToLower(), out var shouldFollow))
        {
            return shouldFollow;
        };

        return false;
    }

    private void SetShouldFollowLead(Client client, bool follow)
    {
        ShouldFollow[client.Game.Me.Name.ToLower()] = follow;
    }

    private async Task GetTaskForClient(Client client, AccountConfig account, CowManager cowManager)
    {
        if (client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] < 50 && !client.Game.Me.HasSkill(Skill.Teleport))
        {
            await BasicIdleClient(client, cowManager);
            return;
        }

        ElapsedEventHandler refreshHandler = (sender, args) =>
        {
            if (client.Game.IsInGame() && client.Game.Me != null)
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
            }
        };
        using var executeRefresh = new ExecuteAtInterval(refreshHandler, TimeSpan.FromSeconds(30));
        executeRefresh.Start();

        if (cowManager.ActiveMode && client.Game.Me.Class != CharacterClass.Sorceress)
        {
            if (client.Game.Me.Id == BoClientPlayerId)
            {
                await HuntLeadClient(client, account, cowManager);
            }
            else
            {
                await BasicFollowClient(client, account, cowManager);
            }

            return;
        }

        switch (client.Game.Me.Class)
        {
            case CharacterClass.Amazon:
                await BasicFollowClient(client, account, cowManager);

                break;
            case CharacterClass.Sorceress:
                if (client.Game.Me.HasSkill(Skill.StaticField) && client.Game.Me.Skills.GetValueOrDefault(Skill.Nova) >= 20)
                {
                    await StaticSorcClient(client, account, cowManager);
                }
                else
                {
                    await BasicFollowClient(client, account, cowManager);
                }
                break;
            case CharacterClass.Necromancer:
                await BasicFollowClient(client, account, cowManager);
                break;
            case CharacterClass.Paladin:
                await BasicFollowClient(client, account, cowManager);
                break;
            case CharacterClass.Barbarian:
                bool shouldBo = client.Game.Me.Id == BoClientPlayerId;
                await BarbClient(client, account, cowManager, shouldBo);
                break;
            case CharacterClass.Druid:
            case CharacterClass.Assassin:
                await BasicFollowClient(client, account, cowManager);
                break;
        }
    }

    private async Task StaticSorcClient(Client client, AccountConfig account, CowManager cowManager)
    {
        Log.Information($"Starting Sorc Client {client.Game.Me.Name}");
        await cowManager.EnsureSweep(client.Game, client.Game.Me.Location);
        ElapsedEventHandler staticFieldAction = (sender, args) =>
        {
            client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
        };
        using var executeStaticField = new ExecuteAtInterval(staticFieldAction, TimeSpan.FromSeconds(0.2));

        ElapsedEventHandler novaAction = (sender, args) =>
        {
            client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
        };

        using var executeNova = new ExecuteAtInterval(novaAction, TimeSpan.FromSeconds(0.2));

        var clusterStopWatch = new Stopwatch();
        var missingBoStopWatch = new Stopwatch();

        var random = new Random();

        // In active mode the lead deliberately walks into monsters, so rescuing it would pull the
        // sorceresses off their clusters for the rest of the run.
        var rescueLead = !cowManager.ActiveMode;
        var sweepProgress = 0;
        MonsterCluster currentCluster = null;
        Point currentTarget = null;
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
        {
            var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
            var cowsNearLead = rescueLead && leadPlayer != null ? cowManager.GetNearbyAliveCows(leadPlayer.Location, 20.0, 10) : [];
            if (rescueLead && leadPlayer != null && leadPlayer.Location != currentTarget && cowsNearLead.Count != 0 && leadPlayer.Location.Distance(client.Game.Me.Location) > 20)
            {
                if (cowsNearLead.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)))
                {
                    Log.Information($"{client.Game.Me.Name}, lightning enhanced cow nearby, next game");
                    NextGame.SetResult(true);
                }
                Log.Information($"{client.Game.Me.Name}, lead client in danger, moving to lead client");
                if (currentCluster != null)
                {
                    cowManager.GiveUpCluster(currentCluster);
                    currentCluster = null;
                }

                currentTarget = cowsNearLead.FirstOrDefault().Location;
                executeStaticField.Stop();
                executeNova.Stop();

                var teleportPath = await _pathingService.GetPathToLocation(client.Game, currentTarget, MovementMode.Teleport);
                if (teleportPath.Count > 0)
                {
                    Log.Information($"Client {client.Game.Me.Name} teleporting nearby cluster {currentTarget}");
                    await MovementHelpers.TakePathOfLocations(client.Game, teleportPath.ToList(), MovementMode.Teleport);
                }
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders))
            {
                Log.Information($"Lost bo on client {client.Game.Me.Name}, moving to barb for bo");
                executeStaticField.Stop();
                executeNova.Stop();
                if (leadPlayer != null)
                {
                    if (leadPlayer.Location.Distance(client.Game.Me.Location) > 10)
                    {
                        var teleportPathLead = await _pathingService.GetPathToLocation(client.Game, leadPlayer.Location, MovementMode.Teleport);
                        await MovementHelpers.TakePathOfLocations(client.Game, teleportPathLead.ToList(), MovementMode.Teleport);
                    }
                    else
                    {
                        var randomPointNear = leadPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                        await client.Game.TeleportToLocationAsync(randomPointNear);
                    }
                }

                // Timed from when the buff actually dropped. The cluster stopwatch used to stand in
                // for this, but once the cows are done it has been running for minutes, so the very
                // first tick without bo ended the run for the whole party.
                if (!missingBoStopWatch.IsRunning)
                {
                    missingBoStopWatch.Restart();
                }

                if (missingBoStopWatch.Elapsed > MissingBoTimeout)
                {
                    Log.Warning($"Lost bo on client {client.Game.Me.Name} and receiving new bo took longer than {MissingBoTimeout}");
                    break;
                }

                continue;
            }

            missingBoStopWatch.Reset();

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
            }

            var nearbyAliveCows = cowManager.GetNearbyAliveCows(client, 30.0, 10);
            if (((double)client.Game.Me.Life) / client.Game.Me.MaxLife <= 0.5 && nearbyAliveCows.Count != 0)
            {
                executeStaticField.Stop();
                executeNova.Stop();
                if (!await TeleportToNearbySafeSpot(client, cowManager, client.Game.Me.Location, 15.0))
                {
                    Log.Information($"Teleporting to nearby safespot {client.Game.Me.Name}");
                    continue;
                }
            }

            var lightningEnhancedCows = nearbyAliveCows.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted));
            if (clusterStopWatch.Elapsed > TimeSpan.FromSeconds(40) && currentCluster != null && leadPlayer?.Location != currentTarget)
            {
                Log.Information($"Taking too much time on cluster, skipping current cluster and moving to next cluster {client.Game.Me.Name}");
                clusterStopWatch.Restart();
            }
            else if (lightningEnhancedCows && leadPlayer?.Location != currentTarget)
            {
                Log.Information($"Lightning enhanced cow nearby, giving up current cluster and moving to next cluster {client.Game.Me.Name}");
            }
            else if (nearbyAliveCows.Count > 4)
            {
                var nearestAlive = nearbyAliveCows.FirstOrDefault();
                var distanceToNearest = nearestAlive.Location.Distance(client.Game.Me.Location);

                if (client.Game.Me.Location.Distance(nearestAlive.Location) > 5
                    && (!ClassHelpers.CanStaticEntity(client, nearestAlive.LifePercentage)
                    || client.Game.WorldObjects.TryGetValue((nearestAlive.Id, EntityType.NPC), out var cow) && cow.Effects.Contains(EntityEffect.Cold)))
                {
                    executeStaticField.Stop();
                    executeNova.Stop();
                    Log.Information($"teleporting nearby due to low life frozen cows with {client.Game.Me.Name} with distance {client.Game.Me.Location.Distance(nearestAlive.Location)}");
                    await client.Game.TeleportToLocationAsync(nearestAlive.Location);
                }

                if (await _attackService.IsInLineOfSight(client, nearestAlive.Location)
                    && (nearestAlive.LifePercentage < 30 || !ClassHelpers.CanStaticEntity(client, nearestAlive.LifePercentage))
                    && distanceToNearest < 10)
                {
                    executeStaticField.Stop();

                    if (client.Game.Me.HasSkill(Skill.Nova))
                    {
                        if (!executeNova.IsRunning())
                        {
                            Log.Information($"Attacking with Nova {client.Game.Me.Name}");
                        }
                        client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                        executeNova.Start();
                    }
                    else if (client.Game.Me.HasSkill(Skill.FrozenOrb))
                    {
                        client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, nearestAlive.Location);
                    }

                }
                else if (await _attackService.IsInLineOfSight(client, nearestAlive.Location)
                    && distanceToNearest < 20)
                {
                    if (client.Game.Me.HasSkill(Skill.StaticField) && ClassHelpers.CanStaticEntity(client, nearestAlive.LifePercentage))
                    {
                        if (!executeStaticField.IsRunning())
                        {
                            Log.Information($"Attacking with Static {client.Game.Me.Name}");
                        }
                        executeNova.Stop();
                        client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                        executeStaticField.Start();
                    }
                    else
                    {
                        if (client.Game.Me.HasSkill(Skill.Nova) && distanceToNearest < 10)
                        {
                            client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                            executeNova.Start();
                        }
                    }
                }
                else
                {
                    if (client.Game.Me.Location.Distance(nearestAlive.Location) > 15 || !await _attackService.IsInLineOfSight(client, client.Game.Me.Location, nearestAlive.Location))
                    {
                        Log.Information($"teleporting nearby with {client.Game.Me.Name} with distance {client.Game.Me.Location.Distance(nearestAlive.Location)}");
                        var destination = nearestAlive.Location.GetPointBeforePointInSameDirection(client.Game.Me.Location, 10);
                        if (!await _attackService.IsInLineOfSight(client, destination, nearestAlive.Location))
                        {
                            Log.Information($"Not in sight, so teleporting directly {client.Game.Me.Name}");
                            destination = nearestAlive.Location;
                        }
                        await client.Game.TeleportToLocationAsync(destination);
                    }

                    if (!executeNova.IsRunning() && !executeStaticField.IsRunning())
                    {
                        Log.Information($"Not attacking at all {client.Game.Me.Name}");
                    }
                }

                continue;
            }

            executeStaticField.Stop();
            executeNova.Stop();
            await PickupItemsAndPotions(client, account, TeleportPickupRadius);

            SetShouldFollowLead(client, false);

            sweepProgress = Math.Max(sweepProgress, currentCluster?.SweepIndex ?? 0);
            cowManager.ReleaseCluster(currentCluster);
            // Anchored on the walking party. The killers clear one area around them together, so
            // the soul packs that open up are ones the party is already standing near instead of
            // whichever distant pack happened to be uncovered first.
            currentCluster = cowManager.ClaimNextCowCluster(client, sweepProgress, HuntingPartyAnchor(client));
            if (currentCluster == null)
            {
                // No cluster left to claim, but that is not the same as nothing left to do. Whole
                // clusters get cleared while strays wander off and are never worth a cluster of
                // their own, and the hunted monsters are lightning immune, so these leftovers are
                // the only thing nova and static are any use against by this point.
                //
                // Anchored on the walking party, nearest to them first, rather than on the sorc:
                // the point is to clear what is slowing the group down, not to chase strays into a
                // corner of the level where killing them helps nobody.
                var partyAt = leadPlayer?.Location ?? client.Game.Me.Location;
                var strays = cowManager.GetNearbyAliveCows(partyAt, StraySearchRadius, 10);
                if (strays.Count != 0)
                {
                    var stray = strays.First();
                    var strayDistance = client.Game.Me.Location.Distance(stray.Location);
                    var staticRange = Math.Max(StaticRangeOf(client), NovaRange);
                    if (strayDistance > staticRange)
                    {
                        executeStaticField.Stop();
                        executeNova.Stop();
                        var strayPath = await _pathingService.GetPathToLocation(client.Game, stray.Location, MovementMode.Teleport);
                        if (strayPath.Count > 0)
                        {
                            await MovementHelpers.TakePathOfLocations(client.Game, strayPath, MovementMode.Teleport);
                        }
                    }
                    else
                    {
                        // Static reaches much further than nova and halves what it touches, so it is
                        // worth casting from the edge of the group rather than only in the middle of
                        // it: everything it softens is one volley of multiple shot from dead, which
                        // is faster than the sorceress finishing anything herself.
                        executeStaticField.Start();
                        if (strayDistance <= NovaRange)
                        {
                            executeNova.Start();
                        }
                        else
                        {
                            executeNova.Stop();
                        }
                    }

                    continue;
                }

                executeStaticField.Stop();
                executeNova.Stop();

                // Nothing left alive worth a spell. Collecting beats standing still, and a
                // teleporting character is the fastest thing in the party for crossing to a drop
                // the walkers would take the rest of the game to reach on foot.
                var loot = NearestPickitLocation(client, client.Game.Me.Location);
                if (loot != null && client.Game.Me.Location.Distance(loot) > TeleportPickupRadius)
                {
                    var lootPath = await _pathingService.GetPathToLocation(client.Game, loot, MovementMode.Teleport);
                    if (lootPath.Count > 0)
                    {
                        await MovementHelpers.TakePathOfLocations(client.Game, lootPath, MovementMode.Teleport);
                    }
                }

                await PickupItemsAndPotions(client, account, TeleportPickupRadius);

                // Nothing to kill and nothing to collect where it stands. Standing still until the
                // party finishes wastes the fastest mover in the group: trailing them puts it where
                // the next drops and the next strays will be, and keeps it close enough to collect
                // the moment something falls.
                if (partyAt != client.Game.Me.Location
                    && client.Game.Me.Location.Distance(partyAt) > KillerTrailDistance)
                {
                    var followPath = await _pathingService.GetPathToLocation(client.Game, partyAt, MovementMode.Teleport);
                    if (followPath.Count > 0)
                    {
                        await MovementHelpers.TakePathOfLocations(client.Game, followPath, MovementMode.Teleport);
                    }
                }

                continue;
            }

            currentTarget = currentCluster.Location;
            clusterStopWatch.Restart();
            Log.Information($"Client {client.Game.Me.Name} obtained next {currentCluster}");

            executeStaticField.Stop();
            executeNova.Stop();

            var clusterPath = await _pathingService.GetPathToLocation(client.Game, currentTarget, MovementMode.Teleport);
            if (clusterPath.Count > 0)
            {
                Log.Information($"Client {client.Game.Me.Name} teleporting nearby {currentCluster}");
                if (clusterPath.Count > 1)
                {
                    await MovementHelpers.TakePathOfLocations(client.Game, clusterPath.SkipLast(1).ToList(), MovementMode.Teleport);
                    await TeleportToNearbySafeSpot(client, cowManager, clusterPath.Last(), 15.0);
                }
            }
        }

        Log.Information($"Stopped Sorc Client {client.Game.Me.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
        executeStaticField.Stop();
        executeNova.Stop();
        NextGame.TrySetResult(true);
    }

    private async Task<bool> TeleportToNearbySafeSpot(Client client, CowManager cowManager, Point toLocation, double minDistance = 0, double maxDistance = 30)
    {
        var nearbyMonsters = cowManager.GetNearbyAliveMonsters(toLocation, 30.0, 100).Select(p => p.Location).ToList();
        return await _attackService.MoveToNearbySafeSpot(client, nearbyMonsters, toLocation, MovementMode.Teleport, minDistance, maxDistance);
    }

    private async Task BasicIdleClient(Client client, CowManager cowManager)
    {
        Log.Information($"Starting Basic idle Client {client.Game.Me.Name}");
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
        {
        }

        Log.Information($"Stopped Idle Client {client.Game.Me.Name}");
    }

    private async Task BasicFollowClient(Client client, AccountConfig account, CowManager cowManager)
    {
        SetShouldFollowLead(client, true);

        var timer = new Stopwatch();
        timer.Start();
        var random = new Random();
        MonsterCluster roamCluster = null;
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
        {
            var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);

            // A cold sorceress is the only one here who can hurt a burning soul once the cows are
            // gone - the nova pair cannot touch them and the walkers arrive on foot. Rather than
            // trailing the party at walking pace she takes a cluster of her own and teleports to
            // it, so two ends of the level are being cleared at once.
            if (!_partyRetreating && CanRoamForSouls(client, cowManager))
            {
                roamCluster ??= cowManager.ClaimNextHuntedCluster(client, 0, double.MaxValue);
                if (roamCluster != null)
                {
                    if (cowManager.IsClusterCleared(roamCluster))
                    {
                        cowManager.ReleaseCluster(roamCluster);
                        roamCluster = null;
                        continue;
                    }

                    var target = cowManager.GetNearestClusterMember(roamCluster, client.Game.Me.Location) ?? roamCluster.Location;
                    if (client.Game.Me.Location.Distance(target) > NovaRange)
                    {
                        var roamPath = await _pathingService.GetPathToLocation(client.Game, target, MovementMode.Teleport);
                        if (roamPath.Count > 0)
                        {
                            await MovementHelpers.TakePathOfLocations(client.Game, roamPath, MovementMode.Teleport);
                        }
                    }

                    await _attackService.AssistPlayer(client, SelfPlayer(client), PriorityCodes(client, cowManager));
                    await PickupItemsAndPotions(client, account, TeleportPickupRadius);
                    continue;
                }
            }
            else if (roamCluster != null)
            {
                cowManager.ReleaseCluster(roamCluster);
                roamCluster = null;
            }

            if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 10)
            {
                await FollowToLocation(client, leadPlayer.Location);
            }

            if (timer.Elapsed > TimeSpan.FromSeconds(5) && client.Game.Me.HasSkill(Skill.Teleport))
            {
                if (leadPlayer != null)
                {
                    var randomPointNear = leadPlayer.Location.Add((short)random.Next(-5, 5), (short)random.Next(-5, 5));
                    await client.Game.TeleportToLocationAsync(randomPointNear);
                    timer.Restart();
                }
            }

            if (_partyRetreating)
            {
                // Break off the advance, but hit back at whatever is on us: standing there taking
                // damage is what stopped the party ever reaching the life it needs to re-engage.
                if (cowManager.GetNearbyAliveMonsters(client, SelfDefenceRange, 1).Count != 0)
                {
                    await _attackService.AssistPlayer(client, SelfPlayer(client), PriorityCodes(client, cowManager));
                }

                continue;
            }

            var canCatchUp = client.Game.Me.HasSkill(Skill.Teleport);
            var nearLead = canCatchUp
                || leadPlayer?.Location == null
                || leadPlayer.Location.Distance(client.Game.Me.Location) < StragglerDistance;
            if (nearLead && cowManager.GetNearbyAliveMonsters(client, 20, 1).Count == 0)
            {
                // A walker only fetches what is lying around the group; anything further out is
                // left to whoever ends up nearest it.
                await PickupItemsAndPotions(client, account,
                    canCatchUp ? TeleportPickupRadius : HuntingPartyPickupRadius,
                    canCatchUp ? null : leadPlayer?.Location);
                SetShouldFollowLead(client, true);
            }

            if (!nearLead)
            {
                // A walker that has fallen behind catches up before it loots or swings at anything.
                continue;
            }

            var assistAnchor = cowManager.ActiveMode && client.Game.Me.Class == CharacterClass.Barbarian
                ? GetProtectAnchor(client, cowManager)
                : leadPlayer;
            await _attackService.AssistPlayer(client, assistAnchor, PriorityCodes(client, cowManager));
        }
    }

    private async Task BarbClient(Client client, AccountConfig account, CowManager cowManager, bool shouldBo)
    {
        Log.Information($"Starting BoBarb Client {client.Game.Me.Name}");
        if (shouldBo)
        {
            await ClassHelpers.CastAllShouts(client);
        }

        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
        {
            if (shouldBo)
            {
                await ClassHelpers.CastAllShouts(client);
            }

            var leadPlayer = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
            if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 25)
            {
                continue;
            }

            var nearbyMonsters = cowManager.GetNearbyAliveMonsters(client, 20, 1);
            if (nearbyMonsters.Count == 0)
            {
                await PickupItemsAndPotions(client, account, 15);
            }
            else if (client.Game.Me.HasSkill(Skill.Whirlwind))
            {
                var nearbyMonster = nearbyMonsters.FirstOrDefault();
                if (nearbyMonster != null && (nearbyMonster.NPCCode == NPCCode.DrehyaTemple || nearbyMonster.Location.Distance(client.Game.Me.Location) < 5))
                {
                    var wwDirection = client.Game.Me.Location.GetPointPastPointInSameDirection(nearbyMonster.Location, 6);
                    if (client.Game.Me.Location.Equals(nearbyMonster.Location))
                    {
                        wwDirection = new Point((ushort)(client.Game.Me.Location.X + 6), client.Game.Me.Location.Y);
                    }

                    var wwDistance = client.Game.Me.Location.Distance(wwDirection);
                    //Log.Information($"player loc: {game.Me.Location}, nearest: {nearest.Location} ww destination: {wwDirection}  ");
                    client.Game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                    Thread.Sleep((int)((wwDistance * 50 + 300)));
                }
            }
        }

        if (shouldBo)
        {
            NextGame.TrySetResult(true);
        }

        Log.Information($"Stopped Barb Client {client.Game.Me?.Name}, cowing manager is finished is: {cowManager.IsFinished()}");
    }

    /// <summary>
    /// Leads the hunting party in active mode: claims one hunted cluster at a time for the whole
    /// group, which follows it, and pulls everyone back along its own trail when someone gets hurt.
    /// </summary>
    private async Task HuntLeadClient(Client client, AccountConfig account, CowManager cowManager)
    {
        Log.Information($"Starting hunt lead client {client.Game.Me.Name}");
        await cowManager.EnsureSweep(client.Game, client.Game.Me.Location);
        var shouldBo = client.Game.Me.Id == BoClientPlayerId;
        if (shouldBo)
        {
            await ClassHelpers.CastAllShouts(client);
        }

        var holdLocation = client.Game.Me.Location;
        var priorityCodes = cowManager.HuntedMonsters;
        MonsterCluster currentCluster = null;
        var clusterStopWatch = new Stopwatch();
        var clusterAliveMembers = int.MaxValue;
        var breadcrumbStopWatch = Stopwatch.StartNew();
        var stragglerStopWatch = new Stopwatch();
        var retreatStopWatch = new Stopwatch();
        var deathStopWatch = new Stopwatch();
        var lastAliveAt = new Dictionary<string, Point>();
        Point deathLocation = null;
        var sweepProgress = 0;

        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !cowManager.IsFinished())
        {
            if (shouldBo)
            {
                await ClassHelpers.CastAllShouts(client);
            }

            if (_partyRetreating)
            {
                if (retreatStopWatch.Elapsed > RetreatTimeout)
                {
                    Log.Warning($"Hunting party still hurt after {RetreatTimeout}, carrying on anyway");
                    _partyRetreating = false;
                }
                else if (!PartyHasRecovered())
                {
                    // Standing still and taking hits is why the party could never heal back up.
                    if (cowManager.GetNearbyAliveMonsters(client, SelfDefenceRange, 1).Count != 0)
                    {
                        await _attackService.AssistPlayer(client, SelfPlayer(client), priorityCodes);
                    }

                    continue;
                }
                else
                {
                    Log.Information($"Hunting party recovered, re-engaging with {cowManager.DescribeProgress()}");
                    _partyRetreating = false;
                }
            }
            else if (AnyHunterHurt())
            {
                var retreatPoint = _huntTrail.FindRetreatPoint(client.Game.Me.Location, RetreatDistance) ?? holdLocation;
                Log.Information($"Hunting party hurt, retreating to {retreatPoint} until everyone is back to {ResumeLifeFraction:P0} life");
                _partyRetreating = true;
                retreatStopWatch.Restart();
                if (currentCluster != null)
                {
                    cowManager.ReleaseCluster(currentCluster);
                    currentCluster = null;
                }

                await MoveToLocation(client, retreatPoint);
                continue;
            }

            if (IsDead(client))
            {
                Log.Warning($"Hunt lead {client.Game.Me.Name} died, nothing left to lead the party with, taking a new game");
                break;
            }

            // Where each hunter last stood alive, which is where its body and gear will be.
            foreach (var hunter in _huntingParty)
            {
                if (hunter.Game.Me != null && !IsDead(hunter))
                {
                    lastAliveAt[hunter.Game.Me.Name] = hunter.Game.Me.Location;
                }
            }

            var dead = DeadHunters();
            if (dead.Count != 0)
            {
                if (!deathStopWatch.IsRunning)
                {
                    deathStopWatch.Restart();
                    deathLocation = lastAliveAt.GetValueOrDefault(dead[0].Game.Me.Name);
                    Log.Warning($"{string.Join(",", dead.Select(d => d.Game.Me.Name))} died at {deathLocation}, clearing the ground before giving up on the game");
                }

                var groundIsClear = deathLocation == null
                    || cowManager.GetNearbyAliveMonsters(deathLocation, ClusterRegistry.ClusterRadius, 1).Count == 0;
                if (groundIsClear)
                {
                    Log.Information($"Ground at {deathLocation} is clear again");
                    deathStopWatch.Reset();
                }
                else if (deathStopWatch.Elapsed > DeathRecoveryTimeout)
                {
                    Log.Warning($"Still could not clear {deathLocation} after {DeathRecoveryTimeout}, taking a new game");
                    break;
                }
            }
            else
            {
                deathStopWatch.Reset();
            }

            cowManager.PruneMonstersOutOfSight(client, OutOfSightPruneDistance);

            // Anything on an escort counts as being on the party. Leaving while one of them is
            // fighting is what puts it behind: it finishes, runs to catch up, and the group pays
            // for the same ground twice. The two walkers are behind almost every straggler wait.
            var partyEngaged = cowManager.GetNearbyAliveMonsters(client, 20, 1).Count != 0
                || EscortUnderAttack(client, cowManager);
            if (!partyEngaged)
            {
                if (breadcrumbStopWatch.Elapsed > BreadcrumbInterval)
                {
                    breadcrumbStopWatch.Restart();
                    _huntTrail.Record(client.Game.Me.Location);
                }
            }
            else
            {
                // Anything on the party gets killed first, cluster or not, so it does not follow
                // the group to the next one.
                await _attackService.AssistPlayer(client, GetProtectAnchor(client, cowManager), priorityCodes);
                continue;
            }

            await PickupItemsAndPotions(client, account, HuntingPartyPickupRadius);

            // Once waiting, hold until they are properly back with the group rather than the moment
            // they cross the line, otherwise a straggler on the boundary flaps the party in place.
            var farBehind = FarBehindHunters(client, stragglerStopWatch.IsRunning ? StragglerDistance - 10 : StragglerDistance);
            if (farBehind.Count != 0)
            {
                if (!stragglerStopWatch.IsRunning)
                {
                    stragglerStopWatch.Restart();
                    Log.Information($"Hunting party waiting for {string.Join(",", farBehind)} to catch up");
                }

                if (stragglerStopWatch.Elapsed < StragglerWait)
                {
                    continue;
                }
            }
            else
            {
                stragglerStopWatch.Reset();
            }

            // The budget is for being stuck, not for being slow. A flat timer abandoned whichever
            // packs took longest to kill - which is the big ones, the ones most worth finishing -
            // and marked them done for good. Killing anything in the cluster buys the full budget
            // again, so the party only leaves when it has stopped making progress.
            if (currentCluster != null && currentCluster.AliveMembers < clusterAliveMembers)
            {
                clusterAliveMembers = currentCluster.AliveMembers;
                clusterStopWatch.Restart();
            }

            if (currentCluster != null && clusterStopWatch.Elapsed > CowManager.HuntedClusterTimeout)
            {
                Log.Information($"Hunting party made no progress on {currentCluster} for {CowManager.HuntedClusterTimeout.TotalSeconds:F0}s with {currentCluster.AliveMembers} left, moving on");
                cowManager.ReleaseCluster(currentCluster);
                currentCluster = null;
            }

            // Cleared means every monster that belonged to the cluster is dead, not that the spot
            // it was first seen at is empty: a pack that chases the party leaves its own cluster.
            if (currentCluster != null && cowManager.IsClusterCleared(currentCluster))
            {
                Log.Information($"Hunting party cleared {currentCluster}, {cowManager.DescribeProgress()}");
                sweepProgress = Math.Max(sweepProgress, currentCluster.SweepIndex);
                cowManager.ReleaseCluster(currentCluster);
                currentCluster = null;
            }

            if (currentCluster == null)
            {
                // While the sorceresses are still clearing, a cluster on the far side of the level
                // is a bad trade: something nearer opens where they are working, and a quarter of
                // the party's hops were long ones carrying half of all the distance it ran.
                currentCluster = cowManager.ClaimNextHuntedCluster(
                    client,
                    sweepProgress,
                    cowManager.CowsAllDone ? double.MaxValue : FarClusterDistance);

                // Preferring something nearby is worth it only while there is something nearby. With
                // the cows at forty nine of fifty one the party was still held inside the cap, had
                // cleared everything within it, and stood still with nine eligible clusters further
                // out - until the ninety second patience timer gave the game away.
                currentCluster ??= cowManager.ClaimNextHuntedCluster(client, sweepProgress, double.MaxValue);
                if (currentCluster == null)
                {
                    // With nothing to hunt, close on the sorceresses rather than standing still.
                    // Clusters open where they have just finished clearing, so waiting here means
                    // crossing the level once one does; two thirds of the time the party spends not
                    // killing anything is walking, and this is the half of it that can be avoided.
                    var trailTarget = NearestKillerLocation(client) ?? holdLocation;
                    if (client.Game.Me.Location.Distance(trailTarget) > KillerTrailDistance)
                    {
                        await MoveWithPartyGuard(client, trailTarget);
                    }

                    continue;
                }

                cowManager.NotifyHuntStarted();
                clusterStopWatch.Restart();
                clusterAliveMembers = currentCluster.AliveMembers;
                Log.Information($"Hunting party heading for {currentCluster}, {cowManager.DescribeProgress()}");
            }

            // Head for whatever is left of the pack rather than the spot it was first seen at, but
            // do not march across the level for one straggler: a fresh game is worth more than the
            // last soul of an old one.
            var survivor = cowManager.GetNearestClusterMember(currentCluster, client.Game.Me.Location);
            var worthChasing = Math.Clamp(
                SurvivorChasePerMonster * currentCluster.AliveMembers,
                MinSurvivorChase,
                MaxSurvivorChase);
            // Only once the party is actually there. Clusters are claimed out to FarClusterDistance
            // but a small pack is only worth chasing MinSurvivorChase, so measuring this on the way
            // made the party claim a cluster, set off, and abandon it on the next tick for being as
            // far away as it was when chosen - marking it done, so nothing ever killed it. The
            // question is whether a pack that moved after we arrived is worth following, not
            // whether the walk we already committed to is too long.
            var arrived = client.Game.Me.Location.Distance(currentCluster.Location) <= ClusterRegistry.ClusterRadius;
            if (arrived && survivor != null && survivor.Distance(client.Game.Me.Location) > worthChasing)
            {
                Log.Information($"{currentCluster} has {currentCluster.AliveMembers} left {survivor.Distance(client.Game.Me.Location):F0} away, not worth more than {worthChasing:F0}, leaving it");
                cowManager.ReleaseCluster(currentCluster);
                currentCluster = null;
                continue;
            }

            var target = survivor ?? currentCluster.Location;
            if (client.Game.Me.Location.Distance(target) > 10)
            {
                await MoveWithPartyGuard(client, target);
            }
        }

        Log.Information($"Stopped hunt lead client {client.Game.Me?.Name}, {cowManager.DescribeProgress()}, finished is {cowManager.IsFinished()}");
        _partyRetreating = false;
        NextGame.TrySetResult(true);
    }

    /// <summary>
    /// Walks to a location but gives up as soon as a party member is hurt, so a long path does not
    /// hold the group in a fight it should be leaving.
    /// </summary>
    private async Task MoveWithPartyGuard(Client client, Point location)
    {
        using var cancellation = new CancellationTokenSource();
        var move = MoveToLocation(client, location, cancellation.Token);
        while (!move.IsCompleted)
        {
            if (await Task.WhenAny(move, Task.Delay(TimeSpan.FromSeconds(0.2))) == move)
            {
                break;
            }

            // The retreating state, not the bare life check. AnyHunterHurt has no hysteresis while
            // the retreat recovers to ResumeLifeFraction, so a hunter hovering near the bar flapped
            // this on and off without the party ever committing to a retreat: the walkers had their
            // paths cancelled every few seconds, reported as success, and re-issued the same move.
            // Two retreats actually happened in ten games while this fired continuously.
            if (_partyRetreating || NextGame.Task.IsCompleted || !client.Game.IsInGame())
            {
                await cancellation.CancelAsync();
                break;
            }
        }

        await move;
    }

    /// <summary>
    /// The monsters this client should pick out of a fight first. Sorceresses get none: the hunted
    /// monsters are lightning immune, so pointing one at them wastes the whole fight.
    /// </summary>
    private static IReadOnlyCollection<NPCCode> PriorityCodes(Client client, CowManager cowManager)
    {
        return cowManager.ActiveMode && client.Game.Me.Class != CharacterClass.Sorceress
            ? cowManager.HuntedMonsters
            : null;
    }

    /// <summary>
    /// Party members that have fallen behind the lead, by name. They cannot teleport back, so the
    /// lead holds rather than walking the rest of the group further away from them.
    /// </summary>
    private List<string> FarBehindHunters(Client client, double distance)
    {
        var behind = new List<string>();
        foreach (var hunter in _huntingParty)
        {
            if (hunter == client || !hunter.Game.IsInGame() || hunter.Game.Me == null || hunter.Game.Area != client.Game.Area
                || IsDead(hunter))
            {
                continue;
            }

            var player = client.Game.Players.FirstOrDefault(p => p.Id == hunter.Game.Me.Id);
            if (player?.Location != null && player.Location.Distance(client.Game.Me.Location) > distance)
            {
                behind.Add(hunter.Game.Me.Name);
            }
        }

        return behind;
    }

    /// <summary>
    /// Where the nearest cow killer is standing, so the party can trail it before it has hunted
    /// clusters of its own. Killers that have left the level are ignored.
    /// </summary>
    private Point NearestKillerLocation(Client client)
    {
        Point nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var killer in _killingClients)
        {
            if (!killer.Game.IsInGame() || killer.Game.Me == null || killer.Game.Area != client.Game.Area)
            {
                continue;
            }

            var distance = killer.Game.Me.Location.Distance(client.Game.Me.Location);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = killer.Game.Me.Location;
            }
        }

        return nearest;
    }

    private static Player SelfPlayer(Client client)
    {
        return client.Game.Players.FirstOrDefault(p => p.Id == client.Game.Me.Id);
    }

    /// <summary>
    /// A character on nothing but zero life is dead. Nothing in the game data models death, so this
    /// is the only signal available, and every party check has to skip them: a corpse never heals,
    /// never catches up, and would otherwise hold the group in place until its own timeouts fire.
    /// </summary>
    private static bool IsDead(Client client)
    {
        return client.Game.IsInGame() && client.Game.Me != null && client.Game.Me.MaxLife > 0 && client.Game.Me.Life == 0;
    }

    private List<Client> DeadHunters()
    {
        return _huntingParty.FindAll(IsDead);
    }

    /// <summary>
    /// Whether a party member the lead is escorting has monsters on it. Only counts members close
    /// enough to reach, so a straggler that is already lost does not pin the group in place.
    /// </summary>
    private bool EscortUnderAttack(Client client, CowManager cowManager)
    {
        foreach (var hunter in _huntingParty)
        {
            if (hunter == client || !hunter.Game.IsInGame() || hunter.Game.Me == null || IsDead(hunter)
                || hunter.Game.Area != client.Game.Area)
            {
                continue;
            }

            var location = hunter.Game.Me.Location;
            if (location.Distance(client.Game.Me.Location) <= StragglerDistance
                && cowManager.GetNearbyAliveMonsters(location, EscortEngagementRange, 1).Count != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where the walking party is, for a killer deciding which cows to clear next. Null before the
    /// party exists, in which case the killer falls back to clearing around itself.
    /// </summary>
    private Point HuntingPartyAnchor(Client client)
    {
        var lead = client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId);
        return lead?.Location;
    }

    /// <summary>
    /// Whether this client should go and hunt soul clusters on its own. A teleporting sorceress with
    /// a cold attack and no nova qualifies: cold is the only damage that touches a burning soul, and
    /// she can cross the level in seconds while the walkers cannot. Only once the cows are finished,
    /// so she is not pulled away from the clearing the party still depends on.
    /// </summary>
    private static bool CanRoamForSouls(Client client, CowManager cowManager)
    {
        var me = client.Game.Me;
        return cowManager.ActiveMode
            && cowManager.CowsAllDone
            && me.Class == CharacterClass.Sorceress
            && me.HasSkill(Skill.Teleport)
            && me.Skills.GetValueOrDefault(Skill.Nova) < 20
            && (me.HasSkill(Skill.FrozenOrb) || me.HasSkill(Skill.Blizzard) || me.HasSkill(Skill.GlacialSpike));
    }

    private bool AnyHunterHurt()
    {
        foreach (var hunter in _huntingParty)
        {
            if (!hunter.Game.IsInGame() || hunter.Game.Me == null || hunter.Game.Me.MaxLife == 0 || IsDead(hunter))
            {
                continue;
            }

            if ((double)hunter.Game.Me.Life / hunter.Game.Me.MaxLife < RetreatLifeFraction)
            {
                return true;
            }
        }

        return false;
    }

    private bool PartyHasRecovered()
    {
        foreach (var hunter in _huntingParty)
        {
            if (!hunter.Game.IsInGame() || hunter.Game.Me == null || hunter.Game.Me.MaxLife == 0 || IsDead(hunter))
            {
                continue;
            }

            if ((double)hunter.Game.Me.Life / hunter.Game.Me.MaxLife < ResumeLifeFraction)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The party member this client should be swinging for: the walking character under attack in
    /// the worst shape, since those are the ones that cannot teleport out. Falls back to itself.
    /// </summary>
    private Player GetProtectAnchor(Client client, CowManager cowManager)
    {
        Player anchor = null;
        var worstFraction = double.MaxValue;
        foreach (var hunter in _huntingParty)
        {
            if (hunter == client || !hunter.Game.IsInGame() || hunter.Game.Me == null || hunter.Game.Me.MaxLife == 0
                || hunter.Game.Me.HasSkill(Skill.Teleport))
            {
                continue;
            }

            var player = client.Game.Players.FirstOrDefault(p => p.Id == hunter.Game.Me.Id);
            if (player?.Location == null || player.Location.Distance(client.Game.Me.Location) > 30)
            {
                continue;
            }

            if (cowManager.GetNearbyAliveMonsters(player.Location, 15, 1).Count == 0)
            {
                continue;
            }

            var fraction = (double)hunter.Game.Me.Life / hunter.Game.Me.MaxLife;
            if (fraction < worstFraction)
            {
                worstFraction = fraction;
                anchor = player;
            }
        }

        return anchor ?? client.Game.Players.FirstOrDefault(p => p.Id == client.Game.Me.Id);
    }

    private async Task<bool> MoveToCowLevel(Client client)
    {
        // The portal is created moments earlier and does not always appear in the entity list by the
        // time this runs. Waiting costs a moment; taking the first of an empty list threw and killed
        // the whole game for all six clients.
        WorldObject cowPortal = null;
        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(100);
            cowPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal)
                .FirstOrDefault(t => t.TownPortalArea == Area.CowLevel);
            return cowPortal != null;
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Client {client.Game.Me.Name} never saw the cow portal in town");
            return false;
        }

        var pathBack = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.RogueEncampment, client.Game.Me.Location, cowPortal.Location, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBack, MovementMode.Teleport))
        {
            Log.Warning($"Client {client.Game.Me.Name} {MovementMode.Teleport} to {EntityCode.RedTownPortal} failed at {client.Game.Me.Location}");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            await client.Game.MoveToAsync(cowPortal);

            if (retryCount > 0 && retryCount % 5 == 0)
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
            }

            client.Game.InteractWithEntity(cowPortal);
            return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                await Task.Delay(50);
                return client.Game.Area == Area.CowLevel;
            }, TimeSpan.FromSeconds(0.5));
        }, TimeSpan.FromSeconds(10)))
        {
            return false;
        }
        client.Game.RequestUpdate(client.Game.Me.Id);
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (retryCount > 0 && retryCount % 5 == 0)
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
            }

            await Task.Delay(100);

            return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.CowLevel, client.Game.Me.Location);
        }, TimeSpan.FromSeconds(10)))
        {
            return false;
        }

        foreach (var (x, y) in new List<(short, short)> { (-3, -3), (3, 3), (-5, 0), (5, 0), (0, 5), (0, -5) })
        {
            var newLocation = client.Game.Me.Location.Add(x, y);
            if (await _attackService.IsInLineOfSight(client, newLocation))
            {
                await client.Game.MoveToAsync(newLocation);
                break;
            }
        }

        return true;
    }
}
