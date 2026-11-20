using ConsoleBot.Attack;
using ConsoleBot.Chicken;
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
using D2NG.Core.MonsterData;
using D2NG.Core.Tracing;
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
    /// <summary>
    /// Whether this character has been confirmed standing in the cow level, by character name.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Game.Area"/>: it lags a transition, and the first burst of monster assigns
    /// after stepping through the red portal arrives inside exactly that window - so gating on it
    /// threw away the pack at the entrance, which is never assigned again while it stays in view.
    /// This is set from the arrival check that already asks the map whether the character's own
    /// position is in the level, which cannot be stale.
    /// </remarks>
    private readonly ConcurrentDictionary<string, bool> InCowLevel = new();
    private readonly ConcurrentDictionary<string, FollowWalk> FollowWalks = new();

    /// <summary>The follow walk a client's loop has under way, if any, so a lead that moves on can cut it short.</summary>
    private sealed class FollowWalk
    {
        public readonly Lock Gate = new();
        public Point Target;
        public CancellationTokenSource Cancel;

        /// <summary>Where the last follow walk was headed; the leash is measured from here.</summary>
        public Point LastTarget;
        public DateTime RetryAfter = DateTime.MinValue;
        public bool NoPathLogged;
        public DateTime? DisagreeingSince;
        public DateTime LastUpdateRequest = DateTime.MinValue;
        public readonly HoldTimer Hold = new();

        public void Reset()
        {
            LastTarget = null;
            RetryAfter = DateTime.MinValue;
            NoPathLogged = false;
            DisagreeingSince = null;
            Hold.Reset();
        }
    }
    private CowManager _cowManager;
    private List<Client> _party = [];
    private Client _leadClient;

    /// <summary>When each party member last found itself out of the game, this game.</summary>
    private readonly ConcurrentDictionary<Client, DateTime> _leftAt = new();
    private readonly ConcurrentDictionary<Client, int> _rejoins = new();

    /// <summary>Each party member's account, for a town visit started from inside its role.</summary>
    private readonly ConcurrentDictionary<Client, AccountConfig> _accountOf = new();

    /// <summary>When each party member last came back into the cow level from a rejoin, this game.</summary>
    private readonly ConcurrentDictionary<Client, DateTime> _returnedAt = new();

    /// <summary>Rejoined members still without battle orders, with where they landed and how long they have waited beside the barbarian.</summary>
    private readonly ConcurrentDictionary<Client, BattleOrdersWaiting> _awaitingBattleOrders = new();

    private sealed class BattleOrdersWaiting
    {
        public DateTime ArrivedAt = DateTime.Now;
        public TimeSpan WaitedInShoutReach;
        public DateTime LastLook = DateTime.Now;
    }

    /// <summary>Each member's portal retreats this game.</summary>
    private readonly ConcurrentDictionary<Client, PortalRetreats> _portalRetreats = new();

    private sealed class PortalRetreats
    {
        public int Count;
        public DateTime? LastReturnAt;
        public bool LastReturnByRedPortal;
    }

    /// <summary>Where the red cow portal's level side stands, from whoever saw it first this game.</summary>
    private volatile Point _redPortalLevelSide;

    /// <summary>When the battle orders barbarian last stepped up to each member missing his shouts.</summary>
    private readonly ConcurrentDictionary<uint, DateTime> _shoutStepAt = new();

    /// <summary>
    /// Party members out of the game or on their way back from a rejoin. Their game data is the old
    /// game's, or empty, until they are back in the level, so the party's checks leave them out.
    /// </summary>
    private readonly ConcurrentDictionary<Client, byte> _away = new();

    /// <summary>Party members whose run has started this game: one that never joined has not left.</summary>
    private readonly ConcurrentDictionary<Client, byte> _joined = new();
    private DateTime _runStartedAt = DateTime.MaxValue;

    /// <summary>
    /// Set from the moment the lead's loop ends out of the game until he is back in the cow level. Nobody
    /// anchors on him meanwhile: his position is gone, or is a spot in town.
    /// </summary>
    private volatile bool _leadAway;

    /// <summary>Where the red cow portal stands in the Rogue Encampment, from whoever saw it first this game.</summary>
    private volatile Point _cowPortalAt;
    private readonly BreadcrumbTrail _huntTrail = new();
    private List<Client> _huntingParty = [];
    private List<Client> _killingClients = [];
    private volatile bool _partyRetreating;

    /// <summary>
    /// The barbarian waits out a lightning enchanted pack in town, and the portal character owes him
    /// a new portal somewhere clear of it. Ending the game instead threw away whole games, mostly
    /// ones where the pack stood at the entrance seconds after arriving.
    /// </summary>
    private volatile bool _leadInTown;
    private volatile bool _safePortalWanted;

    /// <summary>How far a lightning enchanted monster may be from the barbarian before he leaves.</summary>
    private const double LightningEnchantedLeadRange = 20;

    /// <summary>How far the portal character keeps a new portal from any lightning enchanted monster.</summary>
    private const double SafePortalRange = 100;

    /// <summary>How long the barbarian waits in town for a portal back before the game is given up.</summary>
    private static readonly TimeSpan SafePortalTimeout = TimeSpan.FromSeconds(45);

    private const double HuntingPartyPickupRadius = 25;

    /// <summary>
    /// Clients that can teleport range much further for loot: they rejoin the group in one cast, so
    /// there is no cost to letting them sweep a wide area while the walkers stay tight.
    /// </summary>
    private const double TeleportPickupRadius = 100;

    /// <summary>
    /// How long the sorceresses may spend collecting what is still on the ground once nothing is
    /// left to kill.
    /// </summary>
    private static readonly TimeSpan LootSweepBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A sorceress only picks up where she finishes a cluster and nobody comes back afterwards, so
    /// three rounds left a small charm behind the five nearest drops it was lying among.
    /// </summary>
    protected override int MaxPickupRounds => 20;
    private readonly Stopwatch _lootSweep = new();
    private bool _runOver;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Character, uint ItemId), byte> _sweepReported = new();

    /// <summary>Leftovers no teleport path reaches from the level: in another area, or walled off.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _unreachable = new();
    private readonly Lock _lootSweepLock = new();
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
    private static readonly TimeSpan StragglerWait = TimeSpan.FromSeconds(8);

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

    /// <summary>Mana potions a sorceress carries in the inventory beyond what her belt holds.</summary>
    private const int SorceressManaSpares = 4;

    /// <summary>
    /// Mana potions a whirlwind barbarian carries in the inventory beyond what his belt holds. With four, two of the eleven
    /// barbarian chickens of 20 Nov 2026 still left at full life with no mana potion left, one under a Mana Burn Cow King.
    /// </summary>
    private const int WhirlwindManaSpares = 6;

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
        var level = SkillLevel(client, Skill.StaticField);
        if (level <= 0)
        {
            return 0;
        }

        var yards = 2.633 + (0.667 * level);
        return yards * (NovaRange / NovaYards);
    }

    /// <summary>Hard points and what items add, which is the level a skill is cast at.</summary>
    private static int SkillLevel(Client client, Skill skill)
        => client.Game.Me.Skills.GetValueOrDefault(skill) + client.Game.Me.ItemSkills.GetValueOrDefault(skill);

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

    /// <summary>
    /// The longest a step that goes on from the server's answer to a position request waits for it: the fixed sleep
    /// it replaces. The answer comes in 40ms in the median; a whirling barbarian gets none until the whirl ends.
    /// </summary>
    private static readonly TimeSpan UpdateAnswerTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>The one wait for the party's positions before shouting; a player already back in town never answers.</summary>
    private static readonly TimeSpan ShoutUpdateWait = TimeSpan.FromMilliseconds(200);
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
        FollowWalks.TryAdd(accountCharacter.Character.ToLower(), new FollowWalk());
        // Subscribed once for the life of the client and dispatched to whichever manager is running
        // the current game; Client has no way to unsubscribe, so per-game handlers would pile up.
        // Only what is in the level being cleared. Every monster counts now, so without this the
        // clients cluster whatever they see in town, in Tristram on the way to the leg, and at the
        // waypoint - and the party then sets off across the map to kill the townsfolk's neighbours.
        var characterKey = accountCharacter.Character.ToLower();
        client.OnReceivedPacketEvent(InComingPacket.AssignNPC2, p =>
        {
            if (InCowLevel.GetValueOrDefault(characterKey))
            {
                CurrentManagerFor(client)?.OnAssignNpc(p);
            }
        });
        client.OnReceivedPacketEvent(InComingPacket.AssignNPC1, p =>
        {
            if (InCowLevel.GetValueOrDefault(characterKey))
            {
                CurrentManagerFor(client)?.OnAssignNpc(p);
            }
        });
        client.OnReceivedPacketEvent(InComingPacket.NPCState, p => CurrentManagerFor(client)?.OnNpcState(p));
        client.OnReceivedPacketEvent(InComingPacket.NPCMove, p => { var m = new NPCMovePacket(p); CurrentManagerFor(client)?.OnNpcMove(m.Location, m.EntityId); });
        client.OnReceivedPacketEvent(InComingPacket.NPCStop, p => { var m = new NPCStopPacket(p); CurrentManagerFor(client)?.OnNpcStop(m.EntityId, m.Location, m.LifePercentage); });
        client.OnReceivedPacketEvent(InComingPacket.NPCMoveToTarget, p => { var m = new NPCMoveToTargetPacket(p); CurrentManagerFor(client)?.OnNpcMove(m.Location, m.EntityId); });
        client.OnReceivedPacketEvent(InComingPacket.NPCHit, p => { var m = new NpcHitPacket(p); CurrentManagerFor(client)?.OnNpcHit(m.EntityId, m.LifePercentage); });

        // Only the client's own loop walks. These walked from the receive side as well, racing the loop's
        // assist and pickup walks, and one that threw on a dropped connection was unobserved on the
        // thread pool and took the whole process down. All they do now is end a follow walk whose
        // target the lead has left.
        client.OnReceivedPacketEvent(InComingPacket.EntityMove, packet =>
        {
            var entityMovePacket = new EntityMovePacket(packet);
            if (entityMovePacket.UnitType == EntityType.Player && entityMovePacket.UnitId == BoClientPlayerId)
            {
                LeadMoved(characterKey, entityMovePacket.MoveToLocation);
            }
        });

        client.OnReceivedPacketEvent(InComingPacket.ReassignPlayer, packet =>
        {
            var reassignPlayerPacket = new ReassignPlayerPacket(packet);
            if (reassignPlayerPacket.UnitType == EntityType.Player && reassignPlayerPacket.UnitId == BoClientPlayerId)
            {
                LeadMoved(characterKey, reassignPlayerPacket.Location);
            }
        });
        client.OnReceivedPacketEvent(InComingPacket.PartyAutomapInfo, packet =>
        {
            var partyAutomapInfoPacket = new PartyAutomapInfoPacket(packet);
            if (partyAutomapInfoPacket.Id == BoClientPlayerId)
            {
                LeadMoved(characterKey, partyAutomapInfoPacket.Location);
            }
        });
    }

    protected override async Task<bool> PrepareForRun(Client client, AccountConfig account)
    {
        var townManagementOptions = TownOptionsFor(client, account);
        townManagementOptions.GambleLater = GamblesLate(client);
        var isPortalCharacter = _cowconfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase);

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
                    // Blocking on purpose: the 0.4s is also the pause before a failed town task is tried again.
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
            if (!await CreateCowLevel(client) || !await OpenPortalAtCata2(client))
            {
                Log.Warning("{Character} opens no portal to {Area} this game, ending it for the party", client.Game.Me.Name, Area.CatacombsLevel2);
                NextGame.TrySetResult(true);
                return false;
            }

            if (!await WaitForShoutsAndReturnToTown(client))
            {
                return false;
            }
        }
        else
        {
            var portalPlayer = client.Game.Players.FirstOrDefault(p => _cowconfig.PortalCharacterName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (portalPlayer == null)
            {
                Log.Warning($"Client {client.Game.Me.Name} did not find portal character {_cowconfig.PortalCharacterName} in game");
                return false;
            }

            // By waypoint once she has left town, so the shout casters stand at the catacombs before she arrives and shout her
            // on arriving instead of waiting for every member to come through her portal: 2.3 s of her 3.8 s stop over 89
            // games of 20 Nov 2026, and her stop is the party's start. Set off only when her cow portal stood, they came
            // after her - the walkers' way to the town waypoint is longer than to her portal - and her stop grew to 4.9 s.
            var byWaypoint = KnowsCatacombsWaypoint(client) ? await CatacombsWaypointTime(client, portalPlayer) : WaypointTime.TakeHerPortal;
            if (byWaypoint == WaypointTime.GameOver)
            {
                return false;
            }

            if (byWaypoint == WaypointTime.Now && await TakeCatacombsWaypoint(client))
            {
                if (!await WaitForShoutsAndReturnToTown(client))
                {
                    return false;
                }
            }
            else if (!await WalkToTownPortalArea(client) || !await GetBoAtCata2(client, portalPlayer))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> WalkToTownPortalArea(Client client)
    {
        var movementMode = MovementHelpers.PreferredMovement(client.Game);
        var pathTownPortalArea = await _pathingService.GetPathToObjectWithOffset(client.Game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(client.Game.Act), client.Game.Me.Location, EntityCode.Stash, 24, 29, movementMode);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathTownPortalArea, movementMode))
        {
            Log.Warning($"Client {client.Game.Me.Name} {movementMode} to town portal area failed at {client.Game.Me.Location}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Each character's battle orders level as last seen. The lead is picked by it once the whole party is in, after the
    /// town tasks that decide who gambles late, so those go by what this and earlier games showed.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _battleOrdersLevels = new(StringComparer.OrdinalIgnoreCase);

    private void RememberBattleOrders(Client client)
    {
        // No skills yet is not no battle orders: the lead would read as a member who may gamble late.
        if (client.Game.Me?.Name is { } name && !client.Game.Me.Skills.IsEmpty)
        {
            _battleOrdersLevels[name] = client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0);
        }
    }

    private static readonly IReadOnlyList<Skill> SecondShoutOrder = [Skill.Shout, Skill.BattleCommand, Skill.BattleOrders];

    /// <summary>
    /// Whether this barbarian gives the party its battle orders, by the levels this and earlier games showed; one whose
    /// rival is not known yet takes itself for the one.
    /// </summary>
    private bool GivesBattleOrders(Client client)
    {
        RememberBattleOrders(client);
        var accounts = _cowconfig.Accounts.ToList();
        var ownIndex = accounts.FindIndex(account => account.Character.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        var battleOrders = accounts.Select(account => _battleOrdersLevels.TryGetValue(account.Character, out var level) ? level : (int?)null).ToList();
        return ownIndex < 0 || battleOrders[ownIndex] is not int own || !BattleOrdersRank.AnotherGives(ownIndex, own, battleOrders);
    }

    /// <summary>See <see cref="LateGamble"/>.</summary>
    private bool GamblesLate(Client client)
    {
        RememberBattleOrders(client);
        var name = client.Game.Me.Name;
        var accounts = _cowconfig.Accounts.ToList();
        var ownIndex = accounts.FindIndex(account => account.Character.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ownIndex < 0)
        {
            return false;
        }

        var battleOrders = accounts.Select(account => _battleOrdersLevels.TryGetValue(account.Character, out var level) ? level : (int?)null).ToList();
        return LateGamble.Allowed(
            _cowconfig.PortalCharacterName.Equals(name, StringComparison.OrdinalIgnoreCase),
            createsGame: ownIndex == 0,
            // A max life not known yet is not a strong walker's.
            weakWalker: IsWeakWalker(client) || client.Game.Me.MaxLife == 0,
            ownIndex,
            battleOrders,
            killer: client.Game.Me.Class == CharacterClass.Sorceress && client.Game.Me.Skills.GetValueOrDefault(Skill.Nova) >= 20);
    }

    /// <summary>
    /// The gamble <see cref="LateGamble"/> left out of the town tasks, made while the party goes in, then into the level
    /// the way a rejoin goes back: by the red portal, else the portal character's portal as she is now. A member that
    /// cannot get in stays out of this run; waiting on a portal by the id she had before the gamble could end the game
    /// for everyone after she chickened and rejoined meanwhile.
    /// </summary>
    private async Task<bool> GambleAndComeInLate(Client client)
    {
        var name = client.Game.Me.Name;
        var before = StashGold(client.Game);
        var took = Stopwatch.StartNew();
        Log.Information("{Character} gambles while the party goes into the cow level", name);
        var reached = await _townManagementService.GambleIfDue(client, () => RejoinPointless(client, _cowManager));
        var gambled = took.Elapsed;
        var spent = before - StashGold(client.Game);
        if (!RejoinPointless(client, _cowManager))
        {
            await WalkToTownPortalArea(client);
        }

        var entered = !RejoinPointless(client, _cowManager) && await ReturnToCowLevel(client, _cowManager);
        Log.Information("{Character} gambled {Spent} gold in {Seconds:F1}s{Unreached} after the party started, {Outcome} {Total:F1}s after it",
            name, spent, gambled.TotalSeconds, reached ? "" : ", not reaching the gamble merchant",
            entered ? "in the cow level" : "staying out of the run", took.Elapsed.TotalSeconds);
        TraceEvents.Decision(client.Game, "late-gamble",
            ("goldSpent", spent),
            ("reachedMerchant", reached),
            ("gambleSeconds", Math.Round(gambled.TotalSeconds, 1)),
            ("entered", entered),
            ("seconds", Math.Round(took.Elapsed.TotalSeconds, 1)));

        // Out of the game, not idle in town: a killer standing in town still counted as one in the game, so the party
        // went on as though she were clearing.
        if (!entered && !RejoinPointless(client, _cowManager))
        {
            ChickenService.LeaveNow(client, $"{name} could not get into the cow level after gambling");
        }

        return entered;
    }

    private static long StashGold(Game game) => game.Me.Attributes.GetValueOrDefault(D2NG.Core.D2GS.Players.Attribute.GoldInStash);

    /// <summary>Potions and, for the portal character, town portal tomes: what a visit to town before the level buys.</summary>
    private TownManagementOptions TownOptionsFor(Client client, AccountConfig account)
    {
        var townManagementOptions = new TownManagementOptions(account, Act.Act1);

        if (_cowconfig.ActiveMode)
        {
            // Everyone fights in active mode, so everyone drinks. Buy past what the belt holds: the
            // surplus sits in the inventory and refills the belt as columns empty, which is the
            // difference between running dry mid level and finishing the game.
            var beltShortfall = account.HealthPotionTarget(client.Game.Belt.Height)
                - client.Game.Belt.GetHealthPotionsInSlots(account.HealthSlots).Count;
            // Minus what is already carried. Buying the buffer afresh every visit ratcheted the
            // inventory up to 62 potions of its 80 cells, and a full inventory is what stops loot
            // being picked up and makes the next purchase fail with nowhere to put it.
            var carried = InventoryHelpers.SpareHealthPotions(client.Game, account);

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

        // The sorceresses kill with mana. Once the chicken service drank mana outside the damage
        // window, the belt's few mana potions were the only ones they had. A whirlwind barbarian's whirl
        // costs mana too: with the belt's four alone both barbarians left games out of mana potions, three
        // times in thirty games, the lead among them. They stand in town for their portal long enough to buy.
        var manaSpares = client.Game.Me.Class == CharacterClass.Sorceress ? SorceressManaSpares
            : client.Game.Me.HasSkill(Skill.Whirlwind) ? WhirlwindManaSpares
            : 0;
        if (manaSpares > 0)
        {
            townManagementOptions.ManaPotionsToBuy = Math.Max(0,
                account.ManaPotionTarget(client.Game.Belt.Height) + manaSpares - InventoryHelpers.GetTotalManaPotions(client.Game));
        }

        if (_cowconfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase))
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

        return townManagementOptions;
    }

    protected override Task PostInitializeAllJoined(List<Client> clients)
    {
        // Before anything can return early: a game left on the last game's token, cancelled already, ended every assist's
        // walk the moment it began. Not disposed: walks still holding its token may look at it after the game.
        var gameOver = new CancellationTokenSource();
        GameOverToken = gameOver.Token;
        NextGame.Task.ContinueWith(_ => gameOver.Cancel(), TaskScheduler.Default);
        clients.ForEach(RememberBattleOrders);
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
        _leadClient = boClient;
        _party = clients;
        _leftAt.Clear();
        _rejoins.Clear();
        _returnedAt.Clear();
        _awaitingBattleOrders.Clear();
        _portalRetreats.Clear();
        _shoutStepAt.Clear();
        _redPortalLevelSide = null;
        _joined.Clear();
        _away.Clear();
        _runStartedAt = DateTime.Now;
        _leadAway = false;
        _cowPortalAt = null;
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

        List<Client> listeningClients;
        if (canHunt)
        {
            // Everything that is not already killing listens, so packs the party walks into are
            // seen even where no sorceress ever goes.
            listeningClients = clients.Where(c => !killingClients.Contains(c)).ToList();
            _huntingParty = clients.Where(c => c.Game.Me.Class != CharacterClass.Sorceress).ToList();
            Log.Information($"Active mode on, party {string.Join(",", _huntingParty.Select(c => c.Game.Me.Name))} clearing everything in the level");
        }
        else
        {
            listeningClients = clients.Where(c => c.Game.Me.Class == CharacterClass.Sorceress && !killingClients.Contains(c)).ToList();
            listeningClients.Add(boClient);
            _huntingParty = [];
        }

        _killingClients = killingClients;
        _partyRetreating = false;
        _leadInTown = false;
        _safePortalWanted = false;
        _lootSweep.Reset();
        _runOver = false;
        _sweepReported.Clear();
        _unreachable.Clear();
        _huntTrail.Clear();
        InCowLevel.Clear();
        foreach (var walk in FollowWalks.Values)
        {
            walk.Reset();
        }

        _cowManager = new CowManager(killingClients, listeningClients, _mapApiService, _cowconfig.ActiveMode)
        {
            ClearsTheLevel = () => ClientsNeedingMule.Count > 0,
            HuntBudget = _cowconfig.HuntBudgetSeconds is int seconds and > 0 ? TimeSpan.FromSeconds(seconds) : CowManager.ClearingBudget,
        };
        return Task.CompletedTask;
    }

    /// <summary>The cow level is a few hundred units across; anything further off is in another area.</summary>
    protected override bool IsRunArea(Item item) => _cowManager?.InLevel(item.Location) ?? true;

    private bool InThisLevel(Client client, Item item)
        => !_unreachable.ContainsKey(item.Id)
            && (_cowManager?.InLevel(item.Location) ?? item.Location.Distance(client.Game.Me.Location) < 1000);

    /// <summary>
    /// Whether the game is done: the hunt's budget spent, or nothing left to kill and nothing left to collect. The
    /// level being cleared used to end it on the spot, so everything dropped by the last packs, and anything killed
    /// beyond the reach of the sorceress that cleared it, stayed on the ground.
    /// </summary>
    private bool IsRunOver(CowManager cowManager)
    {
        if (!cowManager.IsFinished())
        {
            return false;
        }

        if (_killingClients.All(c => !c.Game.IsInGame()))
        {
            return true;
        }

        lock (_lootSweepLock)
        {
            // Every client's loop asks; the first one to see the end has said why.
            if (_runOver)
            {
                return true;
            }

            // The budget ends the hunt with packs still standing, and a sweep among them stands the sorceresses
            // still to pick up while cows close in: the party leaves as it is, a sweep already running included -
            // a pack released on a retreat counts as done, so "cleared" does not mean nothing is left alive.
            if (cowManager.CutShort)
            {
                Log.Information("Hunt budget spent, {Progress}: leaving without a loot sweep", cowManager.DescribeProgress());
                _runOver = true;
                return true;
            }

            // Only what somebody can carry. An item no killer has room for is put back on the list
            // by every pickup without a word, and the game sat out the whole budget for one helm.
            // In this level only. The list also keeps what was seen in town and the Catacombs on the
            // way in, twenty thousand units off, and the sweep waited out its budget for those.
            var onGround = PickitItemsLeft().Where(i => _killingClients.Any(c => c.Game.IsInGame() && InThisLevel(c, i))).ToList();
            var left = onGround.Where(i => _killingClients.Any(c => c.Game.IsInGame() && InThisLevel(c, i) && c.Game.Inventory.FindFreeSpace(i) != null)).ToList();
            if (!_lootSweep.IsRunning)
            {
                var noRoom = onGround.Except(left).ToList();
                if (noRoom.Count > 0)
                {
                    Log.Information("Level cleared, nobody has room for {Count} items: {Items}", noRoom.Count, string.Join(", ", noRoom.Select(i => i.Name)));
                }

                if (left.Count == 0)
                {
                    _runOver = true;
                    return true;
                }

                Log.Information("Level cleared, collecting {Count} items still on the ground: {Items}", left.Count, string.Join(", ", left.Select(i => i.Name)));
                _lootSweep.Start();
                return false;
            }

            if (left.Count == 0)
            {
                Log.Information("Collected everything left after {Seconds:F1}s", _lootSweep.Elapsed.TotalSeconds);
                _runOver = true;
                return true;
            }

            if (_lootSweep.Elapsed > LootSweepBudget)
            {
                Log.Warning("Leaving {Count} items on the ground after {Seconds:F0}s of collecting: {Items}", left.Count, _lootSweep.Elapsed.TotalSeconds, string.Join(", ", left.Select(i => i.Name)));
                _runOver = true;
                return true;
            }

            return false;
        }
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

            if (GamblesLate(client) && TownManagementService.GambleDue(client.Game))
            {
                if (!await GambleAndComeInLate(client))
                {
                    return false;
                }

                await RunWithRejoins(client, account, _cowManager);
                return true;
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
            if (IsWeakWalker(client))
            {
                await WaitForClearLanding(client, portalPlayer.Id);
            }

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
            await ConfirmInCowLevel(client);
        }

        await RunWithRejoins(client, account, _cowManager);
        return true;
    }

    /// <summary>
    /// The killing sorceresses' and the lead's loops end the game when they stop. A loop that stopped
    /// because its own client left the game leaves that to the rejoin decision instead.
    /// </summary>
    private void EndGameWithLoop(Client client)
    {
        if (client.Game.IsInGame())
        {
            NextGame.TrySetResult(true);
        }
    }

    /// <summary>
    /// Runs the client's role, and when it ends with the client out of the game - a chicken or any
    /// other leave - while the game goes on, rejoins, visits town and goes back into the cow level.
    /// Before this a chicken cost the rest of the game: the sorceresses' and the lead's ended it for
    /// everyone, anyone else's left the party a member short until the end.
    /// </summary>
    private async Task RunWithRejoins(Client client, AccountConfig account, CowManager cowManager)
    {
        _joined[client] = 0;
        _accountOf[client] = account;
        // Read while it is in the game: once it has left, its character data may be gone.
        var weakWalker = IsWeakWalker(client);
        await RunRole(client, account, cowManager);
        while (!client.Game.IsInGame())
        {
            // When the chicken asked to leave, not when this loop noticed: a long step or a whirl can sit between.
            var requested = ChickenService.LastLeaveRequest(client);
            var leftAt = DateTime.Now - requested < CowRejoin.MassLeaveWindow ? requested : DateTime.Now;
            _leftAt[client] = leftAt;
            _away[client] = 0;
            var experienceBefore = client.Game.Me?.Experience;
            if (client == _leadClient)
            {
                _leadAway = true;
            }

            await Task.Delay(CowRejoin.SettleTime);

            var situation = RejoinSituationOf(client, cowManager);
            var (outcome, reason) = CowRejoin.Decide(situation);

            // A weak walker comes back without battle orders at half its life, and the amazon did twice:
            // at 114 life, and at 105 life ten seconds after arriving, waiting for the barbarian while the
            // cows reached her. She makes a small share of the kills; the rest of the game is not worth it.
            if (outcome == RejoinOutcome.Rejoin && weakWalker)
            {
                (outcome, reason) = (RejoinOutcome.StayOut, "a weak walker does not rejoin");
            }
            TraceEvents.Decision(client.Game, "cow-rejoin",
                ("outcome", outcome.ToString()),
                ("reason", reason),
                ("rejoins", situation.RejoinsSoFar),
                ("recentLeaves", situation.RecentLeaves),
                ("killersInGame", situation.KillersInGame),
                ("huntLeft", situation.HuntTimeLeft?.TotalSeconds));
            if (outcome != RejoinOutcome.Rejoin)
            {
                Log.Information("{Character} does not rejoin: {Reason}", account.Character, reason);
                if (outcome == RejoinOutcome.EndGame || EndsGameWhenGone(client))
                {
                    NextGame.TrySetResult(true);
                }

                return;
            }

            _rejoins.AddOrUpdate(client, 1, (_, count) => count + 1);
            bool back;
            try
            {
                back = await RejoinAndReturn(client, account, cowManager, leftAt, experienceBefore);
            }
            catch (Exception e)
            {
                Log.Error(e, "{Character} failed to rejoin", account.Character);
                back = false;
            }

            if (!back)
            {
                if (EndsGameWhenGone(client))
                {
                    NextGame.TrySetResult(true);
                }

                return;
            }

            if (client == _leadClient)
            {
                _leadAway = false;
            }

            _away.TryRemove(client, out _);
            await RunRole(client, account, cowManager);
        }

        // The party stays in the game until every member's run is over and a muling farmer is back from Cain, and that
        // held the rest idle in a level the budget left with packs standing. A farmer leaving mules what is identified;
        // a game it was due to mule from the start clears the level instead (CowManager.ClearingBudget).
        if (cowManager.CutShort && client.Game.IsInGame())
        {
            Log.Information("{Character} leaves at once: the hunt ran out of time with cows still standing", account.Character);
            TraceEvents.Decision(client.Game, "leave", ("reason", "hunt budget spent"));
            await client.Game.LeaveGame();
        }
    }

    /// <summary>
    /// The client's role, which cannot die quietly: a killer or the lead whose loop threw would leave the
    /// rest of the party hunting a level nobody is clearing, with nothing to end the game.
    /// </summary>
    private async Task RunRole(Client client, AccountConfig account, CowManager cowManager)
    {
        try
        {
            await GetTaskForClient(client, account, cowManager);
        }
        catch (Exception e)
        {
            Log.Error(e, "{Character} stopped on an exception, in game {InGame}", account.Character, client.Game.IsInGame());
            if (client.Game.IsInGame() && EndsGameWhenGone(client))
            {
                NextGame.TrySetResult(true);
            }
        }
    }

    /// <summary>The roles whose loop ending has always ended the game: the killing sorceresses and the lead.</summary>
    private bool EndsGameWhenGone(Client client)
        => _killingClients.Contains(client) || client == _leadClient;

    private RejoinSituation RejoinSituationOf(Client client, CowManager cowManager)
    {
        var now = DateTime.Now;
        return new RejoinSituation
        {
            InGame = client.Game.IsInGame(),
            NextGameSet = NextGame.Task.IsCompleted,
            RunOver = cowManager.IsFinished() || IsRunOver(cowManager),
            StrangerFlee = ConsoleBot.Escape.StrangerEscape.FleeRequested,
            Died = ChickenService.LastDeath(client) >= _runStartedAt,
            RejoinsSoFar = _rejoins.GetValueOrDefault(client),
            SinceLastReturn = _returnedAt.TryGetValue(client, out var returned) && _leftAt.TryGetValue(client, out var left) && left >= returned
                ? left - returned
                : null,
            EndsGameWhenGone = EndsGameWhenGone(client),
            // Members whose own loop has not noticed yet count as well: a mass leave arrives within
            // seconds, and the first to look must not take it for a single chicken. One out again soon
            // after coming back counts twice.
            RecentLeaves = CowRejoin.CountRecentLeaves(
                _party.Select(member => (
                    _joined.ContainsKey(member),
                    member.Game.IsInGame(),
                    _leftAt.TryGetValue(member, out var at) ? at : (DateTime?)null,
                    _returnedAt.TryGetValue(member, out var back) ? back : (DateTime?)null)),
                now),
            KillersInGame = _killingClients.Count(killer => killer.Game.IsInGame()),
            HuntTimeLeft = cowManager.HuntTimeLeft,
        };
    }

    /// <summary>Whether the game this rejoiner is in has ended or is ending, so the rejoin stops wherever it is.</summary>
    private bool RejoinPointless(Client client, CowManager cowManager)
        => NextGame.Task.IsCompleted || cowManager.IsFinished() || ConsoleBot.Escape.StrangerEscape.FleeRequested || !client.Game.IsInGame();

    /// <summary>
    /// A death the leave outran: the corpse packet came after the client had gone. After joining the
    /// server assigns the corpse again, and the lifetime experience is lower than before the leave.
    /// </summary>
    private static bool DiedWhileLeaving(Client client, long? experienceBefore)
        => client.Game.Me?.CorpseId != null
            || (experienceBefore is long before && client.Game.Me is { } me && me.Experience > 0 && me.Experience < before);

    /// <summary>Back into the game, a short town visit, and through the red portal into the cow level.</summary>
    private async Task<bool> RejoinAndReturn(Client client, AccountConfig account, CowManager cowManager, DateTime leftAt, long? experienceBefore)
    {
        var key = account.Character.ToLower();
        InCowLevel[key] = false;
        if (!await RejoinCurrentGame(client, account, () => cowManager.IsFinished()) || RejoinPointless(client, cowManager))
        {
            return false;
        }

        if (DiedWhileLeaving(client, experienceBefore))
        {
            Log.Warning("{Character} rejoined and finds it died before leaving, staying in town", client.Game.Me.Name);
            return false;
        }

        if (client == _leadClient)
        {
            // Player ids are handed out afresh on joining; the followers and the lead's own loop go by it.
            BoClientPlayerId = client.Game.Me.Id;
            _leadInTown = false;
            _partyRetreating = false;
        }

        if (FollowWalks.TryGetValue(key, out var walk))
        {
            walk.Reset();
        }

        var options = TownOptionsFor(client, account);
        options.TopUpOnly = true;
        await GeneralHelpers.TryWithTimeout(
            async (_) => RejoinPointless(client, cowManager) || (await _townManagementService.PerformTownTasks(client, options)).Succes,
            RejoinTownBudget);
        if (RejoinPointless(client, cowManager))
        {
            return false;
        }

        // Checked again after the town visit: the corpse is assigned a moment after joining.
        if (DiedWhileLeaving(client, experienceBefore))
        {
            Log.Warning("{Character} rejoined and finds it died before leaving, staying in town", client.Game.Me.Name);
            return false;
        }

        if (!await ReturnToCowLevel(client, cowManager))
        {
            Log.Warning("{Character} rejoined but could not get back into the cow level", client.Game.Me.Name);
            return false;
        }

        _returnedAt[client] = DateTime.Now;
        if (client != _leadClient && !client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders))
        {
            _awaitingBattleOrders[client] = new BattleOrdersWaiting();
        }

        var took = DateTime.Now - leftAt;
        Log.Information("{Character} rejoined after leaving the game and is back in the cow level {Seconds:F1}s after leaving, rejoin {Count} of {Max}",
            client.Game.Me.Name, took.TotalSeconds, _rejoins.GetValueOrDefault(client), CowRejoin.MaxRejoinsPerGame);
        TraceEvents.Decision(client.Game, "cow-rejoin",
            ("outcome", "back"),
            ("seconds", Math.Round(took.TotalSeconds, 1)),
            ("rejoins", _rejoins.GetValueOrDefault(client)));
        return true;
    }

    /// <summary>
    /// Through the red portal, walking first to where it was seen this game since it is only found from
    /// within sight of it, and failing that through the portal character's town portal.
    /// </summary>
    private async Task<bool> ReturnToCowLevel(Client client, CowManager cowManager)
    {
        var portalAt = _cowPortalAt;
        if (portalAt != null && !client.Game.GetEntityByCode(EntityCode.RedTownPortal).Any(t => t.TownPortalArea == Area.CowLevel))
        {
            var movementMode = MovementHelpers.PreferredMovement(client.Game);
            var path = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.RogueEncampment, client.Game.Me.Location, portalAt, movementMode);
            if (path.Count > 0)
            {
                await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode);
            }
        }

        if (RejoinPointless(client, cowManager))
        {
            return false;
        }

        if (await MoveToCowLevel(client))
        {
            return true;
        }

        if (RejoinPointless(client, cowManager))
        {
            return false;
        }

        var portalPlayer = client.Game.Players.FirstOrDefault(p => _cowconfig.PortalCharacterName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        if (portalPlayer == null || client.Game.Me.Id == portalPlayer.Id
            || !await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.CowLevel))
        {
            return false;
        }

        return await ConfirmInCowLevel(client);
    }

    /// <summary>A rejoin's town visit is a top-up, not the full errand list a game starts with.</summary>
    private static readonly TimeSpan RejoinTownBudget = TimeSpan.FromSeconds(45);

    private async Task<bool> GetBoAtCata2(Client client, Player portalPlayer)
    {
        // Clients arrive here on their own schedule, so the portal character may still be cubing the
        // leg and its portal may not exist yet. Long enough to cover that, short enough that a
        // client which cannot get through gives the game up rather than holding five others for
        // three minutes and then failing anyway. Her gamble does not count against it.
        if (!await TryNotCountingGambles(async (retryCount) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            return NextGame.Task.IsCompleted
                || await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.CatacombsLevel2);
        }, TimeSpan.FromSeconds(70)) || NextGame.Task.IsCompleted)
        {
            Log.Warning($"Client {client.Game.Me.Name} taking portal to cata2 failed");
            return false;
        }

        return await WaitForShoutsAndReturnToTown(client);
    }

    /// <summary>See <see cref="PausableWait"/>: a wait the party's gambles do not use up.</summary>
    private static Task<bool> TryNotCountingGambles(Func<int, Task<bool>> action, TimeSpan budget)
        => PausableWait.Try(action, budget, () => TownManagementService.AnyClientGambling);

    /// <summary>
    /// Waits at the catacombs for shouts, then goes back to town. Clients arrive here whenever
    /// their own town tasks finished, so the shout caster is the only one that waits for the whole
    /// party; everyone else leaves as soon as they personally have the buff.
    /// </summary>
    private async Task<bool> WaitForShoutsAndReturnToTown(Client client)
    {
        var isShoutCaster = client.Game.Me.Skills.GetValueOrDefault(Skill.BattleOrders, 0) > 0;
        // The second barbarian starts where the first ends, so the party has all three after two casts, not three:
        // each member waits here for the last of them. Battle command stays before each one's battle orders, which it
        // raises a level.
        var shoutOrder = isShoutCaster && !GivesBattleOrders(client) ? SecondShoutOrder : null;
        var random = new Random();
        // A barbarian still gambling in town does not use up the wait for his shouts.
        await TryNotCountingGambles(async (retryCount) =>
        {
            if (NextGame.Task.IsCompleted || !client.Game.IsInGame())
            {
                return true;
            }

            if (isShoutCaster)
            {
                if (retryCount % 5 == 0)
                {
                    // All at once with one short wait: one at a time held the shouts 0.4s per player, 2s in a full party.
                    var near = client.Game.Players.Where(p => p.Location?.Distance(client.Game.Me.Location) < 10).Select(p => p.Id).ToList();
                    if (near.Count > 0)
                    {
                        await client.Game.RequestUpdatesAndWaitAsync(near, ShoutUpdateWait);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(0.1));
                await ClassHelpers.CastAllShouts(client, shoutOrder);
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

        // A game given up while it stood here is left from here, along with everyone else; a member that chickened here
        // is out already, and its prepare has failed.
        if (NextGame.Task.IsCompleted || !client.Game.IsInGame())
        {
            Log.Information($"Client {client.Game.Me.Name} stops waiting for shouts at {Waypoint.CatacombsLevel2}: the game is over");
            return false;
        }

        var gotShouts = !ClassHelpers.IsMissingShouts(client.Game.Players.FirstOrDefault(p => p.Id == client.Game.Me.Id));

        // Once the shouts are given, not before: a barbarian sent to the waypoint first held every member's shouts up to
        // two seconds. The portal character has it already (she came by it), so her stop, the party's start, is not held.
        await LearnCatacombsWaypoint(client);
        if (!gotShouts)
        {
            Log.Warning($"Client {client.Game.Me.Name} gave up waiting for shouts at {Waypoint.CatacombsLevel2}");
        }

        // The portal the party came through is standing right here and leads home, so use it rather
        // than spending a scroll on a second one. Only the portal character is ever restocked with
        // tomes, so for everyone else creating one is what fails once their scrolls run out.
        if (!await TakeNearestPortalToTown(client)
            && !await TakeWaypointHomeToPortalArea(client)
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
                // Unfiltered: this asks whether it is safe to stand here and plant a portal, and a
                // monster this character cannot hurt is still perfectly able to kill her. Filtering
                // by what she can damage hid the lightning enchanted pack - the one most likely to
                // do it - from the only check looking for it.
                bool LightningEnchantedNear() => cowManager.GetNearbyAliveMonsters(client, 35.0, 100)
                    .Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted));
                if (!LightningEnchantedNear())
                {
                    if (!PartyPrepared.IsCompleted)
                    {
                        var waited = await WaitForPartyBeforePortal(client, cowManager);
                        if (waited == PartyWait.GivenUp)
                        {
                            return false;
                        }

                        if (waited == PartyWait.Crowded || LightningEnchantedNear())
                        {
                            continue;
                        }
                    }

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
    /// The portal character starts her run before the party has prepared; see <see cref="EarlyRunStart"/>. Waiting in
    /// town for the party after her battle orders cost her 0-7s a game. Started the moment she had them, she would
    /// have opened her portal in the level while members still needed the one at the catacombs, or stood alone at
    /// her start for 80s in the game the amazon was still in town.
    /// </summary>
    protected override async Task<bool> StartsRunBeforeParty(Client client, IReadOnlyList<Client> party, Task<bool> partyPrepared)
    {
        // Without her own battle orders she goes with the party as before: she gets none in town.
        if (!_cowconfig.PortalCharacterName.Equals(client.Game.Me?.Name, StringComparison.OrdinalIgnoreCase)
            || !HasOwnBattleOrders(client))
        {
            return false;
        }

        // Each member's own client, not her view of them: from town she no longer sees their states.
        static EarlyRunStart.Member AsSeen(Client member) => new(member.Game.IsInGame() && member.Game.Me != null, HasOwnBattleOrders(member));

        while (!partyPrepared.IsCompleted && !NextGame.Task.IsCompleted && client.Game.IsInGame())
        {
            if (EarlyRunStart.MayStart(HasOwnBattleOrders(client), party.Where(member => member != client).Select(AsSeen)))
            {
                return true;
            }

            await Task.WhenAny(partyPrepared, Task.Delay(100));
        }

        return false;
    }

    private static bool HasOwnBattleOrders(Client client)
        => client.Game.IsInGame() && client.Game.Me?.Effects.ContainsKey(EntityEffect.BattleOrders) == true;

    /// <summary>This many monsters within <see cref="PartyWaitCrowdRadius"/> of the waiting portal character send her on to her next start.</summary>
    private const int PartyWaitCrowd = 2;

    private const double PartyWaitCrowdRadius = 5;

    private enum PartyWait
    {
        Ready,
        Crowded,
        GivenUp,
    }

    /// <summary>
    /// Her portal in the level replaces the one at the catacombs, which the party uses for its battle orders and the
    /// way back, so it is opened only once everyone has prepared. She waits as long as the party's prepare does: giving
    /// up after a set time closed that portal under members still standing at the catacombs, none of them carrying a
    /// tome to make their own. She does not fight while she waits, so cows on her send her on to her next start.
    /// Given up when a prepare failed or the game ended; she then leaves the game, as the party does.
    /// </summary>
    private async Task<PartyWait> WaitForPartyBeforePortal(Client client, CowManager cowManager)
    {
        var waited = Stopwatch.StartNew();
        Log.Information("{Character} waits at {Location} for the party to finish preparing before opening the portal", client.Game.Me.Name, client.Game.Me.Location);
        while (!PartyPrepared.IsCompleted && !NextGame.Task.IsCompleted && client.Game.IsInGame())
        {
            if (CowsOnHer(client, cowManager, PartyWaitCrowdRadius).Count >= PartyWaitCrowd)
            {
                Log.Information("{Character} moves on from {Location} with cows on her after waiting {Seconds:F1}s for the party", client.Game.Me.Name, client.Game.Me.Location, waited.Elapsed.TotalSeconds);
                TraceEvents.Decision(client.Game, "cow-early-start", ("outcome", "moved on"), ("waited", Math.Round(waited.Elapsed.TotalSeconds, 1)));
                return PartyWait.Crowded;
            }

            await Task.WhenAny(PartyPrepared, NextGame.Task, Task.Delay(100));
        }

        if (PartyPrepared.IsCompletedSuccessfully && PartyPrepared.Result)
        {
            Log.Information("{Character} waited {Seconds:F1}s in the level for the party", client.Game.Me.Name, waited.Elapsed.TotalSeconds);
            return PartyWait.Ready;
        }

        var reason = !client.Game.IsInGame() ? "she is out of the game"
            : PartyPrepared.IsCompleted ? "a party member failed to prepare"
            : "the game is over";
        Log.Warning("{Character} opens no portal in the level and leaves the game: {Reason}", client.Game.Me.Name, reason);
        TraceEvents.Decision(client.Game, "cow-early-start", ("outcome", "left"), ("reason", reason), ("waited", Math.Round(waited.Elapsed.TotalSeconds, 1)));
        if (client.Game.IsInGame())
        {
            await client.Game.LeaveGame();
        }

        return PartyWait.GivenUp;
    }

    /// <summary>
    /// Steps into whichever town portal is already open here, whoever made it. Returns false when
    /// there is none to take, leaving the caller to fall back on making its own.
    /// </summary>
    private static async Task<bool> TakeNearestPortalToTown(Client client)
    {
        var portal = client.Game.GetEntityByCode(EntityCode.TownPortal)
            .OrderBy(t => t.Location.Distance(client.Game.Me.Location))
            .FirstOrDefault();
        return portal != null && await TakePortalToTown(client, portal);
    }

    /// <summary>Per character, for the bot's lifetime: whether it has the Catacombs Level 2 waypoint. Unknown until it has opened a waypoint menu.</summary>
    private static readonly ConcurrentDictionary<string, bool> CatacombsWaypointKnown = new(StringComparer.OrdinalIgnoreCase);

    private static bool KnowsCatacombsWaypoint(Client client)
        => client.Game.Me?.Name is { } name && (CatacombsWaypointKnown.TryGetValue(name, out var known) ? known : client.Game.Me.AllowedWaypoints.Contains(Waypoint.CatacombsLevel2));

    /// <summary>Takes the town waypoint to the catacombs; false, and remembered, when the character turns out not to have it.</summary>
    private async Task<bool> TakeCatacombsWaypoint(Client client)
    {
        Log.Information("Client {Character} taking waypoint to {Waypoint}", client.Game.Me.Name, Waypoint.CatacombsLevel2);
        try
        {
            // A waypoint that moved the character but reported late counts: it is where it was going.
            return await _townManagementService.TakeWaypoint(client, Waypoint.CatacombsLevel2) || client.Game.Area == Area.CatacombsLevel2;
        }
        catch (InvalidOperationException e)
        {
            // Only a menu that opened without the waypoint says it is missing; the same exception comes from the town
            // waypoint not being in sight yet.
            if (!client.Game.Me.AllowedWaypoints.IsEmpty && !client.Game.Me.AllowedWaypoints.Contains(Waypoint.CatacombsLevel2))
            {
                CatacombsWaypointKnown[client.Game.Me.Name] = false;
            }

            Log.Information("Client {Character} took no {Waypoint} waypoint, taking the portal: {Reason}", client.Game.Me.Name, Waypoint.CatacombsLevel2, e.Message);
            return client.Game.Area == Area.CatacombsLevel2;
        }
    }

    private enum WaypointTime
    {
        Now,
        TakeHerPortal,
        GameOver,
    }

    /// <summary>
    /// Waits in town for the moment to take the catacombs waypoint: her out of town (the party list's area for her), or her
    /// cow portal standing, means she is on her way. Her portal to the catacombs showing first means she is there already,
    /// and it is the quicker way to her.
    /// </summary>
    private async Task<WaypointTime> CatacombsWaypointTime(Client client, Player portalPlayer)
    {
        var answer = WaypointTime.GameOver;
        await TryNotCountingGambles(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(0.2));
            if (NextGame.Task.IsCompleted || !client.Game.IsInGame())
            {
                answer = WaypointTime.GameOver;
                return true;
            }

            if (client.Game.GetEntityByCode(EntityCode.TownPortal).Any(t => t.TownPortalArea == Area.CatacombsLevel2 && t.TownPortalOwnerId == portalPlayer.Id))
            {
                answer = WaypointTime.TakeHerPortal;
                return true;
            }

            var herArea = client.Game.Players.FirstOrDefault(p => p.Id == portalPlayer.Id)?.Area ?? Area.None;
            if ((herArea != Area.None && !herArea.IsTown()) || client.Game.GetEntityByCode(EntityCode.RedTownPortal).Any(t => t.TownPortalArea == Area.CowLevel))
            {
                answer = WaypointTime.Now;
                return true;
            }

            return false;
        }, TimeSpan.FromSeconds(70));
        if (answer == WaypointTime.GameOver && !NextGame.Task.IsCompleted)
        {
            Log.Warning("Client {Character} saw neither the cow portal nor the portal to {Area}", client.Game.Me.Name, Area.CatacombsLevel2);
        }

        return answer;
    }

    /// <summary>The waypoint standing within reach of the client, if any.</summary>
    private static WorldObject NearbyWaypoint(Client client, double reach = 20)
        => EntityConstants.WayPointEntityCodes.SelectMany(code => client.Game.GetEntityByCode(code))
            .Where(w => w.Location.Distance(client.Game.Me.Location) <= reach)
            .OrderBy(w => w.Location.Distance(client.Game.Me.Location))
            .FirstOrDefault();

    /// <summary>
    /// A member that came through the portal opens the catacombs waypoint's menu once, which activates it, so the next
    /// game it goes by waypoint. The portal character opens her portal beside it.
    /// </summary>
    private static async Task LearnCatacombsWaypoint(Client client)
    {
        if (KnowsCatacombsWaypoint(client) || NearbyWaypoint(client) is not { } waypoint)
        {
            return;
        }

        if (!await client.Game.MoveToAsync(waypoint))
        {
            return;
        }

        var known = client.Game.ReadWaypoints(waypoint);
        if (known != null)
        {
            CatacombsWaypointKnown[client.Game.Me.Name] = known.Contains(Waypoint.CatacombsLevel2);
            Log.Information("Client {Character} activated the {Waypoint} waypoint: {Known}", client.Game.Me.Name, Waypoint.CatacombsLevel2, known.Contains(Waypoint.CatacombsLevel2));
        }
    }

    /// <summary>
    /// Home by the waypoint, then to the portal area. In town is home: a walk that fails afterwards is not worth the
    /// prepare, which a town portal cast in town could only fail.
    /// </summary>
    private async Task<bool> TakeWaypointHomeToPortalArea(Client client)
    {
        if (!await TakeWaypointHome(client))
        {
            return false;
        }

        if (!await WalkToTownPortalArea(client))
        {
            Log.Information("Client {Character} is home by waypoint but did not reach the portal area", client.Game.Me.Name);
        }

        return true;
    }

    /// <summary>
    /// Home by the waypoint the client stands by: a member that came by waypoint may have its shouts before the portal
    /// character has arrived to open a portal, and only she carries tomes for one.
    /// </summary>
    private static async Task<bool> TakeWaypointHome(Client client)
    {
        if (NearbyWaypoint(client) is not { } waypoint)
        {
            return false;
        }

        var town = WayPointHelpers.MapTownArea(client.Game.Act);
        return await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (client.Game.Area == town)
            {
                return true;
            }

            if (!await client.Game.MoveToAsync(waypoint) || !client.Game.TakeWaypoint(waypoint, Waypoint.RogueEncampment))
            {
                return false;
            }

            return await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(50);
                return client.Game.Area == town;
            }, TimeSpan.FromSeconds(2));
        }, TimeSpan.FromSeconds(6));
    }

    private static async Task<bool> TakePortalToTown(Client client, WorldObject portal, TimeSpan? budget = null)
    {
        var townArea = WayPointHelpers.MapTownArea(client.Game.Act);
        return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            // A transition slower than the half second below lands the character in town after the
            // check gave up, and walking to a portal left behind in another area then fails until the
            // caller casts a portal of its own - which town refuses, and the game was lost.
            if (client.Game.Area == townArea)
            {
                return true;
            }

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
        }, budget ?? TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The nearest portal here this member may take to town: a town portal of its own or a party member's,
    /// or the red cow portal's level side (the trace shows it as leading to the Rogue Encampment).
    /// </summary>
    private (WorldObject Portal, double Distance) NearestTownPortal(Client client)
    {
        var here = client.Game.Me.Location;
        var partyIds = client.Game.Players.Where(p => p.Id == client.Game.Me.Id || client.Game.IsInPartyWith(p)).Select(p => p.Id).ToHashSet();
        var red = client.Game.GetEntityByCode(EntityCode.RedTownPortal).Where(portal => portal.TownPortalArea.IsTown() && portal.Location != null).ToList();
        if (red.Count > 0)
        {
            _redPortalLevelSide ??= red[0].Location;
        }

        var portal = client.Game.GetEntityByCode(EntityCode.TownPortal)
            .Where(candidate => candidate.Location != null && partyIds.Contains(candidate.TownPortalOwnerId))
            .Concat(red)
            .MinBy(candidate => candidate.Location.Distance(here));
        return (portal, portal == null ? double.MaxValue : portal.Location.Distance(here));
    }

    /// <summary>Hostiles on the landing by the other party members' view; null when none of them can see it.</summary>
    private int? HostilesAtLanding(Client client, Point landing)
    {
        int? most = null;
        foreach (var member in _party)
        {
            var at = member.Game.Me?.Location;
            if (member == client || at == null || !member.Game.IsInGame() || _away.ContainsKey(member)
                || !InCowLevel.GetValueOrDefault(member.Game.Me.Name.ToLower()) || at.Distance(landing) > PortalLandingSight)
            {
                continue;
            }

            var seen = HostilesAround(member, landing, PortalRetreat.LandingRadius).Count;
            most = Math.Max(most ?? 0, seen);
        }

        return most;
    }

    /// <summary>How near the landing a party member has to be for its view of it to count.</summary>
    private const double PortalLandingSight = 40;

    /// <summary>Where a town portal of this owner stands in the cow level, by a member there; null when none of them sees it.</summary>
    private Point LevelSideOfPortal(uint ownerId)
    {
        foreach (var member in _party)
        {
            var name = member.Game.Me?.Name;
            if (name == null || !member.Game.IsInGame() || !InCowLevel.GetValueOrDefault(name.ToLower()))
            {
                continue;
            }

            var portal = member.Game.GetEntityByCode(EntityCode.TownPortal)
                .FirstOrDefault(candidate => candidate.TownPortalOwnerId == ownerId && candidate.Location != null && candidate.TownPortalArea.IsTown());
            if (portal != null)
            {
                return portal.Location;
            }
        }

        return null;
    }

    /// <summary>
    /// A weak walker waits in town until the members in the level report the portal's landing clear; see
    /// <see cref="PortalRetreat.ShouldEnter"/>. Both of its surrounded leaves in fifteen games came 0.3 s
    /// after it came through onto a landing the sorceress who opened the portal stood on with ten cows.
    /// </summary>
    private async Task WaitForClearLanding(Client client, uint portalOwnerId)
    {
        var waited = Stopwatch.StartNew();
        int? hostiles;
        while (true)
        {
            var landing = LevelSideOfPortal(portalOwnerId);
            hostiles = landing == null ? null : HostilesAtLanding(client, landing);
            if (PortalRetreat.ShouldEnter(waited.Elapsed, hostiles) || NextGame.Task.IsCompleted || !client.Game.IsInGame())
            {
                break;
            }

            await Task.Delay(500);
        }

        if (waited.Elapsed >= TimeSpan.FromSeconds(1))
        {
            Log.Information("{Character} waited {Seconds:F1}s in town for the landing to clear, {Hostiles} on it", client.Game.Me.Name, waited.Elapsed.TotalSeconds, hostiles);
            TraceEvents.Decision(client.Game, "landing-wait",
                ("seconds", Math.Round(waited.Elapsed.TotalSeconds, 1)),
                ("hostiles", hostiles));
        }
    }

    /// <summary>
    /// Out through a portal within reach instead of out of the game: to town, a wait until the landing
    /// is clear, a top-up when hurt, and back in by the same portal. False when no portal may be taken -
    /// none close, a full cursor, the way to one not under its feet past a cow, or this member's retreats
    /// used up - leaving the caller to do what it did before.
    /// </summary>
    private async Task<bool> TryPortalRetreat(Client client, CowManager cowManager, string reason)
    {
        var retreats = _portalRetreats.GetOrAdd(client, _ => new PortalRetreats());
        var (portal, distance) = NearestTownPortal(client);
        if (portal == null || distance > PortalRetreat.PortalReach || client.Game.CursorItem != null
            || !PortalRetreat.RetreatsLeft(retreats.Count) || !PortalRetreat.WayToPortalClear(distance, PortalWalkClear(client, cowManager, portal.Location)))
        {
            return false;
        }

        var me = client.Game.Me;
        var key = me.Name.ToLower();
        var throughRed = portal.Code == EntityCode.RedTownPortal;
        var ownerId = portal.TownPortalOwnerId;
        // Back the way it left, unless the last return through that way met danger again at once.
        var returnByRed = PortalRetreat.MetDangerAgain(DateTime.Now - retreats.LastReturnAt) ? !retreats.LastReturnByRedPortal : throughRed;
        var away = Stopwatch.StartNew();
        InCowLevel[key] = false;
        _away[client] = 0;
        if (!await TakePortalToTown(client, portal, PortalTryBudget))
        {
            InCowLevel[key] = true;
            _away.TryRemove(client, out _);
            return false;
        }

        retreats.Count++;
        Log.Information("{Character} took the portal to town {Distance:F1} away at {Life} life, retreat {Count} of {Max}: {Reason}",
            me.Name, distance, me.Life, retreats.Count, PortalRetreat.MaxRetreatsPerGame, reason);
        TraceEvents.Decision(client.Game, "portal-retreat",
            ("phase", "out"),
            ("reason", reason),
            ("portalDistance", Math.Round(distance, 1)),
            ("redPortal", throughRed),
            ("life", me.Life));

        // The landing to watch is where it will come back out.
        var landing = returnByRed ? _redPortalLevelSide : portal.Location;
        var waited = Stopwatch.StartNew();
        while (!RejoinPointless(client, cowManager)
            && !PortalRetreat.ShouldReturn(waited.Elapsed, landing == null ? null : HostilesAtLanding(client, landing)))
        {
            await Task.Delay(500);
        }

        try
        {
            if (RejoinPointless(client, cowManager))
            {
                return true;
            }

            if (client.Game.Me.MaxLife > 0 && (double)client.Game.Me.Life / client.Game.Me.MaxLife < ResumeLifeFraction
                && _accountOf.TryGetValue(client, out var account))
            {
                var options = TownOptionsFor(client, account);
                options.TopUpOnly = true;
                await GeneralHelpers.TryWithTimeout(
                    async (_) => RejoinPointless(client, cowManager) || (await _townManagementService.PerformTownTasks(client, options)).Succes,
                    RejoinTownBudget);
            }

            var owner = client.Game.Players.FirstOrDefault(p => p.Id == ownerId);
            var back = !RejoinPointless(client, cowManager)
                && ((!returnByRed && owner != null && await _townManagementService.TakeTownPortalToArea(client, owner, Area.CowLevel) && await ConfirmInCowLevel(client))
                    || await ReturnToCowLevel(client, cowManager));
            if (!back)
            {
                // Stuck in town is a member lost for the rest of the game; out of it, the rejoin brings it back.
                if (!RejoinPointless(client, cowManager))
                {
                    Log.Warning("{Character} could not get back from town after a portal retreat, leaving the game", client.Game.Me.Name);
                    ChickenService.LeaveNow(client, $"{client.Game.Me.Name} could not get back from town");
                }

                return true;
            }

            retreats.LastReturnAt = DateTime.Now;
            retreats.LastReturnByRedPortal = returnByRed || owner == null;
            if (FollowWalks.TryGetValue(key, out var walk))
            {
                walk.Reset();
            }

            // Town time is not time spent waiting for battle orders.
            if (_awaitingBattleOrders.TryGetValue(client, out var waiting))
            {
                waiting.ArrivedAt += away.Elapsed;
                waiting.LastLook = DateTime.Now;
            }

            Log.Information("{Character} back from town {Seconds:F1}s after the portal retreat", client.Game.Me.Name, away.Elapsed.TotalSeconds);
            TraceEvents.Decision(client.Game, "portal-retreat",
                ("phase", "back"),
                ("seconds", Math.Round(away.Elapsed.TotalSeconds, 1)),
                ("waited", Math.Round(waited.Elapsed.TotalSeconds, 1)),
                ("redPortal", retreats.LastReturnByRedPortal));
            return true;
        }
        finally
        {
            _away.TryRemove(client, out _);
        }
    }

    /// <summary>On the danger path a portal gets this long; one that cannot be entered by then is no way out.</summary>
    private static readonly TimeSpan PortalTryBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>Whether the walk to a portal passes no cow, by this client's own view.</summary>
    private bool PortalWalkClear(Client client, CowManager cowManager, Point portal)
    {
        var map = cowManager.LevelMap;
        return Kiting.Reachable(new StepSearch
        {
            From = client.Game.Me.Location,
            Cows = CowsOnHer(client, cowManager, 30).Select(cow => cow.Location).ToList(),
            Walkable = point => map == null || (LevelCell(map, point) is int cell && AreaMapExtensions.IsMovable(cell)),
        }, portal);
    }

    /// <summary>Any party member under the retreat line with something on it and a portal within reach goes to town.</summary>
    private async Task<bool> RetreatIfHurtByPortal(Client client, CowManager cowManager)
    {
        var me = client.Game.Me;
        var hurt = me.MaxLife > 0 && (double)me.Life / me.MaxLife < RetreatLifeFraction;
        if (!hurt)
        {
            return false;
        }

        var (_, distance) = NearestTownPortal(client);
        var hostileNear = HostilesAround(client, me.Location, PortalRetreat.LandingRadius).Count > 0;
        return PortalRetreat.ShouldRetreat(weakWalkerInDanger: false, belowRetreatLine: true, hostileNear, distance)
            && await TryPortalRetreat(client, cowManager, $"at {me.Life} of {me.MaxLife} life with monsters on it");
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
        var movementMode = MovementHelpers.PreferredMovement(game);

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
        if (game.Cube.Items.Count != 0 && !InventoryHelpers.MoveCubeItemsToInventory(game))
        {
            // Never dropped (user decision 2026-10-10). Make room in the inventory by stashing what
            // is worth keeping, then empty the cube again; when even that fails, this game is given
            // up rather than an item put on the floor.
            Log.Warning("Client {ClientName}: cube items do not fit the inventory, stashing inventory items to make room", game.Me.Name);
            var pathStash = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, EntityCode.Stash, movementMode);
            await MovementHelpers.TakePathOfLocations(game, pathStash, movementMode);
            var toStash = game.Inventory.Items
                .Where(i => i.IsIdentified && D2NG.Pickit.Pickit.ShouldKeepItem(game, i) && D2NG.Pickit.Pickit.CanTouchInventoryItem(game, i))
                .ToList();
            InventoryHelpers.StashItemsAndGold(game, toStash, 0);
            if (!InventoryHelpers.MoveCubeItemsToInventory(game))
            {
                // Only a failed stash during the town tasks used to ask for the mules, and this stash
                // runs after them, so a full stash abandoned every game from here on without one.
                Log.Error("Client {ClientName}: cube still not empty after stashing, abandoning the game and muling. Cube: {Cube}; inventory {InventoryFree} free cells: {Inventory}; stash {StashFree} free cells",
                    game.Me.Name, string.Join(", ", game.Cube.Items.Select(i => i.Name)), game.Inventory.FreeCellCount(),
                    string.Join(", ", game.Inventory.Items.Select(i => i.Name)), game.Stash.FreeCellCount());
                ClientsNeedingMule.Add(client.LoggedInUserName());
                return false;
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

        return true;
    }

    private void LogTristramPortalAttempt(Client client, WorldObject portal, int attempt, TimeSpan elapsed)
    {
        var me = client.Game.Me;
        var monsters = client.Game.WorldObjects.Values
            .Where(o => o.Type == EntityType.NPC && o.State != EntityState.Dead && o.State != EntityState.Dieing
                && o.Location.Distance(me.Location) <= 20)
            .OrderBy(o => o.Location.Distance(me.Location))
            .Select(o => $"{o.NPCCode} {o.Location.Distance(me.Location):F0} away {o.LifePercentage:F0}%"
                + (_attackService.IsImmuneTo(client, o, ResistType.Lightning) ? " lightning immune" : "")
                + (o.MonsterEnchantments.Count > 0 ? $" [{string.Join(",", o.MonsterEnchantments)}]" : ""));
        Log.Information("{Character} not in Tristram 0.2s after portal click {Attempt} ({Seconds:F1}s): {Distance:F1} from the portal at {Location} in {Area}, life {Life}/{MaxLife}, mana {Mana}/{MaxMana}, cursor {Cursor}, monsters within 20: {Monsters}",
            me.Name, attempt, elapsed.TotalSeconds, me.Location.Distance(portal.Location), me.Location, client.Game.Area,
            me.Life, me.MaxLife, me.Mana, me.MaxMana, client.Game.CursorItem?.Name.ToString() ?? "empty", string.Join("; ", monsters));
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

        var portalTimer = Stopwatch.StartNew();
        var portalAttempts = 0;
        var firstClick = DateTime.Now;
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (client.Game.Area == Area.Tristram)
            {
                return true;
            }

            var tristamPortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).FirstOrDefault(t => t.TownPortalArea == Area.Tristram);
            if (tristamPortal == null)
            {
                if (retryCount % 30 == 0)
                {
                    Log.Information("{Character} sees no Tristram portal at {Location} in {Area}, attempt {Attempt}",
                        client.Game.Me.Name, client.Game.Me.Location, client.Game.Area, retryCount);
                }

                return false;
            }

            // Being struck can cast a teleport from her gear and drop her 30 away, out of reach of
            // both the click and MoveToAsync, which gives up past 20 without moving.
            var distance = client.Game.Me.Location.Distance(tristamPortal.Location);
            if (distance > 5)
            {
                Log.Information("{Character} is {Distance:F1} from the Tristram portal at {Location}, teleporting back",
                    client.Game.Me.Name, distance, client.Game.Me.Location);
                var pathBack = await _pathingService.GetPathToLocation(client.Game, tristamPortal.Location, MovementMode.Teleport);
                await MovementHelpers.TakePathOfLocations(client.Game, pathBack.ToList(), MovementMode.Teleport);
            }
            else
            {
                await client.Game.MoveToAsync(tristamPortal);
            }

            if (portalAttempts++ == 0)
            {
                firstClick = DateTime.Now.AddSeconds(-1);
            }

            client.Game.InteractWithEntity(tristamPortal);
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                return client.Game.Area == Area.Tristram;
            }, TimeSpan.FromSeconds(0.2)))
            {
                LogTristramPortalAttempt(client, tristamPortal, portalAttempts, portalTimer.Elapsed);
                return false;
            }

            return true;

        }, TimeSpan.FromSeconds(15)))
        {
            Log.Error($"Moving to Tristam failed");
            client.Game.LogPacketsSince($"{client.Game.Me.Name} could not enter the Tristram portal after {portalAttempts} clicks", firstClick);
            return false;
        }

        Log.Information("{Character} entered Tristram after {Attempts} portal clicks in {Seconds:F1}s",
            client.Game.Me.Name, portalAttempts, portalTimer.Elapsed.TotalSeconds);

        await client.Game.RequestUpdateAndWaitAsync(client.Game.Me.Id, UpdateAnswerTimeout);

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

    private void LeadMoved(string characterKey, Point location)
    {
        if (!FollowWalks.TryGetValue(characterKey, out var walk))
        {
            return;
        }

        lock (walk.Gate)
        {
            if (FollowRules.ShouldRetarget(walk.Target, location))
            {
                walk.Cancel?.Cancel();
            }
        }
    }

    /// <summary>Walks after the lead when this follower has fallen behind it. Called from the client's own loop only.</summary>
    /// <param name="destination">Where the walk ends when not beside the lead; see <see cref="Kiting.FollowDestination"/>.</param>
    private async Task FollowLead(Client client, Point leadLocation, CowManager cowManager, Point destination = null)
    {
        if (!client.Game.IsInGame()
            || !FollowWalks.TryGetValue(client.Game.Me.Name.ToLower(), out var walk))
        {
            return;
        }

        var now = DateTime.Now;
        await RequestUpdateIfUnconfirmed(client, walk, now);
        if (!Threatened(client, cowManager))
        {
            walk.Hold.Reset();
        }

        bool walking;
        lock (walk.Gate)
        {
            walking = walk.Cancel != null;
        }

        if (now < walk.RetryAfter || !FollowRules.ShouldWalkToLead(client.Game.Me.Location, leadLocation, walk.LastTarget, walking)
            || (destination != null && client.Game.Me.Location.Distance(destination) <= ArrivedAtStop)
            || Holds(client, cowManager, walk, leadLocation))
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        lock (walk.Gate)
        {
            walk.Target = leadLocation;
            walk.LastTarget = leadLocation;
            walk.Cancel = cancel;
        }

        bool pathFound;
        try
        {
            // Stopped by anything that reaches it on the way, so the loop can fight it; the walk picks
            // up again from the same target once it is clear.
            // The lead moving on cancels the token, and so does holding to fight, from the loop below. A
            // party retreat does not stop it: the walk back to the lead is the retreat.
            var move = WhirlOrWalk(client, destination ?? leadLocation, () => NextGame.Task.IsCompleted || !client.Game.IsInGame(), cancel.Token, warnNoPath: !walk.NoPathLogged,
                backOut: () => BacksOutToLead(client, leadLocation, walk.Hold));
            while (!move.IsCompleted)
            {
                await Task.WhenAny(move, Task.Delay(200));
                if (!move.IsCompleted && Holds(client, cowManager, walk, leadLocation))
                {
                    await cancel.CancelAsync();
                }
            }

            pathFound = await move;
        }
        finally
        {
            lock (walk.Gate)
            {
                walk.Target = null;
                walk.Cancel = null;
            }
        }

        // An unreachable lead was re-pathed and logged every tick.
        if (!pathFound)
        {
            walk.NoPathLogged = true;
            walk.RetryAfter = DateTime.Now + FollowRules.NoPathBackoff;
        }
        else
        {
            walk.NoPathLogged = false;
        }
    }

    /// <summary>Monsters within self defence range that this client sees, alive and in line of sight.</summary>
    private bool Threatened(Client client, CowManager cowManager)
    {
        var map = cowManager.LevelMap;
        var here = client.Game.Me.Location;
        return CowsOnHer(client, cowManager, SelfDefenceRange).Any(monster => map == null || LevelInSight(map, here, monster.Location));
    }

    /// <summary>
    /// Whether a follower attacked on its walk to the lead whirls on towards him rather than stopping to fight: when it
    /// would not hold anyway (<see cref="FollowRules.ShouldHold"/>) - a straggler, the party retreating, too hurt or out of
    /// healing, or a hold already spent on something that costs him nothing, which a stop at every attack would turn back
    /// into the step-at-a-time walk the hold's cap is for.
    /// </summary>
    private bool BacksOutToLead(Client client, Point leadLocation, HoldTimer hold)
    {
        var me = client.Game.Me;
        return me?.Location == null || leadLocation == null || me.Location.Distance(leadLocation) >= StragglerDistance || _partyRetreating
            || (me.MaxLife > 0 && (double)me.Life / me.MaxLife < RetreatLifeFraction) || OutOfHealing(client) || hold.Spent;
    }

    /// <summary>Whether this walker stops to fight instead of walking after the lead; see <see cref="HoldTimer"/>.</summary>
    private bool Holds(Client client, CowManager cowManager, FollowWalk walk, Point leadLocation)
    {
        var me = client.Game.Me;
        return walk.Hold.Update(
            DateTime.Now,
            Threatened(client, cowManager),
            nearLead: leadLocation != null && me.Location.Distance(leadLocation) < StragglerDistance,
            retreating: _partyRetreating,
            hurt: me.MaxLife > 0 && (double)me.Life / me.MaxLife < RetreatLifeFraction,
            outOfHealing: OutOfHealing(client),
            life: me.Life);
    }

    /// <summary>Nothing left that heals: healing potions and rejuvenations, belt and inventory.</summary>
    private static bool OutOfHealing(Client client)
        => client.Game.Belt.NumOfHealthPotions() + client.Game.Belt.NumOfRejuvenationPotions() == 0
            && !client.Game.Inventory.Items.Any(i => i.Classification is ClassificationType.HealthPotion or ClassificationType.RejuvenationPotion);

    /// <summary>The walk that follows starts from the position, so the server's answer is waited for.</summary>
    private static async Task RequestUpdateIfUnconfirmed(Client client, FollowWalk walk, DateTime now)
    {
        var confirmedDistance = client.Game.ServerConfirmedDistance;
        if (!FollowRules.Disagrees(confirmedDistance))
        {
            walk.DisagreeingSince = null;
            return;
        }

        walk.DisagreeingSince ??= now;
        if (FollowRules.ShouldRequestUpdate(confirmedDistance, now - walk.DisagreeingSince.Value, now - walk.LastUpdateRequest))
        {
            walk.LastUpdateRequest = now;
            walk.DisagreeingSince = null;
            await client.Game.RequestUpdateAndWaitAsync(client.Game.Me.Id, UpdateAnswerTimeout);
        }
    }

    /// <summary>
    /// The lead as this client sees him, or null while he is away: out of the game, in town, or rejoined
    /// and not yet placed. A missing lead used to end the game at once; once it no longer did, his absent
    /// position crashed the followers' loops.
    /// </summary>
    private Player LeadPlayer(Client client)
        => _leadAway || _leadInTown ? null
            : client.Game.Players.FirstOrDefault(p => p.Id == BoClientPlayerId && p.Location != null);

    /// <summary>The manager for the game in progress, when this client is one of its listeners.</summary>
    private CowManager CurrentManagerFor(Client client)
    {
        var manager = _cowManager;
        return manager != null && manager.Listens(client) ? manager : null;
    }

    private async Task GetTaskForClient(Client client, AccountConfig account, CowManager cowManager)
    {
        if (client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] < 50 && !client.Game.Me.HasSkill(Skill.Teleport))
        {
            await BasicIdleClient(client, cowManager);
            return;
        }

        // Experience is the level-wide score: it counts what the party actually killed, and it
        // falls by itself when the last few stragglers cost minutes to reach.
        //
        // Me.Experience, not the Experience attribute - the server never sends attribute 0x0D, it
        // sends experience on its own packets (0x1A-0x1C), which GameData accumulates here.
        // Reading the attribute logged a flat zero for every character all game. What arrives
        // first is the lifetime total, so this is a running total and not a per-game figure: the
        // gain for a game is the difference between two samples of it.
        void LogExperience()
        {
            if (!client.Game.IsInGame() || client.Game.Me == null)
            {
                return;
            }

            Log.Information("Experience {Character} {Experience} in {Area}",
                client.Game.Me.Name, client.Game.Me.Experience, client.Game.Area);
        }

        ElapsedEventHandler refreshHandler = (sender, args) =>
        {
            if (client.Game.IsInGame() && client.Game.Me != null)
            {
                client.Game.SendUpdateRequest(client.Game.Me.Id);
                LogExperience();
            }
        };
        using var executeRefresh = new ExecuteAtInterval(refreshHandler, TimeSpan.FromSeconds(30));
        executeRefresh.Start();

        // Said once per character per game. Four separate attempts to work out why one sorceress
        // never attacked were all guesses about skills nobody had looked at.
        Log.Information("{Character} is a {Class} lvl {Level}: orb {Orb}, blizzard {Blizzard}, glacial {Glacial}, iceblast {IceBlast}, nova {Nova}, static {Static}, frost nova {FrostNova}, lightning mastery {LightningMastery}, teleport {Teleport}, mana {Mana}/{MaxMana}",
            client.Game.Me.Name, client.Game.Me.Class, client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.Level],
            client.Game.Me.Skills.GetValueOrDefault(Skill.FrozenOrb),
            client.Game.Me.Skills.GetValueOrDefault(Skill.Blizzard),
            client.Game.Me.Skills.GetValueOrDefault(Skill.GlacialSpike),
            client.Game.Me.Skills.GetValueOrDefault(Skill.IceBlast),
            client.Game.Me.Skills.GetValueOrDefault(Skill.Nova),
            client.Game.Me.Skills.GetValueOrDefault(Skill.StaticField),
            client.Game.Me.Skills.GetValueOrDefault(Skill.FrostNova),
            client.Game.Me.Skills.GetValueOrDefault(Skill.LightningMastery),
            client.Game.Me.Skills.GetValueOrDefault(Skill.Teleport),
            client.Game.Me.Mana, client.Game.Me.MaxMana);

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

        LogExperience();

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

        LogExperience();
    }

    private async Task StaticSorcClient(Client client, AccountConfig account, CowManager cowManager)
    {
        Log.Information($"Starting Sorc Client {client.Game.Me.Name}");
        await cowManager.EnsureSweep(client.Game, client.Game.Me.Location);
        var fasterCastRate = client.Game.Items.Values
            .Where(i => i.Action == D2NG.Core.D2GS.Items.Action.Equip && i.EntityType == EntityType.Player && i.PlayerId == client.Game.Me.Id)
            .Sum(i => i.GetValueOfStatType(StatType.FasterCastRate));
        // A little over the animation, so a cast never lands while the last one is still going.
        var castInterval = SorceressCastTiming.CastTime(fasterCastRate) + TimeSpan.FromMilliseconds(40);
        Log.Information("{Character} casts every {Interval:F0}ms at {Fcr}% faster cast rate", client.Game.Me.Name, castInterval.TotalMilliseconds, fasterCastRate);
        using var rotation = new CastRotation(castInterval, skill => client.Game.UseRightHandSkillOnLocation(skill, client.Game.Me.Location));
        var executeStaticField = rotation.Add(Skill.StaticField);
        var executeNova = rotation.Add(Skill.Nova);

        var clusterStopWatch = new Stopwatch();
        var missingBoStopWatch = new Stopwatch();

        var random = new Random();

        // In active mode the lead deliberately walks into monsters, so rescuing it would pull the
        // sorceresses off their clusters for the rest of the run.
        var rescueLead = !cowManager.ActiveMode;
        var isPortalCharacter = _cowconfig.PortalCharacterName.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase);
        var sweepProgress = 0;
        MonsterCluster currentCluster = null;
        Point currentTarget = null;
        var rotationChoice = RotationChoice.Nothing;
        var lastFrostNova = DateTime.MinValue;
        var lastEscape = DateTime.MinValue;
        var recentEscapes = new Queue<DateTime>();
        var lifeSamples = new Queue<(DateTime At, int Life)>();
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !IsRunOver(cowManager))
        {
            // The level is cleared and the game only waits for the loot. Whatever still lives here
            // is what nobody can or will kill, and fighting it first meant a lightning enchanted cow
            // nearby had every sorceress give up a cluster three hundred times in thirty seconds
            // while the last drops stayed on the ground.
            if (cowManager.IsFinished())
            {
                executeStaticField.Stop();
                executeNova.Stop();
                var here = client.Game.Me.Location;
                var leftover = PickitItemsLeft()
                    .Where(i => InThisLevel(client, i) && client.Game.Inventory.FindFreeSpace(i) != null)
                    .OrderBy(i => i.Location.Distance(here))
                    .FirstOrDefault();
                if (leftover != null && here.Distance(leftover.Location) > TeleportPickupRadius)
                {
                    var leftoverPath = await _pathingService.GetPathToLocation(client.Game, leftover.Location, MovementMode.Teleport);
                    if (leftoverPath.Count > 0)
                    {
                        await MovementHelpers.TakePathOfLocations(client.Game, leftoverPath, MovementMode.Teleport);
                    }
                    else if (_unreachable.TryAdd(leftover.Id, 0))
                    {
                        Log.Warning("{Character} found no teleport path from {From} to leftover {Item} at {Location}, {Distance:F0} away",
                            client.Game.Me.Name, here, leftover.Name, leftover.Location, here.Distance(leftover.Location));
                    }
                }

                await PickupItemsAndPotions(client, account, TeleportPickupRadius, stop: GameOver);
                if (leftover != null && PickitItemsLeft().Any(i => i.Id == leftover.Id)
                    && client.Game.Me.Location.Distance(leftover.Location) <= TeleportPickupRadius
                    && _sweepReported.TryAdd((client.Game.Me.Name, leftover.Id), 0))
                {
                    Log.Warning("{Character} at {From} is within reach of leftover {Item} at {Location}, {Distance:F0} away, and did not pick it up",
                        client.Game.Me.Name, client.Game.Me.Location, leftover.Name, leftover.Location, client.Game.Me.Location.Distance(leftover.Location));
                }

                continue;
            }

            // Two lists on purpose. What is dangerous is everything nearby; what is worth casting at
            // is only what this character can hurt. Running the life and lightning-enchanted checks
            // off the filtered list meant the monsters she cannot damage - the ones she cannot
            // shift and so is stuck next to - were invisible to both.
            var nearbyMonsters = cowManager.GetNearbyAliveMonsters(client, 30.0, 10);
            var nearbyAliveCows = HarmableNearby(client, cowManager, 30.0, 10);

            // First, before the portal, the rescue, the battle orders trip, the lightning enchanted and
            // cluster branches, and whatever the pack's size: each of those ran first and let a wave
            // stay on her - the battle orders trip for up to a minute.
            var lifeLost = LifeLostLastSecond(client, lifeSamples);
            var onHer = CowsOnHer(client, cowManager, SorceressRotation.AttackReach(castInterval));
            var escapeInput = new RotationInput
            {
                CowsWithinReach = onHer.Count,
                ChilledWithinReach = onHer.Count(cow => cow.Effects.Contains(EntityEffect.Cold)),
                LifeFraction = client.Game.Me.MaxLife > 0 ? (double)client.Game.Me.Life / client.Game.Me.MaxLife : 1,
                LifeLostLastSecond = lifeLost,
                SinceEscape = DateTime.Now - lastEscape,
            };
            if (SorceressRotation.ShouldEscape(escapeInput))
            {
                executeStaticField.Stop();
                executeNova.Stop();
                if (await Escape(client, cowManager, onHer, escapeInput, castInterval, StaticRangeOf(client), recentEscapes))
                {
                    lastEscape = DateTime.Now;
                }

                continue;
            }

            // At half life with anything near, the same search as an escape, and no more often: the safe
            // spot finder this used counted only what stood within five of a spot, and every tick it could
            // put her back towards a wave she had escaped a moment before.
            var lowLife = escapeInput.LifeFraction <= 0.5;
            if (lowLife && nearbyMonsters.Count != 0 && escapeInput.SinceEscape >= SorceressRotation.EscapeGap)
            {
                executeStaticField.Stop();
                executeNova.Stop();
                if (await Escape(client, cowManager, onHer, escapeInput, castInterval, StaticRangeOf(client), recentEscapes))
                {
                    lastEscape = DateTime.Now;
                }
            }

            if (_safePortalWanted && isPortalCharacter && await TryOpenSafePortal(client, cowManager, () => { executeStaticField.Stop(); executeNova.Stop(); }))
            {
                continue;
            }

            // In town his last known position is still the spot he fled, so rescuing or chasing him
            // for battle orders would teleport straight into the pack he left.
            var leadPlayer = LeadPlayer(client);
            // Back from a rejoin without battle orders: no rescue and no cluster until she has them.
            var battleOrders = BattleOrdersStanceOf(client);
            if (battleOrders == BattleOrdersStance.Hold)
            {
                // Where she is, never back to a spot she escaped; static at what is on her.
                executeNova.Stop();
                if (onHer.Count > 0 && client.Game.Me.Mana >= SorceressRotation.StaticManaCost)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }
                else
                {
                    executeStaticField.Stop();
                }

                continue;
            }

            var cowsNearLead = rescueLead && leadPlayer != null && battleOrders == BattleOrdersStance.Fight
                ? cowManager.GetNearbyAliveMonsters(leadPlayer.Location, 20.0, 10)
                : [];
            if (rescueLead && leadPlayer != null && leadPlayer.Location != currentTarget && cowsNearLead.Count != 0 && leadPlayer.Location.Distance(client.Game.Me.Location) > 20
                && !cowsNearLead.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)))
            {
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

            // Without him, or outside his party where his shouts do not reach, there is nobody to go
            // to, so fight on unbuffed until he is back.
            // Not with a wave on her or at half life: the trip is through whatever stands between.
            if (leadPlayer != null && client.Game.IsInPartyWith(leadPlayer) && !client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders)
                && onHer.Count < SorceressRotation.BattleOrdersTripMaxCowsOnHer && !lowLife)
            {
                Log.Information($"Lost bo on client {client.Game.Me.Name}, moving to barb for bo");
                executeStaticField.Stop();
                executeNova.Stop();
                if (battleOrders == BattleOrdersStance.GoToLead)
                {
                    // Back from a rejoin: a path to within reach of him first - one teleport does not cross
                    // the level - then beside him, but not into the pack he may be standing in.
                    if (leadPlayer.Location.Distance(client.Game.Me.Location) > BattleOrdersApproach)
                    {
                        var approach = client.Game.Me.Location.GetPointBeforePointInSameDirection(leadPlayer.Location, BattleOrdersApproach);
                        var approachPath = await _pathingService.GetPathToLocation(client.Game, approach, MovementMode.Teleport);
                        if (approachPath.Count > 0)
                        {
                            await MovementHelpers.TakePathOfLocations(client.Game, approachPath, MovementMode.Teleport);
                        }
                    }

                    await TeleportToNearbySafeSpot(client, cowManager, leadPlayer.Location, 0, 8);
                    continue;
                }
                else if (leadPlayer != null)
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

            // Still waiting for battle orders with no safe way to him: she defends herself and takes no cluster.
            if (battleOrders == BattleOrdersStance.GoToLead)
            {
                executeNova.Stop();
                if (onHer.Count > 0 && client.Game.Me.Mana >= SorceressRotation.StaticManaCost)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }

                continue;
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
            }

            var lightningEnhancedCows = nearbyMonsters.Any(c => c.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted))
                || CowsOnHer(client, cowManager, 30).Any(KeptAway);
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
                var here = client.Game.Me.Location;
                var pack = HarmableNearby(client, cowManager, 30.0, int.MaxValue);
                if (pack.Count == 0)
                {
                    pack = nearbyAliveCows;
                }

                var nearestAlive = pack[0];
                var view = RotationView.Of(client, pack, here, StaticRangeOf(client), castInterval);
                // The escape was judged above, on every monster in reach; the rotation decides the rest.
                var input = view.Input(client, rotationChoice, DateTime.Now - lastFrostNova, castInterval) with
                {
                    LifeLostLastSecond = lifeLost,
                    SinceEscape = TimeSpan.Zero,
                };
                var choice = SorceressRotation.Decide(input);
                if (choice != rotationChoice)
                {
                    Log.Information("{Character} rotation {From} -> {To}: {Pack} cows, {Static} in static range, {Nova} in nova radius, {Reach} within reach, nearest {Nearest:F1}, mana {Mana}",
                        client.Game.Me.Name, rotationChoice, choice, view.PackAlive, view.StaticLives.Count, view.NovaLives.Count, view.WithinReach, view.Nearest, client.Game.Me.Mana);
                    if (choice != RotationChoice.FrostNova)
                    {
                        TraceRotation(client, choice, view);
                    }

                    rotationChoice = choice;
                }

                var inSight = await _attackService.IsInLineOfSight(client, nearestAlive.Location);
                if (inSight && choice == RotationChoice.FrostNova)
                {
                    executeStaticField.Stop();
                    executeNova.Stop();
                    client.Game.UseRightHandSkillOnLocation(Skill.FrostNova, client.Game.Me.Location);
                    lastFrostNova = DateTime.Now;
                    TraceRotation(client, choice, view);
                    continue;
                }

                if (inSight && choice == RotationChoice.Nova)
                {
                    executeStaticField.Stop();
                    client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                    executeNova.Start();
                    continue;
                }

                if (inSight && choice == RotationChoice.Static)
                {
                    executeNova.Stop();
                    client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                    executeStaticField.Start();
                    continue;
                }

                executeStaticField.Stop();
                executeNova.Stop();
                var moved = (choice == RotationChoice.Approach || !inSight || view.Nearest > view.StaticRange)
                    && await TeleportTowardsPack(client, cowManager, pack, choice == RotationChoice.Approach ? SorceressRotation.NovaRadius : view.StaticRange, castInterval);

                // A tick that neither moved her nor cast is time the pack is not being hurt.
                if (!moved && input.HasStatic && client.Game.Me.Mana >= SorceressRotation.StaticManaCost
                    && view.StaticLives.Any(life => SorceressRotation.StaticDamage(life, view.StaticFloor) > 0))
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                    executeStaticField.Start();
                }
                else if (!moved && view.NovaLives.Count > 0 && client.Game.Me.Mana >= input.NovaManaCost)
                {
                    client.Game.UseRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                    executeNova.Start();
                }

                continue;
            }

            executeStaticField.Stop();
            executeNova.Stop();
            // Giving up a lightning-enchanted pack used to end here too, and the pickup teleported her
            // onto its fresh drops while the others' novas set off its charged bolts: 2513 life to 0 in
            // under 0.4s. The end of level sweep collects them once the pack is dead.
            if (!lightningEnhancedCows)
            {
                await PickupItemsAndPotions(client, account, TeleportPickupRadius, stop: GameOver);
            }

            sweepProgress = Math.Max(sweepProgress, currentCluster?.SweepIndex ?? 0);
            cowManager.ReleaseCluster(currentCluster);
            // Anchored on the walking party. The killers clear one area around them together, so
            // the soul packs that open up are ones the party is already standing near instead of
            // whichever distant pack happened to be uncovered first.
            currentCluster = cowManager.ClaimNextCluster(client, sweepProgress, HuntingPartyAnchor(client), canHarm: CanHarmMonster(client));
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
                var strays = HarmableNearby(client, cowManager, StraySearchRadius, 10);
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

                await PickupItemsAndPotions(client, account, TeleportPickupRadius, stop: GameOver);

                // Nothing to kill and nothing to collect where it stands. Standing still until the
                // party finishes wastes the fastest mover in the group: trailing them puts it where
                // the next drops and the next strays will be, and keeps it close enough to collect
                // the moment something falls.
                if (NearestPickitLocation(client, client.Game.Me.Location) == null
                    && partyAt != client.Game.Me.Location
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

        // Left claimed, nobody else would ever be offered it.
        if (!client.Game.IsInGame() && currentCluster != null)
        {
            cowManager.GiveUpCluster(currentCluster);
        }

        Log.Information($"Stopped Sorc Client {client.Game.Me?.Name}, {cowManager.DescribeProgress()}, finished is {cowManager.IsFinished()}, next game set {NextGame.Task.IsCompleted}, in game {client.Game.IsInGame()}");
        executeStaticField.Stop();
        executeNova.Stop();
        EndGameWithLoop(client);
    }

    /// <summary>What a sorceress sees of the pack for the rotation, counted at the cows' current positions.</summary>
    private sealed record RotationView(
        List<double> StaticLives,
        List<double> NovaLives,
        int InFrostNovaRadius,
        int Chilled,
        int WithinReach,
        int ChilledWithinReach,
        int PackAlive,
        int BelowStaticFloor,
        double Nearest,
        double StaticRange,
        double StaticFloor)
    {
        public static RotationView Of(Client client, List<AliveMonster> pack, Point here, double staticRange, TimeSpan castTime)
        {
            var floor = StaticFloorOf(client);
            var reach = SorceressRotation.AttackReach(castTime);
            var staticLives = new List<double>();
            var novaLives = new List<double>();
            var inFrostNova = 0;
            var chilled = 0;
            var withinReach = 0;
            var chilledWithinReach = 0;
            var nearest = double.MaxValue;
            foreach (var cow in pack)
            {
                var distance = cow.Location.Distance(here);
                nearest = Math.Min(nearest, distance);
                if (distance <= staticRange)
                {
                    staticLives.Add(cow.LifePercentage);
                }

                if (distance <= SorceressRotation.NovaRadius)
                {
                    novaLives.Add(cow.LifePercentage);
                }

                var isChilled = distance <= SorceressRotation.FrostNovaRadius
                    && client.Game.WorldObjects.TryGetValue((cow.Id, EntityType.NPC), out var seen) && seen.Effects.Contains(EntityEffect.Cold);
                if (distance <= reach)
                {
                    withinReach++;
                    if (isChilled)
                    {
                        chilledWithinReach++;
                    }
                }

                if (distance <= SorceressRotation.FrostNovaRadius)
                {
                    inFrostNova++;
                    if (isChilled)
                    {
                        chilled++;
                    }
                }
            }

            return new RotationView(staticLives, novaLives, inFrostNova, chilled, withinReach, chilledWithinReach, pack.Count,
                staticLives.Count(life => SorceressRotation.StaticDamage(life, floor) <= 0), nearest, staticRange, floor);
        }

        public RotationInput Input(Client client, RotationChoice current, TimeSpan sinceFrostNova, TimeSpan castTime)
        {
            var me = client.Game.Me;
            var novaLevel = SkillLevel(client, Skill.Nova);
            return new RotationInput
            {
                StaticRangeLives = StaticLives,
                NovaRadiusLives = NovaLives,
                StaticFloor = StaticFloor,
                NovaDamagePercent = SorceressRotation.NovaDamagePercent(novaLevel, SkillLevel(client, Skill.LightningMastery), client.Game.Players.Count),
                Mana = me.Mana,
                MaxMana = me.MaxMana,
                NovaManaCost = SorceressRotation.NovaManaCost(novaLevel),
                FrostNovaManaCost = SorceressRotation.FrostNovaManaCost(SkillLevel(client, Skill.FrostNova)),
                Classic = !client.Game.ClientCharacter.IsExpansion,
                HasFrostNova = me.HasSkill(Skill.FrostNova),
                HasStatic = me.HasSkill(Skill.StaticField) && StaticRange > 0,
                CowsWithinReach = WithinReach,
                ChilledWithinReach = ChilledWithinReach,
                CowsInFrostNovaRadius = InFrostNovaRadius,
                ChilledInFrostNovaRadius = Chilled,
                PackAlive = PackAlive,
                SinceFrostNova = sinceFrostNova,
                Current = current,
                Nearest = Nearest,
                CastTime = castTime,
            };
        }
    }

    /// <summary>The life static takes nothing below; the same line <see cref="ClassHelpers.CanStaticEntity"/> draws.</summary>
    private static double StaticFloorOf(Client client) => client.Game.ClientCharacter.IsExpansion ? 60 : 0;

    private static void TraceRotation(Client client, RotationChoice choice, RotationView view)
        => TraceEvents.Decision(client.Game, "sorc-rotation",
            ("choice", choice.ToString()),
            ("cowsInStaticRange", view.StaticLives.Count),
            ("cowsInNovaRadius", view.NovaLives.Count),
            ("cowsInFrostNovaRadius", view.InFrostNovaRadius),
            ("cowsWithinReach", view.WithinReach),
            ("nearest", Math.Round(view.Nearest, 1)),
            ("packAlive", view.PackAlive),
            ("belowStaticFloor", view.BelowStaticFloor),
            ("mana", client.Game.Me.Mana),
            ("chilled", view.Chilled));

    /// <summary>
    /// Teleports to where the most live cows fall inside <paramref name="radius"/> with the nearest
    /// monster still out of reach and no lightning enchanted monster or the Cow King near, falling
    /// back to ten short of the nearest cow, or onto it when that point is out of sight. Says whether
    /// a teleport was sent.
    /// </summary>
    private async Task<bool> TeleportTowardsPack(Client client, CowManager cowManager, List<AliveMonster> pack, double radius, TimeSpan castTime)
    {
        var here = client.Game.Me.Location;
        var map = cowManager.LevelMap;
        var dangers = SpotDangers(client, cowManager, here);
        var nearby = HarmableNearby(client, cowManager, SpotSearchRadius, int.MaxValue)
            .Where(cow => !cow.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted))
            .ToList();
        var choice = TeleportSpot.Choose(new SpotSearch
        {
            From = here,
            Cows = (nearby.Count > 0 ? nearby : pack).Select(cow => new NearbyCow(cow.Location, cow.ClusterId)).ToList(),
            Monsters = cowManager.GetNearbyAliveMonsters(here, SpotSearchRadius + TeleportSpot.MaxHop, int.MaxValue).Select(monster => monster.Location).ToList(),
            Dangers = dangers,
            Radius = radius,
            Clearance = TeleportSpot.Clearance(castTime),
            Walkable = point => map != null && LevelCell(map, point) is int cell && AreaMapExtensions.IsMovable(cell),
            InSight = (from, to) => map != null && LevelInSight(map, from, to),
        });

        if (choice != null && choice.Hop < MinimumHop)
        {
            return false;
        }

        if (choice != null)
        {
            bool moved;
            if (choice.Hop <= TeleportSpot.MaxHop)
            {
                moved = await client.Game.TeleportToLocationAsync(choice.Spot, MinimumHop);
            }
            else
            {
                var path = await _pathingService.GetPathToLocation(client.Game, choice.Spot, MovementMode.Teleport);
                moved = path.Count > 0 && await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Teleport);
            }

            TraceEvents.Decision(client.Game, "sorc-teleport-spot",
                ("cowsInRange", choice.CowsInRange),
                ("packsInRange", choice.PacksInRange),
                ("nearest", Math.Round(choice.Nearest, 1)),
                ("hop", Math.Round(choice.Hop, 1)),
                ("radius", Math.Round(radius, 1)),
                ("packAlive", pack.Count),
                ("dangers", dangers.Count),
                ("moved", moved));
            return moved;
        }

        var nearestAlive = pack[0];
        if (here.Distance(nearestAlive.Location) <= 15 && await _attackService.IsInLineOfSight(client, here, nearestAlive.Location))
        {
            return false;
        }

        if (dangers.Any(danger => danger.Distance(nearestAlive.Location) < TeleportSpot.DangerDistance))
        {
            return false;
        }

        Log.Information($"teleporting nearby with {client.Game.Me.Name} with distance {here.Distance(nearestAlive.Location)}");
        var destination = nearestAlive.Location.GetPointBeforePointInSameDirection(here, 10);
        if (!await _attackService.IsInLineOfSight(client, destination, nearestAlive.Location))
        {
            Log.Information($"Not in sight, so teleporting directly {client.Game.Me.Name}");
            destination = nearestAlive.Location;
        }

        var fellBack = here.Distance(destination) >= MinimumHop && await client.Game.TeleportToLocationAsync(destination, MinimumHop);
        TraceEvents.Decision(client.Game, "sorc-teleport-spot",
            ("cowsInRange", pack.Count(cow => cow.Location.Distance(destination) <= radius)),
            ("nearest", Math.Round(nearestAlive.Location.Distance(destination), 1)),
            ("hop", Math.Round(here.Distance(destination), 1)),
            ("radius", Math.Round(radius, 1)),
            ("packAlive", pack.Count),
            ("dangers", dangers.Count),
            ("moved", fellBack),
            ("fallback", true));
        return fellBack;
    }

    /// <summary>Life lost over the last second as a share of maximum, from the samples the loop keeps.</summary>
    private static double LifeLostLastSecond(Client client, Queue<(DateTime At, int Life)> samples)
    {
        var me = client.Game.Me;
        var now = DateTime.Now;
        samples.Enqueue((now, me.Life));
        while (samples.Count > 0 && now - samples.Peek().At > TimeSpan.FromSeconds(1))
        {
            samples.Dequeue();
        }

        var highest = samples.Max(sample => sample.Life);
        return me.MaxLife > 0 ? Math.Max(0, highest - me.Life) / (double)me.MaxLife : 0;
    }

    /// <summary>Hostile monsters within <paramref name="radius"/> of her, by her own client's view; see <see cref="HostilesAround"/>.</summary>
    private List<WorldObject> CowsOnHer(Client client, CowManager cowManager, double radius)
        => HostilesAround(client, client.Game.Me.Location, radius);

    private List<WorldObject> LiveCowsAround(Client client, CowManager cowManager, Point center, double radius)
        => HostilesAround(client, center, radius);

    /// <summary>
    /// Every live hostile monster within <paramref name="radius"/> of a point, from this client's own world
    /// objects at their current positions. Not the manager's list: in passive mode it leaves out the Cow
    /// King and his guards, and a danger check built on it is blind to the pack that killed the amazon.
    /// </summary>
    private List<WorldObject> HostilesAround(Client client, Point center, double radius)
    {
        var around = new List<WorldObject>();
        if (center == null)
        {
            return around;
        }

        foreach (var unit in client.Game.WorldObjects.Values)
        {
            if (unit.Type == EntityType.NPC && !MonsterTracking.IsDead(unit.State) && unit.Location is { } at && at.Distance(center) <= radius
                && !NPCHelpers.IsFriendly(unit.NPCCode) && !_party.Any(member => member.Game.Me?.MercId == unit.Id))
            {
                around.Add(unit);
            }
        }

        return around;
    }

    /// <summary>
    /// Out from under a wave, within one teleport: clear of the nearest monster by what the cows walk
    /// before the next escape could be judged, away from the cows that were on her, keeping what cows it
    /// can in static range so she casts on from there; never towards anything lightning enchanted. The
    /// second escape within <see cref="SorceressRotation.RepeatEscapeWindow"/> is one long hop to where
    /// nothing is near instead. Says whether she moved.
    /// </summary>
    private async Task<bool> Escape(Client client, CowManager cowManager, List<WorldObject> onHer, RotationInput judged, TimeSpan castTime, double staticRange, Queue<DateTime> recentEscapes)
    {
        var here = client.Game.Me.Location;
        var now = DateTime.Now;
        while (recentEscapes.Count > 0 && now - recentEscapes.Peek() > SorceressRotation.RepeatEscapeWindow)
        {
            recentEscapes.Dequeue();
        }

        var longHop = recentEscapes.Count >= SorceressRotation.RepeatEscapes - 1;
        var map = cowManager.LevelMap;
        var search = new SpotSearch
        {
            From = here,
            Cows = HarmableNearby(client, cowManager, SpotSearchRadius, int.MaxValue)
                .Where(cow => !cow.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted))
                .Select(cow => new NearbyCow(cow.Location, cow.ClusterId))
                .ToList(),
            Monsters = cowManager.GetNearbyAliveMonsters(here, SpotSearchRadius + TeleportSpot.MaxHop, int.MaxValue).Select(monster => monster.Location).ToList(),
            Dangers = SpotDangers(client, cowManager, here),
            Radius = staticRange,
            Clearance = SorceressRotation.EscapeClearance(castTime),
            Walkable = point => map != null && LevelCell(map, point) is int cell && AreaMapExtensions.IsMovable(cell),
        };
        var awayFrom = onHer.Count > 0 ? MonsterTracking.Centroid(onHer.Select(cow => cow.Location).ToList(), here) : here;
        var kind = longHop ? "long" : "escape";
        var choice = longHop ? null : TeleportSpot.ChooseEscape(search, awayFrom);
        if (choice == null)
        {
            choice = TeleportSpot.ChooseLongHop(search, awayFrom);
            kind = "long";
        }

        // Neither exists with a wave and a pack on either side, which is when she must move most.
        if (choice == null)
        {
            choice = TeleportSpot.ChooseLeastBad(search);
            kind = "least bad";
        }

        Point spot = choice?.Spot;
        if (spot == null)
        {
            spot = await _attackService.FindNearbySafeSpot(client, search.Monsters.ToList(), here, 15.0);
            kind = "safe spot";
        }

        var moved = false;
        if (spot != null && here.Distance(spot) >= MinimumHop)
        {
            var sent = await client.Game.TeleportToLocationAsync(spot, MinimumHop);
            // The server can place her on the spot in the same moment the cast is judged a failure.
            moved = sent || client.Game.Me.Location.Distance(spot) < MinimumHop
                || client.Game.ServerConfirmedLocation?.Distance(spot) < MinimumHop;
        }

        // Only an escape that moved her counts towards the long hop and the gap; one that found nowhere
        // to go is tried again on the next tick.
        if (moved)
        {
            recentEscapes.Enqueue(now);
        }

        Log.Information("{Character} escaping {Reach} cows within reach, {Chilled} chilled, at {Life} of {MaxLife} life, {Kind} hop {Hop:F1}, {Moved}",
            client.Game.Me.Name, judged.CowsWithinReach, judged.ChilledWithinReach, client.Game.Me.Life, client.Game.Me.MaxLife,
            spot == null ? "no" : kind, spot == null ? 0 : here.Distance(spot), moved ? "moved" : "did not move");
        TraceEvents.Decision(client.Game, "sorc-escape",
            ("cowsWithinReach", judged.CowsWithinReach),
            ("chilledWithinReach", judged.ChilledWithinReach),
            ("life", client.Game.Me.Life),
            ("lifeLostLastSecond", Math.Round(judged.LifeLostLastSecond, 2)),
            ("kind", spot == null ? "none" : kind),
            ("hop", spot == null ? null : Math.Round(here.Distance(spot), 1)),
            ("nearestAtLanding", choice == null ? null : Math.Round(choice.Nearest, 1)),
            ("cowsInStaticRange", choice?.CowsInRange),
            ("moved", moved));
        return moved;
    }

    /// <summary>How near the barbarian a rejoined sorceress paths before the last hop beside him.</summary>
    private const double BattleOrdersApproach = 15;

    /// <summary>Shorter hops are not worth a cast; teleport's own default minimum of ten swallowed every hop under it.</summary>
    private const double MinimumHop = 3;

    /// <summary>
    /// Lightning enchanted monsters and the Cow King near enough to matter for a landing. The King and his
    /// guards are not tracked outside active mode, so they are read from the client's own world objects.
    /// </summary>
    private static List<Point> SpotDangers(Client client, CowManager cowManager, Point here)
    {
        var reach = SpotSearchRadius + TeleportSpot.MaxHop + TeleportSpot.DangerDistance;
        var dangers = cowManager.GetNearbyAliveMonsters(here, reach, int.MaxValue)
            .Where(monster => monster.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted))
            .Select(monster => monster.Location)
            .ToList();
        foreach (var unit in client.Game.WorldObjects.Values)
        {
            if (unit.Type == EntityType.NPC && !MonsterTracking.IsDead(unit.State) && unit.Location is { } location
                && location.Distance(here) <= reach
                && (unit.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)
                    || (unit.NPCCode == NPCCode.HellBovine && unit.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique))))
            {
                dangers.Add(location);
            }
        }

        return dangers;
    }

    /// <summary>How far around her the cows a landing spot is scored on are counted, neighbouring packs included.</summary>
    private const double SpotSearchRadius = 60;

    private static int? LevelCell(AreaMap map, Point point)
    {
        var row = point.Y - map.LevelOrigin.Y;
        var column = point.X - map.LevelOrigin.X;
        return row < 0 || row >= map.Map.Length || column < 0 || column >= map.Map[row].Length ? null : map.Map[row][column];
    }

    /// <summary>The same cell test as <see cref="IAttackService.IsInLineOfSight(Client, Point, Point)"/>, against the level already loaded.</summary>
    private static bool LevelInSight(AreaMap map, Point from, Point to)
    {
        foreach (var point in AttackService.GetPointsOnLine(from.X, from.Y, to.X, to.Y))
        {
            if (LevelCell(map, point) is not int cell || (!AreaMapExtensions.IsMovable(cell) && cell != 1))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> TeleportToNearbySafeSpot(Client client, CowManager cowManager, Point toLocation, double minDistance = 0, double maxDistance = 30)
    {
        var nearbyMonsters = cowManager.GetNearbyAliveMonsters(toLocation, 30.0, 100).Select(p => p.Location).ToList();
        return await _attackService.MoveToNearbySafeSpot(client, nearbyMonsters, toLocation, MovementMode.Teleport, minDistance, maxDistance);
    }

    private async Task BasicIdleClient(Client client, CowManager cowManager)
    {
        Log.Information($"Starting Basic idle Client {client.Game.Me.Name}");
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), NextGame.Task) && client.Game.IsInGame() && !IsRunOver(cowManager))
        {
        }

        Log.Information($"Stopped Idle Client {client.Game.Me.Name}");
    }

    private async Task BasicFollowClient(Client client, AccountConfig account, CowManager cowManager)
    {
        var timer = new Stopwatch();
        timer.Start();
        var random = new Random();
        var kite = new KiteState();
        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !IsRunOver(cowManager))
        {
            var leadPlayer = LeadPlayer(client);
            if (await RetreatIfHurtByPortal(client, cowManager))
            {
                continue;
            }

            var weak = IsWeakWalker(client);
            var keepDistance = weak && Kiting.AttacksFromRange(client.Game.Me.Class) ? Kiting.KeepDistance : (double?)null;

            // Attack and run: a short step back as soon as the cows close in, then straight back to
            // attacking from there - at what is around itself, not around the lead. With no step to take
            // it attacks from where it stands rather than doing nothing.
            var kited = weak ? await StepBack(client, cowManager, leadPlayer, kite) : KiteResult.None;
            if (kited == KiteResult.Left)
            {
                continue;
            }

            if (kited == KiteResult.Stepped)
            {
                var self = SelfPlayer(client);
                if (self?.Location != null && client.Game.IsInGame())
                {
                    await _attackService.AssistPlayer(client, self, PriorityCodes(client, cowManager), keepDistance: keepDistance, whirlOut: true, cancellation: GameOverToken);
                }

                continue;
            }

            // Back from a rejoin without battle orders: to the barbarian first, fighting only what is on it.
            var battleOrders = BattleOrdersStanceOf(client);
            if (battleOrders != BattleOrdersStance.Fight)
            {
                await WaitForBattleOrders(client, cowManager, leadPlayer, battleOrders, keepDistance);
                continue;
            }

            // A weak walker's follow stops at shooting range from a pack the lead stands in, and further
            // from anything lightning enchanted.
            List<WorldObject> nearLeadCows = weak && leadPlayer?.Location != null
                ? LiveCowsAround(client, cowManager, leadPlayer.Location, Kiting.LightningEnchantedDistance + Kiting.LightningEnchantedMargin + 5)
                : [];
            var followTo = weak && leadPlayer?.Location != null
                ? Kiting.FollowDestination(leadPlayer.Location, client.Game.Me.Location,
                    nearLeadCows.Select(cow => cow.Location).ToList(),
                    nearLeadCows.Where(KeptAway).Select(cow => cow.Location).ToList())
                : null;
            await FollowLead(client, leadPlayer?.Location, cowManager, followTo == leadPlayer?.Location ? null : followTo);

            // The loop looks once a pass; a pass begun as the game ended went on to walk to loot and to the lead.
            if (NextGame.Task.IsCompleted)
            {
                break;
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
                if (cowManager.GetNearbyAliveMonsters(client, SelfDefenceRange, 1).Count != 0 && SelfPlayer(client)?.Location != null)
                {
                    await _attackService.AssistPlayer(client, SelfPlayer(client), PriorityCodes(client, cowManager), keepDistance: keepDistance, whirlOut: true, cancellation: GameOverToken);
                }

                continue;
            }

            var canCatchUp = client.Game.Me.HasSkill(Skill.Teleport);
            var nearLead = canCatchUp
                || leadPlayer?.Location == null
                || leadPlayer.Location.Distance(client.Game.Me.Location) < StragglerDistance;
            // Nothing alive near the group either, not just near this character. A teleporting
            // client loots out to a hundred units, and testing only its own surroundings let it
            // wander away from the fight - far enough that the monsters left its view entirely, so
            // it found no enemies to assist against and spent whole games collecting. One of ours
            // logged two hundred and forty six pickups and not a single attack.
            var partyFighting = leadPlayer != null
                && cowManager.GetNearbyAliveMonsters(leadPlayer.Location, EscortEngagementRange, 1).Count != 0;
            if (nearLead && !partyFighting && CowsOnHer(client, cowManager, 20).Count == 0)
            {
                // A walker only fetches what is lying around the group; anything further out is
                // left to whoever ends up nearest it.
                // Anchored on the party, for teleporters too. Letting them range a hundred units
                // from wherever they happened to stand meant drifting after loot until the fight
                // was out of view, and a character that cannot see the monsters finds no enemies to
                // assist against: one of ours logged nearly three hundred pickups and no attacks at
                // all. The wide radius stays - it is measured from the group instead.
                await PickupItemsAndPotions(client, account,
                    canCatchUp ? TeleportPickupRadius : HuntingPartyPickupRadius,
                    leadPlayer?.Location,
                    stop: GameOver);
            }

            if (!nearLead)
            {
                // A walker that has fallen behind catches up before it loots, but still hits back at
                // whatever is on it: a rejoined walker crosses the level alone.
                if (Threatened(client, cowManager) && SelfPlayer(client)?.Location != null)
                {
                    await _attackService.AssistPlayer(client, SelfPlayer(client), PriorityCodes(client, cowManager), keepDistance: keepDistance, whirlOut: true, cancellation: GameOverToken);
                }

                continue;
            }

            // Nor starts a fight in a game that is over: a walker whirling at strays held the party's leave.
            if (GameOver())
            {
                break;
            }

            // Without a lead to stand by, each fights around itself. A weak walker's targets leave out
            // nothing: it attacks from where kiting left it.
            var assistAnchor = (cowManager.ActiveMode && client.Game.Me.Class == CharacterClass.Barbarian
                ? GetProtectAnchor(client, cowManager)
                : leadPlayer) ?? SelfPlayer(client);
            if (assistAnchor?.Location != null)
            {
                await _attackService.AssistPlayer(client, assistAnchor, PriorityCodes(client, cowManager), keepDistance: keepDistance, whirlOut: true, cancellation: GameOverToken);
            }
        }
    }

    /// <summary>
    /// Whether a rejoined member may fight yet; see <see cref="BattleOrdersWait"/>. Its time counts only
    /// while it stands within the barbarian's shout reach; the wait ends for good once it has battle
    /// orders or has waited long enough.
    /// </summary>
    private BattleOrdersStance BattleOrdersStanceOf(Client client)
    {
        if (!_awaitingBattleOrders.TryGetValue(client, out var waiting))
        {
            return BattleOrdersStance.Fight;
        }

        var now = DateTime.Now;
        var lead = LeadPlayer(client);
        var leadPlaced = lead != null && !(_leadClient != null && IsDead(_leadClient));
        var inReach = leadPlaced && lead.Location.Distance(client.Game.Me.Location) < ShoutReach;
        if (inReach)
        {
            waiting.WaitedInShoutReach += now - waiting.LastLook;
        }

        waiting.LastLook = now;
        var hasBattleOrders = client.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders);
        var stance = BattleOrdersWait.Decide(new BattleOrdersSituation(
            Rejoined: true, hasBattleOrders, leadPlaced, leadPlaced && client.Game.IsInPartyWith(lead), waiting.WaitedInShoutReach, now - waiting.ArrivedAt));
        if (stance == BattleOrdersStance.Fight)
        {
            _awaitingBattleOrders.TryRemove(client, out _);
            Log.Information("{Character} {Outcome} {Seconds:F1}s after coming back", client.Game.Me.Name,
                hasBattleOrders ? "has battle orders" : "fights without battle orders", (now - waiting.ArrivedAt).TotalSeconds);
            TraceEvents.Decision(client.Game, "rejoin-battle-orders",
                ("battleOrders", hasBattleOrders),
                ("seconds", Math.Round((now - waiting.ArrivedAt).TotalSeconds, 1)),
                ("waitedInShoutReach", Math.Round(waiting.WaitedInShoutReach.TotalSeconds, 1)));
        }

        return stance;
    }

    /// <summary>The battle orders barbarian's shouts land within this of him (ClassHelpers' shout reach).</summary>
    private const double ShoutReach = 10;

    /// <summary>
    /// A walker waiting for battle orders: to the barbarian, stopping short of any pack he stands in, or
    /// where it is while he is away; and it hits only what is on it.
    /// </summary>
    private async Task WaitForBattleOrders(Client client, CowManager cowManager, Player lead, BattleOrdersStance stance, double? keepDistance)
    {
        if (stance == BattleOrdersStance.GoToLead && lead?.Location != null)
        {
            var nearLead = LiveCowsAround(client, cowManager, lead.Location, Kiting.LightningEnchantedDistance + Kiting.LightningEnchantedMargin + 5);
            var stop = Kiting.FollowDestination(lead.Location, client.Game.Me.Location,
                nearLead.Select(cow => cow.Location).ToList(),
                nearLead.Where(KeptAway).Select(cow => cow.Location).ToList());
            await FollowLead(client, lead.Location, cowManager, stop == lead.Location ? null : stop);
        }

        if (Threatened(client, cowManager) && SelfPlayer(client)?.Location != null)
        {
            await _attackService.AssistPlayer(client, SelfPlayer(client), PriorityCodes(client, cowManager), keepDistance: keepDistance, whirlOut: true, cancellation: GameOverToken);
        }
    }

    /// <summary>
    /// The battle orders barbarian closes on a party member missing his shouts that stopped just out of
    /// their reach - a walker waiting at shooting range from the pack he fights in - once every few
    /// seconds, never mid-whirl, then shouts.
    /// </summary>
    private async Task StepUpToShout(Client client)
    {
        var me = client.Game.Me;
        if (ClassHelpers.IsWhirling(client.Game.Players.FirstOrDefault(p => p.Id == me.Id) ?? me))
        {
            return;
        }

        var now = DateTime.Now;
        var waiting = client.Game.Players.FirstOrDefault(p => p.Id != me.Id && p.Location != null && client.Game.IsInPartyWith(p)
            && ClassHelpers.IsMissingShouts(p)
            && p.Location.Distance(me.Location) is >= ShoutReach and <= ShoutStepRange
            && now - _shoutStepAt.GetValueOrDefault(p.Id) > ShoutStepInterval);
        if (waiting == null)
        {
            return;
        }

        _shoutStepAt[waiting.Id] = now;
        await client.Game.MoveToAsync(me.Location.GetPointBeforePointInSameDirection(waiting.Location, ShoutStepClose));
        await ClassHelpers.CastAllShouts(client);
    }

    private const double ShoutStepRange = 15;
    private const double ShoutStepClose = 8;
    private static readonly TimeSpan ShoutStepInterval = TimeSpan.FromSeconds(5);

    /// <summary>A follow walk that ends short of the lead counts as there within this.</summary>
    private const double ArrivedAtStop = 3;

    /// <summary>One kiting walker's state across its loop.</summary>
    private sealed class KiteState
    {
        public DateTime LastLog = DateTime.MinValue;
        public int StepsSinceLog;
        public bool SurroundedSaid;
    }

    private enum KiteResult
    {
        None,
        Stepped,
        NoStep,
        Left,
    }

    private static readonly TimeSpan KiteLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>The party member kites; see <see cref="Kiting.IsWeakWalker"/>.</summary>
    private static bool IsWeakWalker(Client client)
    {
        var me = client.Game.Me;
        return me != null && Kiting.IsWeakWalker(me.Class, me.MaxLife, me.HasSkill(Skill.Whirlwind));
    }

    /// <summary>
    /// The cows within lightning enchanted distance of a walker, by its own client's view; with
    /// <paramref name="inSight"/> only those it can see, since a cow across a fence is no reason to leave.
    /// </summary>
    private List<CloseCow> CloseCows(Client client, CowManager cowManager, bool inSight = false)
    {
        var here = client.Game.Me.Location;
        var map = cowManager.LevelMap;
        return CowsOnHer(client, cowManager, Kiting.LightningEnchantedDistance)
            .Where(cow => !inSight || map == null || LevelInSight(map, here, cow.Location))
            .Select(cow => new CloseCow(cow.Location.Distance(here), Enchanted(cow), KeptAway(cow)))
            .ToList();
    }

    /// <summary>What a weak walker keeps <see cref="Kiting.LightningEnchantedDistance"/> from: anything lightning enchanted, and the Cow King.</summary>
    private static bool KeptAway(WorldObject monster)
        => monster.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted) || IsCowKing(monster);

    private static bool IsCowKing(WorldObject monster) => CowKing.IsCowKing(monster.NPCCode, monster.MonsterEnchantments, monster.IsBossMinion);

    /// <summary>A champion, a unique or anything else with enchantments: a cow to step back from even alone.</summary>
    private static bool Enchanted(WorldObject cow)
        => cow.IsBoss || cow.IsChampion || cow.MonsterEnchantments.Count > 0;

    /// <summary>
    /// The run of attack and run: when the cows close in, a short step back, by its own client's view of
    /// them; surrounded with no way out at all, a leave the way a chicken does.
    /// </summary>
    private async Task<KiteResult> StepBack(Client client, CowManager cowManager, Player lead, KiteState state)
    {
        var here = client.Game.Me.Location;
        var aroundCows = CloseCows(client, cowManager);
        if (!Kiting.ShouldStepBack(aroundCows))
        {
            state.SurroundedSaid = false;
            return KiteResult.None;
        }

        var close = CowsOnHer(client, cowManager, Kiting.CrowdRadius);
        var seen = CloseCows(client, cowManager, inSight: true);
        var map = cowManager.LevelMap;
        var nearby = CowsOnHer(client, cowManager, 30);
        var search = new StepSearch
        {
            From = here,
            Cows = nearby.Select(cow => cow.Location).ToList(),
            LightningEnchanted = nearby.Where(KeptAway).Select(cow => cow.Location).ToList(),
            Lead = lead?.Location,
            Walkable = point => map == null || (LevelCell(map, point) is int cell && AreaMapExtensions.IsMovable(cell)),
            InSight = (from, to) => map == null || LevelInSight(map, from, to),
        };
        var step = Kiting.ChooseStep(search);

        // At its danger level with no step to take - arriving into a pack beside the portal it came
        // through is the case - a portal within reach beats leaving the game. So it does a step into a
        // dead end: that one is how she came to stand against a ledge and leave anyway.
        if ((step == null || step.DeadEnd)
            && PortalRetreat.ShouldRetreat(Kiting.InDanger(seen), belowRetreatLine: false, hostileNear: true, NearestTownPortal(client).Distance)
            && await TryPortalRetreat(client, cowManager, $"{close.Count} cows on it beside a portal, {(step == null ? "no step" : "only a dead end")}"))
        {
            return KiteResult.Left;
        }

        var wayOut = step == null && Kiting.InDanger(seen) ? Kiting.WayOut(search) : null;
        if (step == null && Kiting.Surrounded(seen, wayOut: wayOut != null))
        {
            if (!state.SurroundedSaid)
            {
                state.SurroundedSaid = true;
                var reason = $"surrounded: {close.Count} cows close, no way out";
                Log.Warning("{Character} {Reason}", client.Game.Me.Name, reason);
                TraceEvents.Decision(client.Game, "weak-walker-surrounded",
                    ("cowsClose", close.Count),
                    ("enchantedClose", close.Count(Enchanted)),
                    ("life", client.Game.Me.Life));
                ChickenService.LeaveNow(client, $"{client.Game.Me.Name} {reason}");
            }

            return KiteResult.Left;
        }

        state.SurroundedSaid = false;
        var wayOutTaken = step == null && wayOut != null;
        step ??= wayOut;
        var notAttacking = Stopwatch.StartNew();
        var moved = false;
        if (step != null)
        {
            if (step.Long)
            {
                var path = await _pathingService.GetPathToLocation(client.Game, step.Spot, MovementMode.Walking);
                moved = path.Count > 0 && await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking);
            }
            else
            {
                // The move assumes arrival; the server's word, when it came during the step, decides.
                moved = await client.Game.MoveToAsync(step.Spot)
                    && !(client.Game.ServerConfirmedAge <= notAttacking.Elapsed && client.Game.ServerConfirmedLocation?.Distance(step.Spot) > MinimumHop);
            }
        }

        notAttacking.Stop();
        var nearest = close.Count == 0 ? 0 : close.Min(cow => cow.Location.Distance(here));
        TraceEvents.Decision(client.Game, "kite-step",
            ("cowsClose", close.Count),
            ("enchantedClose", close.Count(Enchanted)),
            ("lightningEnchantedNear", aroundCows.Count(cow => cow.LightningEnchanted && cow.Distance < Kiting.LightningEnchantedDistance)),
            ("nearest", Math.Round(nearest, 1)),
            ("moved", moved),
            ("hop", step == null ? null : Math.Round(step.Hop, 1)),
            ("long", step?.Long),
            ("deadEnd", step?.DeadEnd),
            ("wayOut", wayOutTaken),
            ("notAttackingMs", notAttacking.ElapsedMilliseconds));

        state.StepsSinceLog++;
        if (DateTime.Now - state.LastLog >= KiteLogInterval)
        {
            Log.Information("{Character} stepped back from {Close} cows, nearest {Nearest:F1}, {Kind} step {Hop:F1}, {Moved}, {Steps} steps since the last line",
                client.Game.Me.Name, close.Count, nearest, step == null ? "no" : step.Long ? "long" : "short", step?.Hop ?? 0, moved ? "moved" : "did not move", state.StepsSinceLog);
            state.LastLog = DateTime.Now;
            state.StepsSinceLog = 0;
        }

        return step == null ? KiteResult.NoStep : KiteResult.Stepped;
    }

    /// <summary>
    /// Opens the barbarian's way back where no lightning enchanted monster is near and nothing is
    /// close enough to be hitting her. Returns false while this spot is not that, so she keeps
    /// fighting and asks again next tick.
    /// </summary>
    private async Task<bool> TryOpenSafePortal(Client client, CowManager cowManager, System.Action stopCasting)
    {
        var here = client.Game.Me.Location;
        if (cowManager.GetNearbyAliveMonsters(here, SafePortalRange, 100).Any(m => m.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted))
            || cowManager.GetNearbyAliveMonsters(here, SelfDefenceRange, 1).Count != 0)
        {
            return false;
        }

        stopCasting();
        if (!await _townManagementService.CreateTownPortal(client))
        {
            Log.Warning("{Character} could not open a portal back for the barbarian at {Location}", client.Game.Me.Name, here);
            return false;
        }

        Log.Information("{Character} opened a portal back for the barbarian at {Location}", client.Game.Me.Name, here);
        _safePortalWanted = false;
        return true;
    }

    /// <summary>
    /// Takes the barbarian to town away from a lightning enchanted pack and back through the portal
    /// character's next portal. False when either leg fails, which leaves the game to be ended.
    /// </summary>
    private async Task<bool> WaitOutLightningEnchanted(Client client, CowManager cowManager, AliveMonster enchanted)
    {
        var portalPlayer = client.Game.Players.FirstOrDefault(p => _cowconfig.PortalCharacterName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        Log.Information("{Character}: lightning enchanted {Monster} {Distance:F0} away, waiting in town for a portal clear of it",
            client.Game.Me.Name, enchanted.NPCCode, enchanted.Location.Distance(client.Game.Me.Location));
        _leadInTown = true;
        // The party's portal is only seen within sight range, and the pack usually catches him away
        // from it; without a portal of his own this ended the game.
        if (portalPlayer == null
            || !await TakeNearestPortalToTown(client) && !await _townManagementService.TakeTownPortalToTown(client))
        {
            Log.Warning("{Character} could not take a portal to town from {Location}", client.Game.Me.Name, client.Game.Me.Location);
            return false;
        }

        InCowLevel[client.Game.Me.Name.ToLower()] = false;
        _safePortalWanted = true;
        var waited = Stopwatch.StartNew();
        // On her word, not on a portal id this side has not seen: the town end of the old portal is a
        // unit of its own with an id of its own, so that took him straight back into the pack.
        await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(0.2));
            return NextGame.Task.IsCompleted || IsRunOver(cowManager) || !_safePortalWanted;
        }, SafePortalTimeout);
        if (NextGame.Task.IsCompleted || IsRunOver(cowManager))
        {
            return true;
        }

        if (_safePortalWanted)
        {
            Log.Warning("{Character} got no portal back within {Seconds:F0}s", client.Game.Me.Name, waited.Elapsed.TotalSeconds);
            return false;
        }

        if (!await _townManagementService.TakeTownPortalToArea(client, portalPlayer, Area.CowLevel))
        {
            Log.Warning("{Character} could not take the portal back to the cow level", client.Game.Me.Name);
            return false;
        }

        await ConfirmInCowLevel(client);
        _leadInTown = false;
        Log.Information("{Character} back in the cow level at {Location} after {Seconds:F1}s in town", client.Game.Me.Name, client.Game.Me.Location, waited.Elapsed.TotalSeconds);
        return true;
    }

    private async Task BarbClient(Client client, AccountConfig account, CowManager cowManager, bool shouldBo)
    {
        Log.Information($"Starting BoBarb Client {client.Game.Me.Name}");
        if (shouldBo)
        {
            await ClassHelpers.CastAllShouts(client);
        }

        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !IsRunOver(cowManager))
        {
            if (shouldBo)
            {
                await StepUpToShout(client);
                await ClassHelpers.CastAllShouts(client);
            }

            var enchanted = shouldBo
                ? cowManager.GetNearbyAliveMonsters(client, LightningEnchantedLeadRange, 100)
                    .FirstOrDefault(m => m.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted))
                : null;
            if (enchanted != null)
            {
                if (!await WaitOutLightningEnchanted(client, cowManager, enchanted))
                {
                    Log.Information($"{client.Game.Me.Name}, lightning enhanced cow nearby, next game");
                    break;
                }

                continue;
            }

            var leadPlayer = LeadPlayer(client);
            if (!shouldBo && await RetreatIfHurtByPortal(client, cowManager))
            {
                continue;
            }

            var battleOrders = shouldBo ? BattleOrdersStance.Fight : BattleOrdersStanceOf(client);
            if (battleOrders != BattleOrdersStance.Fight)
            {
                await WaitForBattleOrders(client, cowManager, leadPlayer, battleOrders, keepDistance: null);
                continue;
            }

            if (leadPlayer != null && leadPlayer.Location.Distance(client.Game.Me.Location) > 25)
            {
                // Left behind and hemmed in is a trap this used to sit in forever: walking back is
                // impossible while a pack is standing in the way, and doing nothing here meant he
                // never cut himself free either. He just stood in the middle of it, further from
                // the group every time they advanced. A barbarian in a crowd is the one character
                // who is perfectly fine - so kill the way out rather than wait for it to open.
                if (cowManager.GetNearbyAliveMonsters(client, SelfDefenceRange, 1).Count != 0)
                {
                    await _attackService.AssistPlayer(client, SelfPlayer(client), PriorityCodes(client, cowManager), whirlOut: true, cancellation: GameOverToken);
                }

                continue;
            }

            var nearbyMonsters = cowManager.GetNearbyAliveMonsters(client, 20, 1);
            if (nearbyMonsters.Count == 0)
            {
                await PickupItemsAndPotions(client, account, 15, stop: GameOver);
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
            EndGameWithLoop(client);
        }

        Log.Information($"Stopped Barb Client {client.Game.Me?.Name}, cowing manager is finished is: {cowManager.IsFinished()}, next game set {NextGame.Task.IsCompleted}, in game {client.Game.IsInGame()}");
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
        IReadOnlyCollection<NPCCode> priorityCodes = null;
        MonsterCluster currentCluster = null;
        var clusterStopWatch = new Stopwatch();
        var clusterAliveMembers = int.MaxValue;
        var breadcrumbStopWatch = Stopwatch.StartNew();
        var stragglerStopWatch = new Stopwatch();
        var retreatStopWatch = new Stopwatch();

        // The walk itself waits for a straggler as the loop does between walks, a little further out so the loop is sure to
        // take the wait over, and gives up on one the same way: once the loop's wait for it has run out, the walk goes on.
        bool HoldForParty() => FarBehindHunters(client, StragglerDistance + 5).Count != 0
            && (!stragglerStopWatch.IsRunning || stragglerStopWatch.Elapsed < StragglerWait);
        var deathStopWatch = new Stopwatch();
        var lastAliveAt = new Dictionary<string, Point>();
        Point deathLocation = null;
        var sweepProgress = 0;

        while (NextGame.Task != await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(0.2)), NextGame.Task) && client.Game.IsInGame() && !IsRunOver(cowManager))
        {
            if (shouldBo)
            {
                await StepUpToShout(client);
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
                        await _attackService.AssistPlayer(client, SelfPlayer(client), priorityCodes, whirlOut: true, cancellation: GameOverToken);
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

                await WhirlOrWalk(client, retreatPoint, () => NextGame.Task.IsCompleted || !client.Game.IsInGame(), backOut: () => true);
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
                var them = hunter.Game.Me;
                if (them?.Name != null && !_away.ContainsKey(hunter) && !IsDead(hunter))
                {
                    lastAliveAt[them.Name] = them.Location;
                }
            }

            var dead = DeadHunters();
            if (dead.Count != 0)
            {
                if (!deathStopWatch.IsRunning)
                {
                    deathStopWatch.Restart();
                    var deadNames = dead.Select(d => d.Game.Me?.Name).Where(name => name != null).ToList();
                    deathLocation = deadNames.Count > 0 ? lastAliveAt.GetValueOrDefault(deadNames[0]) : null;
                    Log.Warning($"{string.Join(",", deadNames)} died at {deathLocation}, clearing the ground before giving up on the game");
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
            // Seen by this client, so the King's pack counts too: the manager leaves it out in passive mode.
            var partyEngaged = CowsOnHer(client, cowManager, 20).Count != 0
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
                await _attackService.AssistPlayer(client, GetProtectAnchor(client, cowManager), priorityCodes, whirlOut: true, cancellation: GameOverToken);
                continue;
            }

            await PickupItemsAndPotions(client, account, HuntingPartyPickupRadius, stop: GameOver);

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
                currentCluster = cowManager.ClaimNextCluster(client, sweepProgress, null, FarClusterDistance, CanHarmMonster(client));

                // Preferring something nearby is worth it only while there is something nearby. With
                // the cows at forty nine of fifty one the party was still held inside the cap, had
                // cleared everything within it, and stood still with nine eligible clusters further
                // out - until the ninety second patience timer gave the game away.
                currentCluster ??= cowManager.ClaimNextCluster(client, sweepProgress, canHarm: CanHarmMonster(client));
                if (currentCluster == null)
                {
                    // With nothing to hunt, close on the sorceresses rather than standing still.
                    // Clusters open where they have just finished clearing, so waiting here means
                    // crossing the level once one does; two thirds of the time the party spends not
                    // killing anything is walking, and this is the half of it that can be avoided.
                    var trailTarget = NearestKillerLocation(client) ?? holdLocation;
                    if (client.Game.Me.Location.Distance(trailTarget) > KillerTrailDistance)
                    {
                        await MoveWithPartyGuard(client, trailTarget, HoldForParty);
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
                await MoveWithPartyGuard(client, target, HoldForParty);
            }
        }

        if (!client.Game.IsInGame() && currentCluster != null)
        {
            cowManager.GiveUpCluster(currentCluster);
        }

        Log.Information($"Stopped hunt lead client {client.Game.Me?.Name}, {cowManager.DescribeProgress()}, finished is {cowManager.IsFinished()}");
        _partyRetreating = false;
        EndGameWithLoop(client);
    }

    private static readonly TimeSpan WalkStopCheck = TimeSpan.FromMilliseconds(100);

    /// <summary>The game has ended for the party: what is left to pick up or fight is not worth its leave waiting.</summary>
    private bool GameOver() => NextGame.Task.IsCompleted;

    /// <summary>Cancelled when this game ends, for the walks an assist makes inside the attack service.</summary>
    private CancellationToken GameOverToken { get; set; }

    /// <summary>
    /// A walk, except that a whirlwind barbarian whirls the stretches a pack stands in or after a walk the server held,
    /// and, with <paramref name="backOut"/>, a monster attacking him on the way breaks the walk off: if it says he
    /// backs out he whirls on towards the way's end until nothing is on him, then walks again; if not, the walk ends
    /// there and the caller fights. Walking with cows on him is what drops a barbarian's life - the hunt lead walked
    /// 190 units to a cluster taking hits and left the game at 19% under Amplify Damage without a whirl in thirteen
    /// seconds - but whirling on is for leaving a fight, not for the lead setting off to the next cluster: whirling
    /// away from the cows on him left them to the amazon twice in four games. Once a whirl did not answer an attack, or
    /// after <see cref="WhirlOut.MaxWhirlsPerWay"/>, the rest is walked. <paramref name="stop"/> and the token are
    /// asked before every whirl and during the walk, and end it there.
    /// </summary>
    private async Task<bool> WhirlOrWalk(Client client, Point location, Func<bool> stop, CancellationToken? token = null, bool warnNoPath = true, Func<bool> backOut = null)
    {
        bool Stopped() => token?.IsCancellationRequested == true || stop();
        bool Arrived() => client.Game.Me.Location.Distance(location) <= ArrivedAtStop;
        bool? breaksOff = null;
        var whirled = 0;
        while (true)
        {
            var whirlForAttacks = backOut?.Invoke() == true;
            var round = await WhirlOut.WhirlAlong(Stopped, Arrived, () => _attackService.WhirlOutOfPack(client, location, whirlForAttacks));
            whirled += round;
            if (Stopped() || Arrived())
            {
                return true;
            }

            breaksOff ??= backOut != null && client.Game.Me is { } me
                && WhirlOut.Whirls(me.HasSkill(Skill.Whirlwind), me.Attributes.GetValueOrDefault(D2NG.Core.D2GS.Players.Attribute.Level), client.Game.Difficulty == Difficulty.Normal);

            // Asked after the round, not before it: an attack a second old when the round began has run out of its window
            // by the time the path is found, and the whirl it declined said nothing about the next attack.
            if (whirlForAttacks && round == 0 && client.Game.UnderAttack)
            {
                breaksOff = false;
            }

            // The stop ends the walk too, not only the whirls before it: a follow walk set off as the game ended went to its
            // end, five to eleven seconds in one game in five, with the whole party waiting to leave.
            var from = client.Game.Me.Location;
            var brokenOff = false;
            using var walkCancel = token is { } outer ? CancellationTokenSource.CreateLinkedTokenSource(outer) : new CancellationTokenSource();
            var walk = MoveToLocation(client, location, walkCancel.Token, warnNoPath);
            while (await Task.WhenAny(walk, Task.Delay(WalkStopCheck)) != walk)
            {
                if (walkCancel.IsCancellationRequested)
                {
                    continue;
                }

                if (stop())
                {
                    await walkCancel.CancelAsync();
                }
                else if (breaksOff == true && whirled < WhirlOut.MaxWhirlsPerWay && client.Game.UnderAttack
                    && (!backOut() || client.Game.Me?.Mana > WhirlOut.AttackedManaReserve))
                {
                    brokenOff = true;
                    await walkCancel.CancelAsync();
                }
            }

            var moved = await walk;
            _attackService.RecordWalk(client, from);
            if (!brokenOff || Stopped())
            {
                return moved;
            }

            if (!backOut())
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Walks to a location but gives up as soon as a party member is hurt, so a long path does not
    /// hold the group in a fight it should be leaving, or when <paramref name="holdForParty"/> says a member has
    /// fallen too far behind: the lead walked ninety units to a cluster while the others still picked up and shot,
    /// and the amazon crossed the gap alone into the pack he was fighting, leaving the game in three of six times.
    /// </summary>
    private async Task MoveWithPartyGuard(Client client, Point location, Func<bool> holdForParty = null)
    {
        using var cancellation = new CancellationTokenSource();
        var move = WhirlOrWalk(client, location, () => _partyRetreating || NextGame.Task.IsCompleted || !client.Game.IsInGame(), cancellation.Token, backOut: () => false);
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
            if (_partyRetreating || NextGame.Task.IsCompleted || !client.Game.IsInGame() || holdForParty?.Invoke() == true)
            {
                await cancellation.CancelAsync();
                break;
            }
        }

        await move;
    }

    /// <summary>
    /// No monster is worth more than another now: the level is cleared as one job, and which
    /// character should be the one hitting a given pack is answered from resistances rather than
    /// from a ranking of monster types.
    /// </summary>
    private static IReadOnlyCollection<NPCCode> PriorityCodes(Client client, CowManager cowManager)
    {
        return null;
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
            // Read once: a rejoin replaces the game data, and Me is null until the server assigns it.
            var them = hunter.Game.Me;
            if (hunter == client || _away.ContainsKey(hunter) || them == null || !hunter.Game.IsInGame() || hunter.Game.Area != client.Game.Area
                || IsDead(hunter))
            {
                continue;
            }

            var player = client.Game.Players.FirstOrDefault(p => p.Id == them.Id);
            if (player?.Location != null && player.Location.Distance(client.Game.Me.Location) > distance)
            {
                behind.Add(them.Name);
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
            var them = killer.Game.Me;
            if (them?.Location == null || !killer.Game.IsInGame() || killer.Game.Area != client.Game.Area)
            {
                continue;
            }

            var distance = them.Location.Distance(client.Game.Me.Location);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = them.Location;
            }
        }

        return nearest;
    }

    private static Player SelfPlayer(Client client)
    {
        var me = client.Game.Me;
        return me == null ? null : client.Game.Players.FirstOrDefault(p => p.Id == me.Id);
    }

    /// <summary>
    /// A character on nothing but zero life is dead. Nothing in the game data models death, so this
    /// is the only signal available, and every party check has to skip them: a corpse never heals,
    /// never catches up, and would otherwise hold the group in place until its own timeouts fire.
    /// </summary>
    private static bool IsDead(Client client)
    {
        var me = client.Game.Me;
        return me != null && client.Game.IsInGame() && me.MaxLife > 0 && me.Life == 0;
    }

    private List<Client> DeadHunters()
    {
        return _huntingParty.FindAll(hunter => !_away.ContainsKey(hunter) && IsDead(hunter));
    }

    /// <summary>
    /// Whether a party member the lead is escorting has monsters on it. Only counts members close
    /// enough to reach, so a straggler that is already lost does not pin the group in place.
    /// </summary>
    private bool EscortUnderAttack(Client client, CowManager cowManager)
    {
        foreach (var hunter in _huntingParty)
        {
            var location = hunter.Game.Me?.Location;
            if (hunter == client || _away.ContainsKey(hunter) || location == null || !hunter.Game.IsInGame() || IsDead(hunter)
                || hunter.Game.Area != client.Game.Area)
            {
                continue;
            }

            if (location.Distance(client.Game.Me.Location) <= StragglerDistance
                && LiveCowsAround(client, cowManager, location, EscortEngagementRange).Count != 0)
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
        return LeadPlayer(client)?.Location;
    }

    /// <summary>
    /// The nearby monsters this character can actually hurt. Everything in the level is a target
    /// now, so a lightning sorceress would otherwise stand next to something immune to lightning
    /// casting at it forever. The resistance table answers this per monster, including the boss
    /// modifiers it spawned with.
    /// </summary>
    private List<AliveMonster> HarmableNearby(Client client, CowManager cowManager, double distance, int count)
    {
        var nearby = cowManager.GetNearbyAliveMonsters(client, distance, int.MaxValue);
        var harmable = new List<AliveMonster>(Math.Min(nearby.Count, count));
        foreach (var monster in nearby)
        {
            if (!_attackService.CanHarm(client, monster.NPCCode, monster.MonsterEnchantments))
            {
                continue;
            }

            harmable.Add(monster);
            if (harmable.Count == count)
            {
                break;
            }
        }

        return harmable;
    }

    private bool AnyHunterHurt()
    {
        foreach (var hunter in _huntingParty)
        {
            var them = hunter.Game.Me;
            if (_away.ContainsKey(hunter) || them == null || them.MaxLife == 0 || !hunter.Game.IsInGame() || IsDead(hunter))
            {
                continue;
            }

            if ((double)them.Life / them.MaxLife < RetreatLifeFraction)
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
            var them = hunter.Game.Me;
            if (_away.ContainsKey(hunter) || them == null || them.MaxLife == 0 || !hunter.Game.IsInGame() || IsDead(hunter))
            {
                continue;
            }

            if ((double)them.Life / them.MaxLife < ResumeLifeFraction)
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
            var them = hunter.Game.Me;
            if (hunter == client || _away.ContainsKey(hunter) || them == null || them.MaxLife == 0 || !hunter.Game.IsInGame() || them.HasSkill(Skill.Teleport))
            {
                continue;
            }

            var player = client.Game.Players.FirstOrDefault(p => p.Id == them.Id);
            if (player?.Location == null || player.Location.Distance(client.Game.Me.Location) > 30)
            {
                continue;
            }

            if (cowManager.GetNearbyAliveMonsters(player.Location, 15, 1).Count == 0)
            {
                continue;
            }

            var fraction = (double)them.Life / them.MaxLife;
            if (fraction < worstFraction)
            {
                worstFraction = fraction;
                anchor = player;
            }
        }

        return anchor ?? SelfPlayer(client);
    }

    /// <summary>
    /// Records that this character is standing in the cow level, so its monster assigns count.
    /// </summary>
    /// <remarks>
    /// Every client has to call this, not just the one that opens the level. Setting it only where
    /// the portal character confirms its own arrival left the other five dropping every assign they
    /// received, and the party then swept a level it had registered a third of: twelve clusters
    /// where a full game finds fifty.
    /// </remarks>
    /// <summary>
    /// Whether a killer may only claim packs it can actually damage. Both arms of the measurement
    /// come from one build so that shared code, which several bots are editing, is identical in
    /// each - flipping this is the only difference between them.
    /// </summary>
    private const bool ClaimOnlyWhatWeCanHarm = false;

    /// <summary>What this client can actually damage, for deciding which packs are worth claiming.</summary>
    private Func<AliveMonster, bool> CanHarmMonster(Client client)
        => ClaimOnlyWhatWeCanHarm
            ? monster => _attackService.CanHarm(client, monster.NPCCode, monster.MonsterEnchantments)
            : null;

    private async Task<bool> ConfirmInCowLevel(Client client)
    {
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            await Task.Delay(100);
            return await _pathingService.IsNavigatablePointInArea(
                client.Game.MapId, Difficulty.Normal, Area.CowLevel, client.Game.Me.Location);
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning("Client {Character} could not be confirmed in the cow level at {Location}, its monsters will not be clustered",
                client.Game.Me.Name, client.Game.Me.Location);
            return false;
        }

        InCowLevel[client.Game.Me.Name.ToLower()] = true;
        return true;
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
            if (cowPortal != null)
            {
                _cowPortalAt ??= cowPortal.Location;
            }

            return cowPortal != null;
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Warning($"Client {client.Game.Me.Name} never saw the cow portal in town");
            return false;
        }

        // Walkers too: a rejoining barbarian or amazon takes this way back into the level.
        var movementMode = MovementHelpers.PreferredMovement(client.Game);
        var pathBack = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.RogueEncampment, client.Game.Me.Location, cowPortal.Location, movementMode);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathBack, movementMode))
        {
            Log.Warning($"Client {client.Game.Me.Name} {movementMode} to {EntityCode.RedTownPortal} failed at {client.Game.Me.Location}");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            await client.Game.MoveToAsync(cowPortal);

            if (retryCount > 0 && retryCount % 5 == 0)
            {
                await client.Game.RequestUpdateAndWaitAsync(client.Game.Me.Id, UpdateAnswerTimeout);
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
        // Not waited for: the check below looks at the position every 100ms and finds the answer there.
        client.Game.SendUpdateRequest(client.Game.Me.Id);
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (retryCount > 0 && retryCount % 5 == 0)
            {
                client.Game.SendUpdateRequest(client.Game.Me.Id);
            }

            await Task.Delay(100);

            return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.CowLevel, client.Game.Me.Location);
        }, TimeSpan.FromSeconds(10)))
        {
            return false;
        }

        await ConfirmInCowLevel(client);

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
