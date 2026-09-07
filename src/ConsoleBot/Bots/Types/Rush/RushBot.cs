using ConsoleBot.Attack;
using ConsoleBot.Chicken;
using ConsoleBot.Enums;
using ConsoleBot.Helpers;
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
using D2NG.Core.D2GS.Quest;
using D2NG.Core.MCP;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using D2NG.Pickit;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Rush;

public sealed class RushBot : IBotInstance
{
    private const int AreaHopAttempts = 3;
    private const double TeleportNoOpRange = 10;
    private const double PortalInteractRange = 5;
    private const double OrbEffectiveRange = 20;
    private const double OrbEngageRange = 15;
    private const double NovaRange = 10;
    private const double StaticFieldRange = 20;
    private const double DiabloStaticUntilLife = 50;
    private const int OrbManaCost = 30;
    private const int BlizzardManaCost = 35;
    private const int NovaManaCost = 30;
    private const double RetreatBelowLifeFraction = 0.5;
    private const double RecoveredLifeFraction = 0.8;
    private const int FillerManaReserve = 60;
    private const int MaxPortalSweepCandidates = 40;
    private const int PortalSweepGrid = 25;
    private const int DurielPullDistance = 34;
    private const int MinimumPullDistance = 15;
    private const int MephistoStagingOffsetFromRedPortalX = -21;
    private const int MephistoStagingOffsetFromRedPortalY = 17;
    private const double MephistoStagingClearance = 45;
    private const ushort DurielDefeatedBit = 0x0020;
    private const ushort DiabloDefeatedBit = 0x4000;
    private const double RusheeHealBelowFraction = 0.70;
    private const uint WarrivsAndarielMessage = 0xB7;
    private const uint TyraelsSevenTombsMessage = 0x012E;
    private static readonly uint[] JerhynMessages = [0x00FD, 0x016A, 0x01BA];
    private static readonly uint[] MeshifMessages = [0x00F1, 0x01C2];
    // A real 1.09 client completing act 4 sends Tyrael exactly one message and nothing else.
    // Captured 2026-08-30 from a client that took Brisara from progression 3 to Nightmare.
    private static readonly uint[] ActFourTyraelMessages = [0x02AC];
    private static readonly Point JerhynCourtyard = new Point(5096, 5141);
    private static readonly TimeSpan SealKillLimit = TimeSpan.FromSeconds(75);
    /// <summary>Act bosses take far longer above Normal, so one limit covers them all.</summary>
    private static readonly TimeSpan BossKillLimit = TimeSpan.FromSeconds(300);
    /// <summary>How long a boss may show no damage before the fight is called unwinnable.</summary>
    private static readonly TimeSpan NoDamageGraceperiod = TimeSpan.FromSeconds(90);
    /// <summary>When to start refreshing the boss record, since a stale one reads as undamaged.</summary>
    private static readonly TimeSpan NoDamageProbeAfter = TimeSpan.FromSeconds(10);

    /// <summary>Bosses whose cold immunity above Normal makes a cold rusher useless.</summary>
    private static readonly HashSet<NPCCode> ColdImmuneAboveNormal = [NPCCode.Duriel];

    /// <summary>Below this life a percentage skill barely moves the bar; switch to direct damage.</summary>
    private const double ColdImmuneStaticUntilLife = 20;
    private const double MeleeRange = 3;
    private const int RetreatsBeforeWithdrawing = 2;
    private const double WithdrawnStandoffRange = 40;
    /// <summary>How close the crew closes on a boss before it starts swinging.</summary>
    private const double CrewEngageRange = TeleportNoOpRange;
    /// <summary>Below this many healing potions the rusher shops rather than fighting on.</summary>
    private const int RestockPotionFloor = 8;
    private static readonly TimeSpan DiabloKillLimit = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan OrbCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleBlockerProbeAfter = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StaleBlockerIgnoreAfter = TimeSpan.FromSeconds(12);
    private static readonly EntityCode[] ChaosSeals =
    [
        EntityCode.RightSeal1,
        EntityCode.RightSeal2,
        EntityCode.TopSeal,
        EntityCode.LeftSeal1,
        EntityCode.LeftSeal2
    ];
    /// <summary>
    /// The five seals form three groups, and each group wakes exactly one boss. The right pair and the
    /// left pair each have a decoy that wakes nothing, and which of the pair is real changes with the
    /// map. Tracking kills per seal instead of per group made the two decoys look like unfinished work
    /// for ever: rb45 revisited one of them three times and never spawned Diablo.
    /// </summary>
    private static readonly Dictionary<EntityCode, int> SealGroups = new()
    {
        [EntityCode.RightSeal1] = 0,
        [EntityCode.RightSeal2] = 0,
        [EntityCode.TopSeal] = 1,
        [EntityCode.LeftSeal1] = 2,
        [EntityCode.LeftSeal2] = 2
    };
    private static readonly Dictionary<(int Dx, int Dy), (int Dx, int Dy)> ActOnePortalFromWaypoint =
        new Dictionary<(int Dx, int Dy), (int Dx, int Dy)>
        {
            [(33, -20)] = (-4, 56),
            [(28, -25)] = (1, 61),
            [(-7, -30)] = (37, 64),
            [(-12, -30)] = (41, 66)
        };
    private static readonly Dictionary<Act, (int Dx, int Dy)> TownPortalFromWaypointByAct =
        new Dictionary<Act, (int Dx, int Dy)>
        {
            [Act.Act2] = (109, -31),
            [Act.Act3] = (-1, 19),
            [Act.Act4] = (2, 24)
        };
    private static readonly Dictionary<Act, EntityCode> TownWaypointByAct =
        new Dictionary<Act, EntityCode>
        {
            [Act.Act1] = EntityCode.WaypointAct1,
            [Act.Act2] = EntityCode.WaypointAct2,
            [Act.Act3] = EntityCode.WaypointAct3,
            [Act.Act4] = EntityCode.WaypointAct4
        };

    private readonly BotConfiguration _botConfiguration;
    private readonly RushConfiguration _rushConfiguration;
    private readonly IPathingService _pathingService;
    private readonly IMapApiService _mapApiService;
    private readonly ITownManagementService _townManagementService;
    private readonly IAttackService _attackService;
    private bool _hasRun;

    /// <summary>
    /// Extra fighters. They do not lead - the rusher still takes the waypoints and opens the portals -
    /// they follow it into fights, buff the party and add a second damage type. A barbarian here also
    /// covers what the sorceress cannot: Nightmare Duriel is cold immune.
    /// </summary>
    private readonly List<Client> _crew = [];
    private List<Task<bool>> _crewPreStages = [];
    private int _gameNumber = 1;
    /// <summary>
    /// The difficulty every map api and pathing call uses, regardless of the game's own difficulty.
    /// The map service returns a different layout per difficulty for the same map id, and only the
    /// Normal one matches the live game: asking for the Nightmare layout put the Durance 3 stairs
    /// where no warp ever appeared. The Mephisto bot has always passed Normal, which is why it crosses
    /// that border in four seconds.
    /// </summary>
    private const Difficulty MapDifficulty = Difficulty.Normal;

    private Difficulty _gameDifficulty;

    /// <summary>Account behind each joined client, so a chickened client can be put back in the game.</summary>
    private readonly Dictionary<Client, AccountConfig> _accountOf = [];

    /// <summary>Everyone who should be in the party, for re-inviting after a rejoin.</summary>
    private readonly List<Client> _partyClients = [];

    /// <summary>Rejoins spent per client this game. A character that cannot survive at all must not loop.</summary>
    private readonly Dictionary<Client, int> _rejoinsUsed = [];

    public RushBot(
        IOptions<BotConfiguration> botConfiguration,
        IOptions<RushConfiguration> rushConfiguration,
        IPathingService pathingService,
        IMapApiService mapApiService,
        ITownManagementService townManagementService,
        IAttackService attackService)
    {
        _botConfiguration = botConfiguration.Value;
        _rushConfiguration = rushConfiguration.Value;
        _gameNumber = _rushConfiguration.StartingGameNumber;
        _pathingService = pathingService;
        _mapApiService = mapApiService;
        _townManagementService = townManagementService;
        _attackService = attackService;
    }

    /// <summary>A rush is finished when it is finished; the host loop must not start another.</summary>
    public bool RunsOnce => true;

    public string GetName()
    {
        return "rush";
    }

    public async Task Run()
    {
        if (_hasRun)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            return;
        }

        _hasRun = true;
        _accountOf.Clear();
        _partyClients.Clear();
        _rejoinsUsed.Clear();
        D2NG.Pickit.Pickit.ReserveInventoryClassification(ClassificationType.HealthPotion);
        D2NG.Pickit.Pickit.ReserveInventoryClassification(ClassificationType.ManaPotion);
        var clients = new List<Client>();
        try
        {
            _rushConfiguration.Validate();
            var roster = await ReadRoster();
            ValidateRoster(roster);

            var unknown = roster.Where(entry => entry.Progression == null).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Unknown character-list progression byte; refusing to guess: {string.Join(", ", unknown.Select(Describe))}");
            }

            var rehearsal = _rushConfiguration.RehearsalDifficulty;
            if (rehearsal != null)
            {
                Log.Information("Rehearsing on {Difficulty}: every rushee is eligible and no destination game is proven", rehearsal);
            }

            var eligible = roster
                .Where(entry => !ReferenceEquals(entry.Account, _rushConfiguration.Rusher))
                .Where(entry => rehearsal != null || !Satisfies(entry.Progression, _rushConfiguration.Destination))
                .OrderBy(entry => entry.Progression.CompletedActs)
                .ToList();

            if (eligible.Count == 0)
            {
                await VerifyDestinationGame(
                    roster.Where(entry => !ReferenceEquals(entry.Account, _rushConfiguration.Rusher)).ToList(),
                    [],
                    clients);
                return;
            }

            var hostEntry = eligible[0];
            _gameDifficulty = rehearsal ?? hostEntry.Progression.CurrentDifficulty;
            var host = await EnterHostGame(hostEntry, clients);
            var rusher = await JoinParticipant(_rushConfiguration.Rusher, clients, attachChicken: true);

            _crew.Clear();
            foreach (var member in _rushConfiguration.Crew ?? [])
            {
                _crew.Add(await JoinParticipant(member, clients, attachChicken: true));
            }

            var rushees = new List<ParticipantSession> { new ParticipantSession(hostEntry, host) };

            foreach (var entry in eligible.Skip(1))
            {
                var client = await JoinParticipant(entry.Account, clients, attachChicken: false);
                rushees.Add(new ParticipantSession(entry, client));
            }

            await VerifyParty(
                host,
                rusher,
                rushees.Select(session => session.Client).Concat(_crew).ToList());

            // Stock up before anything else. The rusher joins with whatever its belt held when it last
            // left, and ChickenService pulls it straight back out if that is nothing - it left twenty
            // seconds after joining, never drinking once and never reaching the per-phase restock.
            // Recover bodies first. A character that died last game joins standing next to its corpse,
            // and until it is picked up the character is without whatever it was wearing. Waiting for
            // the phase pre-stage is too late - that can be minutes away behind the shopping.
            foreach (var session in rushees)
            {
                // Log what the client believes about its body before trying. A corpse was visibly on
                // the ground while PickupCorpseIfExists returned instantly and silently, which means
                // Me.CorpseId was null even though CorpseAssign had arrived - so the state, not the
                // walking, is what to look at.
                Log.Information(
                    "CORPSE {Rushee}: corpseId={CorpseId}, players={Players}, me={MeId} at {Location}",
                    session.Client.Game.Me.Name,
                    session.Client.Game.Me.CorpseId?.ToString() ?? "none",
                    string.Join("/", session.Client.Game.Players.Select(player => $"{player.Id}:{player.Name}")),
                    session.Client.Game.Me.Id,
                    session.Client.Game.Me.Location);

                await GeneralHelpers.PickupCorpseIfExists(session.Client, _pathingService);
            }

            foreach (var member in _crew.Where(candidate => candidate.Game.IsInGame()))
            {
                await GeneralHelpers.PickupCorpseIfExists(member, _pathingService);
            }

            await GeneralHelpers.PickupCorpseIfExists(rusher, _pathingService);

            // Everyone works at once. The rushees have no shopping to do, so they start walking to the
            // portal spot immediately; each crew member shops and then walks. By the time the rusher
            // has travelled and put a portal up, the followers are already standing on the spot -
            // doing this serially left them idle in town for the whole restock.
            var gettingReady = new List<Task> { RestockRusher(rusher, rusher.Game.Act) };

            gettingReady.AddRange(rushees.Select(session => StartRusheePreStage(session.Client)));

            var startingAct = rusher.Game.Act;
            gettingReady.AddRange(_crew
                .Where(candidate => candidate.Game.IsInGame())
                .Select(async member =>
                {
                    await RestockCrewMember(member);
                    if (member.Game.Act == startingAct || await _townManagementService.SwitchAct(member, startingAct))
                    {
                        await StartRusheePreStage(member);
                    }
                }));

            await Task.WhenAll(gettingReady);

            // Waypoint collection is off until ActivateWaypointHere actually registers a waypoint.
            // It detects missing ones correctly but the interact never lands, so it walks there,
            // fails, and the rush walks the same route again - pure overhead. TakeWaypointFromTown
            // already falls back to walking, so nothing is lost by skipping it.
            // await CollectMissingWaypoints(rusher);

            if (!await RunFromAuthoritativeState(rusher, rushees))
            {
                throw new InvalidOperationException("Rush stopped at the first unsupported or failed checkpoint; see preceding evidence");
            }

            if (RequiresDestinationGameProof())
            {
                await VerifyDestinationGame(
                    roster.Where(entry => !ReferenceEquals(entry.Account, _rushConfiguration.Rusher)).ToList(),
                    rushees,
                    clients);
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Rush stopped");
            throw;
        }
        finally
        {
            foreach (var client in clients)
            {
                try
                {
                    if (client.Game.IsInGame())
                    {
                        await client.Game.LeaveGame();
                        await Task.Delay(500);
                    }

                    client.Disconnect();
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Failed to close a rush client cleanly");
                }
            }
        }
    }

    private async Task<List<RosterEntry>> ReadRoster()
    {
        var entries = new List<RosterEntry>();
        foreach (var account in _rushConfiguration.Rushees.Append(_rushConfiguration.Rusher))
        {
            var client = new Client();
            try
            {
                if (!client.Connect(_botConfiguration.Realm, _botConfiguration.KeyOwner, _botConfiguration.GameFolder))
                {
                    throw new InvalidOperationException($"Could not connect account {account.Username} to the realm");
                }

                var characters = await client.Login(account.Username, account.Password);
                var character = characters?.FirstOrDefault(candidate =>
                    candidate.Name.Equals(account.Character, StringComparison.OrdinalIgnoreCase));
                if (character == null)
                {
                    throw new InvalidOperationException(
                        $"Character {account.Character} was not found on account {account.Username}");
                }

                CharacterProgression progression = null;
                if (!CharacterProgression.TryCreate(character, out progression))
                {
                    Log.Warning(
                        "{Character} has unknown progression byte 0x{Progression:X2}",
                        character.Name,
                        character.Progression);
                }
                entries.Add(new RosterEntry(account, character, progression));
                Log.Information(
                    "Roster {Character}: progression {Progression}, {Mode}, {LifeMode}",
                    character.Name,
                    character.Progression,
                    character.IsExpansion ? "Expansion" : "Classic",
                    character.IsHardCore ? "Hardcore" : "Softcore");
            }
            finally
            {
                client.Disconnect();
                await Task.Delay(500);
            }
        }

        return entries;
    }

    private void ValidateRoster(List<RosterEntry> roster)
    {
        var rusher = roster.Single(entry =>
            entry.Account.Character.Equals(_rushConfiguration.Rusher.Character, StringComparison.OrdinalIgnoreCase));
        var mismatches = roster
            .Where(entry => entry.Character.IsExpansion != rusher.Character.IsExpansion
                || entry.Character.IsHardCore != rusher.Character.IsHardCore)
            .ToList();
        if (mismatches.Count > 0)
        {
            throw new ValidationException(
                "All rush participants must share Classic/Expansion and Hardcore/Softcore mode. "
                + $"Rusher is {DescribeMode(rusher.Character)}; mismatches: "
                + string.Join(", ", mismatches.Select(entry => $"{entry.Character.Name} ({DescribeMode(entry.Character)})")));
        }

        if (!rusher.Character.IsExpansion && _rushConfiguration.Destination.Act > 4)
        {
            throw new ValidationException("Classic characters cannot target act 5");
        }

        var destination = _rushConfiguration.Destination;
        var classic = !rusher.Character.IsExpansion;

        // The act 1 to 4 chain is driven by live quest state and carries _gameDifficulty into every
        // map, pathing and waypoint call, and the game difficulty itself comes from the host's own
        // progression. None of it is specific to Normal, so Classic supports the same shapes on
        // Nightmare, and "Hell Act 1 Enter" is simply "Nightmare finished".
        var throughTheActs = (destination.Act == 3 && destination.Milestone == RushMilestone.Enter)
            || (destination.Act == 4 && (destination.Milestone == RushMilestone.Enter || classic));
        var startOfADifficulty = destination.Act == 1
            && destination.Milestone == RushMilestone.Enter
            && destination.Difficulty > Difficulty.Normal;

        var supported = classic
            ? (destination.Difficulty <= Difficulty.Nightmare && throughTheActs) || startOfADifficulty
            : destination.Difficulty == Difficulty.Normal && throughTheActs;
        if (!supported)
        {
            throw new ValidationException(
                "Production rush supports the Classic Act 1 through Hell Act 1 path: Act 3 Enter or "
                + "Act 4 Enter/Complete on Normal or Nightmare, and Act 1 Enter on Nightmare or Hell. "
                + "Expansion is limited to Normal, and Act 5 is not implemented.");
        }
    }

    private async Task<Client> EnterHostGame(RosterEntry hostEntry, List<Client> clients)
    {
        var host = await LoginAndSelect(hostEntry.Account);
        clients.Add(host);
        AcceptPartyInvites(host);
        InviteJoiningPlayers(host);

        const int createAttempts = 10;

        string gameName = null;
        var created = false;
        for (var attempt = 0; attempt < createAttempts && !created; attempt++)
        {
            gameName = $"{_botConfiguration.GameNamePrefix}{_gameNumber++}";
            created = await host.CreateGame(
                _gameDifficulty,
                gameName,
                _botConfiguration.GamePassword,
                _botConfiguration.GameDescriptions[0]);
            if (!created)
            {
                Log.Warning("Could not create {Game}; rolling on to the next game number", gameName);
                await Task.Delay(1000);
            }
        }

        if (!created)
        {
            throw new InvalidOperationException(
                $"Least-progressed host {hostEntry.Character.Name} failed to create a game on {_gameDifficulty} "
                + $"after {createAttempts} names up to {gameName}");
        }

        if (!GeneralHelpers.TryWithTimeout(_ => host.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException($"Host {hostEntry.Character.Name} never initialized in {gameName}");
        }

        host.Game.CleanupCursorItem();
        await Task.Delay(500);
        CurrentGameName = gameName;
        Log.Information(
            "Least-progressed eligible rushee {Character} hosts {Game} on {Difficulty}",
            hostEntry.Character.Name,
            gameName,
            _gameDifficulty);
        return host;
    }

    private async Task<Client> JoinParticipant(AccountConfig account, List<Client> clients, bool attachChicken)
    {
        const int joinAttempts = 3;

        var client = await LoginAndSelect(account);
        clients.Add(client);
        _accountOf[client] = account;
        _partyClients.Add(client);
        if (attachChicken)
        {
            ChickenService.Attach(client, account.Chicken ?? _botConfiguration.Chicken);
        }

        AcceptPartyInvites(client);

        var joined = false;
        for (var attempt = 1; attempt <= joinAttempts && !joined; attempt++)
        {
            joined = await client.JoinGame(CurrentGameName, _botConfiguration.GamePassword);
            if (joined || attempt == joinAttempts)
            {
                break;
            }

            Log.Warning(
                "{Character} failed to join {Game} on attempt {Attempt}/{Attempts}; retrying",
                account.Character,
                CurrentGameName,
                attempt,
                joinAttempts);
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (!await client.RejoinMCP())
            {
                throw new InvalidOperationException(
                    $"{account.Character} failed to reconnect to the realm after joining {CurrentGameName}");
            }
        }

        if (!joined)
        {
            throw new InvalidOperationException(
                $"{account.Character} failed to join {CurrentGameName} after {joinAttempts} attempts");
        }

        if (!GeneralHelpers.TryWithTimeout(_ => client.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException($"{account.Character} never initialized in {CurrentGameName}");
        }

        client.Game.CleanupCursorItem();
        await Task.Delay(500);
        return client;
    }

    private async Task<Client> LoginAndSelect(AccountConfig account)
    {
        var client = new Client();
        if (!client.Connect(_botConfiguration.Realm, _botConfiguration.KeyOwner, _botConfiguration.GameFolder))
        {
            throw new InvalidOperationException($"Could not connect {account.Username} to the realm");
        }

        var characters = await client.Login(account.Username, account.Password);
        var character = characters?.FirstOrDefault(candidate =>
            candidate.Name.Equals(account.Character, StringComparison.OrdinalIgnoreCase));
        if (character == null)
        {
            client.Disconnect();
            throw new InvalidOperationException($"Character {account.Character} was not found on {account.Username}");
        }

        await client.SelectCharacter(character);
        return client;
    }

    private async Task VerifyDestinationGame(
        List<RosterEntry> targets,
        List<ParticipantSession> existingSessions,
        List<Client> clients)
    {
        var sessionsByName = existingSessions.ToDictionary(
            session => session.Entry.Character.Name,
            StringComparer.OrdinalIgnoreCase);
        var targetClients = new List<(RosterEntry Entry, Client Client)>();
        foreach (var entry in targets.OrderBy(candidate => candidate.Progression.CompletedActs))
        {
            if (sessionsByName.TryGetValue(entry.Character.Name, out var session))
            {
                if (session.Client.Game.IsInGame())
                {
                    await session.Client.Game.LeaveGame();
                }

                await Task.Delay(750);
                session.Client.Disconnect();
                await Task.Delay(1000);
            }

            var client = await LoginAndSelect(entry.Account);
            clients.Add(client);
            var character = client.SelectedCharacter();
            if (!CharacterProgression.TryCreate(character, out var progression))
            {
                throw new InvalidOperationException(
                    $"{character.Name} has unknown progression byte 0x{character.Progression:X2} after the source game");
            }

            var refreshedEntry = new RosterEntry(entry.Account, character, progression);
            targetClients.Add((refreshedEntry, client));
            Log.Information(
                "Refreshed roster {Character}: progression {Progression} before destination proof",
                character.Name,
                character.Progression);
        }

        var host = targetClients[0];
        var gameName = $"{_botConfiguration.GameNamePrefix}{_gameNumber++}";
        var proofDifficulty = DestinationProofDifficulty();
        var created = await host.Client.CreateGame(
            proofDifficulty,
            gameName,
            _botConfiguration.GamePassword,
            _botConfiguration.GameDescriptions[0]);
        if (!created)
        {
            throw new InvalidOperationException(
                $"Progression says {Describe(host.Entry)} satisfies the destination, but the realm rejected "
                + $"{proofDifficulty} game creation. This is a progression/protocol contradiction.");
        }

        if (!GeneralHelpers.TryWithTimeout(_ => host.Client.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException($"{host.Entry.Character.Name} created {gameName} but never entered it");
        }

        foreach (var participant in targetClients.Skip(1))
        {
            if (!await participant.Client.JoinGame(gameName, _botConfiguration.GamePassword))
            {
                throw new InvalidOperationException(
                    $"Progression says {Describe(participant.Entry)} satisfies the destination, but the realm rejected "
                    + $"joining {gameName} on {proofDifficulty}. "
                    + "This is a progression/protocol contradiction.");
            }

            if (!GeneralHelpers.TryWithTimeout(_ => participant.Client.Game.Me != null, TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException(
                    $"{participant.Entry.Character.Name} joined {gameName} but never initialized");
            }

            await Task.Delay(750);
        }

        if (proofDifficulty == _rushConfiguration.Destination.Difficulty
            && _rushConfiguration.Destination.Milestone == RushMilestone.Enter)
        {
            foreach (var participant in targetClients)
            {
                if (participant.Client.Game.Act < _rushConfiguration.Destination.ActValue)
                {
                    throw new InvalidOperationException(
                        $"{participant.Entry.Character.Name} entered {proofDifficulty} but spawned in "
                        + $"{participant.Client.Game.Act}, before requested {_rushConfiguration.Destination.ActValue}. "
                        + "This is a progression/protocol contradiction.");
                }
            }
        }

        Log.Information(
            "Verified {Count} target rushees in {Difficulty} game {Game}; host joined first and participants serialized",
            targetClients.Count,
            proofDifficulty,
            gameName);
    }

    private async Task VerifyParty(Client host, Client rusher, List<Client> rushees)
    {
        var allClients = new List<Client> { host, rusher };
        allClients.AddRange(rushees.Where(client => client != host));

        foreach (var client in allClients.Where(client => client != host))
        {
            var player = host.Game.Players.FirstOrDefault(candidate =>
                candidate.Name.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            if (player == null)
            {
                throw new InvalidOperationException($"Host cannot see {client.Game.Me.Name} in {CurrentGameName}");
            }

            var joined = await GeneralHelpers.TryWithTimeout(async attempt =>
            {
                if (host.Game.IsInPartyWith(player))
                {
                    return true;
                }

                if (attempt % 4 == 0)
                {
                    host.Game.InvitePlayer(player);
                    var hostSeenByClient = client.Game.Players.FirstOrDefault(candidate =>
                        candidate.Name.Equals(host.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
                    if (hostSeenByClient != null)
                    {
                        client.Game.InvitePlayer(hostSeenByClient);
                    }
                }

                await Task.Delay(250);
                return false;
            }, TimeSpan.FromSeconds(25));

            if (!joined)
            {
                throw new InvalidOperationException(
                    $"{host.Game.Me.Name} and {client.Game.Me.Name} were not verified in the same party");
            }
        }

        Log.Information("Verified all {Count} rush clients in party {PartyId}", allClients.Count, host.Game.Me.PartyId);
    }

    private async Task<bool> RushAndariel(Client rusher, List<ParticipantSession> rushees)
    {
        if (!await _townManagementService.SwitchAct(rusher, Act.Act1))
        {
            Log.Error("Rusher could not reach the Rogue Encampment");
            return false;
        }

        var preStages = StartRusheePreStages(rushees);
        StartCrewPreStages(Act.Act1);
        await BuffPartyInTown(rusher, Act.Act1);

        if (!await TakeWaypointFromTown(rusher, Waypoint.CatacombsLevel2)
            || !await WalkFromWaypointToArea(
                rusher,
                Area.CatacombsLevel2,
                Waypoint.CatacombsLevel2,
                Area.CatacombsLevel3)
            || !await TraverseTo(rusher, Area.CatacombsLevel4))
        {
            Log.Error("Rusher could not reach Catacombs level 4");
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Rusher could not open the Catacombs level 4 entrance portal");
            return false;
        }

        foreach (var session in rushees)
        {
            if (!await preStages[session])
            {
                Log.Warning(
                    "{Rushee} could not complete concurrent Act 1 pre-staging; retrying at the portal barrier",
                    session.Client.Game.Me.Name);
            }

            if (!await FerryRushee(rusher, session.Client, Area.CatacombsLevel4))
            {
                Log.Error("{Rushee} could not enter Catacombs level 4", session.Client.Game.Me.Name);
                return false;
            }
        }

        var needKill = rushees
            .Where(session => !HasKillCredit(session.Client, QuestId.SistersToTheSlaughter))
            .ToList();
        if (needKill.Count > 0)
        {
            await BringCrewTo(rusher, Area.CatacombsLevel4);
            if (!await MoveToNamedNpc(rusher, NPCCode.Andarial)
                || !await VerifyCreditBarrier(
                    rusher,
                    needKill,
                    Area.CatacombsLevel4,
                    QuestId.SistersToTheSlaughter)
                || !await KillNamedBoss(rusher, NPCCode.Andarial, BossKillLimit))
            {
                return false;
            }
        }

        foreach (var session in rushees)
        {
            if (!await VerifyAndarielCredit(session.Client)
                || !await CompleteActOneWithWarriv(rusher, session.Client))
            {
                return false;
            }
        }

        if (!await ConfirmInArea(rusher, Area.RogueEncampment) && !await ReturnToTown(rusher))
        {
            Log.Error("Rusher could not return to the Rogue Encampment after Andariel");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async _ =>
        {
            rusher.Game.RequestUpdate(rusher.Game.Me.Id);
            await Task.Delay(300);
            return rusher.Game.IsInTown()
                && await _pathingService.IsNavigatablePointInArea(
                    rusher.Game.MapId,
                    MapDifficulty,
                    Area.RogueEncampment,
                    rusher.Game.Me.Location);
        }, TimeSpan.FromSeconds(10)))
        {
            Log.Error("Rusher's town state did not settle after returning from Catacombs level 4");
            return false;
        }

        return true;
    }

    private async Task<bool> WalkFromWaypointToArea(
        Client client,
        Area from,
        Waypoint waypoint,
        Area destination)
    {
        var movementMode = GetMovementMode(client);
        var path = await _pathingService.GetPathFromWaypointToArea(
            client.Game.MapId,
            MapDifficulty,
            from,
            waypoint,
            destination,
            movementMode);

        // The route is computed from where the waypoint drops you, which is only where the character
        // actually stands if it took that waypoint. Arriving on foot - which now happens whenever the
        // waypoint is not held on this difficulty - leaves it somewhere else entirely, and the first
        // hop of the route was a 252 unit teleport that silently did nothing. Close that gap by
        // pathing to the start of the route first.
        if (path.Count > 0 && !await MoveTo(client, path[0], movementMode))
        {
            Log.Error(
                "Rusher could not reach the start of the {Destination} route at {Start} from {Location}",
                destination,
                path[0],
                client.Game.Me.Location);
            return false;
        }

        if (!await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode))
        {
            Log.Error("Rusher failed to move from {Waypoint} toward {Destination}", waypoint, destination);
            return false;
        }

        var warp = client.Game.GetNearestWarp();
        if (warp == null || warp.Location.Distance(client.Game.Me.Location) > 25)
        {
            Log.Error("No warp to {Destination} is within reach at {Location}", destination, client.Game.Me.Location);
            return false;
        }

        await MovementHelpers.TakeWarp(
            client.Game,
            _pathingService,
            _mapApiService,
            movementMode,
            warp,
            destination);
        return await ConfirmInArea(client, destination);
    }

    private static async Task<bool> VerifyAndarielCredit(Client rushee)
    {
        var died = rushee.Game.Me.Life == 0;
        rushee.Game.RequestQuestData();
        await Task.Delay(TimeSpan.FromSeconds(2));

        var creditedBeforeResurrection = HasKillCredit(rushee, QuestId.SistersToTheSlaughter);
        Log.Information(
            "{Rushee} was {State} after Andariel; credit before resurrection={Credited}, flags=0x{Flags:X4}",
            rushee.Game.Me.Name,
            died ? "dead" : "alive",
            creditedBeforeResurrection,
            rushee.Game.Quests.GetCharacterFlags(QuestId.SistersToTheSlaughter));

        if (died)
        {
            rushee.Game.Resurrect();
            if (!GeneralHelpers.TryWithTimeout(_ => rushee.Game.Me.Life > 0, TimeSpan.FromSeconds(10)))
            {
                Log.Error("{Rushee} did not resurrect after the Andariel credit check", rushee.Game.Me.Name);
                return false;
            }

            await Task.Delay(1000);
            rushee.Game.RequestQuestData();
        }

        var credited = GeneralHelpers.TryWithTimeout(
            _ => HasKillCredit(rushee, QuestId.SistersToTheSlaughter),
            TimeSpan.FromSeconds(10));
        if (!credited)
        {
            Log.Error(
                "{Rushee} received no Andariel credit; flags=0x{Flags:X4}",
                rushee.Game.Me.Name,
                rushee.Game.Quests.GetCharacterFlags(QuestId.SistersToTheSlaughter));
        }

        return credited;
    }

    private async Task<bool> CompleteActOneWithWarriv(Client rusher, Client rushee)
    {
        if (rushee.Game.Me.Life == 0)
        {
            Log.Error("{Rushee} is still dead after Andariel credit handling", rushee.Game.Me.Name);
            return false;
        }

        if (!await ConfirmInArea(rushee, Area.RogueEncampment))
        {
            var rusherAsSeen = rushee.Game.Players.FirstOrDefault(player =>
                player.Name.Equals(rusher.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            if (rusherAsSeen == null
                || !await _townManagementService.TakeTownPortalToArea(
                    rushee,
                    rusherAsSeen,
                    Area.RogueEncampment)
                || !await ConfirmInArea(rushee, Area.RogueEncampment))
            {
                Log.Error("{Rushee} could not return through the rusher's portal to the Rogue Encampment",
                    rushee.Game.Me.Name);
                return false;
            }
        }

        var path = await _pathingService.GetPathToNPC(rushee.Game, NPCCode.WarrivAct1, MovementMode.Walking);
        if (path.Count > 0
            && !await MovementHelpers.TakePathOfLocations(rushee.Game, path, MovementMode.Walking))
        {
            Log.Error("{Rushee} could not walk to Warriv", rushee.Game.Me.Name);
            return false;
        }

        var warriv = await WaitForNpc(rushee, NPCCode.WarrivAct1);
        if (warriv == null)
        {
            Log.Error("Warriv was not visible to {Rushee}", rushee.Game.Me.Name);
            return false;
        }

        await rushee.Game.MoveToAsync(warriv);
        if (!HasFinished(rushee, QuestId.SistersToTheSlaughter))
        {
            rushee.Game.InitiateEntityChat(warriv);
            await Task.Delay(500);
            rushee.Game.SendQuestMessage(warriv, WarrivsAndarielMessage);
            await Task.Delay(500);
            rushee.Game.TerminateEntityChat(warriv);

            if (!GeneralHelpers.TryWithTimeout(
                _ => HasFinished(rushee, QuestId.SistersToTheSlaughter),
                TimeSpan.FromSeconds(5)))
            {
                Log.Error("Warriv did not settle Andariel for {Rushee}", rushee.Game.Me.Name);
                return false;
            }
        }

        rushee.Game.InitiateEntityChat(warriv);
        await Task.Delay(500);
        rushee.Game.TravelWithNpc(warriv);
        if (!await GeneralHelpers.TryWithTimeout(
            async _ =>
            {
                rushee.Game.RequestUpdate(rushee.Game.Me.Id);
                await Task.Delay(300);
                return await _pathingService.IsNavigatablePointInArea(
                    rushee.Game.MapId,
                    MapDifficulty,
                    Area.LutGholein,
                    rushee.Game.Me.Location);
            },
            TimeSpan.FromSeconds(15)))
        {
            Log.Error("Warriv did not take {Rushee} to Lut Gholein", rushee.Game.Me.Name);
            return false;
        }

        Log.Information("{Rushee} completed Act 1 and reached Lut Gholein", rushee.Game.Me.Name);
        return true;
    }

    private async Task<bool> PrepareHoradricStaff(Client rusher, Client rushee)
    {
        if (HasFinished(rushee, QuestId.TheHoradricStaff))
        {
            Log.Information("{Rushee} has already spent a Horadric Staff", rushee.Game.Me.Name);
            return true;
        }

        if (!await ConfirmInArea(rushee, Area.LutGholein))
        {
            Log.Error("{Rushee} must be in Lut Gholein before collecting the staff", rushee.Game.Me.Name);
            return false;
        }

        D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.ShaftoftheHoradricStaff);
        D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.TopoftheHoradricStaff);
        D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.HoradricStaff);

        if (!await _townManagementService.SwitchAct(rusher, Act.Act2))
        {
            Log.Error("Rusher could not reach Lut Gholein to collect the Horadric Staff");
            return false;
        }

        if (!Holds(rushee, ItemName.HoradricCube)
            && !await Timed("act2-cube", () => EscortToQuestItem(
                rusher,
                rushee,
                Waypoint.HallsOfTheDeadLevel2,
                Area.HallsOfTheDeadLevel3,
                EntityCode.HoradricCubeChest,
                ItemName.HoradricCube)))
        {
            return false;
        }

        if (!Holds(rushee, ItemName.ShaftoftheHoradricStaff)
            && !Holds(rushee, ItemName.HoradricStaff)
            && !await Timed("act2-staff-of-kings", () => EscortToQuestItem(
                rusher,
                rushee,
                Waypoint.FarOasis,
                Area.MaggotLairLevel3,
                EntityCode.StaffOfKingsChest,
                ItemName.ShaftoftheHoradricStaff)))
        {
            return false;
        }

        if (!Holds(rushee, ItemName.TopoftheHoradricStaff)
            && !Holds(rushee, ItemName.HoradricStaff)
            && !await Timed("act2-amulet", () => EscortToQuestItem(
                rusher,
                rushee,
                Waypoint.LostCity,
                Area.ClawViperTempleLevel2,
                EntityCode.ViperAmuletAltar,
                ItemName.TopoftheHoradricStaff)))
        {
            return false;
        }

        return TransmuteStaff(rushee);
    }

    private async Task<bool> EscortToQuestItem(
        Client rusher,
        Client rushee,
        Waypoint waypoint,
        Area area,
        EntityCode containerCode,
        ItemName itemName)
    {
        if (Holds(rushee, itemName))
        {
            return true;
        }

        var preStage = StartRusheePreStage(rushee);
        if (!await TakeWaypointFromTown(rusher, waypoint)
            || !await TraverseTo(rusher, area)
            || !await MoveToObject(rusher, containerCode)
            || !await ClearAround(rusher, 20, TimeSpan.FromSeconds(240)))
        {
            Log.Error("Rusher could not secure {Container} in {Area}", containerCode, area);
            return false;
        }

        if (!await MoveToObject(rusher, containerCode))
        {
            Log.Error("Rusher could not return to {Container} before opening the portal", containerCode);
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Rusher could not open a portal beside {Container}", containerCode);
            return false;
        }

        if (!await preStage)
        {
            Log.Warning(
                "{Rushee} could not complete concurrent pre-staging for {Item}; retrying at the portal barrier",
                rushee.Game.Me.Name,
                itemName);
        }

        var enteredArea = false;
        for (var attempt = 1; attempt <= 2 && !enteredArea; attempt++)
        {
            // A town portal is created during play, so it is absent from map data and pathing to it
            // returns nothing: MoveToAsync then fails instantly without sending a packet and the
            // rushee stands still clicking a portal it never walked to. FerryRushee is the version
            // that refreshes position and falls back to MoveToWorldObject, so use it here too.
            enteredArea = await FerryRushee(rusher, rushee, area);
            if (!enteredArea && attempt == 1)
            {
                Log.Warning(
                    "{Rushee} could not use the first portal beside {Container}; recreating it",
                    rushee.Game.Me.Name,
                    containerCode);
                if (!await MoveToObject(rusher, containerCode)
                    || !await _townManagementService.CreateTownPortal(rusher))
                {
                    break;
                }

                await Task.Delay(500);
            }
        }

        if (!enteredArea)
        {
            Log.Error("{Rushee} could not reach {Container} in {Area}", rushee.Game.Me.Name, containerCode, area);
            return false;
        }

        var taken = await RusheeTakesFromContainer(rusher, rushee, containerCode, itemName);
        var rusherAsSeen = rushee.Game.Players.First(player =>
            player.Name.Equals(rusher.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        var town = WayPointHelpers.MapTownArea(rushee.Game.Act);
        if (!await _townManagementService.TakeTownPortalToArea(rushee, rusherAsSeen, town)
            || !await ConfirmInArea(rushee, town))
        {
            Log.Error("{Rushee} could not return home from {Area}", rushee.Game.Me.Name, area);
            return false;
        }

        if (!taken)
        {
            return false;
        }

        if (!await ReturnToTown(rusher))
        {
            Log.Error("Rusher could not return to town from {Area}", area);
            return false;
        }

        return true;
    }

    private async Task<bool> RusheeTakesFromContainer(
        Client rusher,
        Client rushee,
        EntityCode containerCode,
        ItemName itemName)
    {
        var expectedContainer = rusher.Game.GetEntityByCode(containerCode).FirstOrDefault();
        WorldObject container = null;
        if (expectedContainer == null
            || !await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(100);
                container = rushee.Game.GetEntityByCode(containerCode)
                    .FirstOrDefault(candidate => candidate.Location.Distance(expectedContainer.Location) <= 3);
                return container != null;
            }, TimeSpan.FromSeconds(10)))
        {
            Log.Error(
                "{Rushee} cannot see the live {Container} from {Location}",
                rushee.Game.Me.Name,
                containerCode,
                rushee.Game.Me.Location);
            return false;
        }

        Item onGround = null;
        var interactionRange = containerCode == EntityCode.ViperAmuletAltar ? 4 : 2;
        var opened = await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            await ClearTick(rusher, 30);
            if (rushee.Game.Me.Life == 0)
            {
                return false;
            }

            rushee.Game.RequestUpdate(rushee.Game.Me.Id);
            await Task.Delay(100);
            if (rushee.Game.Me.Location.Distance(container.Location) > interactionRange)
            {
                if (attempt % 3 == 0)
                {
                    await MovementHelpers.MoveToWorldObject(
                        rushee.Game,
                        _pathingService,
                        _mapApiService,
                        container,
                        MovementMode.Walking);
                }
                else
                {
                    await rushee.Game.MoveToAsync(container);
                }

                return false;
            }

            if (attempt % 4 == 0)
            {
                container = rushee.Game.GetEntityByCode(containerCode)
                    .FirstOrDefault(candidate => candidate.Location.Distance(expectedContainer.Location) <= 3);
                if (container == null)
                {
                    return false;
                }

                rushee.Game.InteractWithEntity(container);
            }

            await Task.Delay(200);
            onGround = rushee.Game.Items.Values.FirstOrDefault(item => item.Ground && item.Name == itemName);
            return onGround != null;
        }, TimeSpan.FromSeconds(40));
        if (!opened)
        {
            Log.Error("{Container} produced no {Item} for {Rushee}", containerCode, itemName, rushee.Game.Me.Name);
            return false;
        }

        var picked = await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            await ClearTick(rusher, 30);
            if (Holds(rushee, itemName))
            {
                return true;
            }

            rushee.Game.RequestUpdate(rushee.Game.Me.Id);
            await Task.Delay(100);
            if (rushee.Game.Me.Location.Distance(onGround.Location) > 2)
            {
                await rushee.Game.MoveToAsync(onGround.Location);
                return false;
            }

            rushee.Game.PickupItem(onGround);
            await Task.Delay(200);
            if (rushee.Game.CursorItem != null)
            {
                rushee.Game.CleanupCursorItem();
                await Task.Delay(200);
            }

            return Holds(rushee, itemName);
        }, TimeSpan.FromSeconds(25));
        if (!picked)
        {
            Log.Error(
                "{Rushee} failed to pick up {Item}; location={Location}, cursor={Cursor}",
                rushee.Game.Me.Name,
                itemName,
                rushee.Game.Me.Location,
                rushee.Game.CursorItem?.Name.ToString() ?? "nothing");
        }

        return picked;
    }

    private static bool Holds(Client client, ItemName itemName)
    {
        return client.Game.Inventory.FindItemByName(itemName) != null
            || client.Game.Cube.FindItemByName(itemName) != null
            || client.Game.Me.Equipment.Values.Any(item => item.Name == itemName)
            || client.Game.CursorItem?.Name == itemName;
    }

    private static bool UnequipIntoInventory(Client client, ItemName itemName)
    {
        var slot = client.Game.Me.Equipment.FirstOrDefault(entry => entry.Value.Name == itemName);
        var worn = slot.Value;
        if (worn == null)
        {
            return true;
        }

        var space = client.Game.Inventory.FindFreeSpace(worn);
        if (space == null)
        {
            Log.Error("No inventory space to remove {Item}", itemName);
            return false;
        }

        client.Game.RemoveBodyItem(slot.Key);
        if (!GeneralHelpers.TryWithTimeout(
            _ => client.Game.CursorItem?.Id == worn.Id,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Removing {Item} from {Slot} did not place it on the cursor", itemName, slot.Key);
            return false;
        }

        client.Game.InsertItemIntoContainer(worn, space, ItemContainer.Inventory);
        if (!GeneralHelpers.TryWithTimeout(
            _ => client.Game.CursorItem == null && client.Game.Inventory.FindItemById(worn.Id) != null,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Moving {Item} into the inventory failed; restoring it to {Slot}", itemName, slot.Key);
            client.Game.EquipItem(worn, slot.Key);
            return false;
        }

        return true;
    }

    private static bool TransmuteStaff(Client client)
    {
        var game = client.Game;
        if (!EnsureItemInInventory(client, ItemName.HoradricStaff))
        {
            return false;
        }

        if (game.Inventory.FindItemByName(ItemName.HoradricStaff) != null)
        {
            return true;
        }

        if (!UnequipIntoInventory(client, ItemName.TopoftheHoradricStaff)
            || !UnequipIntoInventory(client, ItemName.ShaftoftheHoradricStaff))
        {
            return false;
        }

        Item shaft = game.Inventory.FindItemByName(ItemName.ShaftoftheHoradricStaff);
        Item top = game.Inventory.FindItemByName(ItemName.TopoftheHoradricStaff);
        if (shaft == null || top == null)
        {
            Log.Error(
                "{Character} cannot transmute a staff without both pieces; shaft={Shaft}, top={Top}",
                game.Me.Name,
                shaft != null,
                top != null);
            return false;
        }

        Item[] pieces = [shaft, top];
        foreach (var piece in pieces)
        {
            var space = game.Cube.FindFreeSpace(piece);
            if (space == null
                || InventoryHelpers.PutInventoryItemInCube(game, piece, space) != MoveItemResult.Succes)
            {
                Log.Error("Moving {Piece} into the Horadric Cube failed", piece.Name);
                return false;
            }
        }

        if (!InventoryHelpers.TransmuteItemsInCube(game, true))
        {
            Log.Error("Transmuting the Horadric Staff failed");
            return false;
        }

        var staff = game.Cube.FindItemByName(ItemName.HoradricStaff);
        if (staff == null
            || InventoryHelpers.PutCubeItemInInventory(game, staff) != MoveItemResult.Succes)
        {
            Log.Error("The transmuted Horadric Staff could not be moved into inventory");
            return false;
        }

        Log.Information("{Character} assembled the Horadric Staff", game.Me.Name);
        return true;
    }

    private static bool EnsureItemInInventory(Client client, ItemName itemName)
    {
        var game = client.Game;
        if (game.Inventory.FindItemByName(itemName) != null)
        {
            return true;
        }

        if (!UnequipIntoInventory(client, itemName))
        {
            return false;
        }

        if (game.Inventory.FindItemByName(itemName) != null)
        {
            return true;
        }

        var cubeItem = game.Cube.FindItemByName(itemName);
        if (cubeItem != null
            && InventoryHelpers.PutCubeItemInInventory(game, cubeItem) != MoveItemResult.Succes)
        {
            Log.Error("Moving {Item} from the Horadric Cube into inventory failed", itemName);
            return false;
        }

        if (game.CursorItem?.Name == itemName)
        {
            game.CleanupCursorItem();
        }

        return game.Inventory.FindItemByName(itemName) != null
            || !Holds(client, itemName);
    }

    private async Task<bool> RushSevenTombs(Client rusher, List<ParticipantSession> rushees)
    {
        if (rusher.Game.Quests.IsSettledForGame(QuestId.TheSevenTombs))
        {
            Log.Error("The host's Seven Tombs state is already settled in this game");
            return false;
        }

        var staffBearer = rushees.FirstOrDefault(session =>
            EnsureItemInInventory(session.Client, ItemName.HoradricStaff)
            && session.Client.Game.Inventory.FindItemByName(ItemName.HoradricStaff) != null);
        var spentStaff = rushees.All(session => HasFinished(session.Client, QuestId.TheHoradricStaff));
        if (staffBearer == null && !spentStaff)
        {
            Log.Error("No eligible rushee is carrying the Horadric Staff");
            return false;
        }

        var preStages = StartRusheePreStages(rushees);
        StartCrewPreStages(Act.Act2);
        await BuffPartyInTown(rusher, Act.Act2);

        if (!await _townManagementService.SwitchAct(rusher, Act.Act2))
        {
            Log.Error("The rusher could not reach Lut Gholein to prepare for the tombs");
            return false;
        }

        // Restock before the tombs. A Nightmare run ended here with "Right out of healing and
        // rejuvenation potions at 205 of 974 life": the rusher chickened out inside the tomb, so no
        // portal was ever opened and the rushee waited 36s for one that could not come.
        await RestockRusher(rusher, Act.Act2);

        if (!await TakeWaypointFromTown(rusher, Waypoint.CanyonOfTheMagi))
        {
            Log.Error("Rusher could not reach the Canyon of the Magi");
            return false;
        }

        var tomb = await ResolveTombWithOrifice(rusher);
        if (tomb == null
            || !await TraverseTo(rusher, tomb.Value)
            || !await MoveToObject(rusher, EntityCode.HoradricOrifice)
            || !await ClearAround(rusher, 18, TimeSpan.FromSeconds(60))
            || !await MoveToObject(rusher, EntityCode.HoradricOrifice))
        {
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Rusher could not open a portal beside the orifice in {Tomb}", tomb);
            return false;
        }

        // Crew goes through the tomb portal before the rushee, so it is standing in the tomb when the
        // lair opens instead of walking there while the fight happens. A barbarian has no teleport and
        // took the whole fight to cross when it was fetched afterwards.
        await BringCrewTo(rusher, tomb.Value);

        foreach (var session in rushees)
        {
            if (!await preStages[session])
            {
                Log.Warning(
                    "{Rushee} could not complete concurrent tomb pre-staging; retrying at the portal barrier",
                    session.Client.Game.Me.Name);
            }

            if (!await FerryRushee(rusher, session.Client, tomb.Value))
            {
                Log.Error("{Rushee} could not reach the true tomb", session.Client.Game.Me.Name);
                return false;
            }
        }

        if (staffBearer != null
            && !await RusheeInsertsStaff(rusher, staffBearer.Client))
        {
            return false;
        }

        // Shout before anyone goes down. Once the party is in the lair CastAllShouts has nobody in
        // range to buff, so doing it afterwards achieves nothing.
        await BuffFromCrew();

        if (!await EnterDurielsLair(rusher))
        {
            return false;
        }

        // Into the lair on the rusher's heels, the same moment it opens.
        foreach (var member in _crew.Where(candidate => candidate.Game.IsInGame()))
        {
            if (!await EnterDurielsLair(member))
            {
                Log.Warning("{Member} could not follow into Duriel's lair", member.Game.Me.Name);
            }
        }

        await DrawDurielAwayFromPortal(rusher);

        foreach (var session in rushees)
        {
            if (!await EnterDurielsLair(session.Client))
            {
                Log.Error(
                    "{Rushee} could not enter Duriel's lair; refusing to consume the protected kill",
                    session.Client.Game.Me.Name);
                return false;
            }
        }

        if (!await VerifyCreditBarrier(rusher, rushees, Area.DurielsLair, QuestId.TheSevenTombs)
            || !await KillNamedBoss(rusher, NPCCode.Duriel, BossKillLimit))
        {
            return false;
        }

        foreach (var session in rushees)
        {
            if (!await VerifySevenTombsCredit(session.Client))
            {
                return false;
            }
        }

        await Task.Delay(2000);
        foreach (var session in rushees)
        {
            if (!await CompleteActTwo(rusher, session.Client))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> RusheeInsertsStaff(Client rusher, Client rushee)
    {
        var orifice = rushee.Game.GetEntityByCode(EntityCode.HoradricOrifice).FirstOrDefault();
        if (orifice == null)
        {
            Log.Error("{Rushee} cannot see the Horadric Orifice", rushee.Game.Me.Name);
            return false;
        }

        var staff = rushee.Game.Inventory.FindItemByName(ItemName.HoradricStaff);
        if (!await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await ClearTick(rusher, 18);
            rushee.Game.RequestUpdate(rushee.Game.Me.Id);
            await Task.Delay(100);
            if (rushee.Game.Me.Location.Distance(orifice.Location) <= 4)
            {
                return true;
            }

            await rushee.Game.MoveToAsync(orifice);
            return false;
        }, TimeSpan.FromSeconds(20)))
        {
            Log.Error("{Rushee} could not move within four units of the orifice", rushee.Game.Me.Name);
            return false;
        }

        if (rushee.Game.GetEntityByCode(EntityCode.DurielsLairPortal).Count > 0)
        {
            return true;
        }

        if (staff == null)
        {
            Log.Error("{Rushee} has neither a staff nor an open lair entrance", rushee.Game.Me.Name);
            return false;
        }

        rushee.Game.InteractWithEntity(orifice);
        await Task.Delay(500);
        rushee.Game.InteractWithEntity(orifice);
        await Task.Delay(500);
        rushee.Game.RemoveItemFromContainer(staff);
        if (!GeneralHelpers.TryWithTimeout(
            _ => rushee.Game.CursorItem?.Id == staff.Id,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("{Rushee} could not move the staff onto the cursor", rushee.Game.Me.Name);
            return false;
        }

        rushee.Game.InsertHoradricStaff(orifice, staff);
        var inserted = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await ClearTick(rusher, 18);
            await Task.Delay(300);
            return rushee.Game.GetEntityByCode(EntityCode.DurielsLairPortal).Count > 0;
        }, TimeSpan.FromSeconds(30));
        if (!inserted)
        {
            Log.Error("No lair entrance appeared after {Rushee} inserted the staff", rushee.Game.Me.Name);
        }

        return inserted;
    }

    private async Task DrawDurielAwayFromPortal(Client rusher)
    {
        var entry = rusher.Game.Me.Location;
        (int Dx, int Dy)[] directions =
        [
            (0, 1),
            (1, 0),
            (0, -1),
            (-1, 0),
            (1, 1),
            (1, -1),
            (-1, 1),
            (-1, -1)
        ];
        int[] ranges = [DurielPullDistance, 26, 18];

        foreach (var range in ranges)
        {
            foreach (var direction in directions)
            {
                var candidate = new Point(
                    (ushort)Math.Clamp(entry.X + (direction.Dx * range), 0, ushort.MaxValue),
                    (ushort)Math.Clamp(entry.Y + (direction.Dy * range), 0, ushort.MaxValue));
                await MoveTo(rusher, candidate, GetMovementMode(rusher));
                rusher.Game.RequestUpdate(rusher.Game.Me.Id);
                await Task.Delay(200);
                if (rusher.Game.Me.Location.Distance(entry) > MinimumPullDistance)
                {
                    Log.Information("Pulled Duriel away from the arrival portal to {Location}",
                        rusher.Game.Me.Location);
                    return;
                }
            }
        }

        Log.Warning("Could not pull Duriel away from the arrival portal at {Location}", entry);
    }

    private static async Task<bool> VerifySevenTombsCredit(Client rushee)
    {
        var died = rushee.Game.Me.Life == 0;
        rushee.Game.RequestQuestData();
        var credited = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await Task.Delay(500);
            return HasDurielCredit(rushee);
        }, TimeSpan.FromSeconds(10));

        if (!credited && died)
        {
            Log.Information(
                "{Rushee} received no quest pushes while dead; resurrecting and refreshing Seven Tombs",
                rushee.Game.Me.Name);
            rushee.Game.Resurrect();
            await Task.Delay(2000);
            rushee.Game.RequestQuestData();
            credited = await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(500);
                return HasDurielCredit(rushee);
            }, TimeSpan.FromSeconds(10));
        }

        if (!credited)
        {
            Log.Error(
                "{Rushee} received no Seven Tombs credit; flags=0x{Flags:X4}",
                rushee.Game.Me.Name,
                rushee.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs));
            return false;
        }

        if (rushee.Game.Me.Life == 0)
        {
            rushee.Game.Resurrect();
            await Task.Delay(1500);
        }

        return true;
    }

    private async Task<bool> CompleteActTwo(Client rusher, Client rushee)
    {
        rushee.Game.RequestUpdate(rushee.Game.Me.Id);
        await Task.Delay(500);
        var inLair = await _pathingService.IsNavigatablePointInArea(
            rushee.Game.MapId,
            MapDifficulty,
            Area.DurielsLair,
            rushee.Game.Me.Location);
        if (!inLair)
        {
            if (!await _townManagementService.CreateTownPortal(rusher))
            {
                Log.Error("Rusher could not reopen a portal to Duriel's lair");
                return false;
            }

            var rusherAsSeen = rushee.Game.Players.FirstOrDefault(player =>
                player.Name.Equals(rusher.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            if (rusherAsSeen == null || !await FerryRushee(rusher, rushee, Area.DurielsLair))
            {
                Log.Error("{Rushee} could not return to Duriel's lair for Tyrael", rushee.Game.Me.Name);
                return false;
            }
        }

        if (!await TalkToTyrael(rushee)
            || !await ReturnToLutGholein(rushee)
            || !await TalkToJerhyn(rushee)
            || !await TravelWithMeshif(rushee))
        {
            Log.Error("Act 2 completion failed for {Rushee}", rushee.Game.Me.Name);
            return false;
        }

        return true;
    }

    private async Task<bool> RunFromAuthoritativeState(Client rusher, List<ParticipantSession> rushees)
    {
        await RefreshQuestData(rushees);

        var incompleteActOne = rushees
            .Where(session => session.Client.Game.Act <= Act.Act1
                && !HasFinished(session.Client, QuestId.Act1Outro))
            .ToList();
        if (incompleteActOne.Count > 0 && !await Timed("act1-andariel", () => RushAndariel(rusher, incompleteActOne)))
        {
            return false;
        }

        await RefreshQuestData(rushees);
        var incompleteActTwo = rushees
            .Where(session => !HasFinished(session.Client, QuestId.Act2Outro)
                && session.Client.Game.Act <= Act.Act2)
            .ToList();

        if (incompleteActTwo.Count > 0)
        {
            foreach (var session in incompleteActTwo.Where(session =>
                !HasFinished(session.Client, QuestId.TheHoradricStaff)
                && !HasDurielCredit(session.Client)))
            {
                if (!await Timed($"act2-staff-{session.Client.Game.Me.Name}", () => PrepareHoradricStaff(rusher, session.Client)))
                {
                    return false;
                }
            }

            await RefreshQuestData(incompleteActTwo);
            var needDuriel = incompleteActTwo
                .Where(session => !HasDurielCredit(session.Client))
                .ToList();
            if (needDuriel.Count > 0 && !await Timed("act2-duriel", () => RushSevenTombs(rusher, needDuriel)))
            {
                return false;
            }

            await RefreshQuestData(incompleteActTwo);
            var postDuriel = incompleteActTwo
                .Where(session => !HasFinished(session.Client, QuestId.Act2Outro)
                    && session.Client.Game.Act <= Act.Act2)
                .ToList();
            if (postDuriel.Count > 0)
            {
                if (postDuriel.Any(session => !HasDurielCredit(session.Client)))
                {
                    Log.Error(
                        "Act 2 progression stopped before Duriel credit: {Evidence}",
                        string.Join("; ", postDuriel.Select(DescribeQuestState)));
                    return false;
                }

                if (!await Timed("act2-completion", () => RepairPostDurielTransition(rusher, postDuriel)))
                {
                    return false;
                }
            }
        }

        await RefreshQuestData(rushees);
        if (_rushConfiguration.Destination.Difficulty == Difficulty.Normal
            && _rushConfiguration.Destination.Act == 3
            && _rushConfiguration.Destination.Milestone == RushMilestone.Enter)
        {
            return rushees.All(session => session.Client.Game.Act >= Act.Act3);
        }

        var needActThree = rushees
            .Where(session => !HasFinished(session.Client, QuestId.Act3Outro)
                && session.Client.Game.Act <= Act.Act3)
            .ToList();
        if (needActThree.Count > 0 && !await Timed("act3-mephisto", () => RushMephisto(rusher, needActThree)))
        {
            return false;
        }

        await RefreshQuestData(rushees);
        if (_rushConfiguration.Destination.Difficulty == Difficulty.Normal
            && _rushConfiguration.Destination.Act == 4
            && _rushConfiguration.Destination.Milestone == RushMilestone.Enter)
        {
            return rushees.All(session => session.Client.Game.Act >= Act.Act4);
        }

        // Diablo dies and Tyrael pays out in the same game - the reference capture of a real client
        // reaching progression 4 has the whole Chaos Sanctuary fight and the Tyrael conversation in
        // one session. The acknowledged bit a character carries in from an earlier game is not a
        // substitute, so the kill happens every run.
        // A rehearsing rushee spawns in whatever act it last saved in; the production path arrives in
        // act 4 through Mephisto's portal and needs no help.
        if (_rushConfiguration.RehearsalDifficulty != null)
        {
            foreach (var session in rushees.Where(session => session.Client.Game.Act != Act.Act4))
            {
                if (!await _townManagementService.SwitchAct(session.Client, Act.Act4))
                {
                    Log.Error("{Rushee} could not reach act 4 for the rehearsal", session.Client.Game.Me.Name);
                    return false;
                }
            }
        }

        await SweepLoot(rusher, 25);
        if (!await Timed("act4-diablo", () => RushDiablo(rusher, rushees)))
        {
            return false;
        }

        // Diablo's own drop, which nothing collected before: the sweeps ran after each seal and before
        // this fight, never after it.
        await SweepLoot(rusher, 30);

        await RefreshQuestData(rushees);
        foreach (var session in rushees)
        {
            if (!await Timed($"act4-tyrael-{session.Client.Game.Me.Name}", () => CompleteActFourWithTyrael(rusher, session.Client)))
            {
                return false;
            }
        }
        await RefreshQuestData(rushees);
        await RefreshQuestData(rushees);
        if (!rushees.All(session => HasDiabloCredit(session.Client)))
        {
            Log.Error("Act 4 completion did not settle for every rushee: {Evidence}",
                string.Join("; ", rushees.Select(DescribeQuestState)));
            return false;
        }

        return true;
    }

    private async Task<bool> RepairPostDurielTransition(Client rusher, List<ParticipantSession> rushees)
    {
        if (!await _townManagementService.SwitchAct(rusher, Act.Act2)
            || !await _townManagementService.TakeWaypoint(rusher, Waypoint.CanyonOfTheMagi))
        {
            Log.Error("Rusher could not reach the Canyon of the Magi waypoint");
            return false;
        }

        var tomb = await ResolveTombWithOrifice(rusher);
        if (tomb == null)
        {
            return false;
        }

        if (!await TraverseTo(rusher, tomb.Value)
            || !await MoveToObject(rusher, EntityCode.HoradricOrifice)
            || !await EnterDurielsLair(rusher))
        {
            return false;
        }

        var encounterReady = GeneralHelpers.TryWithTimeout(
            _ => rusher.Game.GetNPCsByCode(NPCCode.Duriel).Count > 0
                || rusher.Game.GetNPCsByCode(NPCCode.TyraelAct3).Count > 0,
            TimeSpan.FromSeconds(15));
        if (!encounterReady)
        {
            Log.Error("Neither Duriel nor Tyrael appeared in Duriel's lair");
            return false;
        }

        // The portal is opened before the fight, not after it. Everyone else rides it down instead of
        // walking a level, and it is the only way the crew reaches a lair it is not already standing in.
        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Rusher could not open the lair portal for the rest of the party");
            return false;
        }

        // The crew walks down while the rusher pulls Duriel off the portal, so the lure and the walk
        // overlap. Duriel parked on the entrance kills a rushee the moment it lands.
        var crewArriving = BringCrewTo(rusher, Area.DurielsLair);
        await DrawDurielAwayFromPortal(rusher);
        await crewArriving;

        // Only now the rushee, onto an entrance Duriel has been drawn away from.
        foreach (var session in rushees)
        {
            if (!await FerryRushee(rusher, session.Client, Area.DurielsLair))
            {
                Log.Error("{Rushee} could not enter Duriel's lair", session.Client.Game.Me.Name);
                return false;
            }
        }

        if (rusher.Game.GetNPCsByCode(NPCCode.Duriel).Count > 0
            && !await KillNamedBoss(rusher, NPCCode.Duriel, BossKillLimit))
        {
            Log.Error("Rusher could not re-kill Duriel to spawn Tyrael");
            return false;
        }

        await Task.Delay(2000);

        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Rusher could not open the recovery portal in Duriel's lair");
            return false;
        }

        foreach (var session in rushees)
        {
            var rushee = session.Client;
            if (!await TalkToTyrael(rushee)
                || !await ReturnToLutGholein(rushee)
                || !await TalkToJerhyn(rushee)
                || !await TravelWithMeshif(rushee))
            {
                Log.Error("Post-Duriel transition failed for {Rushee}: {Evidence}",
                    rushee.Game.Me.Name, DescribeQuestState(session));
                return false;
            }
        }

        return true;
    }

    private async Task<bool> RushMephisto(Client rusher, List<ParticipantSession> rushees)
    {
        var preStages = StartRusheePreStages(rushees);
        StartCrewPreStages(Act.Act3);
        await BuffPartyInTown(rusher, Act.Act3);

        if (!await _townManagementService.SwitchAct(rusher, Act.Act3)
            || !await TakeWaypointFromTown(rusher, Waypoint.DuranceOfHateLevel2)
            || !await EnterDuranceThreeFromWaypoint(rusher))
        {
            Log.Error("Rusher could not reach Durance of Hate level 3");
            return false;
        }

        var staging = await MephistoStagingSpot(rusher);
        if (staging == null)
        {
            Log.Error("No verified safe Mephisto staging spot exists between Mephisto and the red portal");
            return false;
        }

        if (!await MoveTo(rusher, staging, GetMovementMode(rusher)))
        {
            Log.Error("Could not reach verified Mephisto staging spot {Spot}; refusing to open an entrance portal", staging);
            return false;
        }

        if (!await ClearAround(rusher, 20, TimeSpan.FromSeconds(90))
            || !await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Could not establish a safe Durance staging portal");
            return false;
        }

        foreach (var session in rushees)
        {
            if (!await preStages[session])
            {
                Log.Warning(
                    "{Rushee} could not complete concurrent act 3 pre-staging; retrying at the portal barrier",
                    session.Client.Game.Me.Name);
            }

            if (session.Client.Game.Act != Act.Act3)
            {
                Log.Error("{Rushee} must be in act 3 before the Mephisto checkpoint: {Evidence}",
                    session.Client.Game.Me.Name, DescribeQuestState(session));
                return false;
            }

        }

        // One portal, everyone at once. Ferried one after another this cost 11s for the rushee and then
        // another 14s for the crew, all of it while the rusher stood waiting at Mephisto.
        var crewArriving = BringCrewTo(rusher, Area.DuranceOfHateLevel3);
        var rusheesArriving = rushees
            .Select(session => FerryRushee(rusher, session.Client, Area.DuranceOfHateLevel3))
            .ToList();
        await Task.WhenAll(rusheesArriving);
        await crewArriving;

        if (rusheesArriving.Any(arrival => !arrival.Result))
        {
            Log.Error("A rushee could not reach the Durance staging area");
            return false;
        }

        if (!await MoveToNamedNpc(rusher, NPCCode.Mephisto))
        {
            return false;
        }

        foreach (var session in rushees)
        {
            await StageAtRedPortal(rusher, session.Client);
        }

        if (!await VerifyCreditBarrier(rusher, rushees, Area.DuranceOfHateLevel3, QuestId.TheGuardian))
        {
            return false;
        }

        if (!await KillNamedBoss(rusher, NPCCode.Mephisto, BossKillLimit))
        {
            return false;
        }

        var resurrected = new List<ParticipantSession>();
        foreach (var session in rushees.Where(session => session.Client.Game.Me.Life == 0))
        {
            session.Client.Game.Resurrect();
            resurrected.Add(session);
            await Task.Delay(1500);
        }

        await RefreshQuestData(rushees);
        foreach (var session in rushees)
        {
            if (!HasKillCredit(session.Client, QuestId.TheGuardian))
            {
                Log.Error("{Rushee} was present for Mephisto but received no Guardian credit: {Evidence}",
                    session.Client.Game.Me.Name, DescribeQuestState(session));
                return false;
            }
        }

        if (resurrected.Count > 0 && !await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Could not reopen the Durance portal for resurrected rushees");
            return false;
        }

        foreach (var session in resurrected)
        {
            if (!await FerryRushee(rusher, session.Client, Area.DuranceOfHateLevel3))
            {
                Log.Error("{Rushee} could not return to the Durance after resurrection",
                    session.Client.Game.Me.Name);
                return false;
            }
        }

        return await TakeRedPortalToActFour(rusher, rushees);
    }

    private async Task<bool> RushDiablo(Client rusher, List<ParticipantSession> rushees)
    {
        if (!rusher.Game.Me.HasSkill(Skill.Teleport))
        {
            Log.Error("The production Chaos Sanctuary path requires a teleporting rusher");
            return false;
        }

        foreach (var session in rushees)
        {
            if (session.Client.Game.Me.Life == 0)
            {
                session.Client.Game.Resurrect();
                await Task.Delay(1000);
                await GeneralHelpers.PickupCorpseIfExists(session.Client, _pathingService);
            }

            if (session.Client.Game.Act != Act.Act4)
            {
                Log.Error("{Rushee} must be in act 4 before Diablo: {Evidence}",
                    session.Client.Game.Me.Name, DescribeQuestState(session));
                return false;
            }
        }

        var preStages = StartRusheePreStages(rushees);
        StartCrewPreStages(Act.Act4);
        await BuffPartyInTown(rusher, Act.Act4);

        if (!await _townManagementService.SwitchAct(rusher, Act.Act4))
        {
            Log.Error("The rusher could not reach the Pandemonium Fortress to prepare for Chaos Sanctuary");
            return false;
        }

        var account = _rushConfiguration.Rusher;
        var townOptions = new TownManagementOptions(account, Act.Act4)
        {
            HealthPotionsToBuy = PotionsToBuy(
                rusher.Game,
                rusher.Game.Belt.Height * account.HealthSlots.Count,
                rusher.Game.Belt.NumOfHealthPotions(),
                InventoryHelpers.GetTotalHealthPotions(rusher.Game),
                HealthReserve),
            ManaPotionsToBuy = PotionsToBuy(
                rusher.Game,
                rusher.Game.Belt.Height * account.ManaSlots.Count,
                rusher.Game.Belt.NumOfManaPotions(),
                InventoryHelpers.GetTotalManaPotions(rusher.Game),
                ManaReserve)
        };
        var townResult = await _townManagementService.PerformTownTasks(rusher, townOptions);
        if (!townResult.Succes)
        {
            Log.Error("The rusher could not restock before Chaos Sanctuary");
            return false;
        }

        Point lastAnchor = null;
        var diabloAppeared = false;

        // Diablo needs all five seals opened and all three seal bosses dead. A boss that walks out of
        // streaming range is removed exactly like one that died, so only a boss seen dying counts: in
        // rb42 De Seis wandered off, the seal was called done and the whole circuit ran again.
        var openedSeals = new HashSet<EntityCode>();
        var killedGroups = new HashSet<int>();
        for (var attempt = 1; attempt <= 3 && !diabloAppeared; attempt++)
        {
            if (!await ReturnToSanctuary(rusher))
            {
                return false;
            }

            List<EntityCode> revisit = null;
            if (attempt > 1)
            {
                revisit = ChaosSeals
                    .Where(candidate => !openedSeals.Contains(candidate)
                        || !killedGroups.Contains(SealGroups[candidate]))
                    .ToList();
                if (revisit.Count == 0)
                {
                    revisit = null;
                }
                else
                {
                    Log.Information("Seal pass {Attempt} revisits {Seals}", attempt, string.Join(", ", revisit));
                }
            }

            lastAnchor = await RunSealCircuit(rusher, openedSeals, killedGroups, revisit);
            if (!rusher.Game.IsInGame() && !await RejoinIfChickened(rusher))
            {
                Log.Error("The rusher left the game during the Chaos Sanctuary seal circuit");
                return false;
            }

            if (lastAnchor == null)
            {
                continue;
            }

            // Diablo only comes when all three seal bosses are dead, so waiting a minute at the star
            // with one still alive is a minute thrown away - and it is exactly what happened whenever
            // De Seis walked out of view. Go straight back to the seal that still owes a boss.
            var groupsNeeded = SealGroups.Values.Distinct().Count();
            if (killedGroups.Count < groupsNeeded)
            {
                Log.Information(
                    "Not waiting for Diablo: {Missing} of {Needed} seal bosses are still unaccounted for",
                    groupsNeeded - killedGroups.Count,
                    groupsNeeded);

                // A boss that died out of view leaves no death packet, so give it a moment in case one
                // is already up before walking the seals again.
                diabloAppeared = GeneralHelpers.TryWithTimeout(
                    _ => rusher.Game.GetNPCsByCode(NPCCode.Diablo).Count > 0,
                    TimeSpan.FromSeconds(2));
                if (!diabloAppeared)
                {
                    continue;
                }
            }

            if (!await MoveToObject(rusher, EntityCode.DiabloStar))
            {
                continue;
            }

            diabloAppeared = GeneralHelpers.TryWithTimeout(
                _ => rusher.Game.GetNPCsByCode(NPCCode.Diablo).Count > 0,
                TimeSpan.FromSeconds(60));
            if (!diabloAppeared)
            {
                Log.Warning(
                    "Diablo did not appear after seal pass {Attempt}; walking the seals again for a surviving boss",
                    attempt);
            }
        }

        if (!diabloAppeared || lastAnchor == null)
        {
            Log.Error("Diablo did not appear after three complete seal passes");
            return false;
        }

        // Diablo is up, so it is finally safe to bring a rushee across. Handing over the portal any
        // earlier parks a level one on uncleared ground for the whole circuit, and a seal boss that
        // survives sends the rusher back for another pass while the rushee stands there alone.
        // A fresh portal, never a remembered one. Every seal casts a portal for the crew and each cast
        // replaces the last, so the one recorded on the first pass was long gone by the time the rushee
        // went looking: it swept sixteen town points for ninety seconds and the kill was refused.
        if (!await MoveTo(rusher, lastAnchor, MovementMode.Teleport)
            || !await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Error("Could not open the Chaos Sanctuary portal at the cleared anchor");
            return false;
        }

        Log.Information("Chaos Sanctuary portal is open at the cleared anchor {Anchor}", lastAnchor);

        foreach (var session in rushees)
        {
            if (!await preStages[session])
            {
                Log.Warning(
                    "{Rushee} could not complete concurrent act 4 pre-staging; retrying at the portal barrier",
                    session.Client.Game.Me.Name);
            }

            // The portal is already open at the cleared anchor, but the rushee only steps through once
            // Diablo is actually up. Sending it in early leaves a level one standing in the sanctuary
            // through the spawn, and a seal that failed to pop can still be retried while it waits.
            if (!GeneralHelpers.TryWithTimeout(
                    _ => rusher.Game.GetNPCsByCode(NPCCode.Diablo).Count > 0,
                    TimeSpan.FromSeconds(30)))
            {
                Log.Warning("Diablo has not appeared yet; bringing {Rushee} in anyway to hold its credit",
                    session.Client.Game.Me.Name);
            }

            if (!await FerryRushee(rusher, session.Client, Area.ChaosSanctuary))
            {
                Log.Error("Refusing to kill Diablo without {Rushee} in the credit area",
                    session.Client.Game.Me.Name);
                return false;
            }
        }

        await BringCrewTo(rusher, Area.ChaosSanctuary);

        if (!await VerifyCreditBarrier(rusher, rushees, Area.ChaosSanctuary, QuestId.TerrorsEnd))
        {
            Log.Error("Refusing to kill Diablo because the party/area credit barrier failed");
            return false;
        }

        // Bring the rushee to the star as well, rather than leaving it at the anchor a hundred units
        // away while Diablo dies. Being in the area is enough for the kill credit, but every character
        // seen to actually collect the act 4 reward from Tyrael was standing at the fight.
        if (!await MoveToObject(rusher, EntityCode.DiabloStar))
        {
            return false;
        }

        foreach (var session in rushees)
        {
            if (!await MoveTo(session.Client, rusher.Game.Me.Location, GetMovementMode(session.Client)))
            {
                Log.Warning(
                    "{Rushee} could not close on the star; killing Diablo with it at range",
                    session.Client.Game.Me.Name);
            }
        }

        if (!await KillDiablo(rusher))
        {
            return false;
        }

        foreach (var session in rushees.Where(session => session.Client.Game.Me.Life == 0))
        {
            session.Client.Game.Resurrect();
            await Task.Delay(1500);
            await GeneralHelpers.PickupCorpseIfExists(session.Client, _pathingService);
        }

        await RefreshQuestData(rushees);
        foreach (var session in rushees)
        {
            if (!HasDiabloCredit(session.Client))
            {
                Log.Error("{Rushee} was present for Diablo but received no Terror's End credit: {Evidence}",
                    session.Client.Game.Me.Name, DescribeQuestState(session));
                return false;
            }
        }

        foreach (var session in rushees)
        {
            session.Client.Game.CompleteQuest(QuestId.TerrorsEnd);
            session.Client.Game.RequestQuestData();
        }

        await Task.Delay(1000);
        return true;
    }

    private async Task<bool> CompleteActFourWithTyrael(Client rusher, Client rushee)
    {
        if (rushee.Game.Me.Life == 0)
        {
            rushee.Game.Resurrect();
            await Task.Delay(1500);
            await GeneralHelpers.PickupCorpseIfExists(rushee, _pathingService);
        }
        else if (!rushee.Game.IsInTown())
        {
            var rusherAsSeen = rushee.Game.Players.FirstOrDefault(player =>
                player.Name.Equals(rusher.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            if (rusherAsSeen == null
                || !await _townManagementService.TakeTownPortalToArea(
                    rushee,
                    rusherAsSeen,
                    Area.ThePandemoniumFortress))
            {
                Log.Error(
                    "{Rushee} could not return through the rusher's portal to the Pandemonium Fortress for Tyrael",
                    rushee.Game.Me.Name);
                return false;
            }
        }

        if (!await ConfirmInArea(rushee, Area.ThePandemoniumFortress))
        {
            Log.Error("{Rushee} is not in the Pandemonium Fortress for Tyrael", rushee.Game.Me.Name);
            return false;
        }

        var path = await _pathingService.GetPathToNPC(rushee.Game, NPCCode.TyraelAct4, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rushee.Game, path, MovementMode.Walking);
        }

        var tyrael = await WaitForNpc(rushee, NPCCode.TyraelAct4);
        if (tyrael == null)
        {
            Log.Error("Act 4 Tyrael was not visible to {Rushee}", rushee.Game.Me.Name);
            return false;
        }

        await rushee.Game.MoveToAsync(tyrael);

        // Go through InteractWithNPC rather than opening the chat directly: it sends the
        // MakeEntityMove that a real client sends before the conversation, and the direct path
        // skipped it. Every failed act 4 attempt was missing exactly that packet.
        rushee.Game.InteractWithNPC(tyrael);
        await Task.Delay(200);
        rushee.Game.InitiateEntityChat(tyrael);
        GeneralHelpers.TryWithTimeout(
            _ => ActFourTyraelMessages.All(message => rushee.Game.GetOfferedMessages(tyrael).Contains(message)),
            TimeSpan.FromSeconds(2));
        var offeredMessages = rushee.Game.GetOfferedMessages(tyrael);
        Log.Information(
            "Act 4 Tyrael offers {Messages} to {Rushee}",
            string.Join(", ", offeredMessages.Select(message => $"0x{message:X4}")),
            rushee.Game.Me.Name);

        // Tyrael answers InitiateEntityChat with NPCWantInteract and never sends an NPCInfo, so his
        // messages are never advertised - the same silence Warriv keeps. The ids below are captured,
        // so they go out regardless and the quest state is what proves they landed.
        // Say only what the reference client says. Following the ids Tyrael advertises is what broke
        // this before: 0x0298 and 0x029E walk him into a different branch that answers with 0x029F,
        // and 0x02AC is then ignored, so act 4 never closes.
        foreach (var message in ActFourTyraelMessages)
        {
            rushee.Game.SendQuestMessage(tyrael, message);
            await Task.Delay(400);
        }

        Log.Information(
            "{Rushee} said {Messages} to Act 4 Tyrael",
            rushee.Game.Me.Name,
            string.Join(", ", ActFourTyraelMessages.Select(message => $"0x{message:X4}")));

        // The captured client closes with a second conversation: terminate twice, interact again,
        // open and close the chat once more, and only then leave.
        await Task.Delay(500);
        rushee.Game.TerminateEntityChat(tyrael);
        rushee.Game.TerminateEntityChat(tyrael);
        await Task.Delay(200);
        rushee.Game.InteractWithNPC(tyrael);
        await Task.Delay(200);
        rushee.Game.InitiateEntityChat(tyrael);
        await Task.Delay(1000);
        rushee.Game.TerminateEntityChat(tyrael);
        await Task.Delay(300);
        rushee.Game.RequestQuestData();
        await Task.Delay(1000);

        Log.Information(
            "{Rushee} interacted with Act 4 Tyrael; terror=0x{Terror:X4}, outro=0x{Outro:X4}",
            rushee.Game.Me.Name,
            rushee.Game.Quests.GetCharacterFlags(QuestId.TerrorsEnd),
            rushee.Game.Quests.GetCharacterFlags(QuestId.Act4Outro));

        if (!HasFinished(rushee, QuestId.Act4Outro))
        {
            // Not fatal on its own: what act 4 completion really has to survive is the destination
            // proof, where the realm either lets this character into a Nightmare game or does not.
            Log.Warning(
                "{Rushee} finished the Tyrael conversation but Act 4 outro still reads 0x{Outro:X4}",
                rushee.Game.Me.Name,
                rushee.Game.Quests.GetCharacterFlags(QuestId.Act4Outro));
        }

        return true;
    }

    private async Task<bool> VerifyCreditBarrier(
        Client rusher,
        List<ParticipantSession> rushees,
        Area area,
        QuestId quest)
    {
        if (rusher.Game.Quests.IsSettledForGame(quest))
        {
            if (_rushConfiguration.RehearsalDifficulty == null)
            {
                Log.Error(
                    "Refusing to complete {Quest}: host quest state settled this game before it started",
                    quest);
                return false;
            }

            Log.Warning("Rehearsal: {Quest} is already settled for the host; fighting anyway", quest);
        }

        foreach (var session in rushees)
        {
            var rushee = session.Client;
            if (!rusher.Game.IsInGame() || !rushee.Game.IsInGame())
            {
                Log.Error(
                    "Credit barrier failed for {Rushee} before {Quest}: a participant left the game",
                    rushee.Game.Me?.Name ?? session.Entry.Character.Name,
                    quest);
                return false;
            }

            rushee.Game.RequestUpdate(rushee.Game.Me.Id);
            await Task.Delay(300);
            var inArea = await _pathingService.IsNavigatablePointInArea(
                rushee.Game.MapId,
                MapDifficulty,
                area,
                rushee.Game.Me.Location);
            var rusherAsSeen = rushee.Game.Players.FirstOrDefault(player =>
                player.Name.Equals(rusher.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            var rusheeAsSeen = rusher.Game.Players.FirstOrDefault(player =>
                player.Name.Equals(rushee.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            var partied = rusherAsSeen != null
                && rusheeAsSeen != null
                && rushee.Game.IsInPartyWith(rusherAsSeen)
                && rusher.Game.IsInPartyWith(rusheeAsSeen);
            if (!inArea || !partied)
            {
                Log.Error(
                    "Credit barrier failed for {Rushee} before {Quest}: inArea={InArea}, partied={Partied}, location={Location}",
                    rushee.Game.Me.Name,
                    quest,
                    inArea,
                    partied,
                    rushee.Game.Me.Location);
                return false;
            }
        }

        return true;
    }

    private async Task<bool> FerryRushee(Client rusher, Client rushee, Area area)
    {
        var ferryTimer = Stopwatch.StartNew();
        var ferried = false;
        try
        {
            ferried = await FerryRusheeCore(rusher, rushee, area);
            return ferried;
        }
        finally
        {
            Log.Information(
                "TIMING ferry-{Area} for {Rushee} {Outcome} in {Seconds:0.0}s",
                area,
                rushee.Game.Me?.Name,
                ferried ? "ok" : "FAILED",
                ferryTimer.Elapsed.TotalSeconds);
        }
    }

    private async Task<bool> FerryRusheeCore(Client rusher, Client rushee, Area area)
    {
        var rusherAsSeen = rushee.Game.Players.FirstOrDefault(player =>
            player.Name.Equals(rusher.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        if (rusherAsSeen == null)
        {
            Log.Error("{Rushee} cannot see rusher {Rusher}", rushee.Game.Me.Name, rusher.Game.Me.Name);
            return false;
        }

        if (!await MoveRusheeToPortalSpot(rushee, rusherAsSeen, area))
        {
            return false;
        }

        if (await ConfirmInArea(rushee, area, attempts: 1))
        {
            return true;
        }

        // Arrival is only worth confirming after an interact, because that is the only thing that can
        // move the rushee between areas. Confirming at the top of every attempt cost a map API round
        // trip and half a second of sleep per step of a walk, which is most of why a level one takes
        // so long to cross to a portal.
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            if (!rusher.Game.IsInGame())
            {
                Log.Error(
                    "{Rusher} left the game, so no portal to {Area} is coming; abandoning the ferry",
                    rusher.Game.Me?.Name,
                    area);
                return false;
            }

            // Take the nearest matching portal, not the first. The rusher opens a portal more than
            // once, so the rushee can hold a dead record alongside the live one; picking the stale
            // one puts the target hundreds of units away and every move becomes a no-op while the
            // rushee is in fact standing on the real portal.
            rushee.Game.RequestUpdate(rushee.Game.Me.Id);
            await Task.Delay(300);
            var portal = rushee.Game.GetEntityByCode(EntityCode.TownPortal)
                .Where(candidate =>
                    candidate.TownPortalArea == area
                    && candidate.TownPortalOwnerId == rusherAsSeen.Id)
                .OrderBy(candidate => rushee.Game.Me.Location.Distance(candidate.Location))
                .FirstOrDefault();
            if (portal == null)
            {
                await Task.Delay(300);
                continue;
            }

            Log.Debug(
                "Ferry {Rushee} attempt {Attempt}: me={Me}, portal {PortalId} at {PortalLocation}, distance {Distance:0.0}",
                rushee.Game.Me.Name,
                attempt,
                rushee.Game.Me.Location,
                portal.Id,
                portal.Location,
                rushee.Game.Me.Location.Distance(portal.Location));

            // Mind the gap between the two move helpers. RushBot's MoveTo returns true without
            // sending anything inside TeleportNoOpRange, and Game.MoveToAsync returns false without
            // sending anything beyond 20 units, so a naive threshold leaves a band where the rushee
            // neither moves nor interacts - a 2 unit threshold stalled it for 20 passes at 2.8 units.
            // Interact inside PortalInteractRange, short-hop up to the no-op range, path beyond it.
            var gap = rushee.Game.Me.Location.Distance(portal.Location);
            if (gap > PortalInteractRange)
            {
                if (attempt % 3 == 0)
                {
                    await MovementHelpers.MoveToWorldObject(
                        rushee.Game,
                        _pathingService,
                        _mapApiService,
                        portal,
                        GetMovementMode(rushee));
                }
                else if (gap <= TeleportNoOpRange)
                {
                    await rushee.Game.MoveToAsync(portal.Location);
                }
                else
                {
                    await MoveTo(rushee, portal.Location, GetMovementMode(rushee));
                }

                continue;
            }

            rushee.Game.InteractWithEntity(portal);
            await Task.Delay(1200);
            if (await ConfirmInArea(rushee, area))
            {
                return true;
            }
        }

        return await ConfirmInArea(rushee, area);
    }

    private async Task<bool> MoveRusheeToPortalSpot(Client rushee, Player rusher, Area destination)
    {
        var spot = await ResolvePortalSpot(rushee);
        if (spot != null
            && await WalkRusheeTo(rushee, spot)
            && await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(250);
                return CanSeePortal(rushee, rusher, destination);
            }, TimeSpan.FromSeconds(3)))
        {
            return true;
        }

        Log.Information("No matching portal visible from the measured {Act} spot; sweeping town", rushee.Game.Act);
        return await FindPortalSpotBySweep(rushee, rusher, destination);
    }

    private async Task<bool> StartRusheePreStage(Client rushee)
    {
        await GeneralHelpers.PickupCorpseIfExists(rushee, _pathingService);
        HealRusheeIfHurt(rushee);
        var spot = await ResolvePortalSpot(rushee);
        if (spot == null)
        {
            return false;
        }

        var staged = await WalkRusheeTo(rushee, spot);
        if (staged)
        {
            Log.Information("{Rushee} is pre-staged at {Spot}", rushee.Game.Me.Name, spot);
        }

        return staged;
    }

    /// <summary>
    /// Tops a rushee up at the town healer before it is sent anywhere. Talking to the healer is free,
    /// which matters because a rushee has neither potions nor the gold to buy any, and it arrives in
    /// town on a fraction of its life every time it is resurrected.
    /// </summary>
    private static void HealRusheeIfHurt(Client rushee)
    {
        var me = rushee.Game.Me;
        if (me == null || me.MaxLife == 0 || !rushee.Game.IsInTown())
        {
            return;
        }

        if ((double)me.Life / me.MaxLife > RusheeHealBelowFraction)
        {
            return;
        }

        NPCHelpers.HealAtHealer(rushee.Game);
    }

    /// <summary>
    /// Gets the crew next to the rusher in town and shouts, before anyone leaves. Battle orders is
    /// what keeps a squishy rusher alive through a Nightmare clear, and it has to land before it
    /// departs - buffing at the boss is far too late, and the rusher was running its potions dry just
    /// clearing the tomb, then leaving before the crew was ever called for.
    /// </summary>
    private async Task BuffPartyInTown(Client rusher, Act act)
    {
        if (_crew.Count == 0 || !rusher.Game.IsInTown())
        {
            return;
        }

        foreach (var member in _crew.Where(candidate => candidate.Game.IsInGame()))
        {
            if (!member.Game.IsInTown() && !await ReturnToTown(member))
            {
                continue;
            }

            if (member.Game.Act != act && !await _townManagementService.SwitchAct(member, act))
            {
                continue;
            }

            // CastAllShouts only reaches players within ten units, so stand with them first.
            if (member.Game.Me.Location.Distance(rusher.Game.Me.Location) > 8)
            {
                await MoveTo(member, rusher.Game.Me.Location, GetMovementMode(member));
            }
        }

        await BuffFromCrew();
    }

    /// <summary>
    /// Walks the crew to the town portal spot while the rusher is still teleporting out, exactly as
    /// the rushees are pre-staged. Without this the crew only starts moving when it is called for, and
    /// a barbarian on foot is still crossing town when the fight is over.
    /// </summary>
    private void StartCrewPreStages(Act act)
    {
        _crewPreStages = _crew.Select(member => Task.Run(async () =>
        {
            if (!member.Game.IsInTown() && !await ReturnToTown(member))
            {
                return false;
            }

            if (member.Game.Act != act && !await _townManagementService.SwitchAct(member, act))
            {
                return false;
            }

            return await StartRusheePreStage(member);
        })).ToList();
    }

    private Dictionary<ParticipantSession, Task<bool>> StartRusheePreStages(
        IEnumerable<ParticipantSession> rushees)
    {
        return rushees.ToDictionary(
            session => session,
            session => StartRusheePreStage(session.Client));
    }

    private async Task<Point> ResolvePortalSpot(Client rushee)
    {
        var act = rushee.Game.Act;
        if (!TownWaypointByAct.TryGetValue(act, out var waypointCode))
        {
            return null;
        }

        var townArea = WayPointHelpers.MapTownArea(act);
        var town = await _mapApiService.GetArea(rushee.Game.MapId, MapDifficulty, townArea);
        var waypoint = town?.Objects?.GetValueOrDefault((int)waypointCode)?.FirstOrDefault();
        if (waypoint == null)
        {
            Log.Warning("Map API did not provide {Waypoint} in {Area}", waypointCode, townArea);
            return null;
        }

        if (act == Act.Act1)
        {
            var stash = town.Objects?.GetValueOrDefault((int)EntityCode.Stash)?.FirstOrDefault();
            if (stash == null)
            {
                return null;
            }

            var variant = (waypoint.X - stash.X, waypoint.Y - stash.Y);
            if (!ActOnePortalFromWaypoint.TryGetValue(variant, out var actOneOffset))
            {
                Log.Warning("Rogue Encampment portal variant {Variant} is not measured", variant);
                return null;
            }

            return OffsetOrNull(waypoint, actOneOffset.Dx, actOneOffset.Dy);
        }

        return TownPortalFromWaypointByAct.TryGetValue(act, out var offset)
            ? OffsetOrNull(waypoint, offset.Dx, offset.Dy)
            : null;
    }

    private static bool CanSeePortal(Client rushee, Player rusher, Area destination)
    {
        return rushee.Game.GetEntityByCode(EntityCode.TownPortal)
            .Any(portal => portal.TownPortalArea == destination && portal.TownPortalOwnerId == rusher.Id);
    }

    private async Task<bool> FindPortalSpotBySweep(Client rushee, Player rusher, Area destination)
    {
        var townArea = WayPointHelpers.MapTownArea(rushee.Game.Act);
        var town = await _mapApiService.GetArea(rushee.Game.MapId, MapDifficulty, townArea);
        if (town?.Objects == null)
        {
            Log.Error("Map API provided no town objects to sweep in {Area}", townArea);
            return false;
        }

        if (CanSeePortal(rushee, rusher, destination))
        {
            return true;
        }

        var candidates = town.Objects
            .SelectMany(entry => entry.Value.Select(point => (Code: entry.Key, Point: point)))
            .GroupBy(candidate => (candidate.Point.X / PortalSweepGrid, candidate.Point.Y / PortalSweepGrid))
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Point.Distance(rushee.Game.Me.Location))
            .Take(MaxPortalSweepCandidates)
            .ToList();

        foreach (var candidate in candidates)
        {
            // A portal owner who has left the game has no portal to find. Rope swept town for 75 seconds
            // in rb36 after the rusher had chickened, and the circuit waited on it.
            if (rushee.Game.Players.All(player => player.Id != rusher.Id))
            {
                Log.Warning("{Rushee} stopped sweeping town: portal owner {Owner} is no longer in the game", rushee.Game.Me.Name, rusher.Name);
                return false;
            }

            if (!await WalkRusheeTo(rushee, candidate.Point))
            {
                continue;
            }

            if (await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(250);
                return CanSeePortal(rushee, rusher, destination);
            }, TimeSpan.FromSeconds(2)))
            {
                var portal = rushee.Game.GetEntityByCode(EntityCode.TownPortal)
                    .First(entity =>
                        entity.TownPortalArea == destination
                        && entity.TownPortalOwnerId == rusher.Id);
                Log.Information(
                    "Found {Act} portal at {Portal}; visible from {Location}",
                    rushee.Game.Act,
                    portal.Location,
                    rushee.Game.Me.Location);
                return true;
            }
        }

        Log.Error(
            "{Rushee} swept {Count} town points without seeing the rusher portal to {Destination}",
            rushee.Game.Me.Name,
            candidates.Count,
            destination);
        return false;
    }

    private async Task<bool> WalkRusheeTo(Client rushee, Point destination)
    {
        rushee.Game.RequestUpdate(rushee.Game.Me.Id);
        await Task.Delay(200);
        if (rushee.Game.Me.Location.Distance(destination) <= TeleportNoOpRange)
        {
            return true;
        }

        var path = await _pathingService.GetPathToLocation(rushee.Game, destination, MovementMode.Walking);
        if (path.Count == 0
            || !await MovementHelpers.TakePathOfLocations(rushee.Game, path, MovementMode.Walking))
        {
            Log.Error(
                "{Rushee} failed to walk to {Destination}; stopped at {Location}",
                rushee.Game.Me.Name,
                destination,
                rushee.Game.Me.Location);
            return false;
        }

        return true;
    }

    private async Task<Point> MephistoStagingSpot(Client rusher)
    {
        var map = await _mapApiService.GetArea(rusher.Game.MapId, MapDifficulty, Area.DuranceOfHateLevel3);
        var mephisto = map?.Npcs?.GetValueOrDefault((int)NPCCode.Mephisto)?.FirstOrDefault();
        var gate = MapPointOf(map, EntityCode.PandemoniumFortressPortal)
            ?? rusher.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault()?.Location;
        if (mephisto == null || gate == null)
        {
            return null;
        }

        var staging = OffsetOrNull(
            gate,
            MephistoStagingOffsetFromRedPortalX,
            MephistoStagingOffsetFromRedPortalY);
        if (staging == null)
        {
            return null;
        }

        if (staging.Distance(mephisto) < MephistoStagingClearance
            || !await _pathingService.IsNavigatablePointInArea(
                rusher.Game.MapId, MapDifficulty, Area.DuranceOfHateLevel3, staging))
        {
            return null;
        }

        Log.Information(
            "Using fixed Mephisto staging spot {Spot}, anchored between red portal {Portal} and Mephisto {Mephisto}",
            staging,
            gate,
            mephisto);
        return staging;
    }

    private async Task StageAtRedPortal(Client rusher, Client rushee)
    {
        var portal = rusher.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
        if (portal == null)
        {
            return;
        }

        await MoveTo(rushee, portal.Location, GetMovementMode(rushee));
        rushee.Game.RequestUpdate(rushee.Game.Me.Id);
        await Task.Delay(500);
    }

    private async Task<bool> TakeRedPortalToActFour(Client rusher, List<ParticipantSession> rushees)
    {
        if (!await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await Task.Delay(500);
            return rusher.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).Count > 0;
        }, TimeSpan.FromSeconds(30)))
        {
            Log.Error("No red portal appeared after Mephisto died");
            return false;
        }

        var portalSpot = rusher.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal)
            .First().Location;
        var travellers = rushees.Select(session => session.Client).Append(rusher);
        foreach (var client in travellers)
        {
            var portal = client.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
            if (portal == null)
            {
                await MoveTo(client, portalSpot, GetMovementMode(client));
                client.Game.RequestUpdate(client.Game.Me.Id);
                await Task.Delay(700);
                portal = client.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
            }

            if (portal == null)
            {
                Log.Error("{Name} cannot see the red portal from {Location}",
                    client.Game.Me.Name, client.Game.Me.Location);
                return false;
            }

            var entered = await GeneralHelpers.TryWithTimeout(async attempt =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
                await Task.Delay(200);
                if (client.Game.Me.Location.Distance(portal.Location) > 5)
                {
                    if (attempt % 3 == 0)
                    {
                        await MovementHelpers.MoveToWorldObject(
                            client.Game, _pathingService, _mapApiService, portal, GetMovementMode(client));
                    }
                    else
                    {
                        await client.Game.MoveToAsync(portal);
                    }
                    return false;
                }

                client.Game.InteractWithEntity(portal);
                await Task.Delay(1500);
                return await ConfirmInArea(client, Area.ThePandemoniumFortress);
            }, TimeSpan.FromSeconds(30));
            if (!entered)
            {
                Log.Error("{Name} failed to take the red portal to act 4", client.Game.Me.Name);
                return false;
            }
        }

        return true;
    }

    private async Task<bool> MoveToNamedNpc(Client client, NPCCode npcCode)
    {
        var mode = GetMovementMode(client);
        var path = await _pathingService.GetPathToNPC(client.Game, npcCode, mode);
        if (path.Count == 0 || !await MovementHelpers.TakePathOfLocations(client.Game, path, mode))
        {
            Log.Error("Could not reach {Npc} in {Area}", npcCode, client.Game.Area);
            return false;
        }

        return true;
    }

    private async Task<bool> KillNamedBoss(Client rusher, NPCCode npcCode, TimeSpan limit)
    {
        if (!GeneralHelpers.TryWithTimeout(
            _ => rusher.Game.GetNPCsByCode(npcCode).Count > 0,
            TimeSpan.FromSeconds(10)))
        {
            Log.Error("Rusher cannot see {Npc}", npcCode);
            return false;
        }

        var boss = rusher.Game.GetNPCsByCode(npcCode).First();
        if (boss.State is EntityState.Dead or EntityState.Dieing)
        {
            Log.Error("{Npc} was already dead before the protected kill", npcCode);
            return false;
        }

        // Duriel is cold immune above Normal, so a frozen orb sorceress does literally nothing to him.
        // With crew on hand she sticks to static field and lets them kill it.
        // Being in the game is not being in the room. A client only receives entities from its own
        // area, so seeing the boss is the test - the crew sat in town while the rusher held static
        // field on a boss nothing could damage, and the fight only ended because a human joined it.
        var killersPresent = _crew
            .Where(member => member.Game.IsInGame() && member.Game.GetNPCsByCode(npcCode).Count > 0)
            .ToList();
        var coldIsUseless = ColdIsUselessAgainst(boss) && killersPresent.Count > 0;
        if (coldIsUseless)
        {
            Log.Information(
                "{Npc} is cold immune on {Difficulty}; the rusher sticks to static field and {Crew} kills it",
                npcCode,
                _gameDifficulty,
                string.Join("/", killersPresent.Select(member => member.Game.Me.Name)));
        }
        else if (ColdIsUselessAgainst(boss))
        {
            Log.Warning(
                "{Npc} is cold immune on {Difficulty} and no crew member is in the room; the rusher "
                + "statics it down and finishes with its weapon",
                npcCode,
                _gameDifficulty);
            coldIsUseless = true;
        }

        if (NeedsPotions(rusher))
        {
            await RestockMidRun(rusher, rusher.Game.Act);
        }

        await BuffFromCrew();
        using var crewAssist = StartCrewAssist(rusher, boss);

        var timer = Stopwatch.StartNew();
        await KeepArmorUp(rusher);
        var anchor = rusher.Game.Me.Location;
        var startingLife = boss.LifePercentage;
        var retreats = 0;
        var withdrawn = false;
        var lifeMoved = false;
        while (timer.Elapsed < limit && rusher.Game.IsInGame())
        {
            // Give up early on a boss that is taking no damage at all. A frozen orb sorceress against
            // cold-immune Nightmare Duriel does literally nothing, and the old behaviour was to tank
            // for the whole limit until the potions ran out and ChickenService lost the game. Failing
            // in 45s with the reason named is far better than failing in 300s without one.
            if (!lifeMoved && timer.Elapsed > NoDamageGraceperiod)
            {
                Log.Error(
                    "{Npc} has taken no observable damage in {Seconds:0}s - the rusher's attacks look "
                    + "ineffective against it, most likely an immunity. Abandoning rather than burning "
                    + "the whole {Limit:0}s limit.",
                    npcCode,
                    NoDamageGraceperiod.TotalSeconds,
                    limit.TotalSeconds);
                return false;
            }

            // Boss fights had no disengage at all, so the rusher stood and drank until it ran dry and
            // ChickenService took it out of the game - twice against Nightmare Duriel, at 205 and 266
            // of 974 life. Backing off to where the fight started lets potions and regeneration catch
            // up, and the kill limit is generous enough to absorb the pause.
            var self = rusher.Game.Me;
            if (self.MaxLife > 0 && (double)self.Life / self.MaxLife < RetreatBelowLifeFraction)
            {
                retreats++;

                // Shop before chickening. Retreating does not refill the belt, and the rusher was
                // repeatedly grinding down to ChickenService's floor and abandoning the game with it.
                if (NeedsPotions(rusher))
                {
                    if (await RestockMidRun(rusher, rusher.Game.Act))
                    {
                        continue;
                    }

                    // Fighting on from town is not fighting. The boss stops being visible, which reads
                    // as a kill in zero seconds and burns the rushee's credit along with the game.
                    if (rusher.Game.IsInTown())
                    {
                        Log.Error(
                            "{Rusher} is stranded in town after restocking; abandoning the fight rather "
                            + "than reporting a kill nobody was present for",
                            rusher.Game.Me.Name);
                        return false;
                    }
                }

                await MoveTo(rusher, anchor, GetMovementMode(rusher));
                GeneralHelpers.TryWithTimeout(
                    _ => rusher.Game.Me.MaxLife == 0
                        || (double)rusher.Game.Me.Life / rusher.Game.Me.MaxLife >= RecoveredLifeFraction,
                    TimeSpan.FromSeconds(6));

                // Twice driven off with crew on hand means this is not the rusher's fight. A cold-only
                // sorceress kept trading with cold-immune Nightmare Duriel until her potions ran out
                // and ChickenService took her - and the whole kill was gated on her, even though the
                // barbarian was the one doing damage. Withdraw and let the crew finish.
                if (retreats >= RetreatsBeforeWithdrawing && _crew.Any(member => member.Game.IsInGame()))
                {
                    withdrawn = true;
                    Log.Information(
                        "{Rusher} is withdrawing from {Npc} after {Retreats} retreats; the crew finishes it",
                        self.Name,
                        npcCode,
                        retreats);
                }

                continue;
            }

            if (withdrawn)
            {
                // Stay alive and out of reach. The rushee's credit is what matters, not who lands the
                // killing blow, and the crew assist tasks are still running.
                if (rusher.Game.Me.Location.Distance(boss.Location) < WithdrawnStandoffRange)
                {
                    await MoveTo(rusher, anchor, GetMovementMode(rusher));
                }

                await Task.Delay(500);
                continue;
            }

            var current = rusher.Game.WorldObjects.GetValueOrDefault((boss.Id, EntityType.NPC))
                ?? rusher.Game.GetNPCsByCode(npcCode).FirstOrDefault(candidate => candidate.Id == boss.Id);
            // LifePercentage only moves when the server sends an NPCState, so a stale record reads
            // exactly like an unharmed boss. Ask for a refresh before believing "no damage" - without
            // this the grace period abandons fights the crew is actually winning.
            if (current != null)
            {
                if (current.LifePercentage != startingLife)
                {
                    lifeMoved = true;
                }
                else if (timer.Elapsed > NoDamageProbeAfter)
                {
                    rusher.Game.RequestUpdate(current.Id);
                }
            }

            if (current == null || current.State is EntityState.Dead or EntityState.Dieing)
            {
                // Time to kill is the only measure of the attack rotation that matters. Attack counts
                // and skill switches are proxies and can point the wrong way.
                Log.Information(
                    "{Npc} died after {Seconds:0.0}s on {Difficulty}",
                    npcCode,
                    timer.Elapsed.TotalSeconds,
                    _gameDifficulty);
                return true;
            }

            await AttackTarget(rusher, current, coldIsUseless);
        }

        Log.Error("{Npc} was still alive after {Seconds:0}s", npcCode, limit.TotalSeconds);
        return false;
    }

    private async Task<bool> ClearAround(Client client, double radius, TimeSpan limit)
    {
        const int quietChecksNeeded = 8;
        var quietChecks = 0;
        var frozen = new Dictionary<uint, (string State, TimeSpan Since, bool Probed)>();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < limit)
        {
            var hostiles = await ClearTick(client, radius)
                - PruneStaleBlockers(client, radius, frozen, timer.Elapsed);
            if (hostiles <= 0)
            {
                if (++quietChecks >= quietChecksNeeded)
                {
                    return true;
                }
            }
            else
            {
                quietChecks = 0;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private async Task<int> ClearTick(Client client, double radius)
    {
        if (!client.Game.IsInGame())
        {
            return 0;
        }

        var hostiles = NearbyHostiles(client, radius);
        if (hostiles.Count == 0)
        {
            return 0;
        }

        await _attackService.AssistPlayer(client, client.Game.Me);
        var blocking = 0;
        foreach (var hostile in hostiles)
        {
            if (await _attackService.IsInLineOfSight(client, hostile.Location))
            {
                blocking++;
            }
        }

        return blocking;
    }

    private static int PruneStaleBlockers(
        Client client,
        double radius,
        Dictionary<uint, (string State, TimeSpan Since, bool Probed)> frozen,
        TimeSpan now)
    {
        var stale = 0;
        foreach (var hostile in NearbyHostiles(client, radius))
        {
            var state = $"{hostile.Location}/{hostile.LifePercentage:0.###}/{hostile.State}";
            if (!frozen.TryGetValue(hostile.Id, out var seen) || seen.State != state)
            {
                frozen[hostile.Id] = (state, now, false);
                continue;
            }

            var unchanged = now - seen.Since;
            if (!seen.Probed && unchanged > StaleBlockerProbeAfter)
            {
                client.Game.RequestUpdate(hostile.Id);
                frozen[hostile.Id] = (state, seen.Since, true);
                continue;
            }

            if (seen.Probed && unchanged > StaleBlockerIgnoreAfter)
            {
                stale++;
            }
        }

        return stale;
    }

    private async Task<bool> ReturnToSanctuary(Client rusher)
    {
        rusher.Game.RequestUpdate(rusher.Game.Me.Id);
        await Task.Delay(500);
        if (await _pathingService.IsNavigatablePointInArea(
            rusher.Game.MapId, MapDifficulty, Area.ChaosSanctuary, rusher.Game.Me.Location))
        {
            return true;
        }

        if (!await _townManagementService.SwitchAct(rusher, Act.Act4)
            || !await TakeWaypointFromTown(rusher, Waypoint.RiverOfFlame))
        {
            return false;
        }

        // The crew waits in town. Ferrying it out to the river just to shout and go straight back cost
        // a portal and about twenty seconds; the shouts happen at the first seal instead, where the crew
        // has to arrive anyway.
        return await TraverseTo(rusher, Area.ChaosSanctuary);
    }

    private async Task<Point> RunSealCircuit(
        Client rusher,
        HashSet<EntityCode> openedSeals,
        HashSet<int> killedGroups,
        IReadOnlyCollection<EntityCode> onlySeals = null)
    {
        Point anchor = null;
        var firstSeal = true;
        foreach (var seal in ChaosSeals)
        {
            if (onlySeals != null && !onlySeals.Contains(seal))
            {
                continue;
            }

            // A chicken used to be the end of the run. Put whoever left back in the game and back in
            // the party before walking into the next pack.
            foreach (var member in _crew.ToList())
            {
                await RejoinIfChickened(member);
            }

            if (!rusher.Game.IsInGame())
            {
                if (!await RejoinIfChickened(rusher))
                {
                    return anchor;
                }

                await RestockRusher(rusher, Act.Act4);
                if (!await ReturnThroughCrewPortal(rusher) && !await ReturnToSanctuary(rusher))
                {
                    return anchor;
                }
            }

            // Top up before the next pack. RestockMidRun already existed and was wired into the named
            // boss fights, but never into the seal circuit, so the sanctuary was the one fight the
            // rusher entered on whatever was left - and ran dry in eleven runs today.
            if (NeedsPotions(rusher))
            {
                await RestockMidRun(rusher, Act.Act4);
            }

            var fallbackAnchor = await SealAnchor(rusher, seal);
            var map = await _mapApiService.GetArea(rusher.Game.MapId, MapDifficulty, Area.ChaosSanctuary);
            var sealPoint = MapPointOf(map, seal);
            if (fallbackAnchor == null || sealPoint == null)
            {
                Log.Warning("No map data for {Seal}; skipping it on this pass", seal);
                continue;
            }

            // The old anchors sat 30 units from the seal, inside the pack: 17 to 47 monsters within eight
            // units within seconds, Amplify Damage and Decrepify on her, a potion every 0.8s and still
            // dead - nine runs the same way. The standoff is on the side she arrives from, so the pack
            // has to cross open ground to reach her and the crew's portal opens away from it.
            var standoff = await SealStandoff(rusher, sealPoint, fallbackAnchor);

            // The first seal is where the party gathers, so its portal goes on the seal itself and the
            // ground around it is cleared before anything is woken. Later seals keep their portal at the
            // standoff, which is where the rusher fights from anyway.
            var portalSpot = firstSeal ? sealPoint : standoff;

            Task crewFollowing = Task.CompletedTask;
            if (_crew.Count > 0)
            {
                await MoveTo(rusher, portalSpot, GetMovementMode(rusher));
                if (!await _townManagementService.CreateTownPortal(rusher))
                {
                    Log.Warning("Could not open a seal portal for the crew at {Spot}", portalSpot);
                }

                crewFollowing = BringCrewTo(rusher, Area.ChaosSanctuary, portalAlreadyOpen: true);
            }

            // Portal first so the crew is already travelling, then clear, then open. The crew and the
            // rushee arrive into cleared ground at the seal instead of into whatever was standing on it.
            var groupDone = killedGroups.Contains(SealGroups[seal]);

            // Clear where the boss will land, at every seal and not only the first. The boss spawns on
            // the seal, so whatever is standing there joins its fight the moment it wakes.
            if (!groupDone)
            {
                // The crew clears with her. It has to walk to this seal anyway, and a pack killed before
                // the seal opens is a pack that never joins the boss fight.
                using var clearAssist = StartCrewAssist(rusher, killLocation: sealPoint);
                await ClearAround(rusher, sealPoint, 25, TimeSpan.FromSeconds(12));
            }

            if (firstSeal)
            {
                firstSeal = false;
                await crewFollowing;
                crewFollowing = Task.CompletedTask;
                await BuffFromCrew();
            }
            var (sealOpened, bossConfirmed) = await RunSeal(rusher, seal, standoff, sealPoint, groupDone);
            await crewFollowing;

            // A spare portal owned by the crew. If the rusher chickens, she comes back through this
            // instead of traversing the River of Flame again.
            await EnsureCrewPortal();

            // Take what the fight dropped, potions first. The sanctuary drops far more than the party
            // drinks, and a full belt off the floor is a town trip not made.
            await SweepLoot(rusher, 15);

            // One seal that will not open must not cost the other four. Aborting the pass here left
            // rb45 with two seals it had never even visited and no record that they were outstanding.
            if (sealOpened)
            {
                openedSeals.Add(seal);
                anchor = standoff;
            }
            else
            {
                Log.Warning("{Seal} did not open on this pass; it stays on the list", seal);
            }

            if (bossConfirmed)
            {
                killedGroups.Add(SealGroups[seal]);
            }

            // Back to town the moment the seal is done, while the rusher crosses to the next one. The
            // crew rides the next seal's portal instead of walking the sanctuary, and this way the walk
            // to town overlaps the rusher's trip rather than following it.
            _crewPreStages.AddRange(_crew
                .Where(member => member.Game.IsInGame() && !member.Game.IsInTown())
                .Select(async member =>
                {
                    if (!await ReturnToTown(member))
                    {
                        Log.Warning(
                            "{Member} could not return to town after the seal; it will walk to the next one",
                            member.Game.Me.Name);
                    }

                    return true;
                }));
        }

        return anchor;
    }

    private async Task<Point> SealAnchor(Client rusher, EntityCode seal)
    {
        var map = await _mapApiService.GetArea(rusher.Game.MapId, MapDifficulty, Area.ChaosSanctuary);
        var right1 = MapPointOf(map, EntityCode.RightSeal1);
        var right2 = MapPointOf(map, EntityCode.RightSeal2);
        var top = MapPointOf(map, EntityCode.TopSeal);
        var left1 = MapPointOf(map, EntityCode.LeftSeal1);
        var left2 = MapPointOf(map, EntityCode.LeftSeal2);

        return seal switch
        {
            EntityCode.RightSeal1 or EntityCode.RightSeal2 when right1 != null && right2 != null =>
                right1.X < right2.X ? right1.Add(30, -10) : right1.Add(12, -38),
            EntityCode.TopSeal when top != null =>
                await _pathingService.IsNavigatablePointInArea(
                    rusher.Game.MapId, MapDifficulty, Area.ChaosSanctuary, top.Add(-20, 0))
                    ? top.Add(-37, 31)
                    : top.Add(0, 70),
            EntityCode.LeftSeal1 or EntityCode.LeftSeal2 when left1 != null && left2 != null =>
                left1.Y > left2.Y ? left1.Add(26, -21) : left1.Add(20, 40),
            _ => null
        };
    }

    private async Task<(bool Opened, bool BossConfirmed)> RunSeal(
        Client rusher,
        EntityCode seal,
        Point standoff,
        Point sealPoint,
        bool groupAlreadyKilled)
    {
        // No trip to the standoff first: OpenSeal walks to the seal from wherever she stands, and going
        // by way of the standoff meant crossing the same ground twice at every seal.
        if (!await OpenSeal(rusher, seal, sealPoint))
        {
            return (false, false);
        }

        // The decoy of a pair still has to be opened, but nothing wakes at it, so waiting there for a
        // boss is pure dead time once its group has already given up one.
        if (groupAlreadyKilled)
        {
            return (true, false);
        }

        // Straight back out. One teleport cast is interrupted by the pack that just woke up, so retry
        // until the server moves her, then fall back to pathing if she is still in it.
        await HopTo(rusher, standoff, TimeSpan.FromSeconds(1.5));

        var confirmed = await FightFromAnchor(rusher, standoff, seal, sealPoint);
        return (true, confirmed);
    }

    // 32, not 38: a boss at its seal sits right on the edge of what the client streams from a
    // thirty-eight unit standoff, which is how De Seis kept vanishing without dying.
    private const double StandoffDistance = 32;

    /// <summary>
    /// A walkable point about <see cref="StandoffDistance"/> units from the seal on the side the rusher
    /// approaches from, rotating up to 90 degrees off that line when walls block it. Falls back to the
    /// old anchor when nothing fits.
    /// </summary>
    private async Task<Point> SealStandoff(Client rusher, Point sealPoint, Point fallback)
    {
        var me = rusher.Game.Me.Location;
        double dx = me.X - sealPoint.X;
        double dy = me.Y - sealPoint.Y;
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            dx = fallback.X - sealPoint.X;
            dy = fallback.Y - sealPoint.Y;
        }

        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            dx = 1;
        }

        var length = Math.Sqrt(dx * dx + dy * dy);
        dx /= length;
        dy /= length;

        foreach (var angle in new[] { 0.0, 30.0, -30.0, 60.0, -60.0, 90.0, -90.0 })
        {
            var radians = angle * Math.PI / 180.0;
            var rx = dx * Math.Cos(radians) - dy * Math.Sin(radians);
            var ry = dx * Math.Sin(radians) + dy * Math.Cos(radians);
            foreach (var distance in new[] { StandoffDistance, 32.0, 26.0 })
            {
                var candidate = new Point((ushort)(sealPoint.X + rx * distance), (ushort)(sealPoint.Y + ry * distance));
                if (await _pathingService.IsNavigatablePointInArea(rusher.Game.MapId, MapDifficulty, Area.ChaosSanctuary, candidate))
                {
                    Log.Information("Standoff for seal at {Seal}: {Standoff} ({Distance} units, {Angle} degrees off the approach)", sealPoint, candidate, distance, angle);
                    return candidate;
                }
            }
        }

        Log.Warning("No standoff found near seal at {Seal}; using the old anchor {Anchor}", sealPoint, fallback);
        return fallback;
    }

    private async Task<bool> OpenSeal(Client rusher, EntityCode seal, Point sealPoint)
    {
        if (rusher.Game.GetEntityByCode(seal)
            .Any(entity => entity.State is EntityState.Activated or EntityState.Activating))
        {
            return true;
        }

        if (!await MoveTo(rusher, sealPoint, MovementMode.Teleport))
        {
            return false;
        }

        return await GeneralHelpers.TryWithTimeout(async _ =>
        {
            var entity = rusher.Game.GetEntityByCode(seal).FirstOrDefault();
            if (entity == null)
            {
                await Task.Delay(200);
                return false;
            }

            if (entity.State is EntityState.Activated or EntityState.Activating)
            {
                return true;
            }

            rusher.Game.InteractWithEntity(entity);
            await Task.Delay(200);
            return false;
        }, TimeSpan.FromSeconds(8));
    }

    /// <summary>Casting time between kite hops at a seal; the hop itself may take up to 1.5s to land.</summary>
    private static readonly TimeSpan SealKiteInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>How close to a seal she goes to look for a boss she cannot see. Far enough out to stay
    /// clear of the pack, close enough for the server to stream the boss in.</summary>
    private const double SealLookDistance = 28;

    /// <summary>How long a seal boss may take no damage before cold is written off against it.</summary>
    private static readonly TimeSpan NoColdDamageGrace = TimeSpan.FromSeconds(15);

    /// <summary>Healing potions left that sends the rusher to town mid-seal. Above the chicken
    /// threshold on purpose: she has to survive the walk to her own portal.</summary>
    private const int SealRestockFloor = 14;

    /// <summary>
    /// Fights the seal boss. Returns true only when a super unique was seen dying: every seal boss sends
    /// state Dieing then Dead before it is removed, while a boss that merely walks out of range sends
    /// neither, so this is the one signal that separates a kill from a disappearance.
    /// </summary>
    private async Task<bool> FightFromAnchor(Client rusher, Point anchor, EntityCode seal, Point sealPoint)
    {
        var timer = Stopwatch.StartNew();
        var moveTimer = Stopwatch.StartNew();
        var kiteTimer = Stopwatch.StartNew();
        var quietTimer = Stopwatch.StartNew();
        var idleTicks = 0;
        var steppedTowardSeal = false;
        var lookedForVanishedBoss = false;
        var restockedThisSeal = false;
        uint? bossSeen = null;

        // The crew starts on the standoff and moves onto the boss as soon as there is one. Anchoring it
        // to the standoff for the whole fight left the barbarian far from a boss that had wandered off,
        // which matters most when the boss is immune to cold and the crew is the only thing hurting it.
        var crewAssist = StartCrewAssist(rusher, killLocation: anchor);
        var crewOnBoss = false;
        double bossLife = 100;
        var bossDamage = Stopwatch.StartNew();
        var coldUseless = false;
        try
        {
            while (timer.Elapsed < SealKillLimit && rusher.Game.IsInGame())
            {
                if (await KeepArmorUp(rusher))
                {
                    continue;
                }

                // Leaving to restock beats drinking the last potion. Every seal that killed a run today
                // ended on an empty belt, and the trip costs about twenty seconds against a lost game. The
                // floor is well above the chicken threshold so she still has potions for the walk out.
                if (!restockedThisSeal
                    && InventoryHelpers.GetTotalHealthPotions(rusher.Game) < SealRestockFloor)
                {
                    restockedThisSeal = true;
                    Log.Information(
                        "{Rusher} is down to {Potions} healing potions at {Seal}; restocking before finishing it",
                        rusher.Game.Me.Name,
                        InventoryHelpers.GetTotalHealthPotions(rusher.Game),
                        seal);
                    await HopTo(rusher, anchor, TimeSpan.FromSeconds(1.5));
                    if (await RestockMidRun(rusher, Act.Act4))
                    {
                        // The kill limit is for fighting, not for shopping.
                        timer.Restart();
                        moveTimer.Restart();
                        kiteTimer.Restart();
                        quietTimer.Restart();
                        continue;
                    }
                }

                // No low-life disengage. CSBot's sorceress has none either: potions and the chicken keep her
                // alive, and every disengage variant tried here (anchor no-op, retreat point, safe-spot search)
                // cost five seconds of no casting per cycle and never let the fight resume once life sat
                // under half - rb33 spent its last 25 seconds disengaging and drank the belt dry.
                // Measured from the rusher, the way CSBot does it: the anchor is a fixed retreat point and a
                // boss that died outside its radius was never seen dead, so the fight ran to the 75s cap.
                // The boss lives at its seal, not at the standoff: Infector came into view 53 seconds into
                // rb40's first seal and was never within 60 of her or the standoff, so the fight ended on
                // idle with the boss alive and the circuit needed a second pass.
                var superUniques = NPCHelpers.GetNearbySuperUniques(rusher, rusher.Game.Me.Location, 60)
                    .Concat(NPCHelpers.GetNearbySuperUniques(rusher, anchor, 60))
                    .Concat(NPCHelpers.GetNearbySuperUniques(rusher, sealPoint, 80))
                    .DistinctBy(candidate => candidate.Id)
                    .ToList();
                if (superUniques.Any(candidate => candidate.State is EntityState.Dead or EntityState.Dieing))
                {
                    return true;
                }

                // A dead boss stops being reported at all, so waiting to see one dead never fires: the
                // rusher stayed clearing surviving minions until the 75 second cap before moving to the
                // next seal. Remembering the boss lets its disappearance end the fight.
                if (bossSeen != null && superUniques.All(candidate => candidate.Id != bossSeen))
                {
                    if (!lookedForVanishedBoss)
                    {
                        lookedForVanishedBoss = true;
                        Log.Debug("Seal boss {Boss} vanished without dying; searching around the seal {Seal}", bossSeen, sealPoint);

                        // Walk a short circle around the seal rather than looking from one side. The boss
                        // spawns on the seal and wanders out of streaming range, and a single look missed
                        // De Seis on all three passes of rb54 - and his seal has no partner seal to give the
                        // kill instead, so every miss cost a whole pass.
                        // The seal itself first: she stood on it to open it, so it is always reachable,
                        // and it is the middle of the ground the boss wanders. Then a short circle of
                        // whatever else is walkable. In rb58 all four offsets happened to be unwalkable,
                        // the loop skipped every one without moving, and the search gave up after two
                        // seconds having looked nowhere at all.
                        var searchSpots = new List<Point> { sealPoint };
                        foreach (var (dx, dy) in new[] { (0, -22), (22, 0), (0, 22), (-22, 0), (18, -18), (-18, 18) })
                        {
                            var spot = new Point((ushort)(sealPoint.X + dx), (ushort)(sealPoint.Y + dy));
                            if (await _pathingService.IsNavigatablePointInArea(
                                    rusher.Game.MapId, MapDifficulty, Area.ChaosSanctuary, spot))
                            {
                                searchSpots.Add(spot);
                            }
                        }

                        foreach (var spot in searchSpots)
                        {
                            await HopTo(rusher, spot, TimeSpan.FromSeconds(1.5));
                            await Task.Delay(600);
                            if (NPCHelpers.GetNearbySuperUniques(rusher, rusher.Game.Me.Location, 60)
                                .Any(candidate => candidate.Id == bossSeen))
                            {
                                Log.Information("Found seal boss {Boss} again from {Spot}", bossSeen, spot);
                                break;
                            }
                        }

                        continue;
                    }

                    Log.Information("Seal boss {Boss} left without dying; {Seal} stays on the revisit list", bossSeen, seal);
                    return false;
                }

                var sealBoss = superUniques.FirstOrDefault(candidate =>
                    candidate.State is not EntityState.Dead and not EntityState.Dieing);
                bossSeen ??= sealBoss?.Id;

                if (sealBoss != null)
                {
                    if (!crewOnBoss)
                    {
                        crewOnBoss = true;
                        crewAssist.Dispose();
                        crewAssist = StartCrewAssist(rusher, sealBoss);
                    }

                    if (sealBoss.LifePercentage < bossLife - 1)
                    {
                        bossLife = sealBoss.LifePercentage;
                        bossDamage.Restart();
                    }
                    else if (!coldUseless
                        && (ColdIsUselessAgainst(sealBoss) || bossDamage.Elapsed > NoColdDamageGrace))
                    {
                        // Measured, not guessed: a boss whose life has not moved in ten seconds of orbing is
                        // immune to cold whatever the table says, and orb is worth nothing against it. Static
                        // field is lightning and still bites, and the crew does the killing.
                        coldUseless = true;
                        Log.Information(
                            "{Boss} has taken no cold damage in {Seconds:0}s; static only from here and the crew finishes it",
                            sealBoss.NPCCode,
                            bossDamage.Elapsed.TotalSeconds);
                    }
                }
                // Only the boss is worth chasing; everything else has to come into orb range on its own.
                // Chasing the nearest hostile within 40 sent her after Storm Casters fourteen times in one
                // seal. When the ground is clear and no boss has shown itself, step toward the seal once to
                // bring it into view rather than declaring the seal done.
                var target = sealBoss ?? NearbyHostiles(rusher, OrbEffectiveRange).FirstOrDefault();
                if (target == null)
                {
                    if (bossSeen == null && !steppedTowardSeal)
                    {
                        steppedTowardSeal = true;
                        var peek = await ApproachPoint(rusher, sealPoint, SealLookDistance);
                        if (peek != null)
                        {
                            Log.Debug("{Rusher} sees no boss from {Location}; stepping toward the seal via {Peek}", rusher.Game.Me.Name, rusher.Game.Me.Location, peek);
                            await HopTo(rusher, peek, TimeSpan.FromSeconds(1.5));
                            idleTicks = 0;
                            continue;
                        }
                    }

                    if (++idleTicks > 15)
                    {
                        return false;
                    }
                    await Task.Delay(200);
                    continue;
                }

                idleTicks = 0;

                // Timed kite. The anchor is where the seal pack stands: five seconds after reaching it she had
                // 21 monsters inside six units and Decrepify and Amplify Damage on her, and 25 potions went in
                // 30 seconds. One hop every 2.5 seconds keeps the melee off her and leaves room to cast; the
                // per-tick kite of rb31 (164 teleports, 9 orbs) and the 5-second safe-spot search of rb33
                // both starved the casting instead.
                var crowd = NearbyHostiles(rusher, 6);
                if (crowd.Count >= 3 && kiteTimer.Elapsed > SealKiteInterval)
                {
                    var kite = await KitePointAwayFromPack(rusher, anchor);
                    if (kite != null)
                    {
                        // One cast is not a hop: under forty attackers the teleport is interrupted and she
                        // stays put, which is how rb35 ended with three "kites" that never moved her. Retry
                        // until the server reassigns her or the budget runs out, the way rb30 - the only run
                        // to finish the circuit - effectively did.
                        var hopped = await GeneralHelpers.TryWithTimeout(
                            async _ => await rusher.Game.TeleportToLocationAsync(kite),
                            TimeSpan.FromSeconds(1.5));
                        Log.Debug("{Rusher} kiting {Count} melee hostiles to {Kite}: hopped={Hopped}", rusher.Game.Me.Name, crowd.Count, kite, hopped);
                        kiteTimer.Restart();
                        continue;
                    }

                    kiteTimer.Restart();
                }

                // The boss is the target of record, but a boss out of orb range must not turn into a teleport
                // loop: in rb32 the approach teleport failed against the pack and was retried every 200ms
                // for eighty seconds - four teleports a second, two orbs in total, both clients drank dry.
                // Whatever stands in range gets fought first; the boss is chased at most once a second.
                if (target.Location.Distance(rusher.Game.Me.Location) > OrbEffectiveRange)
                {
                    var inRange = NearbyHostiles(rusher, OrbEffectiveRange).FirstOrDefault();

                    // Once the pack is thinned out there is nothing left to stand off from, so press the
                    // boss instead of holding position. Waiting on a near-empty floor is what made the last
                    // third of every seal slow.
                    var thinnedOut = NearbyHostiles(rusher, 25).Count < 6;
                    if (inRange != null)
                    {
                        target = inRange;
                        quietTimer.Restart();
                    }
                    else if (!thinnedOut && quietTimer.Elapsed < TimeSpan.FromSeconds(1))
                    {
                        // Standing off means the pack crosses to her. Give it a second before walking
                        // into it after the boss.
                        await Task.Delay(100);
                        continue;
                    }
                    else if (thinnedOut || moveTimer.Elapsed > TimeSpan.FromSeconds(1))
                    {
                        // A walkable point on her side of the boss, not a fixed offset: the fixed point sat
                        // in a wall at the first right seal and she spent 37 seconds teleporting into it.
                        var approach = await ApproachPoint(rusher, target.Location, OrbEngageRange);
                        if (approach != null)
                        {
                            Log.Debug("{Rusher} approaching {Target} at {TargetLocation} via {Approach}", rusher.Game.Me.Name, target.NPCCode, target.Location, approach);
                            await HopTo(rusher, approach, TimeSpan.FromSeconds(1));
                        }

                        moveTimer.Restart();
                        await Task.Delay(100);
                        continue;
                    }
                    else
                    {
                        await Task.Delay(100);
                        continue;
                    }
                }

                // Orb and static alternate here, the way CSBot does it: orb while the target is not
                // chilled, static while the chill is on it. Against a genuinely immune boss the chill never
                // lands, so orb keeps going and the crew does the killing - which is the whole reason the
                // crew exists. No branch withholds damage, which is what made the rusher idle at a seal.
                await AlternateOrbAndStatic(rusher, target, coldUseless && target.Id == bossSeen);
            }

            return false;
        }
        finally
        {
            crewAssist.Dispose();
        }
    }

    /// <summary>
    /// Attacks a target. <paramref name="coldIsUseless"/> drops the cold skills entirely: against a
    /// cold-immune boss frozen orb and blizzard do nothing at all, so the sorceress contributes static
    /// field - which is not cold and still bites down to the difficulty floor - and leaves the killing
    /// to the crew instead of burning mana and standing in reach for no damage.
    /// </summary>
    /// <summary>How often orb may be recast, so static gets its turn between casts.</summary>
    private static readonly TimeSpan OrbInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, DateTime> _lastOrb = [];

    /// <summary>
    /// The Chaos Sanctuary attack, lifted from CSBot's sorceress action. Orb goes out when the target is
    /// not chilled or static has stopped biting; static field runs on the caster the rest of the time.
    /// An immune boss never chills, so orb simply keeps firing and the crew finishes it.
    /// </summary>
    private async Task AlternateOrbAndStatic(Client client, WorldObject target, bool skipCold = false)
    {
        var me = client.Game.Me;
        var distance = target.Location.Distance(me.Location);
        var name = me.Name;
        var orbReady = !_lastOrb.TryGetValue(name, out var lastOrb) || DateTime.Now - lastOrb > OrbInterval;

        // Orb whenever its cast delay is up; static only fills the gap. Gating orb on the target being
        // unchilled meant one orb per chill and static for the rest, and on 1.09 static cannot kill -
        // the user watched her cast almost no orbs at the seals.
        if (!skipCold
            && me.HasSkill(Skill.FrozenOrb)
            && orbReady
            && distance < StaticFieldRange)
        {
            // On the location, not the entity: orb is a travelling missile and the entity form misses a
            // target that has already moved.
            client.Game.UseRightHandSkillOnLocation(Skill.FrozenOrb, target.Location);
            _lastOrb[name] = DateTime.Now;
            await Task.Delay(250);
            return;
        }

        if (me.HasSkill(Skill.StaticField) && distance < StaticFieldRange)
        {
            // Static is an area effect centred on the caster, so it is cast on self, repeating.
            client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, me.Location);
            await Task.Delay(100);
            return;
        }

        await AttackTarget(client, target);
    }

    private async Task AttackTarget(Client client, WorldObject target, bool coldIsUseless = false)
    {
        var me = client.Game.Me;
        var distance = target.Location.Distance(me.Location);

        // Against an immune target this returns in every branch and never falls through. The fall
        // through was the bug: past the skipped cold branches it reached _attackService.AssistPlayer,
        // which knows nothing about immunities and cast frozen orb anyway.
        if (coldIsUseless)
        {
            if (target.LifePercentage > ColdImmuneStaticUntilLife
                && me.HasSkill(Skill.StaticField)
                && ClassHelpers.CanStaticEntity(client, target.LifePercentage))
            {
                if (distance >= StaticFieldRange)
                {
                    await MoveTo(client, target.Location, GetMovementMode(client));
                }
                else
                {
                    client.Game.UseRightHandSkillOnEntity(Skill.StaticField, target);
                }

                await Task.Delay(200);
                return;
            }

            // Static is spent or unavailable. Only non-cold direct damage is worth anything now.
            if (me.HasSkill(Skill.Nova) && me.Mana > NovaManaCost && distance <= NovaRange)
            {
                client.Game.UseRightHandSkillOnLocation(Skill.Nova, me.Location);
                await Task.Delay(200);
                return;
            }

            // Static has floored it and there is no lightning to finish with, so the sorceress walks
            // in and swings. It is slow, but it is real damage against a cold immune and it is what
            // gets Duriel down when no crew arrived.
            if (distance > MeleeRange)
            {
                await MoveTo(client, target.Location, GetMovementMode(client));
            }
            else
            {
                client.Game.RepeatRightHandSkillOnEntity(Skill.Attack, target);
            }

            await Task.Delay(200);
            return;
        }

        if (!coldIsUseless && me.HasSkill(Skill.FrozenOrb) && me.Mana > OrbManaCost)
        {
            // Give ground first, then cast from it. Orb outranges everything in the sanctuary, so
            // standing in the pack is a choice - but repositioning instead of casting is not: something
            // is always within crowding range in there, and returning early meant she never attacked.
            await KiteToClearGround(client, target);

            client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, target);
            await Task.Delay(200);
            return;
        }

        if (!coldIsUseless && me.HasSkill(Skill.Blizzard) && me.Mana > BlizzardManaCost)
        {
            client.Game.UseRightHandSkillOnEntity(Skill.Blizzard, target);
            await Task.Delay(200);
            return;
        }

        if (me.HasSkill(Skill.Nova) && me.Mana > NovaManaCost)
        {
            if (distance > NovaRange)
            {
                await client.Game.TeleportToLocationAsync(target.Location);
                return;
            }

            client.Game.UseRightHandSkillOnLocation(Skill.Nova, me.Location);
            await Task.Delay(200);
            return;
        }

        if (me.HasSkill(Skill.StaticField)
            && distance < StaticFieldRange
            && ClassHelpers.CanStaticEntity(client, target.LifePercentage))
        {
            client.Game.UseRightHandSkillOnEntity(Skill.StaticField, target);
            await Task.Delay(200);
            return;
        }

        await _attackService.AssistPlayer(client, client.Game.Me);
    }

    private async Task<bool> KillDiablo(Client rusher)
    {
        var diablo = rusher.Game.GetNPCsByCode(NPCCode.Diablo).FirstOrDefault();
        if (diablo == null)
        {
            return false;
        }

        // The crew never fought Diablo: StartCrewAssist was wired into the seal fights and the named
        // boss fights, but not into this one, so the barbarian stood in the sanctuary while the rusher
        // soloed the boss the crew exists for.
        using var crewAssist = StartCrewAssist(rusher, diablo);

        var timer = Stopwatch.StartNew();
        var orbTimer = Stopwatch.StartNew();
        var filledThisCooldown = false;
        while (timer.Elapsed < DiabloKillLimit && rusher.Game.IsInGame())
        {
            var current = rusher.Game.WorldObjects.GetValueOrDefault((diablo.Id, EntityType.NPC));
            if (current == null || current.State is EntityState.Dead or EntityState.Dieing)
            {
                return true;
            }

            var me = rusher.Game.Me;
            var distance = current.Location.Distance(me.Location);
            if (distance > 15)
            {
                await rusher.Game.TeleportToLocationAsync(current.Location);
                continue;
            }

            if (current.LifePercentage > DiabloStaticUntilLife && me.HasSkill(Skill.StaticField))
            {
                rusher.Game.UseRightHandSkillOnEntity(Skill.StaticField, current);
                await Task.Delay(100);
                continue;
            }

            if (me.HasSkill(Skill.FrozenOrb) && me.Mana > OrbManaCost && orbTimer.Elapsed > OrbCooldown)
            {
                rusher.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, current);
                orbTimer.Restart();
                filledThisCooldown = false;
                await Task.Delay(250);
                continue;
            }

            if (!me.HasSkill(Skill.FrozenOrb))
            {
                await AttackTarget(rusher, current);
                continue;
            }

            if (!filledThisCooldown && me.HasSkill(Skill.StaticField) && me.Mana > FillerManaReserve)
            {
                rusher.Game.UseRightHandSkillOnEntity(Skill.StaticField, current);
                filledThisCooldown = true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<bool> MoveTo(Client client, Point destination, MovementMode movementMode)
    {
        client.Game.RequestUpdate(client.Game.Me.Id);
        await Task.Delay(200);
        if (client.Game.Me.Location.Distance(destination) <= TeleportNoOpRange)
        {
            return true;
        }

        var arrived = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (!client.Game.IsInGame())
            {
                return true;
            }

            var path = await _pathingService.GetPathToLocation(client.Game, destination, movementMode);
            if (path.Count == 0)
            {
                return movementMode == MovementMode.Teleport
                    ? await client.Game.TeleportToLocationAsync(destination)
                    : await client.Game.MoveToAsync(destination);
            }

            return await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode)
                || client.Game.Me.Location.Distance(destination) <= TeleportNoOpRange;
        }, TimeSpan.FromSeconds(30));
        return arrived && client.Game.IsInGame();
    }

    /// <summary>
    /// Gets a client back to town, recasting a portal when its own is out of reach. A rusher ends a
    /// fight wherever the fight ended, which is regularly nowhere near the portal it opened, and
    /// TakeTownPortalToTown needs one nearby. Casting another costs a moment; failing loses the run.
    /// </summary>
    /// <summary>
    /// The field waypoints a Classic rush needs, with the act whose town they are reached from.
    /// Waypoints are held per difficulty, so a rusher that has finished Hell can still arrive on
    /// Nightmare holding only the four town ones.
    /// </summary>
    private static readonly (Waypoint Waypoint, Act Act, Area Area)[] RushFieldWaypoints =
    [
        (Waypoint.CatacombsLevel2, Act.Act1, Area.CatacombsLevel2),
        (Waypoint.FarOasis, Act.Act2, Area.FarOasis),
        (Waypoint.LostCity, Act.Act2, Area.LostCity),
        (Waypoint.DuranceOfHateLevel2, Act.Act3, Area.DuranceOfHateLevel2),
        (Waypoint.RiverOfFlame, Act.Act4, Area.RiverOfFlame)
    ];

    private static readonly Dictionary<Act, Waypoint> TownWaypointOfAct = new()
    {
        [Act.Act1] = Waypoint.RogueEncampment,
        [Act.Act2] = Waypoint.LutGholein,
        [Act.Act3] = Waypoint.KurastDocks,
        [Act.Act4] = Waypoint.ThePandemoniumFortress
    };

    /// <summary>Every waypoint object code, since which one a level uses varies by area.</summary>
    private static readonly EntityCode[] WaypointCodes =
    [
        EntityCode.WaypointAct1, EntityCode.WaypointAct1JailAndUp,
        EntityCode.WaypointAct2, EntityCode.WaypointAct2Sewer, EntityCode.WaypointAct2Cellar,
        EntityCode.WaypointAct3, EntityCode.WaypointAct3Durance,
        EntityCode.WaypointAct4Levels
    ];

    /// <summary>
    /// Walks the rusher to any field waypoint it does not yet hold on this difficulty and activates
    /// it, so later runs teleport instead of walking. Deliberately best effort: a waypoint that
    /// cannot be collected costs the walk it was going to cost anyway.
    /// </summary>
    /// <remarks>
    /// The Canyon of the Magi is absent on purpose and cannot be collected this way. Its only
    /// adjacent levels are the seven tombs, and the Arcane Sanctuary - the route a player uses -
    /// has no adjacency in the map data at all, so there is nothing to route along. That one has to
    /// be taken by hand before a Nightmare or Hell rush will get past act 2.
    /// </remarks>
    private async Task CollectMissingWaypoints(Client rusher)
    {
        foreach (var (waypoint, act, area) in RushFieldWaypoints)
        {
            if (!await RefreshAllowedWaypoints(rusher, act))
            {
                continue;
            }

            // An empty list means the waypoint menu never opened, not that the rusher holds nothing.
            // Guessing "missing" there sends it walking to waypoints it already owns, which is worse
            // than doing nothing: the rush can take them itself through TakeWaypointFromTown.
            if (rusher.Game.Me.AllowedWaypoints.IsEmpty)
            {
                Log.Warning(
                    "Could not read {Client}'s waypoint list; skipping collection and letting the rush take them",
                    rusher.Game.Me.Name);
                return;
            }

            if (rusher.Game.Me.AllowedWaypoints.Contains(waypoint))
            {
                continue;
            }

            Log.Information(
                "Collecting the {Waypoint} waypoint on {Difficulty} so later runs do not walk it",
                waypoint,
                _gameDifficulty);

            if (!await TraverseTo(rusher, area) || !await ActivateWaypointHere(rusher, waypoint))
            {
                Log.Warning("Could not collect {Waypoint}; the rush will walk to it instead", waypoint);
                await ReturnToTown(rusher);
                continue;
            }

            // Ride the waypoint just activated back to town. The rusher walked in here, so it has no
            // portal of its own to go home by, and a town waypoint is always held.
            if (!TownWaypointOfAct.TryGetValue(act, out var townWaypoint)
                || !await _townManagementService.TakeWaypoint(rusher, townWaypoint))
            {
                await ReturnToTown(rusher);
            }
        }
    }

    /// <summary>
    /// Opens the town waypoint so the server sends a waypoint menu, which is the only thing that
    /// fills <see cref="Self.AllowedWaypoints"/>. Without this the set is empty and every waypoint
    /// looks missing.
    /// </summary>
    private async Task<bool> RefreshAllowedWaypoints(Client rusher, Act act)
    {
        if (!await _townManagementService.SwitchAct(rusher, act))
        {
            return false;
        }

        if (!TownWaypointByAct.TryGetValue(act, out var townWaypoint)
            || !await MoveToObject(rusher, townWaypoint))
        {
            return false;
        }

        var waypoint = rusher.Game.GetEntityByCode(townWaypoint).FirstOrDefault();
        if (waypoint == null)
        {
            return false;
        }

        rusher.Game.InteractWithEntity(waypoint);
        await Task.Delay(700);
        return true;
    }

    /// <summary>
    /// Activates whichever waypoint object this level uses. Interacting is not proof it registered,
    /// so the outcome is confirmed against the waypoint menu the server sends back - an unverified
    /// interact reported success once and the waypoint was still missing on the next run.
    /// </summary>
    private async Task<bool> ActivateWaypointHere(Client rusher, Waypoint expected)
    {
        foreach (var code in WaypointCodes)
        {
            if (!await MoveToObject(rusher, code))
            {
                continue;
            }

            var waypoint = rusher.Game.GetEntityByCode(code).FirstOrDefault();
            if (waypoint == null)
            {
                continue;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                rusher.Game.RequestUpdate(rusher.Game.Me.Id);
                await Task.Delay(300);
                Log.Debug(
                    "Activating {Code} for {Waypoint}: me={Me}, object {Id} at {Location}, distance {Distance:0.0}",
                    code,
                    expected,
                    rusher.Game.Me.Location,
                    waypoint.Id,
                    waypoint.Location,
                    rusher.Game.Me.Location.Distance(waypoint.Location));

                rusher.Game.InteractWithEntity(waypoint);
                if (GeneralHelpers.TryWithTimeout(
                    _ => rusher.Game.Me.AllowedWaypoints.Contains(expected),
                    TimeSpan.FromSeconds(2)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Times a step and logs it in one greppable shape, so a change to routing or attack rotation can
    /// be judged on how long the step actually took rather than on proxies like attack counts.
    /// Grep the log for "TIMING" to get the whole run as a table.
    /// </summary>
    private async Task<bool> Timed(string step, Func<Task<bool>> action)
    {
        var timer = Stopwatch.StartNew();
        var succeeded = false;
        try
        {
            succeeded = await action();
            return succeeded;
        }
        finally
        {
            Log.Information(
                "TIMING {Step} {Outcome} in {Seconds:0.0}s on {Difficulty}",
                step,
                succeeded ? "ok" : "FAILED",
                timer.Elapsed.TotalSeconds,
                _gameDifficulty);
        }
    }

    /// <summary>
    /// Tops the rusher's belt and inventory up before a long fight. Running dry mid-fight makes
    /// ChickenService pull it out of the game, which strands any rushee waiting on its portal.
    /// </summary>
    /// <summary>Same as the rusher's restock, against the crew member's own belt configuration.</summary>
    private async Task RestockCrewMember(Client member)
    {
        var account = (_rushConfiguration.Crew ?? []).FirstOrDefault(candidate =>
            candidate.Character.Equals(member.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        if (account == null)
        {
            return;
        }

        var options = new TownManagementOptions(account, member.Game.Act)
        {
            HealthPotionsToBuy = PotionsToBuy(
                member.Game,
                member.Game.Belt.Height * account.HealthSlots.Count,
                member.Game.Belt.NumOfHealthPotions(),
                InventoryHelpers.GetTotalHealthPotions(member.Game),
                HealthReserve),
            ManaPotionsToBuy = PotionsToBuy(
                member.Game,
                member.Game.Belt.Height * account.ManaSlots.Count,
                member.Game.Belt.NumOfManaPotions(),
                InventoryHelpers.GetTotalManaPotions(member.Game),
                ManaReserve)
        };

        var result = await _townManagementService.PerformTownTasks(member, options);
        if (!result.Succes)
        {
            Log.Warning("{Member} could not restock; continuing on what it carries", member.Game.Me.Name);
        }
    }

    /// <summary>
    /// Goes shopping mid-rush when supplies are low, then comes back through the portal it left by.
    /// Running dry is what makes ChickenService abandon the game and lose the run, so restocking is
    /// always better than fighting on: a trip to town costs seconds, a chicken costs everything.
    /// </summary>
    private async Task<bool> RestockMidRun(Client client, Act act)
    {
        var area = client.Game.Area;
        var wasInTown = client.Game.IsInTown();

        if (!wasInTown && !await _townManagementService.CreateTownPortal(client))
        {
            Log.Warning("{Client} could not open a portal to go restock", client.Game.Me.Name);
            return false;
        }

        if (!await ReturnToTown(client))
        {
            Log.Warning("{Client} could not reach town to restock", client.Game.Me.Name);
            return false;
        }

        await RestockRusher(client, act);

        if (wasInTown)
        {
            return true;
        }

        var self = client.Game.Players.FirstOrDefault(player =>
            player.Name.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        // The portal was created during play, so it is absent from map data and cannot be pathed to.
        // Closing on the object first is what makes the trip back work at all.
        if (self == null
            || !await CloseOnOwnPortal(client)
            || !await _townManagementService.TakeTownPortalToArea(client, self, area))
        {
            Log.Warning("{Client} restocked but could not get back to {Area}", client.Game.Me.Name, area);
            return false;
        }

        return true;
    }

    /// <summary>
    /// How many one by one items still fit in the inventory. Buying past this puts each potion on the
    /// cursor with nowhere to drop it, which fails, retries, and leaves town believing it restocked.
    /// </summary>
    /// <summary>Spare healing carried in the inventory, on top of a full belt.</summary>
    // 16, down from 28. At 28 the reserve ate the whole inventory - a STOCK line read one free slot -
    // so nothing else could be picked up, and a rare ring and a rare breast plate were walked past.
    // Potions off the floor keep the belt full anyway.
    private const int HealthReserve = 16;

    /// <summary>Spare mana carried in the inventory, on top of a full belt.</summary>
    private const int ManaReserve = 10;

    /// <summary>
    /// How many potions to buy: enough to fill the belt, plus whatever reserve still fits in the
    /// inventory. Only the reserve is bounded by inventory space - belt potions go to the belt and need
    /// none. Clamping the whole amount by free slots meant a full inventory bought nothing at all, and
    /// the party walked into the Chaos Sanctuary with an empty belt.
    /// </summary>
    private static long PotionsToBuy(Game game, long beltCapacity, int beltCarried, int totalCarried, int reserve)
    {
        var beltShortfall = Math.Max(0, beltCapacity - beltCarried);
        var reserveShortfall = Math.Min(reserve, FreeInventorySlots(game));
        return Math.Max(0, Math.Min(beltCapacity + reserve - totalCarried, beltShortfall + reserveShortfall));
    }

    private static long FreeInventorySlots(Game game)
    {
        var used = game.Inventory.Items.Sum(item => (long)item.Width * item.Height);
        return Math.Max(0, (long)game.Inventory.Width * game.Inventory.Height - used);
    }

    /// <summary>Whether this client is low enough on healing to be worth a shopping trip.</summary>
    private static bool NeedsPotions(Client client)
    {
        return InventoryHelpers.GetTotalHealthPotions(client.Game) < RestockPotionFloor;
    }

    private async Task RestockRusher(Client rusher, Act act)
    {
        var account = _rushConfiguration.Rusher;
        var options = new TownManagementOptions(account, act)
        {
            HealthPotionsToBuy = PotionsToBuy(
                rusher.Game,
                rusher.Game.Belt.Height * account.HealthSlots.Count,
                rusher.Game.Belt.NumOfHealthPotions(),
                InventoryHelpers.GetTotalHealthPotions(rusher.Game),
                HealthReserve),
            ManaPotionsToBuy = PotionsToBuy(
                rusher.Game,
                rusher.Game.Belt.Height * account.ManaSlots.Count,
                rusher.Game.Belt.NumOfManaPotions(),
                InventoryHelpers.GetTotalManaPotions(rusher.Game),
                ManaReserve)
        };

        var result = await _townManagementService.PerformTownTasks(rusher, options);
        if (!result.Succes)
        {
            Log.Warning("The rusher could not restock in act {Act}; continuing on what it carries", act);
        }

        Log.Information(
            "STOCK {Client}: belt {Belt} health, {Total} total health, {Mana} total mana, {Free} free "
            + "inventory slots, holding [{Items}]",
            rusher.Game.Me.Name,
            rusher.Game.Belt.NumOfHealthPotions(),
            InventoryHelpers.GetTotalHealthPotions(rusher.Game),
            InventoryHelpers.GetTotalManaPotions(rusher.Game),
            FreeInventorySlots(rusher.Game),
            string.Join(", ", rusher.Game.Inventory.Items.Select(item => item.Name)));
    }

    /// <summary>
    /// Brings the crew to wherever the rusher is fighting, through the same portal the rushee uses.
    /// Best effort: a crew member that cannot get there costs its damage, not the run.
    /// </summary>
    private async Task BringCrewTo(
        Client rusher,
        Area area,
        Area? stagingArea = null,
        bool portalAlreadyOpen = false)
    {
        if (_crew.Count == 0)
        {
            return;
        }

        // Drop a portal where the rusher actually stands. The Chaos Sanctuary is large and the rusher
        // teleports across it, so a portal cast at the entrance leaves a walking barbarian crossing the
        // level on foot - which is how it missed every fight it was added for. CSBot re-portals for the
        // same reason. Skipped when the crew is staging into a level the rusher has already left.
        if (!portalAlreadyOpen
            && stagingArea == null
            && !rusher.Game.IsInTown()
            && !await _townManagementService.CreateTownPortal(rusher))
        {
            Log.Warning("Could not open a fresh portal for the crew in {Area}", area);
        }

        foreach (var pre in _crewPreStages)
        {
            try
            {
                await pre;
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "A crew pre-stage failed");
            }
        }

        _crewPreStages = [];

        await Task.WhenAll(_crew.Select(async member =>
        {
            var arrivalTimer = Stopwatch.StartNew();

            // Already there is already there. Re-ferrying a member that is standing in the area costs
            // a trip to town and back for nothing, three times over on a seal circuit.
            if (await _pathingService.IsNavigatablePointInArea(
                    member.Game.MapId, MapDifficulty, area, member.Game.Me.Location))
            {
                return;
            }

            // The crew never walks a level. A barbarian has no teleport, and fetching one on foot took
            // longer than the whole fight it was meant to join. It goes back to town first and rides
            // the rusher's portal, so the only walking is the few units to the portal itself.
            if (!member.Game.IsInTown() && !await ReturnToTown(member))
            {
                Log.Warning(
                    "{Member} could not get back to town to take a portal to {Area}",
                    member.Game.Me.Name,
                    area);
                return;
            }

            // And in the RIGHT town. The crew logs in to act 1 and stays there while the rusher moves
            // on, so "already in town" was true and useless: it then hunted for an act 2 portal in the
            // Rogue Encampment. This is why the barbarian never reached a single fight.
            if (member.Game.Act != rusher.Game.Act
                && !await _townManagementService.SwitchAct(member, rusher.Game.Act))
            {
                Log.Warning(
                    "{Member} could not travel to act {Act} to follow the rusher",
                    member.Game.Me.Name,
                    rusher.Game.Act);
                return;
            }

            // Same supply rule as the rusher: shop while standing in town rather than running dry in
            // the fight. Rushees are different - they have no gold, so they get the free healer NPC
            // instead, which is all HealRusheeIfHurt does.
            if (NeedsPotions(member))
            {
                await RestockCrewMember(member);
            }

            // Somewhere to stand first. Duriel's lair is reached from inside the tomb, and the crew
            // starts in town - it cannot take a lair portal it is nowhere near.
            if (stagingArea != null && !await FerryRushee(rusher, member, stagingArea.Value))
            {
                Log.Warning(
                    "{Member} could not reach {Staging} to stage for {Area}",
                    member.Game.Me.Name,
                    stagingArea.Value,
                    area);
                return;
            }

            if (member.Game.Me.Life == 0)
            {
                member.Game.Resurrect();
                await Task.Delay(1000);
                await GeneralHelpers.PickupCorpseIfExists(member, _pathingService);
            }

            // Duriel's lair is portal-only - it is entered through the object the staff creates, not
            // through a town portal - so the generic ferry has nothing to look for there.
            // Duriel's lair has two entrances depending on where the member stands: the object the
            // staff creates, for a member already in the tomb, and the rusher's town portal for one
            // still in town. Hunting only for the first left the crew failing in Lut Gholein.
            var arrived = await FerryRushee(rusher, member, area)
                || (area == Area.DurielsLair && await EnterDurielsLair(member));
            if (!arrived)
            {
                Log.Warning(
                    "{Member} could not reach {Area}; the fight goes ahead without it",
                    member.Game.Me.Name,
                    area);
                return;
            }

            Log.Information(
                "TIMING crew-arrive-{Area} for {Member} finished in {Seconds:0.0}s",
                area,
                member.Game.Me.Name,
                arrivalTimer.Elapsed.TotalSeconds);

            // Shout while the party is still standing together at the portal. Waiting until the boss
            // fight is too late: CastAllShouts only buffs players within ten units, and by then
            // everyone has scattered.
            await BuffFromCrew();
        }));
    }

    /// <summary>
    /// Casts battle orders and the other shouts if anyone standing with the crew is missing them.
    /// Worth doing before every fight: the buff is what keeps a level one rushee and a squishy
    /// sorceress alive, and it expires.
    /// </summary>
    private async Task BuffFromCrew()
    {
        foreach (var member in _crew.Where(candidate => candidate.Game.IsInGame()))
        {
            if (ClassHelpers.AnyPlayerIsMissingShouts(member))
            {
                await ClassHelpers.CastAllShouts(member);
            }
        }
    }

    /// <summary>
    /// Runs the crew as assist bots on the rusher until the returned source is cancelled. This is the
    /// same shape AssistBot uses, and it is what lets a barbarian carry a fight the cold-only rusher
    /// cannot - Nightmare Duriel being immune to cold.
    /// </summary>
    /// <summary>
    /// Disposing a CancellationTokenSource does not cancel it, so `using var` on the raw source left
    /// every seal's assist loop running until the member left the game. Two loops then pulled the
    /// barbarian between the old anchor and the new one, and it never fought at the second seal.
    /// </summary>
    private sealed class CrewAssist(CancellationTokenSource cancellation) : IDisposable
    {
        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private CrewAssist StartCrewAssist(
        Client rusher,
        WorldObject focus = null,
        Point killLocation = null)
    {
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        foreach (var member in _crew)
        {
            _ = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested && member.Game.IsInGame())
                {
                    try
                    {
                        // A member still in town is being ferried by BringCrewTo, and two movement
                        // drivers on one client pull against each other.
                        if (member.Game.IsInTown())
                        {
                            await Task.Delay(500, cancellationToken);
                            continue;
                        }

                        // Close on the boss, not the rusher. The rusher withdraws when it cannot hurt
                        // the target, and a crew that follows it walks away from the fight it is there
                        // to win. AssistPlayer picks its own targets but does not walk the crew in.
                        // No single boss to close on during a seal, so close on the published kill
                        // location instead, the way CSBot's crew works off a shared KillLocation.
                        // AssistPlayer picks targets but never walks the crew in, so without this the
                        // barbarian stood wherever the portal dropped it.
                        if (focus == null && killLocation != null)
                        {
                            member.Game.RequestUpdate(member.Game.Me.Id);
                            if (member.Game.Me.Location.Distance(killLocation) > CrewEngageRange)
                            {
                                await MoveTo(member, killLocation, GetMovementMode(member));
                                continue;
                            }
                        }

                        if (focus != null)
                        {
                            member.Game.RequestUpdate(member.Game.Me.Id);
                            var live = member.Game.WorldObjects.GetValueOrDefault((focus.Id, EntityType.NPC)) ?? focus;
                            // Must exceed TeleportNoOpRange, not some tighter number: MoveTo returns
                            // success without moving inside that range, so a smaller engage distance
                            // leaves a band where the crew neither closes nor swings. Same trap that
                            // stranded the rushee at 2.8 units from a portal.
                            if (member.Game.Me.Location.Distance(live.Location) > CrewEngageRange)
                            {
                                await MoveTo(member, live.Location, GetMovementMode(member));
                                continue;
                            }
                        }

                        // CSBot's rule: a cursed barbarian never swings. Whirlwind under Iron Maiden
                        // reflected 1465 life in under half a second and chickened Rope one second
                        // after it arrived. CSBot walks to its paladin for a cleanse; this crew has
                        // none, so it runs away from the nearest hostile until the curse drops.
                        if (member.Game.Me.Effects.ContainsKey(EntityEffect.Ironmaiden))
                        {
                            // A healer removes the curse outright, so a quick trip out and back beats
                            // running in circles waiting for it to expire while the pack follows.
                            if (!await CleanseCurseAtHealer(member))
                            {
                                await RunFromHostilesWhileCursed(member);
                            }

                            continue;
                        }

                        // Assist on itself, the way CSBot's barbarian does. AssistPlayer picks targets
                        // within twenty units of the player it is given, and the rusher now stands
                        // thirty-eight units off the pack on purpose - so anchoring on her left the
                        // barbarian swinging at whatever happened to be near her instead of at the pack
                        // it is standing in.
                        if (member.Game.Me != null)
                        {
                            await _attackService.AssistPlayer(member, member.Game.Me);
                        }
                        else
                        {
                            await Task.Delay(300, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        Log.Warning(exception, "{Member} stopped assisting", member.Game.Me?.Name);
                        return;
                    }
                }
            });
        }

        return new CrewAssist(cancellation);
    }

    /// <summary>
    /// Whether cold damage is pointless against this target, so a cold rusher should contribute static
    /// field and leave the killing to the crew.
    /// </summary>
    /// <remarks>
    /// Base resistances are not on the wire - the client reads them from monstats.txt - so Duriel is a
    /// hardcoded constant. Monster enchantments ARE sent, though, so a Cold Enchanted seal boss is
    /// detectable at runtime; that covers Chaos Sanctuary De Seis, who is cold immune in Hell and on
    /// Nightmare only sometimes. Treating "cold enchanted" as "cold is pointless" is a hypothesis, not
    /// something measured - it costs a slower kill if wrong, never a lost fight, because static field
    /// and the crew still apply.
    /// </remarks>
    private bool ColdIsUselessAgainst(WorldObject target)
    {
        if (_gameDifficulty == Difficulty.Normal || target == null)
        {
            return false;
        }

        // De Seis carrying Magic Resistant above Normal behaves as cold immune: the one such fight
        // measured took 268 seconds and 440 landed hits, against 8 to 54 hits for every other seal boss.
        // Cold Enchanted counts here too, but only on him: both modifiers stack onto his already high
        // base cold resistance and push him over immunity, while on a Storm Caster or a Venom Lord the
        // same modifiers cost nothing - those died to orb in 10 to 26 seconds.
        if (target.NPCCode == NPCCode.OblivionKnight
            && target.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique)
            && (target.MonsterEnchantments.Contains(MonsterEnchantment.MagicResistant)
                || target.MonsterEnchantments.Contains(MonsterEnchantment.ColdEnchanted)))
        {
            return true;
        }

        // Everywhere else Cold Enchanted is not immunity, it is cold damage on the monster's own
        // attacks: five other seal bosses carried it and died to orb in 12 to 23 seconds.
        return ColdImmuneAboveNormal.Contains(target.NPCCode);
    }

    private async Task<bool> ReturnToTown(Client client)
    {
        if (client.Game.IsInTown())
        {
            return true;
        }

        if (await CloseOnOwnPortal(client) && await _townManagementService.TakeTownPortalToTown(client))
        {
            return true;
        }

        Log.Warning(
            "{Client} could not reach an existing portal in {Area}; casting a fresh one",
            client.Game.Me.Name,
            client.Game.Area);
        return await _townManagementService.CreateTownPortal(client)
            && await CloseOnOwnPortal(client)
            && await _townManagementService.TakeTownPortalToTown(client);
    }

    /// <summary>
    /// Walks to the client's own nearest town portal so that the town management service, which
    /// reaches it with <c>MoveToAsync</c> and so gives up silently beyond 20 units, always has a
    /// short hop to make. Recasting leaves two portals in the level and the service takes the first
    /// it finds, which is regularly the old distant one.
    /// </summary>
    private async Task<bool> CloseOnOwnPortal(Client client)
    {
        var portal = client.Game.GetEntityByCode(EntityCode.TownPortal)
            .Where(candidate => candidate.TownPortalOwnerId == client.Game.Me.Id)
            .OrderBy(candidate => client.Game.Me.Location.Distance(candidate.Location))
            .FirstOrDefault();
        if (portal == null)
        {
            return false;
        }

        client.Game.RequestUpdate(client.Game.Me.Id);
        await Task.Delay(300);
        return client.Game.Me.Location.Distance(portal.Location) <= TeleportNoOpRange
            || await MoveTo(client, portal.Location, GetMovementMode(client));
    }

    private async Task<bool> TakeWaypointFromTown(Client client, Waypoint waypoint)
    {
        if (!client.Game.IsInTown()
            && (!await _townManagementService.CreateTownPortal(client)
                || !await _townManagementService.TakeTownPortalToTown(client)))
        {
            return false;
        }

        try
        {
            return await _townManagementService.TakeWaypoint(client, waypoint);
        }
        catch (InvalidOperationException exception)
        {
            // Waypoints are held per difficulty. A rusher that has finished Hell can still arrive on
            // Nightmare holding nothing but the four town waypoints, which is exactly what happened
            // the first time this ran. Walk the route instead - the area crossing search already does
            // town to Catacombs 2 - so a missing waypoint costs time rather than the run.
            if (!Enum.TryParse<Area>(waypoint.ToString(), out var area))
            {
                Log.Error(exception, "No area matches waypoint {Waypoint}, so it cannot be walked to", waypoint);
                return false;
            }

            Log.Warning(
                "{Client} does not hold the {Waypoint} waypoint on {Difficulty}; walking to {Area} instead",
                client.Game.Me.Name,
                waypoint,
                _gameDifficulty,
                area);
            return await TraverseTo(client, area);
        }
    }

    private static Point MapPointOf(AreaMap map, EntityCode entityCode)
    {
        return map?.Objects != null
            && map.Objects.TryGetValue((int)entityCode, out var points)
            && points.Count > 0
                ? points[0]
                : null;
    }

    private static Point OffsetOrNull(Point point, int dx, int dy)
    {
        try
        {
            return point.Add((short)dx, (short)dy);
        }
        catch (ArithmeticException)
        {
            return null;
        }
    }

    /// <summary>
    /// A walkable point 18 to 25 units from the character, away from the nearby pack, rotating up to
    /// 180 degrees off that line when walls block the way. Uses the cached map only, so it is cheap
    /// enough to call every few seconds mid-fight.
    /// </summary>
    private async Task<Point> KitePointAwayFromPack(Client client, Point standoff)
    {
        var pack = NearbyHostiles(client, 15);
        var me = client.Game.Me.Location;
        if (pack.Count == 0)
        {
            return null;
        }

        // Back to the standoff first. An away-vector is meaningless inside a pack that surrounds her:
        // at De Seis's seal in rb43 it moved her from one side of the knights to the other, twenty-one
        // of them within six units, and she chickened. The standoff is open ground on the approach side
        // and is walkable by construction, so it is the one point worth retreating to.
        if (standoff != null
            && me.Distance(standoff) > TeleportNoOpRange
            && NearbyHostiles(client, 60).Count(hostile => hostile.Location.Distance(standoff) < 10) < 3)
        {
            return standoff;
        }

        double dx = me.X - pack.Average(hostile => hostile.Location.X);
        double dy = me.Y - pack.Average(hostile => hostile.Location.Y);
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            dx = 1;
        }

        var length = Math.Sqrt(dx * dx + dy * dy);
        dx /= length;
        dy /= length;

        foreach (var angle in new[] { 0.0, 60.0, -60.0, 120.0, -120.0, 180.0 })
        {
            var radians = angle * Math.PI / 180.0;
            var rx = dx * Math.Cos(radians) - dy * Math.Sin(radians);
            var ry = dx * Math.Sin(radians) + dy * Math.Cos(radians);
            foreach (var distance in new[] { 25.0, 18.0 })
            {
                var candidate = new Point((ushort)(me.X + rx * distance), (ushort)(me.Y + ry * distance));
                if (await _pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, Area.ChaosSanctuary, candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A walkable point about <paramref name="distance"/> units from a target on the character's side,
    /// rotating up to 60 degrees off the direct line when walls block it. Null when nothing fits.
    /// </summary>
    private async Task<Point> ApproachPoint(Client client, Point target, double distance)
    {
        var me = client.Game.Me.Location;
        double dx = me.X - target.X;
        double dy = me.Y - target.Y;
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            return null;
        }

        var length = Math.Sqrt(dx * dx + dy * dy);
        dx /= length;
        dy /= length;

        foreach (var angle in new[] { 0.0, 30.0, -30.0, 60.0, -60.0 })
        {
            var radians = angle * Math.PI / 180.0;
            var rx = dx * Math.Cos(radians) - dy * Math.Sin(radians);
            var ry = dx * Math.Sin(radians) + dy * Math.Cos(radians);
            foreach (var reach in new[] { distance, distance + 4, distance - 4 })
            {
                var candidate = new Point((ushort)(target.X + rx * reach), (ushort)(target.Y + ry * reach));
                if (candidate.Distance(me) >= TeleportNoOpRange
                    && await _pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, Area.ChaosSanctuary, candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One retried teleport for a hop within reach, the pathed mover beyond it. A single teleport reaches
    /// about 30 units; rb41 spent 35 seconds retrying a 40-unit hop toward Infector that could never land.
    /// </summary>
    private async Task HopTo(Client client, Point point, TimeSpan budget)
    {
        if (client.Game.Me.Location.Distance(point) > 25)
        {
            await MoveTo(client, point, MovementMode.Teleport);
            return;
        }

        await GeneralHelpers.TryWithTimeout(async _ => await client.Game.TeleportToLocationAsync(point), budget);
        if (client.Game.Me.Location.Distance(point) > 15)
        {
            await MoveTo(client, point, MovementMode.Teleport);
        }
    }

    /// <summary>
    /// Casts the best cold armor the character owns when it is not already up, and reports whether it
    /// cast. Every other sorceress in this repo does this - the cow, Chaos Sanctuary, Pindle and probe
    /// bots all keep it up - and the rusher was the only one fighting without it: she carries Shiver
    /// Armor at level five and had never cast it in a single run.
    /// </summary>
    private static async Task<bool> KeepArmorUp(Client client)
    {
        var me = client.Game.Me;
        if (me == null || me.Mana < 20)
        {
            return false;
        }

        if (me.HasSkill(Skill.ChillingArmor) && !me.Effects.ContainsKey(EntityEffect.ChillingArmor))
        {
            client.Game.UseRightHandSkillOnLocation(Skill.ChillingArmor, me.Location);
        }
        else if (me.HasSkill(Skill.ShiverArmor) && !me.Effects.ContainsKey(EntityEffect.Shiverarmor))
        {
            client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, me.Location);
        }
        else if (me.HasSkill(Skill.FrozenArmor)
            && !me.HasSkill(Skill.ShiverArmor)
            && !me.HasSkill(Skill.ChillingArmor)
            && !me.Effects.ContainsKey(EntityEffect.Frozenarmor))
        {
            client.Game.UseRightHandSkillOnLocation(Skill.FrozenArmor, me.Location);
        }
        else
        {
            return false;
        }

        await Task.Delay(150);
        return true;
    }

    /// <summary>Rejoins allowed per client per game. The rejoin costs about half a minute, so it is a
    /// backstop and not a plan: the fix for a chicken is not dying in the first place.</summary>
    private const int MaxRejoinsPerGame = 10;

    /// <summary>
    /// Puts a client that chickened back into the game and back into the party. Leaving used to be
    /// final: the crew was gone for the rest of the run and the rusher leaving failed the whole rush,
    /// which is how most of today's runs ended. The chicken watch keeps running across the rejoin, so
    /// the character is protected again the moment it is back.
    /// </summary>
    private async Task<bool> RejoinIfChickened(Client client)
    {
        try
        {
            return await RejoinChickenedClient(client);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Rejoining a chickened client failed");
            return false;
        }
    }

    private async Task<bool> RejoinChickenedClient(Client client)
    {
        if (client.Game.IsInGame())
        {
            return true;
        }

        if (!_accountOf.TryGetValue(client, out var account))
        {
            return false;
        }

        var used = _rejoinsUsed.GetValueOrDefault(client);
        if (used >= MaxRejoinsPerGame)
        {
            Log.Warning("{Character} has used its {Max} rejoins this game; leaving it out", account.Character, MaxRejoinsPerGame);
            return false;
        }

        _rejoinsUsed[client] = used + 1;
        Log.Warning("{Character} is out of {Game}; rejoining ({Used} of {Max})", account.Character, CurrentGameName, used + 1, MaxRejoinsPerGame);
        for (var attempt = 1; attempt <= 3 && !client.Game.IsInGame(); attempt++)
        {
            try
            {
                // Leaving disconnects the realm session, so joining without reconnecting first throws on
                // a disposed socket - and in rb50 that exception ended the whole rush rather than the
                // one character it belonged to.
                if (await client.RejoinMCP()
                    && await client.JoinGame(CurrentGameName, _botConfiguration.GamePassword))
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "{Character} threw while rejoining", account.Character);
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        if (!client.Game.IsInGame()
            || !GeneralHelpers.TryWithTimeout(_ => client.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            Log.Warning("{Character} could not rejoin {Game}", account.Character, CurrentGameName);
            return false;
        }

        client.Game.CleanupCursorItem();
        await Task.Delay(500);
        await GeneralHelpers.PickupCorpseIfExists(client, _pathingService);

        // Invites go out from everyone else: the rejoining client accepts, but nobody would ever ask.
        foreach (var other in _partyClients.Where(candidate => candidate != client && candidate.Game.IsInGame()))
        {
            var seen = other.Game.Players.FirstOrDefault(player =>
                player.Name.Equals(client.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            if (seen != null && !other.Game.IsInPartyWith(seen))
            {
                other.Game.InvitePlayer(seen);
            }
        }

        await Task.Delay(1000);
        Log.Information("{Character} is back in {Game}", account.Character, CurrentGameName);
        return true;
    }

    /// <summary>
    /// Keeps one crew portal standing wherever the crew is fighting, so a rusher who chickened can step
    /// back into the sanctuary from town instead of walking the River of Flame again.
    /// </summary>
    private async Task EnsureCrewPortal()
    {
        var member = _crew.FirstOrDefault(candidate =>
            candidate.Game.IsInGame() && !candidate.Game.IsInTown() && candidate.Game.Me != null);
        if (member == null)
        {
            return;
        }

        var standing = member.Game.GetEntityByCode(EntityCode.TownPortal)
            .Any(portal => portal.TownPortalOwnerId == member.Game.Me.Id
                && portal.Location.Distance(member.Game.Me.Location) < 40);
        if (standing)
        {
            return;
        }

        if (!await _townManagementService.CreateTownPortal(member))
        {
            Log.Debug("{Member} could not put up a spare portal", member.Game.Me.Name);
        }
    }

    /// <summary>Takes a crew member's portal back into the sanctuary, when one is visible from town.</summary>
    private async Task<bool> ReturnThroughCrewPortal(Client rusher)
    {
        foreach (var member in _crew.Where(candidate => candidate.Game.IsInGame() && candidate.Game.Me != null))
        {
            var owner = rusher.Game.Players.FirstOrDefault(player =>
                player.Name.Equals(member.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            if (owner == null)
            {
                continue;
            }

            var portal = rusher.Game.GetEntityByCode(EntityCode.TownPortal)
                .FirstOrDefault(candidate => candidate.TownPortalArea == Area.ChaosSanctuary
                    && candidate.TownPortalOwnerId == owner.Id);
            if (portal == null)
            {
                continue;
            }

            if (await _townManagementService.TakeTownPortalToArea(rusher, owner, Area.ChaosSanctuary))
            {
                Log.Information(
                    "The rusher came back through {Member}'s portal instead of walking the River of Flame",
                    member.Game.Me.Name);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fights whatever stands around a point until nothing is left within <paramref name="radius"/> or
    /// the time runs out. Used before opening the first seal so the party gathers on cleared ground.
    /// </summary>
    private async Task ClearAround(Client rusher, Point centre, double radius, TimeSpan limit)
    {
        var timer = Stopwatch.StartNew();
        var kiteTimer = Stopwatch.StartNew();
        while (timer.Elapsed < limit && rusher.Game.IsInGame())
        {
            if (await KeepArmorUp(rusher))
            {
                continue;
            }

            // Only what is actually reachable counts. Judging by distance alone had her working on
            // monsters standing behind the sanctuary walls, nowhere near the seal or the path to it,
            // and the clear spent its whole budget on them.
            var hostiles = new List<WorldObject>();
            foreach (var hostile in NearbyHostiles(rusher, radius))
            {
                if (hostile.Location.Distance(centre) > radius)
                {
                    continue;
                }

                if (await _attackService.IsInLineOfSight(rusher, hostile.Location))
                {
                    hostiles.Add(hostile);
                }
            }

            if (hostiles.Count == 0)
            {
                return;
            }

            var crowd = NearbyHostiles(rusher, 6);
            if (crowd.Count >= 3 && kiteTimer.Elapsed > SealKiteInterval)
            {
                kiteTimer.Restart();
                var kite = await KitePointAwayFromPack(rusher, centre);
                if (kite != null)
                {
                    await HopTo(rusher, kite, TimeSpan.FromSeconds(1.5));
                    continue;
                }
            }

            var target = hostiles.FirstOrDefault(hostile =>
                hostile.Location.Distance(rusher.Game.Me.Location) <= OrbEffectiveRange);
            if (target == null)
            {
                var approach = await ApproachPoint(rusher, hostiles[0].Location, OrbEngageRange);
                if (approach == null)
                {
                    return;
                }

                await HopTo(rusher, approach, TimeSpan.FromSeconds(1));
                continue;
            }

            await AlternateOrbAndStatic(rusher, target);
        }
    }

    /// <summary>Healing potions worth carrying before the floor is left alone.</summary>
    private const int HealthPotionCeiling = 24;

    /// <summary>Mana potions worth carrying before the floor is left alone.</summary>
    private const int ManaPotionCeiling = 12;

    /// <summary>Inventory slots kept clear for the potion reserve, whatever the floor is offering.</summary>
    private const int LootFreeSlotFloor = 4;

    /// <summary>Whether an item on the ground is worth the seconds it takes to walk over and take it.</summary>
    private static bool WantsOffTheFloor(Client client, Item item)
    {
        switch (item.Classification)
        {
            case ClassificationType.RejuvenationPotion:
                return true;
            case ClassificationType.HealthPotion:
                return InventoryHelpers.GetTotalHealthPotions(client.Game) < HealthPotionCeiling;
            case ClassificationType.ManaPotion:
                return InventoryHelpers.GetTotalManaPotions(client.Game) < ManaPotionCeiling;
        }

        // Never loot the potion reserve out of existence: a full inventory buys no spare potions, and
        // an empty belt is what ends runs.
        return (item.IsGold || FreeInventorySlots(client.Game) > LootFreeSlotFloor)
            && D2NG.Pickit.Pickit.ShouldPickupItem(client.Game, item, shouldPickupGoldItems: true);
    }

    /// <summary>
    /// Takes what a fight dropped: potions to stay out of town, and whatever the pickit rules want.
    /// Capped in both count and time so looting never costs more than the trip it saves.
    /// </summary>
    private static async Task SweepLoot(Client client, double radius)
    {
        if (!client.Game.IsInGame() || client.Game.Me == null)
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        var wanted = client.Game.Items.Values
            .Where(item => item.Ground
                && item.Location != null
                && item.Location.Distance(client.Game.Me.Location) < radius
                && WantsOffTheFloor(client, item))
            .OrderBy(item => item.Location.Distance(client.Game.Me.Location))
            .Take(6)
            .ToList();

        var taken = 0;
        foreach (var item in wanted)
        {
            if (timer.Elapsed > LootSweepLimit || !client.Game.IsInGame())
            {
                break;
            }

            // Re-checked per item: belts and inventories fill up as the sweep goes on.
            if (!WantsOffTheFloor(client, item))
            {
                continue;
            }

            if (await GeneralHelpers.TryWithTimeout(async _ =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
                await Task.Delay(100);
                var reach = client.Game.Me.Location.Distance(item.Location);
                if (reach > 3)
                {
                    // The walk helper sends nothing beyond twenty units, so an item across the room was
                    // retried until the sweep timed out. Teleport covers it in one cast.
                    if (reach > 15 && client.Game.Me.HasSkill(Skill.Teleport))
                    {
                        await client.Game.TeleportToLocationAsync(item.Location);
                    }
                    else
                    {
                        await client.Game.MoveToAsync(item.Location);
                    }

                    return false;
                }

                client.Game.PickupItem(item);
                await Task.Delay(150);
                return !client.Game.Items.TryGetValue(item.Id, out var onGround) || !onGround.Ground;
            }, TimeSpan.FromSeconds(1.5)))
            {
                taken++;
            }

            if (client.Game.CursorItem != null)
            {
                client.Game.CleanupCursorItem();
                await Task.Delay(200);
            }

            // The cube is twelve more slots inside a two by two footprint, and the town step sells or
            // keeps whatever is in it exactly as it does the inventory. Never potions though: a potion
            // in the cube cannot be drunk, so it is worse than not carrying it at all.
            if (FreeInventorySlots(client.Game) <= LootFreeSlotFloor
                && item.Classification is not (ClassificationType.HealthPotion
                    or ClassificationType.ManaPotion
                    or ClassificationType.RejuvenationPotion))
            {
                var carried = client.Game.Inventory.FindItemById(item.Id);
                var cubeSpace = carried == null ? null : client.Game.Cube.FindFreeSpace(carried);
                if (cubeSpace != null
                    && InventoryHelpers.PutInventoryItemInCube(client.Game, carried, cubeSpace) == MoveItemResult.Succes)
                {
                    Log.Debug("{Client} stored {Item} in the cube to keep room free", client.Game.Me.Name, carried.Name);
                    await Task.Delay(150);
                }
            }
        }

        if (taken > 0)
        {
            Log.Information(
                "{Client} picked up {Taken} items in {Seconds:0.0}s; holding {Health} healing and {Mana} mana",
                client.Game.Me.Name,
                taken,
                timer.Elapsed.TotalSeconds,
                InventoryHelpers.GetTotalHealthPotions(client.Game),
                InventoryHelpers.GetTotalManaPotions(client.Game));
        }
    }

    /// <summary>How long a loot sweep may take before the run matters more than the floor.</summary>
    private static readonly TimeSpan LootSweepLimit = TimeSpan.FromSeconds(6);

    /// <summary>Least time between two healer trips for the same character.</summary>
    private static readonly TimeSpan CleanseInterval = TimeSpan.FromSeconds(45);

    private readonly Dictionary<string, DateTime> _lastCleanse = [];

    /// <summary>
    /// Takes a cursed crew member to the town healer, which removes the curse and refills life, then
    /// brings it back through the nearest portal. Iron Maiden is the one curse worth a trip: a
    /// whirlwinding barbarian under it reflected 1465 life onto itself in under half a second.
    /// </summary>
    private async Task<bool> CleanseCurseAtHealer(Client member)
    {
        var name = member.Game.Me?.Name;
        if (name == null)
        {
            return false;
        }

        if (_lastCleanse.TryGetValue(name, out var previous) && DateTime.Now - previous < CleanseInterval)
        {
            return false;
        }

        _lastCleanse[name] = DateTime.Now;
        var area = Area.ChaosSanctuary;
        Log.Information("{Member} is iron maidened; going to the healer", name);
        if (!await ReturnToTown(member))
        {
            return false;
        }

        NPCHelpers.HealAtHealer(member.Game);

        foreach (var owner in member.Game.Players.OrderBy(player =>
            player.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            var portal = member.Game.GetEntityByCode(EntityCode.TownPortal)
                .FirstOrDefault(candidate => candidate.TownPortalArea == area
                    && candidate.TownPortalOwnerId == owner.Id);
            if (portal == null)
            {
                continue;
            }

            if (await _townManagementService.TakeTownPortalToArea(member, owner, area))
            {
                Log.Information("{Member} is cleansed and back in {Area}", name, area);
                return true;
            }
        }

        Log.Warning("{Member} was cleansed but found no portal back to {Area}", name, area);
        return false;
    }

    private static async Task RunFromHostilesWhileCursed(Client member)
    {
        var nearest = NearbyHostiles(member, 40).FirstOrDefault();
        var me = member.Game.Me.Location;
        var jitter = new Random();
        var away = nearest == null
            ? me.Add((short)jitter.Next(-8, 9), (short)jitter.Next(-8, 9))
            : nearest.Location.GetPointPastPointInSameDirection(me, 10)
                .Add((short)jitter.Next(-3, 4), (short)jitter.Next(-3, 4));
        Log.Information("{Member} is iron maidened, running to {Away}", member.Game.Me.Name, away);
        await member.Game.MoveToAsync(away);
        await Task.Delay(150);
    }

    private static List<WorldObject> NearbyHostiles(Client client, double radius)
    {
        return [.. client.Game.WorldObjects
            .Where(entry => entry.Key.Item2 == EntityType.NPC)
            .Select(entry => entry.Value)
            .Where(entity => entity.State is not EntityState.Dead and not EntityState.Dieing
                && !NPCHelpers.IsFriendly(entity.NPCCode)
                && entity.Location.Distance(client.Game.Me.Location) < radius)
            .OrderBy(entity => entity.Location.Distance(client.Game.Me.Location))];
    }

    private static bool HasFinished(Client client, QuestId quest)
    {
        return client.Game.Quests.IsComplete(quest)
            || client.Game.Quests.IsAcknowledged(quest);
    }

    private static bool HasKillCredit(Client client, QuestId quest)
    {
        return HasFinished(client, quest)
            || client.Game.Quests.IsAwaitingReward(quest)
            || client.Game.Quests.IsCreditedThisGame(quest);
    }

    private static bool HasDurielCredit(Client client)
    {
        return HasKillCredit(client, QuestId.TheSevenTombs)
            || (client.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs) & DurielDefeatedBit) != 0;
    }

    private static bool HasDiabloCredit(Client client)
    {
        return HasKillCredit(client, QuestId.TerrorsEnd)
            || (client.Game.Quests.GetCharacterFlags(QuestId.TerrorsEnd) & DiabloDefeatedBit) != 0;
    }

    private static async Task RefreshQuestData(IEnumerable<ParticipantSession> sessions)
    {
        foreach (var session in sessions)
        {
            session.Client.Game.RequestQuestData();
        }

        await Task.Delay(1500);
    }

    private bool RequiresDestinationGameProof()
    {
        return _rushConfiguration.RehearsalDifficulty == null
            && (_rushConfiguration.Destination.Difficulty != Difficulty.Normal
                || _rushConfiguration.Destination.Milestone == RushMilestone.Complete);
    }

    private Difficulty DestinationProofDifficulty()
    {
        if (_rushConfiguration.Destination.Difficulty == Difficulty.Normal
            && _rushConfiguration.Destination.Act == 4
            && _rushConfiguration.Destination.Milestone == RushMilestone.Complete)
        {
            return Difficulty.Nightmare;
        }

        return _rushConfiguration.Destination.Difficulty;
    }

    private async Task<bool> TraverseTo(Client client, Area destination)
    {
        var traverseTimer = Stopwatch.StartNew();
        var arrived = false;
        try
        {
            arrived = await TraverseToCore(client, destination);
            return arrived;
        }
        finally
        {
            Log.Information(
                "TIMING traverse-{Destination} {Outcome} in {Seconds:0.0}s on {Difficulty}",
                destination,
                arrived ? "ok" : "FAILED",
                traverseTimer.Elapsed.TotalSeconds,
                _gameDifficulty);
        }
    }

    private async Task<bool> TraverseToCore(Client client, Area destination)
    {
        var route = await _pathingService.GetAreaRoute(
            client.Game.MapId,
            MapDifficulty,
            client.Game.Area,
            destination);
        if (route.Count == 0)
        {
            Log.Error("No route from {From} to {Destination}", client.Game.Area, destination);
            return false;
        }

        foreach (var target in route.Skip(1))
        {
            var entered = false;
            for (var attempt = 0; attempt < AreaHopAttempts && !entered; attempt++)
            {
                var mode = GetMovementMode(client);
                var from = client.Game.Area;

                // Ask where we actually are first. Straight off a waypoint the cached location still
                // holds the old one, and pathing snaps the destination but never the source: a stale
                // position 37 units off the walkable grid made every exit look unreachable and ended
                // the rush at "1 exits, 0 crossings, tried []".
                client.Game.RequestUpdate(client.Game.Me.Id);
                await Task.Delay(500);

                // Wait for the position to be one the map agrees is walkable before pathing from it.
                // Pathing from a stale position is what produced "no reachable exit or crossing" from a
                // spot that was plainly fine. Pathing from the waypoint instead was worse: it returned a
                // path starting 430 units away that nothing could follow.
                if (!await SyncPosition(client, from))
                {
                    // A waypoint tile is flagged unwalkable in the map grid, so standing on one makes
                    // every path come back empty: pathing snaps the destination but never the source.
                    // Step off it the way a player does, without pathing, since pathing is what is broken.
                    await NudgeOntoWalkableGround(client, from);
                }

                // The crossing search ranks candidates by distance from where you stand, so retrying from
                // the same spot returns the same unreachable candidate every time. Move first.
                if (attempt > 0)
                {
                    await StepAside(client, from);
                }

                List<Point> path;
                try
                {
                    path = await _pathingService.GetPathToAdjacentArea(
                        client.Game.MapId,
                        MapDifficulty,
                        from,
                        client.Game.Me.Location,
                        target,
                        mode);
                }
                catch (InvalidOperationException exception)
                {
                    // One unreachable read is worth another attempt, not the end of the rush.
                    Log.Warning(
                        "No path from {From} into {Target} on attempt {Attempt} at {Location}: {Reason}",
                        from,
                        target,
                        attempt + 1,
                        client.Game.Me.Location,
                        exception.Message);
                    await Task.Delay(1000);
                    continue;
                }
                Log.Information(
                    "CROSSING {From}->{Target} attempt {Attempt} via {Source}: at {Location}, path {Count} "
                    + "points {First}..{Last}",
                    from,
                    target,
                    attempt + 1,
                    "position",
                    client.Game.Me.Location,
                    path.Count,
                    path.Count > 0 ? path[0].ToString() : "none",
                    path.Count > 0 ? path[^1].ToString() : "none");

                // Teleport tops out around 30 units and the planner strides right at that ceiling, so a
                // hop that will not land leaves the rusher parked repeating the same failed path. The
                // result was being discarded, which is why it looked like the level was impassable.
                var moved = await MovementHelpers.TakePathOfLocations(client.Game, path, mode);
                if (!moved && mode == MovementMode.Teleport)
                {
                    Log.Information(
                        "Teleport path stalled at {Location} on the way to {Target}; walking it instead",
                        client.Game.Me.Location,
                        target);
                    await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking);
                }

                client.Game.RequestUpdate(client.Game.Me.Id);
                await Task.Delay(400);
                var warp = client.Game.GetNearestWarp();
                Log.Information(
                    "CROSSING {From}->{Target} landed at {Location}, nearest warp {Warp} at {Distance:0.0}",
                    from,
                    target,
                    client.Game.Me.Location,
                    warp?.Location.ToString() ?? "none",
                    warp == null ? -1 : warp.Location.Distance(client.Game.Me.Location));
                if (warp == null || warp.Location.Distance(client.Game.Me.Location) >= 25)
                {
                    // The warp is streamed when the client comes into range, so a path that stopped
                    // short leaves nothing to take. Worth another pass rather than calling the level
                    // impassable - this is where the Durance 3 entrance is flaky.
                    Log.Debug(
                        "No warp within reach of {Location} for {Target} on attempt {Attempt}",
                        client.Game.Me.Location,
                        target,
                        attempt + 1);
                }
                else
                {
                    await MovementHelpers.TakeWarp(
                        client.Game,
                        _pathingService,
                        _mapApiService,
                        mode,
                        warp,
                        target);
                }

                entered = await ConfirmInArea(client, target);
            }

            if (!entered)
            {
                Log.Error("Rusher stopped in {Area} while trying to enter {Target}", client.Game.Area, target);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Waits until the position the client believes it is at is one the map agrees is walkable, before
    /// anything paths from it. Position is stale for a moment after every area change, and pathing from a
    /// stale one shows up as a teleport that reports failure without moving, or as an exit that looks
    /// unreachable from a spot that is plainly fine.
    /// </summary>
    /// <summary>
    /// The Durance 3 stairs, taken the way the Mephisto bot takes them: path from the waypoint that was
    /// just used and take the warp that comes into view. Done immediately, while still standing on the
    /// waypoint, this is a four second hop.
    /// </summary>
    /// <remarks>
    /// The generic crossing search does not work here. It ranks candidates by distance from the current
    /// position, and the waypoint tile it starts from is not walkable in the map grid, so it returns an
    /// empty path and the rusher stands still. Pathing from the waypoint has no such problem, but only
    /// while the rusher is still on it - on a retry it yields a path starting hundreds of units away.
    /// </remarks>
    private async Task<bool> EnterDuranceThreeFromWaypoint(Client rusher)
    {
        var mode = GetMovementMode(rusher);
        // Difficulty.Normal on purpose, matching the Mephisto bot, which crosses this border in four
        // seconds including in Hell games. The map api returns a different layout per difficulty for the
        // same map id, and the Nightmare layout put the stairs somewhere the warp never appeared.
        var path = await _pathingService.GetPathFromWaypointToArea(
            rusher.Game.MapId,
            Difficulty.Normal,
            Area.DuranceOfHateLevel2,
            Waypoint.DuranceOfHateLevel2,
            Area.DuranceOfHateLevel3,
            mode);
        Log.Information(
            "Durance 3 path from the waypoint: {Count} points {First}..{Last}, standing at {Location}",
            path.Count,
            path.Count > 0 ? path[0].ToString() : "none",
            path.Count > 0 ? path[^1].ToString() : "none",
            rusher.Game.Me.Location);

        if (!await MovementHelpers.TakePathOfLocations(rusher.Game, path, mode))
        {
            Log.Warning("Moving to the Durance 3 warp stopped at {Location}", rusher.Game.Me.Location);
        }

        var warp = rusher.Game.GetNearestWarp();
        if (warp == null || warp.Location.Distance(rusher.Game.Me.Location) > 20)
        {
            Log.Error(
                "No Durance 3 warp within reach: nearest {Warp} while at {Location}",
                warp?.Location.ToString() ?? "none",
                rusher.Game.Me.Location);
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(
                async _ =>
                {
                    if (warp.Location.Distance(rusher.Game.Me.Location) > 5
                        && !await rusher.Game.TeleportToLocationAsync(warp.Location))
                    {
                        return false;
                    }

                    await rusher.Game.MoveToAsync(warp.Location);
                    return rusher.Game.TakeWarp(warp);
                },
                TimeSpan.FromSeconds(4)))
        {
            Log.Error("Taking the Durance 3 warp failed at {Location}", rusher.Game.Me.Location);
            return false;
        }

        return await ConfirmInArea(rusher, Area.DuranceOfHateLevel3);
    }

    /// <summary>How close a monster gets before the sorceress gives ground rather than trading hits.</summary>
    private const double CrowdedRange = 12;

    /// <summary>Life fraction below which giving ground is worth losing a cast for.</summary>
    private const double KiteBelowLifeFraction = 0.85;

    /// <summary>Minimum gap between repositions, so the fight is casting punctuated by moves.</summary>
    private static readonly TimeSpan KiteInterval = TimeSpan.FromSeconds(2.5);

    private readonly Dictionary<string, DateTime> _lastKite = [];

    /// <summary>How far from a target orb still lands, so a retreat stays a retreat and not a withdrawal.</summary>
    private const double OrbStandoff = 25;

    /// <summary>
    /// Steps to open ground that still has line of sight to the target. Returns whether it moved, so the
    /// caller casts on the next pass rather than casting from where it was already being hit.
    /// </summary>
    private async Task<bool> KiteToClearGround(Client client, WorldObject target)
    {
        var me = client.Game.Me;

        // Casting beats repositioning. Moving before every cast turned the fight into constant
        // teleporting with almost no orbs thrown, so give ground only when actually being hurt, and
        // no more often than this.
        if (me.MaxLife > 0 && (double)me.Life / me.MaxLife > KiteBelowLifeFraction)
        {
            return false;
        }

        if (_lastKite.TryGetValue(me.Name, out var previous) && DateTime.Now - previous < KiteInterval)
        {
            return false;
        }

        var crowd = client.Game.WorldObjects.Values
            .Where(entity => entity.Type == EntityType.NPC
                && entity.Id != target.Id
                && entity.Location.Distance(me.Location) <= CrowdedRange)
            .Select(entity => entity.Location)
            .ToList();
        if (crowd.Count == 0)
        {
            return false;
        }

        _lastKite[me.Name] = DateTime.Now;
        return await _attackService.MoveToNearbySafeSpot(
            client,
            crowd,
            target.Location,
            GetMovementMode(client),
            CrowdedRange,
            OrbStandoff);
    }

    private async Task<bool> SyncPosition(Client client, Area area)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, MapDifficulty, area, client.Game.Me.Location))
            {
                return true;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(250);
        }

        Log.Warning(
            "{Name} is at {Location} in {Area}, which the map does not consider walkable",
            client.Game.Me.Name,
            client.Game.Me.Location,
            area);
        return false;
    }

    /// <summary>
    /// Moves a few units to ground the map considers walkable, using direct movement rather than the
    /// pathing service, which cannot produce a path out of an unwalkable source in the first place.
    /// </summary>
    private async Task<bool> NudgeOntoWalkableGround(Client client, Area area)
    {
        var from = client.Game.Me.Location;
        foreach (var (dx, dy) in new[]
                 {
                     (0, 18), (18, 0), (0, -18), (-18, 0),
                     (13, 13), (-13, 13), (13, -13), (-13, -13),
                 })
        {
            var candidate = new Point((ushort)(from.X + dx), (ushort)(from.Y + dy));
            if (!await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, MapDifficulty, area, candidate))
            {
                continue;
            }

            // Deliberately not MoveTo: that helper reports success without sending anything under its
            // no-op range, which is the distance this step lives in.
            await client.Game.MoveToAsync(candidate);
            await Task.Delay(400);
            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(300);

            if (await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, MapDifficulty, area, client.Game.Me.Location))
            {
                Log.Information(
                    "Stepped off unwalkable ground at {From} onto {To}",
                    from,
                    client.Game.Me.Location);
                return true;
            }
        }

        Log.Warning("Could not step off unwalkable ground at {From} in {Area}", from, area);
        return false;
    }

    private async Task StepAside(Client client, Area area)
    {
        var from = client.Game.Me.Location;
        var mode = GetMovementMode(client);
        foreach (var (dx, dy) in new[] { (0, -40), (40, 0), (0, 40), (-40, 0), (30, -30), (-30, 30) })
        {
            var candidate = new Point((ushort)(from.X + dx), (ushort)(from.Y + dy));
            if (!await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, MapDifficulty, area, candidate))
            {
                continue;
            }

            var path = await _pathingService.GetPathToLocation(client.Game, candidate, mode);
            if (path.Count > 0 && await MovementHelpers.TakePathOfLocations(client.Game, path, mode))
            {
                Log.Debug("Stepped aside from {From} to {To} before retrying", from, client.Game.Me.Location);
                return;
            }
        }
    }

    /// <param name="attempts">
    /// How many times to re-ask before giving up. Confirming arrival after an interact is worth eight
    /// tries; asking whether we are somewhere before setting off is worth one, and looping there spent
    /// four seconds of every single ferry proving a negative.
    /// </param>
    private async Task<bool> ConfirmInArea(Client client, Area area, int attempts = 8)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(500);
            if (await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId,
                    MapDifficulty,
                    area,
                    client.Game.Me.Location))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> MoveToObject(Client client, EntityCode code)
    {
        var mode = GetMovementMode(client);
        for (var attempt = 0; attempt < AreaHopAttempts; attempt++)
        {
            var path = await _pathingService.GetPathToObject(client.Game, code, mode);
            if (path.Count > 0 && await MovementHelpers.TakePathOfLocations(client.Game, path, mode))
            {
                return true;
            }
            await Task.Delay(500);
        }

        Log.Error("Could not reach {Object} in {Area}", code, client.Game.Area);
        return false;
    }

    private async Task<Area?> ResolveTombWithOrifice(Client client)
    {
        var canyon = await _mapApiService.GetArea(client.Game.MapId, MapDifficulty, Area.CanyonOfTheMagi);
        var candidates = new List<Area>();
        if (canyon?.TombArea != null)
        {
            candidates.Add(canyon.TombArea.Value);
        }

        for (var tomb = Area.TalRashasTomb1; tomb <= Area.TalRashasTomb7; tomb++)
        {
            if (!candidates.Contains(tomb))
            {
                candidates.Add(tomb);
            }
        }

        foreach (var tomb in candidates)
        {
            var map = await _mapApiService.GetArea(client.Game.MapId, MapDifficulty, tomb);
            if (map?.Objects?.ContainsKey((int)EntityCode.HoradricOrifice) == true)
            {
                Log.Information(
                    "The orifice is in {Tomb}{Note}",
                    tomb,
                    canyon?.TombArea == tomb ? "" : $", though the canyon reported {canyon?.TombArea}");
                return tomb;
            }
        }

        Log.Error("No tomb in map {MapId} has an orifice; canyon reported {Reported}", client.Game.MapId, canyon?.TombArea);
        return null;
    }

    private async Task<bool> EnterDurielsLair(Client rusher)
    {
        var lairTimer = Stopwatch.StartNew();
        var entered = false;
        try
        {
            entered = await EnterDurielsLairCore(rusher);
            return entered;
        }
        finally
        {
            // Report the outcome, not just the elapsed time. Logging "finished" from a finally made a
            // 12.3s failure read exactly like a success, and it was believed.
            Log.Information(
                "TIMING lair-entry for {Client} {Outcome} in {Seconds:0.0}s",
                rusher.Game.Me?.Name,
                entered ? "ok" : "FAILED",
                lairTimer.Elapsed.TotalSeconds);
        }
    }

    private async Task<bool> EnterDurielsLairCore(Client rusher)
    {
        WorldObject portal = null;
        await GeneralHelpers.TryWithTimeout(async _ =>
        {
            portal = rusher.Game.GetEntityByCode(EntityCode.DurielsLairPortal).FirstOrDefault();
            if (portal != null)
            {
                return true;
            }

            var orifice = rusher.Game.GetEntityByCode(EntityCode.HoradricOrifice).FirstOrDefault();
            if (orifice != null)
            {
                rusher.Game.InteractWithEntity(orifice);
            }
            await Task.Delay(500);
            return false;
        }, TimeSpan.FromSeconds(12));

        if (portal == null)
        {
            Log.Error("Spent-staff game did not expose Duriel's lair portal after touching the orifice");
            return false;
        }

        return await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            rusher.Game.RequestUpdate(rusher.Game.Me.Id);
            if (rusher.Game.Me.Location.Distance(portal.Location) > 5)
            {
                if (attempt % 3 == 0)
                {
                    await MovementHelpers.MoveToWorldObject(
                        rusher.Game,
                        _pathingService,
                        _mapApiService,
                        portal,
                        GetMovementMode(rusher));
                }
                else
                {
                    await rusher.Game.MoveToAsync(portal);
                }
                return false;
            }

            rusher.Game.InteractWithEntity(portal);
            await Task.Delay(1500);
            return await ConfirmInArea(rusher, Area.DurielsLair);
        }, TimeSpan.FromSeconds(30));
    }

    private async Task<bool> MoveToActTwoPortalSpot(Client rushee, Player rusher, Area destination)
    {
        var town = await _mapApiService.GetArea(rushee.Game.MapId, MapDifficulty, Area.LutGholein);
        var waypoint = town?.Objects?.GetValueOrDefault((int)EntityCode.WaypointAct2)?.FirstOrDefault();
        if (waypoint == null)
        {
            Log.Error("Map API did not provide the Lut Gholein waypoint");
            return false;
        }

        var portalSpot = new Point((ushort)(waypoint.X + 109), (ushort)(waypoint.Y - 31));
        var path = await _pathingService.GetPathToLocation(
            rushee.Game,
            portalSpot,
            GetMovementMode(rushee));
        await MovementHelpers.TakePathOfLocations(rushee.Game, path, GetMovementMode(rushee));

        var visible = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await Task.Delay(250);
            return rushee.Game.GetEntityByCode(EntityCode.TownPortal)
                .Any(portal => portal.TownPortalArea == destination && portal.TownPortalOwnerId == rusher.Id);
        }, TimeSpan.FromSeconds(10));
        if (!visible)
        {
            Log.Error("{Rushee} cannot see the rusher portal from measured spot {Spot}",
                rushee.Game.Me.Name, portalSpot);
        }

        return visible;
    }

    private async Task<bool> TalkToTyrael(Client rushee)
    {
        var lair = await _mapApiService.GetArea(rushee.Game.MapId, MapDifficulty, Area.DurielsLair);
        var tyraelSpot = lair?.Npcs?.GetValueOrDefault((int)NPCCode.TyraelAct3)?.FirstOrDefault();
        if (tyraelSpot == null)
        {
            Log.Error("Map API did not place Tyrael in Duriel's lair");
            return false;
        }

        var approach = await FindApproachTo(rushee, tyraelSpot);
        if (approach == null)
        {
            Log.Error("No walkable approach to Tyrael at {Spot}", tyraelSpot);
            return false;
        }

        var path = await _pathingService.GetPathToLocation(rushee.Game, approach, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rushee.Game, path, MovementMode.Walking);
        }

        var visible = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (rushee.Game.GetNPCsByCode(NPCCode.TyraelAct3).Count > 0)
            {
                return true;
            }

            await rushee.Game.MoveToAsync(approach);
            await Task.Delay(300);
            return rushee.Game.GetNPCsByCode(NPCCode.TyraelAct3).Count > 0;
        }, TimeSpan.FromSeconds(20));
        var tyrael = visible ? NPCHelpers.GetUniqueNPC(rushee.Game, NPCCode.TyraelAct3) : null;
        if (tyrael == null)
        {
            Log.Error("Tyrael was not visible from {Location}", rushee.Game.Me.Location);
            return false;
        }

        await rushee.Game.MoveToAsync(tyrael);
        rushee.Game.InitiateEntityChat(tyrael);
        await Task.Delay(1000);
        rushee.Game.SendQuestMessage(tyrael, TyraelsSevenTombsMessage);
        await Task.Delay(1500);
        rushee.Game.TerminateEntityChat(tyrael);
        rushee.Game.RequestQuestData();
        await Task.Delay(1000);

        var flags = rushee.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs);
        Log.Information("{Rushee} talked to Tyrael; seven tombs is 0x{Flags:X4}", rushee.Game.Me.Name, flags);
        return flags != 0;
    }

    private async Task<Point> FindApproachTo(Client client, Point target)
    {
        var candidates = new List<Point> { target };
        int[] radii = [8, 16, 24, 32];
        (int Dx, int Dy)[] directions =
        [
            (0, 1),
            (0, -1),
            (1, 0),
            (-1, 0),
            (1, 1),
            (1, -1),
            (-1, 1),
            (-1, -1)
        ];

        foreach (var radius in radii)
        {
            foreach (var direction in directions)
            {
                candidates.Add(new Point(
                    (ushort)(target.X + (direction.Dx * radius)),
                    (ushort)(target.Y + (direction.Dy * radius))));
            }
        }

        foreach (var candidate in candidates)
        {
            if (await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId,
                    MapDifficulty,
                    client.Game.Area,
                    candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<bool> ReturnToLutGholein(Client rushee)
    {
        var returned = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            var portal = rushee.Game.GetEntityByCode(EntityCode.TownPortal)
                .OrderBy(entity => entity.Location.Distance(rushee.Game.Me.Location))
                .FirstOrDefault();
            if (portal == null)
            {
                await Task.Delay(100);
                return false;
            }

            rushee.Game.RequestUpdate(rushee.Game.Me.Id);
            await Task.Delay(100);
            if (rushee.Game.Me.Location.Distance(portal.Location) > 5)
            {
                await MovementHelpers.MoveToWorldObject(
                    rushee.Game,
                    _pathingService,
                    _mapApiService,
                    portal,
                    MovementMode.Walking);
                return false;
            }

            rushee.Game.InteractWithEntity(portal);
            await Task.Delay(1500);
            return await _pathingService.IsNavigatablePointInArea(
                rushee.Game.MapId,
                MapDifficulty,
                Area.LutGholein,
                rushee.Game.Me.Location);
        }, TimeSpan.FromSeconds(30));
        if (!returned)
        {
            Log.Error("{Rushee} could not take Tyrael's portal to Lut Gholein", rushee.Game.Me.Name);
        }
        return returned;
    }

    private async Task<bool> TalkToJerhyn(Client rushee)
    {
        var path = await _pathingService.GetPathToLocation(rushee.Game, JerhynCourtyard, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rushee.Game, path, MovementMode.Walking);
        }

        var jerhyn = await WaitForNpc(rushee, NPCCode.Jerhyn);
        if (jerhyn == null)
        {
            Log.Error("Jerhyn was not visible from {Location}", rushee.Game.Me.Location);
            return false;
        }

        await rushee.Game.MoveToAsync(jerhyn);
        rushee.Game.InitiateEntityChat(jerhyn);
        await Task.Delay(750);
        foreach (var message in JerhynMessages)
        {
            rushee.Game.SendQuestMessage(jerhyn, message);
            await Task.Delay(400);
        }
        rushee.Game.TerminateEntityChat(jerhyn);
        rushee.Game.RequestQuestData();
        await Task.Delay(1000);
        return true;
    }

    private async Task<bool> TravelWithMeshif(Client rushee)
    {
        var path = await _pathingService.GetPathToNPC(rushee.Game, NPCCode.Meshif, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rushee.Game, path, MovementMode.Walking);
        }

        var meshif = await WaitForNpc(rushee, NPCCode.Meshif);
        if (meshif == null)
        {
            Log.Error("Meshif was not visible from {Location}", rushee.Game.Me.Location);
            return false;
        }

        await rushee.Game.MoveToAsync(meshif);
        rushee.Game.InitiateEntityChat(meshif);
        await Task.Delay(750);
        foreach (var message in MeshifMessages)
        {
            rushee.Game.SendQuestMessage(meshif, message);
            await Task.Delay(400);
        }
        rushee.Game.TravelWithNpc(meshif);

        var travelled = GeneralHelpers.TryWithTimeout(_ => rushee.Game.Act == Act.Act3, TimeSpan.FromSeconds(15));
        if (!travelled)
        {
            Log.Error("Meshif did not take {Rushee} to act 3; seven tombs 0x{Flags:X4}",
                rushee.Game.Me.Name,
                rushee.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs));
        }
        return travelled;
    }

    private static async Task<WorldObject> WaitForNpc(Client client, NPCCode code)
    {
        WorldObject npc = null;
        await GeneralHelpers.TryWithTimeout(async _ =>
        {
            npc = client.Game.GetNPCsByCode(code).FirstOrDefault();
            if (npc != null)
            {
                return true;
            }
            await Task.Delay(250);
            return false;
        }, TimeSpan.FromSeconds(15));
        return npc;
    }

    private static MovementMode GetMovementMode(Client client)
    {
        return client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
    }

    private static void AcceptPartyInvites(Client client)
    {
        client.OnReceivedPacketEvent(InComingPacket.EventMessage, packet =>
        {
            var notification = new EventNotifyPacket(packet);
            if (notification.PlayerRelationType != PlayerRelationType.InvitesYouToParty)
            {
                return;
            }

            var inviter = client.Game.Players.FirstOrDefault(player => player.Id == notification.EntityId);
            if (inviter != null)
            {
                client.Game.AcceptInvite(inviter);
            }
        });
    }

    private static void InviteJoiningPlayers(Client host)
    {
        host.OnReceivedPacketEvent(InComingPacket.PlayerInGame, packet =>
        {
            var joined = new PlayerInGamePacket(packet);
            if (host.Game.Me?.Id == joined.Id)
            {
                return;
            }

            var player = host.Game.Players.FirstOrDefault(candidate => candidate.Id == joined.Id);
            if (player != null && !host.Game.IsInPartyWith(player))
            {
                host.Game.InvitePlayer(player);
            }
        });
    }

    private static string Describe(RosterEntry entry)
    {
        return $"{entry.Character.Name}=0x{entry.Character.Progression:X2}";
    }

    private static bool Satisfies(CharacterProgression progression, RushDestination destination)
    {
        var needed = ((int)destination.Difficulty * progression.ActsPerDifficulty)
            + (int)destination.ActValue;
        if (destination.Milestone == RushMilestone.Complete)
        {
            needed++;
        }

        return progression.CompletedActs >= needed;
    }

    private static string DescribeMode(Character character)
    {
        return $"{(character.IsExpansion ? "Expansion" : "Classic")}/{(character.IsHardCore ? "Hardcore" : "Softcore")}";
    }

    private static string DescribeQuestState(ParticipantSession session)
    {
        return $"{session.Client.Game.Me.Name}: act={session.Client.Game.Act}, "
            + $"staff=0x{session.Client.Game.Quests.GetCharacterFlags(QuestId.TheHoradricStaff):X4}, "
            + $"tombs=0x{session.Client.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs):X4}, "
            + $"act2outro=0x{session.Client.Game.Quests.GetCharacterFlags(QuestId.Act2Outro):X4}, "
            + $"guardian=0x{session.Client.Game.Quests.GetCharacterFlags(QuestId.TheGuardian):X4}, "
            + $"terror=0x{session.Client.Game.Quests.GetCharacterFlags(QuestId.TerrorsEnd):X4}";
    }

    private string CurrentGameName { get; set; }

    private sealed class RosterEntry
    {
        public RosterEntry(AccountConfig account, Character character, CharacterProgression progression)
        {
            Account = account;
            Character = character;
            Progression = progression;
        }

        public AccountConfig Account { get; }
        public Character Character { get; }
        public CharacterProgression Progression { get; }
    }

    private sealed class ParticipantSession
    {
        public ParticipantSession(RosterEntry entry, Client client)
        {
            Entry = entry;
            Client = client;
        }

        public RosterEntry Entry { get; }
        public Client Client { get; }
    }
}
