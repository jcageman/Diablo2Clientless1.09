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
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.RushProbe;

/// <summary>
/// Harness that proves, one step at a time and against a live server, the primitives a rush depends
/// on: logging on, provisioning a throwaway character, reading quest state, and recovering from
/// death. Every step is opt in through configuration so a failure stops the run before the next step
/// sends anything.
/// </summary>
public class RushProbeBot : IBotInstance
{
    private readonly BotConfiguration _config;
    private readonly RushProbeConfiguration _probeConfig;
    private readonly IPathingService _pathingService;
    private readonly ITownManagementService _townManagementService;
    private readonly IAttackService _attackService;
    private readonly IMapApiService _mapApiService;
    private int _gameNumber = 1;
    private string _currentGameName;
    private Client _sharedRusher;

    /// <summary>
    /// The one rusher client every step of a rush shares, so the whole run happens in a single game.
    /// </summary>
    private Client SharedRusher() => _sharedRusher ??= new Client();
    private Client _rusherForInvites;
    private readonly List<string> _createdCharacters = [];
    private Character _probeCharacter;
    private List<Character> _characters = [];

    public RushProbeBot(
        IOptions<BotConfiguration> config,
        IOptions<RushProbeConfiguration> probeConfig,
        IPathingService pathingService,
        ITownManagementService townManagementService,
        IAttackService attackService,
        IMapApiService mapApiService)
    {
        _config = config.Value;
        _probeConfig = probeConfig.Value;
        _pathingService = pathingService;
        _townManagementService = townManagementService;
        _attackService = attackService;
        _mapApiService = mapApiService;
    }

    public string GetName() => "rushprobe";

    public async Task Run()
    {
        var client = new Client();
        try
        {
            await RunSteps(client);
        }
        catch (Exception e)
        {
            Log.Error(e, "Probe failed with an exception");
        }
        finally
        {
            try
            {
                await CleanUp(client);
                if (client.Game.IsInGame())
                {
                    await client.Game.LeaveGame();
                }
                client.Disconnect();
            }
            catch (Exception e)
            {
                // Never let teardown throw: it runs in a finally and would hide whatever actually
                // went wrong in the step above it.
                Log.Warning(e, "Probe teardown failed");
            }

            Log.Information("Probe finished");
        }

        // The host restarts a bot whose Run returns, which for a probe would mean creating another
        // character every few seconds. A harness runs once on purpose.
        Environment.Exit(0);
    }

    private async Task RunSteps(Client client)
    {
        foreach (var step in _probeConfig.Steps ?? ["login"])
        {
            Log.Information("=== probe step: {Step} ===", step);
            var succeeded = step.ToLowerInvariant() switch
            {
                "login" => await Login(client),
                "cleanup" => await DeleteStaleProbeCharacters(client),
                "create" => await CreateThrowawayCharacter(client) != null,
                "quests" => await ReadQuestState(client),
                "death" => await DieAndRecover(client),
                "andariel" => await RushAndariel(client),
                "staff" => await PrepareHoradricStaff(client),
                "opentomb" => await OpenTombForManualInsert(client),
                "duriel" => await RushDuriel(client),
                "tombs" => await RushSevenTombs(client),
                "mephisto" => await RushMephisto(client),
                "jerhyn" => await RushJerhyn(client),
                "diablo" => await RushDiablo(client),
                "baal" => await RushBaal(client),
                "portalspots" => await MeasureActOnePortalSpots(),
                "warriv" => await CompleteActOneWithWarriv(client),
                "traverse" => await TraverseWithoutWaypoints(),
                _ => LogUnknownStep(step)
            };

            if (!succeeded)
            {
                Log.Error("Probe step {Step} failed, stopping before any later step runs", step);
                break;
            }
        }

        if (_sharedRusher != null)
        {
            if (_sharedRusher.Game.IsInGame())
            {
                _sharedRusher.Game.CleanupCursorItem();
                await _sharedRusher.Game.LeaveGame();
            }

            _sharedRusher.Disconnect();
        }
    }

    /// <summary>
    /// Deletes characters left behind by earlier runs. Runs straight after login, which is the only
    /// point where deletion is guaranteed to work: joining a game tears the realm connection down.
    /// </summary>
    private async Task<bool> DeleteStaleProbeCharacters(Client client)
    {
        if (_probeConfig.ReuseExistingCharacter)
        {
            Log.Information("Keeping existing probe characters: reuse is on, so their progress is the point");
            return true;
        }

        if (!IsDeletionAllowed())
        {
            return true;
        }

        // Never log on twice: a second attempt on the same client fails and disposes the realm
        // connection, which then breaks every later step. The login step already listed them.
        var stale = _characters.Where(IsProbeOwnedName).ToList();
        if (stale.Count == 0)
        {
            Log.Information("No stale probe characters to delete");
            return true;
        }

        foreach (var character in stale)
        {
            await client.DeleteCharacter(character.Name);
        }

        return true;
    }

    /// <summary>
    /// Game names run mm1, mm2, mm3 and so on, which is how players name a series of runs, rather
    /// than anything that stands out in a game list.
    /// </summary>
    private string NextGameName() => $"{_config.GameNamePrefix}{_gameNumber++}";

    /// <summary>
    /// The milestone the whole rush rests on: the rusher fights down to Andariel while the throwaway
    /// character waits at a portal in her level, and afterwards the character has credit for a quest
    /// it never fought.
    /// </summary>
    private async Task<bool> RushAndariel(Client rusheeClient)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No throwaway character to rush, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            _rusherForInvites = rusherClient;

            // A rushee that has already left act 1 cannot be helped here, and trying wastes the run: the
            // rusher opens its portal in the catacombs while the rushee stands in another act, where that
            // portal does not exist. A town portal is act bound - its town side appears only in the act it
            // was cast in - so nothing the rusher does in act 1 is reachable from act 2.
            if (rusheeClient.Game.Quests.IsComplete(QuestId.Act1Outro))
            {
                Log.Information("Rushee has already finished act 1 and travelled on, skipping this step");
                return true;
            }

            await WaitForExtraPlayers(rusheeClient);

            if (!await TakeWaypointFromTown(rusherClient, Waypoint.CatacombsLevel2))
            {
                Log.Error("Rusher failed to take the catacombs level 2 waypoint");
                return false;
            }

            if (!await WalkFromWaypointToArea(rusherClient, Area.CatacombsLevel2, Waypoint.CatacombsLevel2, Area.CatacombsLevel3)
                || !await WalkToAdjacentArea(rusherClient, Area.CatacombsLevel4))
            {
                return false;
            }

            Log.Information("Rusher reached {Area} at {Location}", rusherClient.Game.Area, rusherClient.Game.Me.Location);

            // Stage at the level entrance: proximity turned out not to matter for credit, and parking a
            // level one character next to Andariel just gets it killed before she dies.
            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Error("Rusher failed to open the staging portal in {Area}", rusherClient.Game.Area);
                return false;
            }

            var rusherAsSeenByRushee = rusheeClient.Game.Players.Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
            if (rusherAsSeenByRushee == null)
            {
                Log.Error("Rushee cannot see the rusher, so it cannot find the staging portal");
                return false;
            }

            if (!await MoveRusheeToPortalSpot(rusheeClient, rusherAsSeenByRushee, Area.CatacombsLevel4)
                || !await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, Area.CatacombsLevel4))
            {
                Log.Error("Rushee failed to take the staging portal into the catacombs");
                return false;
            }

            Log.Information("Rushee staged at the level entrance in {Area} at {Location}; credit before the kill: {Credited}",
                rusheeClient.Game.Area,
                rusheeClient.Game.Me.Location,
                rusheeClient.Game.Quests.IsCreditedThisGame(QuestId.SistersToTheSlaughter));

            // A character that already carries the credit cannot be credited again: its own game reports
            // the quest settled, so killing her a second time proves nothing and the reward is all that is
            // left to collect.
            var alreadyCredited = rusheeClient.Game.Quests.IsAwaitingReward(QuestId.SistersToTheSlaughter)
                || rusheeClient.Game.Quests.IsComplete(QuestId.SistersToTheSlaughter);
            if (alreadyCredited)
            {
                Log.Information("Rushee already has Andariel credit, going straight to Warriv");
            }
            else if (!await MoveToAndariel(rusherClient) || !await KillAndariel(rusherClient))
            {
                return false;
            }

            if (!await VerifyAndarielCredit(rusheeClient))
            {
                return false;
            }

            // Finish while the rusher is still here: a town portal disappears with its owner, so a
            // rushee left behind in the catacombs has no way back to town at all.
            return await CompleteActOneWithWarriv(rusheeClient);
        }
        finally
        {
            // The game is deliberately kept open: every step of a rush runs in the same
            // one, and tearing it down here would cost a fresh game per step. RunSteps
            // closes it once at the end.
        }
    }

    /// <summary>
    /// Optionally holds before the descent so a person can join the game and watch, inviting whoever
    /// turns up into the party.
    /// </summary>
    private async Task WaitForExtraPlayers(Client rusheeClient)
    {
        if (_probeConfig.WaitForExtraPlayers <= 0)
        {
            return;
        }

        var wanted = 2 + _probeConfig.WaitForExtraPlayers;
        Log.Information("Waiting up to {Seconds}s for {Wanted} players in the game, currently {Current}: {Names}",
            _probeConfig.WaitForPlayersTimeoutSeconds,
            wanted,
            rusheeClient.Game.Players.Count,
            string.Join(", ", rusheeClient.Game.Players.Select(p => p.Name)));

        await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(1000);
            return rusheeClient.Game.Players.Count >= wanted;
        }, TimeSpan.FromSeconds(_probeConfig.WaitForPlayersTimeoutSeconds));

        Log.Information("Continuing with {Count} players: {Names}",
            rusheeClient.Game.Players.Count,
            string.Join(", ", rusheeClient.Game.Players.Select(p => p.Name)));

        if (_rusherForInvites != null)
        {
            await InviteEveryoneToParty(_rusherForInvites);
        }
    }

    private async Task<bool> WalkFromWaypointToArea(Client client, Area from, Waypoint waypoint, Area toArea)
    {
        var movementMode = GetMovementMode(client);
        var path = await _pathingService.GetPathFromWaypointToArea(client.Game.MapId, Difficulty.Normal, from, waypoint, toArea, movementMode);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode))
        {
            Log.Error("Rusher failed to move towards {Area} from the waypoint", toArea);
            return false;
        }

        return await TakeNearestWarp(client, toArea);
    }

    private async Task<bool> WalkToAdjacentArea(Client client, Area toArea)
    {
        await SyncPosition(client);
        var movementMode = GetMovementMode(client);
        var path = await _pathingService.GetPathToArea(client.Game, toArea, movementMode);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode))
        {
            Log.Error("Rusher failed to move towards {Area}", toArea);
            return false;
        }

        return await TakeNearestWarp(client, toArea);
    }

    private async Task<bool> TakeNearestWarp(Client client, Area toArea)
    {
        var warp = client.Game.GetNearestWarp();
        if (warp == null || warp.Location.Distance(client.Game.Me.Location) > 20)
        {
            Log.Error("No warp to {Area} within reach at {Location}", toArea, client.Game.Me.Location);
            return false;
        }

        if (!await MovementHelpers.TakeWarp(client.Game, _pathingService, _mapApiService, GetMovementMode(client), warp, toArea))
        {
            Log.Error("Taking the warp to {Area} failed", toArea);
            return false;
        }

        Log.Information("Rusher entered {Area}", client.Game.Area);
        return true;
    }

    private static MovementMode GetMovementMode(Client client)
        => client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;

    /// <summary>
    /// Walks the rusher to Andariel and stops just short of her, which is where the portal for the
    /// rushee gets opened.
    /// </summary>
    private async Task<bool> MoveToAndariel(Client rusherClient)
    {
        var movementMode = GetMovementMode(rusherClient);
        var path = await _pathingService.GetPathToNPC(rusherClient.Game, NPCCode.Andarial, movementMode);
        if (path.Count == 0)
        {
            Log.Error("The map api does not know where Andariel is in this game");
            return false;
        }

        if (!await MovementHelpers.TakePathOfLocations(rusherClient.Game, path, movementMode))
        {
            Log.Error("Rusher failed to reach Andariel");
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout(
            (_) => rusherClient.Game.GetNPCsByCode(NPCCode.Andarial).Count > 0,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Rusher cannot see Andariel at {Location}", rusherClient.Game.Me.Location);
            return false;
        }

        var andariel = rusherClient.Game.GetNPCsByCode(NPCCode.Andarial).First();
        Log.Information("Rusher is at {Location}, Andariel at {Andariel}, {Distance:0} apart",
            rusherClient.Game.Me.Location, andariel.Location, rusherClient.Game.Me.Location.Distance(andariel.Location));
        return true;
    }

    private async Task<bool> KillAndariel(Client rusherClient)
    {
        if (!GeneralHelpers.TryWithTimeout(
            (_) => rusherClient.Game.GetNPCsByCode(NPCCode.Andarial).Count > 0,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Rusher cannot find Andariel at {Location}", rusherClient.Game.Me.Location);
            return false;
        }

        var andariel = rusherClient.Game.GetNPCsByCode(NPCCode.Andarial).First();
        Log.Information("Killing Andariel at {Location}", andariel.Location);

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(120))
        {
            // Her entity dying is the signal. The game wide quest array is not: it stayed empty across
            // every successful rush, because it carries the host's own quest state rather than a
            // running log of what died here.
            var current = rusherClient.Game.WorldObjects.GetValueOrDefault((andariel.Id, EntityType.NPC));
            if (current == null || current.State == EntityState.Dead || current.State == EntityState.Dieing)
            {
                Log.Information("Andariel is dead after {Seconds:0.0}s", timer.Elapsed.TotalSeconds);
                return true;
            }

            if (current.LifePercentage < 100)
            {
                Log.Debug("Andariel at {Life}% life", current.LifePercentage);
            }

            await _attackService.AssistPlayer(rusherClient, rusherClient.Game.Me);
            await Task.Delay(200);
        }

        Log.Error("Andariel was still alive after 120 seconds");
        return false;
    }

    /// <summary>
    /// Confirms the rushee got credit. Credit is the bit 13 flag the kill sets, not the completed
    /// flag, which needs an NPC visit the rushee has not made.
    /// </summary>
    private static async Task<bool> VerifyAndarielCredit(Client rusheeClient)
    {
        var diedDuringTheFight = rusheeClient.Game.Me.Life == 0;
        Log.Information("Rushee is {State} after the fight", diedDuringTheFight ? "dead" : $"alive with {rusheeClient.Game.Me.Life} life");

        // Read while still dead first. Resurrecting teleports the character to town, which takes it
        // and its corpse out of the area the credit depends on, so that must not happen before the
        // state has been looked at.
        if (diedDuringTheFight)
        {
            rusheeClient.Game.RequestQuestData();
            await Task.Delay(TimeSpan.FromSeconds(2));
            Log.Information("Dead in the area: credited={Credited}, words={Words}",
                rusheeClient.Game.Quests.IsCreditedThisGame(QuestId.SistersToTheSlaughter),
                Describe(rusheeClient.Game.Quests.Describe()));

            rusheeClient.Game.Resurrect();
            if (!GeneralHelpers.TryWithTimeout((_) => rusheeClient.Game.Me.Life > 0, TimeSpan.FromSeconds(10)))
            {
                Log.Warning("Rushee did not come back from the dead");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        rusheeClient.Game.RequestQuestData();
        var credited = GeneralHelpers.TryWithTimeout(
            (_) => rusheeClient.Game.Quests.IsCreditedThisGame(QuestId.SistersToTheSlaughter)
                || rusheeClient.Game.Quests.IsAwaitingReward(QuestId.SistersToTheSlaughter)
                || rusheeClient.Game.Quests.IsComplete(QuestId.SistersToTheSlaughter),
            TimeSpan.FromSeconds(10));

        Log.Information("Rushee quest words: {Quests}", rusheeClient.Game.Quests.Describe());
        Log.Information("Game quest words: {Quests}", rusheeClient.Game.Quests.Describe(game: true));
        Log.Information("Rushee credited: {Credited}, complete: {Complete}, awaiting reward: {Awaiting}",
            rusheeClient.Game.Quests.IsCreditedThisGame(QuestId.SistersToTheSlaughter),
            rusheeClient.Game.Quests.IsComplete(QuestId.SistersToTheSlaughter),
            rusheeClient.Game.Quests.IsAwaitingReward(QuestId.SistersToTheSlaughter));

        await Task.CompletedTask;
        if (!credited)
        {
            Log.Error("Rushee has neither credit nor an outstanding reward for Andariel");
            return false;
        }

        Log.Information("Rushee has credit for Andariel without ever fighting her");
        return true;
    }

    /// <summary>
    /// Turns credit into completion and then into act 2 access: the rushee returns to town, claims the
    /// quest from Warriv, and travels. Credit alone leaves the quest unfinished and the next act shut.
    /// </summary>
    private async Task<bool> CompleteActOneWithWarriv(Client rusheeClient)
    {
        if (!rusheeClient.Game.IsInGame())
        {
            Log.Error("Rushee is not in a game, run the andariel step first");
            return false;
        }

        if (!rusheeClient.Game.Quests.IsCreditedThisGame(QuestId.SistersToTheSlaughter)
            && !rusheeClient.Game.Quests.IsAwaitingReward(QuestId.SistersToTheSlaughter))
        {
            Log.Error("Rushee has no Andariel credit to turn into completion, words {Words}",
                Describe(rusheeClient.Game.Quests.Describe()));
            return false;
        }

        if (!rusheeClient.Game.IsInTown() && !await TakeAnyPortalToTown(rusheeClient))
        {
            Log.Error("Rushee failed to get back to town");
            return false;
        }

        var path = await _pathingService.GetPathToNPC(rusheeClient.Game, NPCCode.WarrivAct1, MovementMode.Walking);
        if (path.Count > 0 && !await MovementHelpers.TakePathOfLocations(rusheeClient.Game, path, MovementMode.Walking))
        {
            Log.Error("Rushee failed to walk to Warriv");
            return false;
        }

        var warriv = NPCHelpers.GetUniqueNPC(rusheeClient.Game, NPCCode.WarrivAct1);
        if (warriv == null)
        {
            Log.Error("Warriv is not in sight at {Location}", rusheeClient.Game.Me.Location);
            return false;
        }

        await rusheeClient.Game.MoveToAsync(warriv);
        rusheeClient.Game.InitiateEntityChat(warriv);
        await Task.Delay(500);

        // Send the message a real 1.09 client sent to finish this quest. What the server advertises
        // through 0x27 is logged for comparison but not relied on: it says nothing for Warriv.
        var offered = rusheeClient.Game.GetOfferedMessages(warriv);
        Log.Information("Warriv advertises {Offered}, claiming with 0x{MessageId:X2}",
            offered.Count > 0 ? string.Join(", ", offered.Select(m => $"0x{m:X2}")) : "nothing",
            WarrivsAndarielMessage);

        rusheeClient.Game.SendQuestMessage(warriv, WarrivsAndarielMessage);
        var completed = GeneralHelpers.TryWithTimeout(
            (_) => rusheeClient.Game.Quests.IsComplete(QuestId.SistersToTheSlaughter),
            TimeSpan.FromSeconds(5));

        await Task.Delay(500);
        rusheeClient.Game.TerminateEntityChat(warriv);

        Log.Information("After Warriv: words {Words}", Describe(rusheeClient.Game.Quests.Describe()));
        if (!completed)
        {
            Log.Error("Andariel is still not complete after talking to Warriv");
            return false;
        }

        Log.Information("Andariel complete; act 1 outro reads 0x{Outro:X4}",
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act1Outro));

        // Now travel, which is the thing the completion unlocks.
        rusheeClient.Game.InitiateEntityChat(warriv);
        await Task.Delay(500);
        rusheeClient.Game.TravelWithNpc(warriv);
        if (!GeneralHelpers.TryWithTimeout((_) => rusheeClient.Game.Act == Act.Act2, TimeSpan.FromSeconds(10)))
        {
            Log.Error("Warriv did not take the rushee to act 2, still in {Area}", rusheeClient.Game.Area);
            return false;
        }

        Log.Information("Rushee travelled to {Area}, act 1 rushed end to end", rusheeClient.Game.Area);
        return true;
    }

    /// <summary>
    /// Message a 1.09 client sent to Warriv to finish Sisters to the Slaughter, taken byte for byte
    /// from a capture of that conversation.
    /// </summary>
    private const uint WarrivsAndarielMessage = 0xB7;

    /// <summary>
    /// What a real client sends Tyrael in Duriel's lair to finish the seven tombs. He advertises nothing
    /// through <c>NPCInfo</c>, so there is no id to echo and this had to be captured:
    /// <c>0x31, 7F 00 00 00, 2E 01 00 00</c>.
    /// </summary>
    private const uint TyraelsSevenTombsMessage = 0x012E;

    /// <summary>
    /// What a real client sends Jerhyn once Tyrael is done, in order.
    /// </summary>
    private static readonly uint[] JerhynMessages = [0x00FD, 0x016A, 0x01BA];

    /// <summary>
    /// What a real client sends Meshif before asking him to sail.
    /// </summary>
    private static readonly uint[] MeshifMessages = [0x00F1, 0x01C2];

    /// <summary>
    /// Takes whatever town portal is standing in the current area, which is how a rushee gets home: it
    /// has no book of town portal of its own, and the rusher's portal is right there.
    /// </summary>
    private static async Task<bool> TakeAnyPortalToTown(Client client)
    {
        var portal = client.Game.GetEntityByCode(EntityCode.TownPortal).FirstOrDefault();
        if (portal == null)
        {
            Log.Error("No town portal in {Area} to take home", client.Game.Area);
            return false;
        }

        var previousArea = client.Game.Area;
        return await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await client.Game.MoveToAsync(portal);
            client.Game.InteractWithEntity(portal);
            return await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(100);
                return client.Game.Area != previousArea && client.Game.IsInTown();
            }, TimeSpan.FromSeconds(1));
        }, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Proves the rusher can reach places no waypoint serves, by walking the adjacency graph. Two cases
    /// that both lack a level exit in the map data: the whole way from act 1 town down to the catacombs,
    /// and the river of flame into the chaos sanctuary.
    /// </summary>
    private async Task<bool> TraverseWithoutWaypoints()
    {
        var client = new Client();
        try
        {
            if (!client.Connect(_config.Realm, _config.KeyOwner, _config.GameFolder))
            {
                Log.Error("Rusher failed to connect");
                return false;
            }

            var characters = await client.Login(_probeConfig.RusherUsername, _probeConfig.RusherPassword);
            var rusher = characters?.Find(c => c.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
            if (rusher == null)
            {
                Log.Error("Rusher {Character} not found", _probeConfig.RusherCharacter);
                return false;
            }

            await client.SelectCharacter(rusher);
            var gameName = NextGameName();
            if (!await client.CreateGame(_config.Difficulty, gameName, _config.GamePassword, _config.GameDescriptions[0]))
            {
                Log.Error("Failed to create game {GameName}", gameName);
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) => client.Game.Me != null, TimeSpan.FromSeconds(5)))
            {
                Log.Error("Character never initialized");
                return false;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(500);
            Log.Information("Traversal test in game {GameName} as {Character} in {Area}", gameName, rusher.Name, client.Game.Area);

            // The rusher is an act 4 character, so get her to act 1 town before walking to the catacombs.
            if (!await _townManagementService.SwitchAct(client, Act.Act1))
            {
                Log.Error("Could not switch the rusher to act 1");
                return false;
            }

            var townToCatacombs = await TraverseTo(client, Area.CatacombsLevel2);

            // Second case: reach the chaos sanctuary from the river of flame, which has no exit either.
            // Waypoints are taken from town, so get back there first wherever the first test ended up.
            var chaos = false;
            if (!client.Game.IsInTown())
            {
                await _townManagementService.TakeTownPortalToTown(client);
            }

            if (await _townManagementService.SwitchAct(client, Act.Act4)
                && await _townManagementService.TakeWaypoint(client, Waypoint.RiverOfFlame))
            {
                chaos = await TraverseTo(client, Area.ChaosSanctuary) && await MoveToChaosStar(client);
            }
            else
            {
                Log.Error("Could not take the river of flame waypoint");
            }

            Log.Information("Traversal results: town to catacombs {Catacombs}, river of flame to chaos {Chaos}",
                townToCatacombs, chaos);
            return townToCatacombs && chaos;
        }
        finally
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }
            client.Disconnect();
        }
    }

    /// <summary>
    /// Walks the area chain to a destination, one border at a time.
    /// </summary>
    private async Task<bool> TraverseTo(Client client, Area destination)
    {
        var route = await _pathingService.GetAreaRoute(client.Game.MapId, Difficulty.Normal, client.Game.Area, destination);
        if (route.Count == 0)
        {
            Log.Error("No route from {From} to {To}", client.Game.Area, destination);
            return false;
        }

        Log.Information("Route of {Hops} areas: {Route}", route.Count - 1, string.Join(" -> ", route));
        var timer = Stopwatch.StartNew();
        foreach (var hop in route.Skip(1))
        {
            var entered = false;
            for (var attempt = 1; attempt <= AreaHopAttempts && !entered; attempt++)
            {
                entered = await TryHop(client, hop, attempt);
                if (!entered)
                {
                    Log.Warning("Attempt {Attempt} of {Total} to get from {Area} into {Hop} failed at {Location}",
                        attempt, AreaHopAttempts, client.Game.Area, hop, client.Game.Me.Location);
                    client.Game.RequestUpdate(client.Game.Me.Id);
                    await Task.Delay(500);
                }
            }

            if (!entered)
            {
                Log.Error("Stuck in {Area} trying to reach {Hop} at {Location}", client.Game.Area, hop, client.Game.Me.Location);
                return false;
            }
        }

        Log.Information("Reached {Destination} in {Seconds:0.0}s at {Location}",
            destination, timer.Elapsed.TotalSeconds, client.Game.Me.Location);
        return true;
    }

    /// <summary>
    /// One attempt at a border, changing tactics after the first. The warp based route throws for areas the
    /// map api reports no exits for, such as the river of flame into the chaos sanctuary, which is an open
    /// border rather than a doorway - so that failure falls back to the crossing search.
    /// </summary>
    /// <summary>
    /// Waits until the position the client believes it is at is one the map data agrees is walkable, before
    /// anything tries to path from it.
    /// </summary>
    /// <remarks>
    /// Position is stale for a moment after every area change, and pathing from a stale one is the single
    /// biggest cause of lost runs: it surfaces as "teleport of length 0", as a teleport that reports
    /// failure without moving, or as "no reachable exit or crossing" from a spot that is plainly fine.
    /// </remarks>
    private async Task<bool> SyncPosition(Client client)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, Difficulty.Normal, client.Game.Area, client.Game.Me.Location))
            {
                return true;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(250);
        }

        Log.Warning("{Name} is at {Location} in {Area}, which the map does not consider walkable",
            client.Game.Me.Name, client.Game.Me.Location, client.Game.Area);
        return false;
    }

    private async Task<bool> TryHop(Client client, Area hop, int attempt)
    {
        if (attempt == 1)
        {
            return await EnterAdjacentArea(client, hop);
        }

        try
        {
            if (await WalkToAdjacentArea(client, hop))
            {
                return true;
            }
        }
        catch (InvalidOperationException e)
        {
            Log.Debug("No warp route into {Hop} ({Message})", hop, e.Message);
        }

        // Stand somewhere else before asking again. The crossing search ranks candidates by distance from
        // where you are, so retrying from the same spot returns the same unreachable point every time.
        await StepAside(client);
        return await EnterAdjacentArea(client, hop);
    }

    private async Task StepAside(Client client)
    {
        var from = client.Game.Me.Location;
        foreach (var (dx, dy) in new[] { (0, -40), (40, 0), (0, 40), (-40, 0), (30, -30), (-30, 30) })
        {
            var candidate = new Point((ushort)(from.X + dx), (ushort)(from.Y + dy));
            if (!await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, Difficulty.Normal, client.Game.Area, candidate))
            {
                continue;
            }

            var path = await _pathingService.GetPathToLocation(client.Game, candidate, GetMovementMode(client));
            if (path.Count > 0 && await MovementHelpers.TakePathOfLocations(client.Game, path, GetMovementMode(client)))
            {
                Log.Debug("Stepped aside from {From} to {To} before retrying", from, client.Game.Me.Location);
                return;
            }
        }
    }

    private async Task<bool> EnterAdjacentArea(Client client, Area target)
    {
        await SyncPosition(client);
        var movementMode = GetMovementMode(client);
        var from = client.Game.Area;
        List<Point> path;
        try
        {
            path = await _pathingService.GetPathToAdjacentArea(
                client.Game.MapId, Difficulty.Normal, from, client.Game.Me.Location, target, movementMode);
        }
        catch (InvalidOperationException e)
        {
            Log.Error("No path from {From} into {Target}: {Message}", from, target, e.Message);
            return false;
        }

        await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode);

        // Position, not Game.Area, decides whether the hop happened. Area is set from every map reveal
        // packet, and reveals arrive for neighbouring levels as they come into view: standing on the
        // underground passage entrance flipped Area to the dungeon while the character was still in
        // stony field, which made a hop that never happened look successful.
        if (await ConfirmInArea(client, target))
        {
            Log.Information("{From} -> {Target} by crossing the border", from, target);
            return true;
        }

        // A border with a real exit needs the warp taking rather than a step across.
        var warp = client.Game.GetNearestWarp();
        if (warp != null && warp.Location.Distance(client.Game.Me.Location) < 25
            && await MovementHelpers.TakeWarp(client.Game, _pathingService, _mapApiService, movementMode, warp, target))
        {
            Log.Information("{From} -> {Target} through a warp at {Warp}", from, target, warp.Location);
            return await ConfirmInArea(client, target);
        }

        return await ConfirmInArea(client, target);
    }

    /// <summary>
    /// Whether the character is standing in the given area, judged by asking whether its position is a
    /// point inside that area's map. This is the only reliable test: the area the client reports comes
    /// from map reveal packets, which arrive for neighbouring levels too.
    /// </summary>
    private async Task<bool> ConfirmInArea(Client client, Area area)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            client.Game.RequestUpdate(client.Game.Me.Id);
            await Task.Delay(250);
            if (await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, area, client.Game.Me.Location))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Moves to the star in the middle of the chaos sanctuary, the point the seals and Diablo sit around.
    /// The map api reports it as an object, so no geometry or guesswork is involved.
    /// </summary>
    private async Task<bool> MoveToChaosStar(Client client)
    {
        var movementMode = GetMovementMode(client);
        var path = await _pathingService.GetPathToObject(client.Game, EntityCode.DiabloStar, movementMode);
        if (path.Count == 0)
        {
            Log.Error("The map api does not place the star in this {Area}", client.Game.Area);
            return false;
        }

        if (!await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode))
        {
            Log.Error("Failed to reach the star, stopped at {Location}", client.Game.Me.Location);
            return false;
        }

        Log.Information("At the star at {Location} in {Area}", client.Game.Me.Location, client.Game.Area);
        return true;
    }

    /// <summary>
    /// Assembles a horadric staff in the rushee's own inventory, so that a person can log on as that
    /// character and put it in the orifice by hand.
    /// </summary>
    /// <remarks>
    /// Inserting the staff is the one act 2 step whose packet (0x44) has never been observed coming out
    /// of a real client. It has a name in <see cref="OutGoingPacket"/> and nothing else, and the working
    /// agreement is that unobserved bytes do not get sent, so the bot does everything up to the
    /// insertion and hands over to a person with a sniffer running.
    /// <para>
    /// The rushee does every pickup itself. A character that has already finished a quest cannot pick
    /// its items up, so the level 90 rusher cannot carry a staff of kings even to hand it over: it
    /// clears and holds the ground while the level one walks in and takes the item. That also means the
    /// rushee needs its own cube, which is why the cube is fetched for the rushee rather than the
    /// rusher.
    /// </para>
    /// <para>
    /// The rushee hosts, which matters twice over: quest credit needs it, and the pieces spawn from the
    /// game's quest state rather than the finder's, so a game made by a character that already
    /// assembled a staff has empty containers. Seen in a capture - a character that already owned a cube
    /// opened the halls of the dead chest and got nothing out of it.
    /// </para>
    /// </remarks>
    private async Task<bool> PrepareHoradricStaff(Client rusheeClient)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No character to build a staff for, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            _rusherForInvites = rusherClient;

            // Nothing to build, and nothing to build it from: the containers are empty in a game hosted by
            // a character that already finished this. The tombs step copes with a spent staff by taking the
            // lair entrance that insert left open, so this is a skip and not a failure.
            if (rusheeClient.Game.Quests.IsComplete(QuestId.TheHoradricStaff))
            {
                Log.Information("{Name} has already finished the horadric staff, skipping this step",
                    _probeCharacter.Name);
                return true;
            }

            // The cube is a required inventory item already, but the staff pieces are not, and a sold
            // staff of kings cannot be bought back.
            D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.ShaftoftheHoradricStaff);
            D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.TopoftheHoradricStaff);
            D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.HoradricStaff);

            // Restock the rusher before anything else. Everything after this point depends on it being
            // able to hold ground while a level one walks around in front of it.
            await RestockRusher(rusherClient, D2NG.Core.D2GS.Act.Act.Act2);

            // The rushee transmutes, so the rushee is the one that needs a cube.
            if (rusheeClient.Game.Inventory.FindItemByName(ItemName.HoradricCube) == null
                && !await EscortToQuestItem(rusherClient, rusheeClient, Waypoint.HallsOfTheDeadLevel2,
                        Area.HallsOfTheDeadLevel3, EntityCode.HoradricCubeChest, ItemName.HoradricCube))
            {
                return false;
            }

            if (!await EscortToQuestItem(rusherClient, rusheeClient, Waypoint.FarOasis,
                    Area.MaggotLairLevel3, EntityCode.StaffOfKingsChest, ItemName.ShaftoftheHoradricStaff))
            {
                return false;
            }

            if (!await EscortToQuestItem(rusherClient, rusheeClient, Waypoint.LostCity,
                    Area.ClawViperTempleLevel2, EntityCode.ViperAmuletAltar, ItemName.TopoftheHoradricStaff))
            {
                return false;
            }

            if (!TransmuteStaff(rusheeClient))
            {
                return false;
            }

            Log.Information("{Name} is holding a horadric staff. Log on as it, create a game, and run the "
                + "opentomb step so the rusher clears the orifice room and opens a portal: the insert has "
                + "to come from a real client.", _probeCharacter.Name);
            return true;
        }
        finally
        {
            // The game is deliberately kept open: every step of a rush runs in the same
            // one, and tearing it down here would cost a fresh game per step. RunSteps
            // closes it once at the end.
        }
    }


    /// <summary>
    /// Clears the orifice room in the real tal rasha tomb and leaves a portal open in it, then holds the
    /// room until the game ends. Used to hand a cleared tomb to a person playing the character that
    /// carries the staff, so the insertion can be captured off a real client.
    /// </summary>
    /// <remarks>
    /// The game has to have been created by the character that holds the staff: the orifice refuses a
    /// staff when the game's seven tombs is already settled, which is the same rule that credited nobody
    /// for three Andariel runs. So this step joins a game rather than making one, and the name comes
    /// from configuration.
    /// </remarks>
    private async Task<bool> OpenTombForManualInsert(Client unused)
    {
        if (string.IsNullOrEmpty(_probeConfig.ManualGameName))
        {
            Log.Error("No manualGameName configured, so there is no game to join");
            return false;
        }

        var rusherClient = new Client();
        try
        {
            if (!rusherClient.Connect(_config.Realm, _config.KeyOwner, _config.GameFolder))
            {
                Log.Error("Rusher failed to connect to the realm");
                return false;
            }

            var characters = await rusherClient.Login(_probeConfig.RusherUsername, _probeConfig.RusherPassword);
            var rusher = characters?.Find(c => c.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
            if (rusher == null)
            {
                Log.Error("Rusher character {Character} not found on {Account}", _probeConfig.RusherCharacter, _probeConfig.RusherUsername);
                return false;
            }

            await rusherClient.SelectCharacter(rusher);
            if (!await rusherClient.JoinGame(_probeConfig.ManualGameName, _config.GamePassword))
            {
                Log.Error("Rusher failed to join game {GameName}. Create it first with the character that "
                    + "carries the staff.", _probeConfig.ManualGameName);
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) => rusherClient.Game.Me != null, TimeSpan.FromSeconds(10)))
            {
                Log.Error("Rusher never initialized in game {GameName}", _probeConfig.ManualGameName);
                return false;
            }

            rusherClient.Game.RequestUpdate(rusherClient.Game.Me.Id);
            ChickenService.Attach(rusherClient, _probeConfig.Chicken ?? _config.Chicken);
            await RestockRusher(rusherClient, D2NG.Core.D2GS.Act.Act.Act2);

            // Accept whoever is playing the staff carrier into the party, so the same game can go on to
            // credit a kill without rejoining.
            AcceptPartyInvites(rusherClient);
            await InviteEveryoneToParty(rusherClient);

            if (!Enum.TryParse<Waypoint>(_probeConfig.ManualWaypoint, true, out var manualWaypoint))
            {
                Log.Error("Unknown manualWaypoint {Waypoint}", _probeConfig.ManualWaypoint);
                return false;
            }

            if (!Enum.TryParse<EntityCode>(_probeConfig.ManualTargetObject, true, out var manualObject))
            {
                Log.Error("Unknown manualTargetObject {Object}", _probeConfig.ManualTargetObject);
                return false;
            }

            if (!await TakeWaypointFromTown(rusherClient, manualWaypoint))
            {
                Log.Error("Rusher failed to take the {Waypoint} waypoint", manualWaypoint);
                return false;
            }

            Area target;
            if (string.IsNullOrWhiteSpace(_probeConfig.ManualTargetArea))
            {
                // No area named means the tombs, and which tomb is real is per game rather than fixed.
                var canyon = await _mapApiService.GetArea(rusherClient.Game.MapId, Difficulty.Normal, Area.CanyonOfTheMagi);
                if (canyon?.TombArea == null)
                {
                    Log.Error("The map api did not name the real tomb for this game, so there is nothing to aim at");
                    return false;
                }

                target = canyon.TombArea.Value;
                Log.Information("The real tomb in this game is {Tomb}", target);
            }
            else if (!Enum.TryParse(_probeConfig.ManualTargetArea, true, out target))
            {
                Log.Error("Unknown manualTargetArea {Area}", _probeConfig.ManualTargetArea);
                return false;
            }

            if (!await TraverseTo(rusherClient, target))
            {
                return false;
            }

            if (!await MoveToObject(rusherClient, manualObject))
            {
                return false;
            }

            var focus = rusherClient.Game.GetEntityByCode(manualObject).FirstOrDefault();
            Log.Information("{Object} is entity {Id} at {Location}", manualObject, focus?.Id, focus?.Location);

            // Duriel's lair has no adjacent level in the map data, so it cannot be routed into: it is
            // entered through an object, and that object is not created by inserting the staff - no
            // AssignObject followed the capture - so it is one of the tomb's own objects being switched
            // on. Dump what is here with its state so the right code can be named rather than guessed.
            foreach (var nearby in rusherClient.Game.WorldObjects.Values
                .Where(o => o.Type == EntityType.Object
                    && o.Location.Distance(rusherClient.Game.Me.Location) < 60)
                .OrderBy(o => o.Location.Distance(rusherClient.Game.Me.Location)))
            {
                Log.Information("NEARBY object code {Code} ({Number}) id {Id} state {State} at {Location}, {Distance:0} away",
                    nearby.Code, (int)nearby.Code, nearby.Id, nearby.State, nearby.Location,
                    nearby.Location.Distance(rusherClient.Game.Me.Location));
            }

            if (!await ClearAround(rusherClient, 18, TimeSpan.FromSeconds(45)))
            {
                Log.Error("Could not get the room quiet, not opening a portal into it");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Error("Rusher failed to open a portal in {Area}", rusherClient.Game.Area);
                return false;
            }

            Log.Information("Portal open in {Area} next to {Object}. Take it from town at the portal spot, "
                + "do what you need to, and I will hold the room for {Seconds}s.",
                rusherClient.Game.Area, manualObject, _probeConfig.HoldTombSeconds);

            // Keep killing while the person walks in and works. A portal also disappears with its owner,
            // so leaving early would strand them.
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(_probeConfig.HoldTombSeconds) && rusherClient.Game.IsInGame())
            {
                // Tight radius on purpose. Assisting against something thirty units away makes the
                // sorceress repeat static field at nothing it can reach, forever.
                await ClearTick(rusherClient, 18);
                await Task.Delay(200);
            }

            return true;
        }
        finally
        {
            if (rusherClient.Game.IsInGame())
            {
                rusherClient.Game.CleanupCursorItem();
                await rusherClient.Game.LeaveGame();
            }
            rusherClient.Disconnect();
        }
    }

    /// <summary>
    /// Number of each potion below which the rusher goes shopping rather than pressing on.
    /// </summary>
    private const int MinimumPotions = 3;

    /// <summary>
    /// Tops the rusher up mid rush if it is running dry, instead of letting it fight to empty. Running out
    /// is not just a survival problem: the town service only teleports while mana is over twenty, so an
    /// empty belt silently downgrades the rusher to walking, and it then dies to things it would normally
    /// outrun.
    /// </summary>
    private async Task EnsureSupplies(Client rusherClient, D2NG.Core.D2GS.Act.Act returnToAct)
    {
        if (string.IsNullOrWhiteSpace(_probeConfig.RestockAct) || !rusherClient.Game.IsInGame())
        {
            return;
        }

        var health = InventoryHelpers.GetTotalHealthPotions(rusherClient.Game);
        var mana = InventoryHelpers.GetTotalManaPotions(rusherClient.Game);
        if (health >= MinimumPotions && mana >= MinimumPotions)
        {
            return;
        }

        Log.Information("Rusher is down to {Health} health and {Mana} mana potions, restocking mid rush",
            health, mana);
        await RestockRusher(rusherClient, returnToAct);
    }

    /// <summary>
    /// Buys the rusher's potions back up. A teleporting sorceress empties its mana belt over a few runs
    /// and then crawls.
    /// </summary>
    /// <remarks>
    /// Restocking happens in act 4 rather than in the act the run needs. In normal difficulty the act 1
    /// and act 2 vendors sell minor and light potions, which are close to worthless on a level 90
    /// character, while Jamella sells the full size ones.
    /// </remarks>
    private async Task RestockRusher(Client rusherClient, D2NG.Core.D2GS.Act.Act returnToAct)
    {
        if (string.IsNullOrWhiteSpace(_probeConfig.RestockAct))
        {
            return;
        }

        if (!Enum.TryParse<D2NG.Core.D2GS.Act.Act>(_probeConfig.RestockAct, true, out var restockAct))
        {
            Log.Warning("Unknown restock act {Act}, skipping the restock", _probeConfig.RestockAct);
            return;
        }

        Log.Warning("Restocking runs the full town routine on the rusher, which sells what its keep rules "
            + "do not protect. Set restockAct to empty to skip it.");

        var account = new AccountConfig
        {
            Username = _probeConfig.RusherUsername,
            Password = _probeConfig.RusherPassword,
            Character = _probeConfig.RusherCharacter,
            ResurrectMerc = false
        };

        var game = rusherClient.Game;
        var options = new TownManagementOptions(account, restockAct)
        {
            // A count of zero skips the vendor entirely, so ask for whatever is missing from the belt
            // plus a margin, the same way the assist bot sizes its shopping.
            HealthPotionsToBuy = Math.Max(0, game.Belt.Height * account.HealthSlots.Count + 10 - InventoryHelpers.GetTotalHealthPotions(game)),
            ManaPotionsToBuy = Math.Max(0, game.Belt.Height * account.ManaSlots.Count + 10 - InventoryHelpers.GetTotalManaPotions(game))
        };

        Log.Information("Restocking the rusher in {Act}: {Health} health and {Mana} mana potions",
            restockAct, options.HealthPotionsToBuy, options.ManaPotionsToBuy);

        // Deliberately not fatal. The run can be done on whatever is already in the belt, and losing a
        // game that is otherwise fine to a shopping trip would be a poor trade.
        if (!(await _townManagementService.PerformTownTasks(rusherClient, options)).Succes)
        {
            Log.Warning("Restocking did not finish, carrying on with what is in the belt");
        }

        if (rusherClient.Game.Act != returnToAct && !await _townManagementService.SwitchAct(rusherClient, returnToAct))
        {
            Log.Warning("Could not switch back to {Act} after restocking, still in {Current}",
                returnToAct, rusherClient.Game.Act);
        }
    }

    /// <summary>
    /// Fetches one quest item with the rushee. The rusher goes ahead, clears the room around the
    /// container and opens a portal into it; the rushee takes that portal, opens the container, picks
    /// the item up and goes home. The rusher keeps swinging the whole time the rushee is in the room.
    /// </summary>
    private async Task<bool> EscortToQuestItem(Client rusherClient, Client rusheeClient, Waypoint waypoint,
        Area area, EntityCode container, ItemName itemName)
    {
        if (Holds(rusheeClient, itemName))
        {
            Log.Information("Rushee already has {Item}", itemName);
            return true;
        }

        await PrePositionRushee(rusheeClient);
        await EnsureSupplies(rusherClient, D2NG.Core.D2GS.Act.Act.Act2);

        if (!await TakeWaypointFromTown(rusherClient, waypoint))
        {
            Log.Error("Rusher failed to take the {Waypoint} waypoint", waypoint);
            return false;
        }

        if (!await TraverseTo(rusherClient, area))
        {
            return false;
        }

        if (!await MoveToObject(rusherClient, container))
        {
            return false;
        }

        // The room has to be genuinely empty before a level one is invited into it, not merely quiet for
        // one tick: a single missed skeleton kills the rushee and costs the whole run.
        if (!await ClearAround(rusherClient, 20, TimeSpan.FromSeconds(240)))
        {
            Log.Error("Could not clear the room around {Container} in {Area}, not bringing the rushee in",
                container, area);
            return false;
        }

        // Back onto the container before opening the way in. Clearing wanders the rusher and the portal
        // lands wherever it finished: one run cast it nineteen units from the cube chest, so the rushee
        // arrived out of reach of the very thing it had come for and clicked at nothing for forty seconds.
        if (!await MoveToObject(rusherClient, container))
        {
            Log.Warning("Could not get back onto {Container} before opening the portal", container);
        }

        if (!await _townManagementService.CreateTownPortal(rusherClient))
        {
            Log.Error("Rusher failed to open a portal in {Area}", area);
            return false;
        }

        var rusherAsSeenByRushee = rusheeClient.Game.Players.Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
        if (rusherAsSeenByRushee == null)
        {
            Log.Error("Rushee cannot see the rusher, so it cannot find the portal into {Area}", area);
            return false;
        }

        if (!await MoveRusheeToPortalSpot(rusheeClient, rusherAsSeenByRushee, area))
        {
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (attempt) =>
        {
            if (attempt % 5 == 0)
            {
                rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
            }

            await Task.Delay(200);
            return rusheeClient.Game.GetEntityByCode(EntityCode.TownPortal)
                .Any(t => t.TownPortalArea == area && t.TownPortalOwnerId == rusherAsSeenByRushee.Id);
        }, TimeSpan.FromSeconds(20)))
        {
            // Say what it could see. A portal that is present but owned by someone else, or leading
            // somewhere else, is a different bug from no portal at all.
            var seen = rusheeClient.Game.GetEntityByCode(EntityCode.TownPortal)
                .Select(t => $"{t.Id} to {t.TownPortalArea} owned by {t.TownPortalOwnerId} at {t.Location}");
            Log.Error("The rushee never saw a portal into {Area} owned by {Owner}. Standing at {Location}, "
                + "portals in sight: [{Seen}]",
                area, rusherAsSeenByRushee.Id, rusheeClient.Game.Me.Location, string.Join("; ", seen));
            return false;
        }

        if (!await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, area))
        {
            Log.Error("Rushee failed to take the portal into {Area}", area);
            return false;
        }

        Log.Information("Rushee is in {Area} at {Location}", rusheeClient.Game.Area, rusheeClient.Game.Me.Location);

        var taken = await RusheeTakesFromContainer(rusherClient, rusheeClient, container, itemName);

        // Send the rushee home before reporting anything: a rushee left in a dungeon when the rusher
        // leaves has no way out at all. It rides the rusher's portal rather than casting its own, because
        // a level one has no scrolls - TakeTownPortalToTown casts first and failed outright on it.
        if (!await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee,
                WayPointHelpers.MapTownArea(rusheeClient.Game.Act)))
        {
            Log.Warning("Rushee could not ride the rusher's portal back to town from {Area}", area);
        }

        if (!taken)
        {
            return false;
        }

        if (!await _townManagementService.TakeTownPortalToTown(rusherClient))
        {
            Log.Error("Rusher failed to get back to town from {Area}", area);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The rushee opens the container and picks up what falls out, while the rusher keeps killing.
    /// </summary>
    /// <remarks>
    /// The rushee has to be the one doing both. A character cannot pick up a quest item it has no use
    /// for, so the rusher could neither carry the piece nor hand it over.
    /// <para>
    /// Opening is a plain entity interaction and nothing else: a 1.09 client opening the halls of the
    /// dead chest sent one <c>EntityInteract</c> carrying the object's id and type 2. The item appearing
    /// is the only completion signal worth waiting on - the chests and the viper altar do not agree on
    /// what state they report afterwards, and an empty container reports success either way.
    /// </para>
    /// </remarks>
    private async Task<bool> RusheeTakesFromContainer(Client rusherClient, Client rusheeClient, EntityCode entityCode, ItemName itemName)
    {
        var container = rusheeClient.Game.GetEntityByCode(entityCode).FirstOrDefault();
        if (container == null)
        {
            Log.Error("Rushee cannot see {Code} from {Location} in {Area}",
                entityCode, rusheeClient.Game.Me.Location, rusheeClient.Game.Area);
            return false;
        }

        Item onGround = null;
        var opened = await GeneralHelpers.TryWithTimeout(async (attempt) =>
        {
            await ClearTick(rusherClient, 30);

            if (rusheeClient.Game.Me.Life == 0)
            {
                return false;
            }

            // Stand on the container before touching it, so that whatever falls out lands underfoot.
            // What leaves a container is spent whether or not anyone picks it up: an amulet that dropped
            // from the viper altar four units away and was never collected recorded the tainted sun
            // against the rushee anyway, and every later game it hosted had an empty altar.
            //
            // Walking, not teleporting: a teleport of less than ten units sends nothing at all and
            // silently reports success. The rushee cannot teleport anyway.
            if (rusheeClient.Game.Me.Location.Distance(container.Location) > 2)
            {
                await rusheeClient.Game.MoveToAsync(container);
                return false;
            }

            if (attempt % 8 == 0)
            {
                rusheeClient.Game.InteractWithEntity(container);
            }

            await Task.Delay(200);
            onGround = rusheeClient.Game.Items.Values.FirstOrDefault(i => i.Ground && i.Name == itemName);
            return onGround != null;
        }, TimeSpan.FromSeconds(40));

        if (!opened)
        {
            Log.Error("{Code} in {Area} produced no {Item}, rushee {State}. An empty container means the "
                + "game's host has already done this quest.",
                entityCode, rusheeClient.Game.Area, itemName,
                rusheeClient.Game.Me.Life == 0 ? "is dead" : "is alive");
            return false;
        }

        var picked = await GeneralHelpers.TryWithTimeout(async (attempt) =>
        {
            await ClearTick(rusherClient, 30);

            if (Holds(rusheeClient, itemName))
            {
                return true;
            }

            // Stand on it, not near it. Picking from four units away was refused eighty-eight times in a
            // row at the viper altar.
            if (rusheeClient.Game.Me.Location.Distance(onGround.Location) > 2)
            {
                await rusheeClient.Game.MoveToAsync(onGround.Location);
                if (attempt % 5 == 0)
                {
                    rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
                }

                return false;
            }

            rusheeClient.Game.PickupItem(onGround);
            await Task.Delay(200);

            // A picked up item can land on the cursor rather than in the inventory, and there it is
            // invisible to every container check while also being gone from the ground. That is how an
            // amulet was lost once already: off the altar, off the floor, never in the inventory.
            if (rusheeClient.Game.CursorItem != null)
            {
                rusheeClient.Game.CleanupCursorItem();
                await Task.Delay(200);
            }

            return Holds(rusheeClient, itemName);
        }, TimeSpan.FromSeconds(25));

        if (!picked)
        {
            var stillThere = rusheeClient.Game.Items.Values
                .Where(i => i.Ground && i.Name == itemName)
                .Select(i => $"id {i.Id} at {i.Location}");
            Log.Error("Rushee failed to pick up {Item} lying at {Location}. Standing at {Standing}, "
                + "{Distance:0.0} away. On the ground now: [{Still}]. Inventory holds [{Inventory}] with "
                + "free space {Free}. Cube holds [{Cube}]. Cursor holds {Cursor}. Wearing [{Worn}].",
                itemName, onGround.Location, rusheeClient.Game.Me.Location,
                rusheeClient.Game.Me.Location.Distance(onGround.Location),
                string.Join("; ", stillThere),
                string.Join(", ", rusheeClient.Game.Inventory.Items.Select(i => i.Name.ToString())),
                rusheeClient.Game.Inventory.HasAnyFreeSpace(),
                string.Join(", ", rusheeClient.Game.Cube.Items.Select(i => i.Name.ToString())),
                rusheeClient.Game.CursorItem?.Name.ToString() ?? "nothing",
                string.Join(", ", rusheeClient.Game.Me.Equipment.Select(e => $"{e.Key}={e.Value.Name}")));
            return false;
        }

        Log.Information("Rushee picked up {Item}", itemName);
        return true;
    }

    /// <summary>
    /// Where in each town the portals appear, in absolute coordinates.
    /// </summary>
    /// <remarks>
    /// A town portal object is only assigned to a client that is close enough to it, and the town
    /// waypoint is not close enough: a rushee waiting on the Lut Gholein waypoint at (5069, 5084) was
    /// never told a portal existed. Towns are not randomised - the map api reports that same waypoint at
    /// exactly (5069, 5084) for any map id, and the character stood on it - so one measurement of the
    /// portal spot holds for every game.
    /// <para>
    /// It cannot be measured from inside an escort: going through a portal you opened yourself closes it,
    /// so a rusher that walked its own portal into town to stand on the spot would both destroy the
    /// portal the rushee was waiting for and strand itself, since a portal cannot be cast in town. Each
    /// town has to be measured once, out of band, and written down here.
    /// </para>
    /// <para>
    /// An absolute point is only safe where the town does not move. Comparing the map api across five map
    /// ids: Kurast Docks, the Pandemonium Fortress and Harrogath come back byte identical, and Lut
    /// Gholein keeps its origin and its waypoint at (5069, 5084) with only four object codes shifting. The
    /// Rogue Encampment moves wholesale - not one object code is stable, and it is not even a rigid body:
    /// the town has four layout variants, and five sampled seeds landed on three of them, each with its
    /// own set of offsets from the waypoint. So act 1 gets no entry: a hardcoded point there would be
    /// wrong most of the
    /// time. It does not need one, because the town is small enough that a rushee standing where it
    /// spawned is already told about portals, which is how the act 1 Andariel rush works today.
    /// </para>
    /// <para>
    /// Act 5 is not a guess: <c>BaalBot</c> has walked its non-portal characters to this point in
    /// Harrogath to wait for a portal for as long as it has existed.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How many of a town's objects the portal sweep is willing to visit before giving up. Bounded so a
    /// town whose portal never appears fails in a knowable time rather than walking a level one in
    /// circles.
    /// </summary>
    private static readonly Point JerhynCourtyard = new(5096, 5141);

    private const ushort JerhynMessage = 0x00C9;

    private const int AreaHopAttempts = 3;

    private const int MaxPortalSweepCandidates = 40;

    /// <summary>
    /// Cell size used to thin a town's objects before sweeping, so the candidates spread across the town
    /// instead of clustering on whichever decorative code happens to have twenty instances in one corner.
    /// </summary>
    private const int PortalSweepGrid = 25;

    /// <summary>
    /// How long a monster may sit at an identical position, life and state before the bot asks the server
    /// whether it is really there.
    /// </summary>
    private static readonly TimeSpan StaleBlockerProbeAfter = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long after that it stops counting as something worth waiting for.
    /// </summary>
    private static readonly TimeSpan StaleBlockerIgnoreAfter = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Where portals land in the Rogue Encampment, as an offset from the stash, keyed by the vector from
    /// the stash to the waypoint. That vector identifies which of act 1's four town layouts this game
    /// rolled: twelve sampled map ids fell into exactly four groups by it.
    /// </summary>
    /// <remarks>
    /// Act 1 cannot use a fixed point the way the other towns do, because nothing in it is stable - the
    /// origin, the stash and the waypoint all move, and they move relative to each other. The spot only
    /// has to be close enough for the server to mention the portal, so a near miss costs nothing.
    /// </remarks>
    /// <summary>
    /// Where portals land in the Rogue Encampment as an offset from the waypoint, keyed by the vector from
    /// the stash to the waypoint, which identifies which of act 1's four layouts a game rolled.
    /// </summary>
    /// <remarks>
    /// One measurement per layout covers every game of that layout for ever, because the layout is fixed
    /// and only its position in the world moves. Thirty six sampled map ids fall into exactly four groups,
    /// and the waypoint's offset from the level origin is constant within each, which confirms the layouts
    /// are rigid:
    /// <list type="table">
    /// <item><description>(33, -20) — 52% of games, e.g. map 3</description></item>
    /// <item><description>(28, -25) — 22%, e.g. map 1</description></item>
    /// <item><description>(-7, -30) — 15%, e.g. map 9</description></item>
    /// <item><description>(-12, -30) — 11%, e.g. map 16</description></item>
    /// </list>
    /// One hundred and thirty sampled map ids produced exactly these four and nothing else, so the table
    /// is complete and act 1 never has to fall back to the object sweep again. Measured by the
    /// <c>portalspots</c> step, which makes games until it has seen every layout.
    /// </remarks>
    private static readonly Dictionary<(int Dx, int Dy), (int Dx, int Dy)> ActOnePortalFromWaypoint = new()
    {
        // portal (4495, 4665), waypoint (4499, 4609)
        [(33, -20)] = (-4, 56),

        // portal (6045, 4970), waypoint (6044, 4909)
        [(28, -25)] = (1, 61),

        // portal (5676, 5558), waypoint (5639, 5494)
        [(-7, -30)] = (37, 64),

        // portal (5675, 5955), waypoint (5634, 5889). Measured, and two units off the value that had been
        // derived from the older stash relative entry, which is why it was worth observing rather than
        // converting.
        [(-12, -30)] = (41, 66),
    };

    /// <summary>
    /// The waypoint every town's portal spot is measured from.
    /// </summary>
    private static readonly Dictionary<D2NG.Core.D2GS.Act.Act, EntityCode> TownWaypointByAct = new()
    {
        [D2NG.Core.D2GS.Act.Act.Act1] = EntityCode.WaypointAct1,
        [D2NG.Core.D2GS.Act.Act.Act2] = EntityCode.WaypointAct2,
        [D2NG.Core.D2GS.Act.Act.Act3] = EntityCode.WaypointAct3,
        [D2NG.Core.D2GS.Act.Act.Act4] = EntityCode.WaypointAct4,
        [D2NG.Core.D2GS.Act.Act.Act5] = EntityCode.WaypointAct5,
    };

    /// <summary>
    /// Where portals land, as an offset from the town's waypoint.
    /// </summary>
    /// <remarks>
    /// The waypoint is the right anchor: the map api reports one for every town, it is what the rushee is
    /// usually standing on already, and it does not move within a layout. Offsets rather than absolute
    /// points also survive a town whose origin shifts. Measured against the map api at map id 1, and
    /// permanent for these towns, which the api returns identically for every map id.
    /// <para>
    /// Act 1 is the exception and keeps its own table: the Rogue Encampment has four layouts and the
    /// stash and waypoint move relative to each other, so the offset has to be chosen per variant.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<D2NG.Core.D2GS.Act.Act, (int Dx, int Dy)> TownPortalFromWaypointByAct = new()
    {
        // Lut Gholein waypoint (5069, 5084), portal (5178, 5053). Measured from a portal opened in halls
        // of the dead level 3; every earlier sweep failed because the objects nearest where a rushee
        // spawns are all in the south of town and this is not.
        [D2NG.Core.D2GS.Act.Act.Act2] = (109, -31),

        // Kurast Docks waypoint (5159, 5049), portal (5158, 5068) - measured during the act 3 to act 4 work.
        [D2NG.Core.D2GS.Act.Act.Act3] = (-1, 19),

        // Pandemonium Fortress waypoint (5044, 5019), portal (5046, 5043) - measured by the sweep on the
        // first run that ferried a rushee out of act 4. The fortress has one portal spot and the map api
        // returns the town identically for every map id, so this holds for good.
        [D2NG.Core.D2GS.Act.Act.Act4] = (2, 24),

        // Harrogath waypoint (5114, 5069), portal (5100, 5025) - the spot BaalBot has always used.
        [D2NG.Core.D2GS.Act.Act.Act5] = (-14, -44),
    };

    /// <summary>
    /// Walks the rushee to where the rusher's portal will appear, so that it is close enough to be told
    /// the portal exists at all.
    /// </summary>
    /// <summary>
    /// Walks the rushee to where portals land before one exists, so the walk happens while the rusher is
    /// still travelling instead of after it opens the portal. The recorded spot only has to be close: what
    /// matters is being inside the range at which the server bothers to mention the portal.
    /// </summary>
    private async Task PrePositionRushee(Client rusheeClient)
    {
        var spot = await ResolvePortalSpot(rusheeClient);
        if (spot == null || rusheeClient.Game.Me.Location.Distance(spot) < 20)
        {
            return;
        }

        await WalkRusheeTo(rusheeClient, spot);
    }

    /// <summary>
    /// Where portals land in the town the rushee is standing in, or null if nobody has measured it.
    /// </summary>
    private async Task<Point> ResolvePortalSpot(Client rusheeClient)
    {
        var act = rusheeClient.Game.Act;
        if (!TownWaypointByAct.TryGetValue(act, out var waypointCode))
        {
            return null;
        }

        var town = await _mapApiService.GetArea(rusheeClient.Game.MapId, Difficulty.Normal, rusheeClient.Game.Area);
        var waypoint = town?.Objects?.GetValueOrDefault((int)waypointCode)?.FirstOrDefault();
        if (waypoint == null)
        {
            return null;
        }

        if (act == D2NG.Core.D2GS.Act.Act.Act1)
        {
            var stash = town.Objects?.GetValueOrDefault((int)EntityCode.Stash)?.FirstOrDefault();
            if (stash == null)
            {
                return null;
            }

            var variant = (waypoint.X - stash.X, waypoint.Y - stash.Y);
            if (!ActOnePortalFromWaypoint.TryGetValue(variant, out var actOneOffset))
            {
                Log.Information("Act 1 town variant {Variant} has not been measured yet, the sweep will find it",
                    variant);
                return null;
            }

            return OffsetOrNull(waypoint, actOneOffset.Dx, actOneOffset.Dy);
        }

        if (!TownPortalFromWaypointByAct.TryGetValue(act, out var offset))
        {
            Log.Information("No portal spot measured for {Act} yet. The sweep will find it and log the "
                + "waypoint offset to add here.", act);
            return null;
        }

        return OffsetOrNull(waypoint, offset.Dx, offset.Dy);
    }

    private async Task<bool> MoveRusheeToPortalSpot(Client rusheeClient, Player rusher, Area area)
    {
        // Try the recorded spot first, but verify rather than assume. Act 1 and act 2 both have a handful
        // of town layout variants, so a spot measured in one game is a strong hint and not a guarantee;
        // falling back to the sweep costs a slow walk once instead of failing the run.
        var spot = await ResolvePortalSpot(rusheeClient);
        if (spot != null
            && await WalkRusheeTo(rusheeClient, spot)
            && await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(250);
                return CanSeePortal(rusheeClient, rusher, area);
            }, TimeSpan.FromSeconds(3)))
        {
            return true;
        }

        Log.Information("No portal visible from the recorded spot for {Act}, sweeping instead",
            rusheeClient.Game.Act);
        return await FindPortalSpotBySweep(rusheeClient, rusher, area);
    }

    private static bool CanSeePortal(Client rusheeClient, Player rusher, Area area)
        => rusheeClient.Game.GetEntityByCode(EntityCode.TownPortal)
            .Any(t => t.TownPortalArea == area && t.TownPortalOwnerId == rusher.Id);

    /// <summary>
    /// Finds the portal spot for a town nobody has measured, by walking the rushee to that town's fixed
    /// objects one at a time until the portal comes into view, and reporting where that happened.
    /// </summary>
    /// <remarks>
    /// This exists because the spot cannot be measured the obvious way. Walking through a portal you
    /// opened yourself closes it, so the rusher cannot come to town, stand on the spot and leave the
    /// portal there for the rushee - and it could not get back either, since a portal cannot be cast in
    /// town.
    /// <para>
    /// Sweeping the map api's objects is safe to do blind: the rusher stays in the field with its portal
    /// open the whole time, town has nothing in it that can kill a level one, and the candidate list is
    /// finite and ordered by distance. Whatever it finds gets logged so it can go into
    /// <see cref="TownPortalSpotByAct"/> and the sweep never has to run for that act again.
    /// </para>
    /// </remarks>
    private async Task<bool> FindPortalSpotBySweep(Client rusheeClient, Player rusher, Area area)
    {
        var town = await _mapApiService.GetArea(rusheeClient.Game.MapId, Difficulty.Normal, rusheeClient.Game.Area);
        if (town?.Objects == null)
        {
            Log.Error("The map api knows no objects in {Area} to sweep for the portal", rusheeClient.Game.Area);
            return false;
        }

        if (CanSeePortal(rusheeClient, rusher, area))
        {
            var seen = rusheeClient.Game.GetEntityByCode(EntityCode.TownPortal)
                .First(t => t.TownPortalArea == area && t.TownPortalOwnerId == rusher.Id);
            await ReportPortalSpot(rusheeClient, seen.Location, town);
            return true;
        }

        // Thin the objects onto a coarse grid before ordering by distance. Taking the nearest dozen raw
        // positions swept one corner of Lut Gholein and never went near the northern half, because the
        // decorative codes have many instances clustered together.
        var candidates = town.Objects
            .SelectMany(kv => kv.Value.Select(p => (Code: kv.Key, Point: p)))
            .GroupBy(c => (c.Point.X / PortalSweepGrid, c.Point.Y / PortalSweepGrid))
            .Select(g => g.First())
            .OrderBy(c => c.Point.Distance(rusheeClient.Game.Me.Location))
            .Take(MaxPortalSweepCandidates)
            .ToList();

        Log.Information("Sweeping {Count} objects in {Area} looking for the rusher's portal",
            candidates.Count, rusheeClient.Game.Area);

        foreach (var candidate in candidates)
        {
            if (!await WalkRusheeTo(rusheeClient, candidate.Point))
            {
                continue;
            }

            if (await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(250);
                return CanSeePortal(rusheeClient, rusher, area);
            }, TimeSpan.FromSeconds(2)))
            {
                var portal = rusheeClient.Game.GetEntityByCode(EntityCode.TownPortal)
                    .First(t => t.TownPortalArea == area && t.TownPortalOwnerId == rusher.Id);
                await ReportPortalSpot(rusheeClient, portal.Location, town);
                return true;
            }

            // Report anything portal shaped that is in sight but did not match, so that a portal the
            // rushee can see but not recognise is distinguishable from no portal at all. TownPortalOwnerId
            // is filled in from the PortalOwner packet, and it is not obvious that a bystander gets one.
            var portalish = rusheeClient.Game.WorldObjects.Values
                .Where(o => o.Code == EntityCode.TownPortal || o.Code == EntityCode.RedTownPortal)
                .Select(o => $"{o.Code} {o.Id} to {o.TownPortalArea} owned by {o.TownPortalOwnerId} at {o.Location}")
                .ToList();
            Log.Information("No match from object {Code} at {Point}; portal shaped objects in sight: [{Portals}]",
                candidate.Code, candidate.Point, string.Join("; ", portalish));
        }

        Log.Error("Swept {Count} points in {Area} and never saw a portal into {Target} owned by {Owner}",
            candidates.Count, rusheeClient.Game.Area, area, rusher.Id);
        return false;
    }

    /// <summary>
    /// Reports a measured portal spot in absolute terms and relative to the town's stash and waypoint, so
    /// a table can be built from it. Which anchor to use is an open question for act 1, whose four layout
    /// variants move the stash and the waypoint relative to each other.
    /// </summary>
    private static async Task ReportPortalSpot(Client rusheeClient, Point portal, AreaMap town)
    {
        await Task.CompletedTask;
        var stash = town.Objects?.GetValueOrDefault((int)EntityCode.Stash)?.FirstOrDefault();
        var waypoint = town.Objects?
            .FirstOrDefault(o => EntityConstants.WayPointEntityCodes.Contains((EntityCode)o.Key)).Value?
            .FirstOrDefault();

        Log.Information("MEASURED portal spot for {Act}: {Portal} absolute, origin {Origin} offset "
            + "({OriginDx}, {OriginDy}), stash {Stash} offset ({StashDx}, {StashDy}), "
            + "waypoint {Waypoint} offset ({WpDx}, {WpDy})",
            rusheeClient.Game.Act, portal, town.LevelOrigin,
            town.LevelOrigin == null ? 0 : portal.X - town.LevelOrigin.X,
            town.LevelOrigin == null ? 0 : portal.Y - town.LevelOrigin.Y,
            stash, stash == null ? 0 : portal.X - stash.X, stash == null ? 0 : portal.Y - stash.Y,
            waypoint, waypoint == null ? 0 : portal.X - waypoint.X, waypoint == null ? 0 : portal.Y - waypoint.Y);

        // The waypoint offset is the entry to paste, so say which table it belongs in.
        if (waypoint == null)
        {
            return;
        }

        if (rusheeClient.Game.Act == D2NG.Core.D2GS.Act.Act.Act1 && stash != null)
        {
            Log.Information("ADD TO ActOnePortalFromWaypoint: [({VarDx}, {VarDy})] = ({Dx}, {Dy})",
                waypoint.X - stash.X, waypoint.Y - stash.Y,
                portal.X - waypoint.X, portal.Y - waypoint.Y);
            return;
        }

        Log.Information("ADD TO TownPortalFromWaypointByAct: [{Act}] = ({Dx}, {Dy})",
            rusheeClient.Game.Act, portal.X - waypoint.X, portal.Y - waypoint.Y);
    }

    private async Task<bool> WalkRusheeTo(Client rusheeClient, Point destination)
    {
        var movementMode = GetMovementMode(rusheeClient);
        var path = await _pathingService.GetPathToLocation(rusheeClient.Game, destination, movementMode);
        if (!await MovementHelpers.TakePathOfLocations(rusheeClient.Game, path, movementMode))
        {
            Log.Error("Rushee failed to walk to {Destination}, stopped at {Location}",
                destination, rusheeClient.Game.Me.Location);
            return false;
        }

        Log.Information("Rushee waiting at {Location} in {Area}", rusheeClient.Game.Me.Location, rusheeClient.Game.Area);
        return true;
    }

    private async Task<bool> MoveToObject(Client client, EntityCode entityCode)
    {
        await SyncPosition(client);
        var movementMode = GetMovementMode(client);
        for (var attempt = 1; attempt <= AreaHopAttempts; attempt++)
        {
            var path = await _pathingService.GetPathToObject(client.Game, entityCode, movementMode);
            if (path.Count == 0)
            {
                Log.Error("The map api does not place {Code} in this {Area}", entityCode, client.Game.Area);
                return false;
            }

            if (await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode))
            {
                Log.Information("At {Code} in {Area} at {Location}", entityCode, client.Game.Area, client.Game.Me.Location);
                return true;
            }

            Log.Warning("Attempt {Attempt} of {Total} to reach {Code} stopped at {Location}",
                attempt, AreaHopAttempts, entityCode, client.Game.Me.Location);
            client.Game.RequestUpdate(client.Game.Me.Id);
            await StepAside(client);
        }

        Log.Error("Failed to reach {Code}, stopped at {Location}", entityCode, client.Game.Me.Location);
        return false;
    }

    /// <summary>
    /// Kills everything within <paramref name="radius"/> and only reports success once the area has been
    /// empty for several consecutive checks, because a level one is about to walk into it.
    /// </summary>
    /// <remarks>
    /// One quiet tick is not enough: monsters wander in, and a corpse-to-be that is out of range this
    /// instant is in range by the time the rushee arrives. Fangskin guards the viper altar and the
    /// maggot lair chest sits in a nest, so this is the step that decides whether the rushee survives.
    /// </remarks>
    private async Task<bool> ClearAround(Client client, double radius, TimeSpan limit)
    {
        const int quietChecksNeeded = 8;
        var quiet = 0;
        var lastReport = TimeSpan.Zero;
        var frozen = new Dictionary<uint, (string State, TimeSpan Since, bool Probed)>();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < limit)
        {
            var hostiles = await ClearTick(client, radius) - PruneStaleBlockers(client, radius, frozen, timer.Elapsed);
            quiet = hostiles <= 0 ? quiet + 1 : 0;
            if (hostiles > 0 && timer.Elapsed - lastReport > TimeSpan.FromSeconds(15))
            {
                lastReport = timer.Elapsed;
                var offenders = NearbyHostiles(client, radius)
                    .Take(4)
                    .ToList()
                    .Select(o => $"{o.NPCCode}/{o.Code} id {o.Id} {o.State} {o.LifePercentage:0}% at {o.Location} ({o.Location.Distance(client.Game.Me.Location):0} away)");
                Log.Information("{Hostiles} still up within {Radius} of {Location} after {Seconds:0}s: {Offenders}",
                    hostiles, radius, client.Game.Me.Location, timer.Elapsed.TotalSeconds, string.Join("; ", offenders));
            }
            if (quiet >= quietChecksNeeded)
            {
                Log.Information("{Area} around {Location} clear after {Seconds:0.0}s",
                    client.Game.Area, client.Game.Me.Location, timer.Elapsed.TotalSeconds);
                return true;
            }

            await Task.Delay(200);
        }

        Log.Warning("Still counting monsters near {Location} after {Seconds:0}s",
            client.Game.Me.Location, limit.TotalSeconds);
        return false;
    }

    /// <summary>
    /// One round of attacking whatever is nearby. Returns how many live monsters are within the radius,
    /// so callers can decide whether the area counts as clear.
    /// </summary>
    /// <summary>
    /// One round of attacking whatever is nearby. Returns how many live monsters within the radius could
    /// actually reach the staging point, which is not the same as how many are within the radius.
    /// </summary>
    /// <remarks>
    /// Monsters out of line of sight do not count as blockers. The maggot lair chest sits next to a sand
    /// maggot queen walled into the nest: it reported the same 16% life in two separate games, never moved
    /// a unit in four minutes of being attacked and never hit anything, so waiting for it to die never
    /// terminated. Everything in this act that guards a container is melee, so something that cannot see
    /// the spot cannot reach a rushee standing on it either - and the rusher stays and keeps swinging the
    /// whole time the rushee is there.
    /// </remarks>
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

    /// <summary>
    /// Counts the nearby monsters that have stopped behaving like monsters, so the caller can discount
    /// them. Anything whose position and life have not moved for a while is asked for an update, and if
    /// that changes nothing either it is treated as a record the server no longer has.
    /// </summary>
    /// <remarks>
    /// The maggot lair chest taught this. A sand maggot queen sat eleven units away reporting the same
    /// 16% life in three separate games, in line of sight, never moving a unit and never dying, while the
    /// rusher sent 693 attacks at it - not an immunity, which would still have moved and aggroed it, but
    /// a stale entry in the world object cache. Waiting for it to die never terminates.
    /// </remarks>
    private static int PruneStaleBlockers(Client client, double radius,
        Dictionary<uint, (string State, TimeSpan Since, bool Probed)> frozen, TimeSpan now)
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

            var still = now - seen.Since;
            if (!seen.Probed && still > StaleBlockerProbeAfter)
            {
                // Ask before writing it off: an entity the server still has answers this.
                client.Game.RequestUpdate(hostile.Id);
                frozen[hostile.Id] = (state, seen.Since, true);
                continue;
            }

            if (seen.Probed && still > StaleBlockerIgnoreAfter)
            {
                stale++;
                if (still < StaleBlockerIgnoreAfter + TimeSpan.FromSeconds(1))
                {
                    Log.Warning("Ignoring {Npc} id {Id} at {Location}: unchanged at {Life:0}% for {Seconds:0}s "
                        + "through an update request, so the server no longer has it",
                        hostile.NPCCode, hostile.Id, hostile.Location, hostile.LifePercentage, still.TotalSeconds);
                }
            }
        }

        return stale;
    }

    /// <summary>
    /// Whether the character has this item anywhere it counts: worn, carried, or in the cube.
    /// </summary>
    /// <remarks>
    /// The equipment slots are the reason this exists. Picking up the amulet of the viper puts it
    /// straight onto an empty amulet slot rather than into the inventory, so a check that only looked at
    /// containers saw the item vanish from the ground and never arrive - eighty-two pick attempts from
    /// directly on top of an amulet the character was already wearing. Nothing was ever wrong with the
    /// pickup itself.
    /// </remarks>
    private static bool Holds(Client client, ItemName itemName)
    {
        return client.Game.Inventory.FindItemByName(itemName) != null
            || client.Game.Cube.FindItemByName(itemName) != null
            || client.Game.Me.Equipment.Values.Any(i => i.Name == itemName)
            || client.Game.CursorItem?.Name == itemName;
    }

    /// <summary>
    /// Moves an item off the character and into the inventory, so it can go into the cube. A worn amulet
    /// is not a cube ingredient where it is.
    /// </summary>
    private static bool UnequipIntoInventory(Client client, ItemName itemName)
    {
        var slot = client.Game.Me.Equipment.FirstOrDefault(e => e.Value.Name == itemName);
        var worn = slot.Value;
        if (worn == null)
        {
            return true;
        }

        var space = client.Game.Inventory.FindFreeSpace(worn);
        if (space == null)
        {
            Log.Error("No inventory space to take {Item} off", itemName);
            return false;
        }

        // By slot, not by item id. The container move packet does nothing for a worn item.
        client.Game.RemoveBodyItem(slot.Key);
        if (!GeneralHelpers.TryWithTimeout((_) => client.Game.CursorItem?.Id == worn.Id, TimeSpan.FromSeconds(5)))
        {
            Log.Error("Taking {Item} off slot {Slot} never reached the cursor", itemName, slot.Key);
            return false;
        }

        client.Game.InsertItemIntoContainer(worn, space, ItemContainer.Inventory);
        if (!GeneralHelpers.TryWithTimeout(
            (_) => client.Game.CursorItem == null && client.Game.Inventory.FindItemById(worn.Id) != null,
            TimeSpan.FromSeconds(5)))
        {
            // Put it back on rather than abandoning it on the cursor, where leaving the game loses it.
            Log.Error("Moving {Item} from the cursor into the inventory failed, putting it back on", itemName);
            client.Game.EquipItem(worn, slot.Key);
            return false;
        }

        Log.Information("Took {Item} off {Slot} and put it in the inventory", itemName, slot.Key);
        return true;
    }

    private static List<WorldObject> NearbyHostiles(Client client, double radius)
    {
        return [.. client.Game.WorldObjects
            .Where(o => o.Key.Item2 == EntityType.NPC)
            .Select(o => o.Value)
            .Where(o => o.State != EntityState.Dead
                && o.State != EntityState.Dieing
                && !NPCHelpers.IsFriendly(o.NPCCode)
                && o.Location.Distance(client.Game.Me.Location) < radius)
            .OrderBy(o => o.Location.Distance(client.Game.Me.Location))];
    }

    /// <summary>
    /// Cubes the two pieces into a horadric staff. Done by the rushee, which is the character that has
    /// to be holding it: whoever inserts the staff is whoever carries it.
    /// </summary>
    private static bool TransmuteStaff(Client client)
    {
        var game = client.Game;
        if (game.Inventory.FindItemByName(ItemName.HoradricStaff) != null)
        {
            Log.Information("A horadric staff is already assembled");
            return true;
        }

        // The amulet arrives worn rather than carried, and a worn item is not a cube ingredient.
        if (!UnequipIntoInventory(client, ItemName.TopoftheHoradricStaff)
            || !UnequipIntoInventory(client, ItemName.ShaftoftheHoradricStaff))
        {
            return false;
        }

        var shaft = game.Inventory.FindItemByName(ItemName.ShaftoftheHoradricStaff);
        var top = game.Inventory.FindItemByName(ItemName.TopoftheHoradricStaff);
        if (shaft == null || top == null)
        {
            Log.Error("Cannot cube a staff without both pieces, carrying shaft {Shaft} and top {Top}",
                shaft != null, top != null);
            return false;
        }

        foreach (var piece in new[] { shaft, top })
        {
            var space = game.Cube.FindFreeSpace(piece);
            if (space == null)
            {
                Log.Error("No room in the cube for {Piece}", piece.Name);
                return false;
            }

            if (InventoryHelpers.PutInventoryItemInCube(game, piece, space) != MoveItemResult.Succes)
            {
                Log.Error("Putting {Piece} in the cube failed", piece.Name);
                return false;
            }
        }

        if (!InventoryHelpers.TransmuteItemsInCube(game, true))
        {
            Log.Error("Transmuting the staff failed");
            return false;
        }

        var staff = game.Cube.FindItemByName(ItemName.HoradricStaff);
        if (staff == null)
        {
            Log.Error("The cube produced no horadric staff, it holds {Items}",
                string.Join(", ", game.Cube.Items.Select(i => i.Name)));
            return false;
        }

        if (InventoryHelpers.PutCubeItemInInventory(game, staff) != MoveItemResult.Succes)
        {
            Log.Error("Moving the assembled staff out of the cube failed");
            return false;
        }

        Log.Information("Assembled a horadric staff");
        return true;
    }

    /// <summary>
    /// Enters Duriel's lair through the portal the staff opened and kills him, holding the room
    /// afterwards so that whoever is being rushed can reach Tyrael.
    /// </summary>
    /// <remarks>
    /// The lair cannot be routed into. The map api reports no adjacent level for it at all, so it is
    /// reached only by the object the staff insertion creates: code 100, which appeared next to the
    /// orifice with an id higher than every other object in the level, meaning the server made it rather
    /// than switching an existing one on.
    /// </remarks>
    private async Task<bool> RushDuriel(Client unused)
    {
        if (string.IsNullOrEmpty(_probeConfig.ManualGameName))
        {
            Log.Error("No manualGameName configured, so there is no game to join");
            return false;
        }

        var rusherClient = new Client();
        try
        {
            if (!await JoinManualGameAsRusher(rusherClient))
            {
                return false;
            }

            if (!await TakeWaypointFromTown(rusherClient, Waypoint.CanyonOfTheMagi))
            {
                Log.Error("Rusher failed to take the canyon of the magi waypoint");
                return false;
            }

            var canyon = await _mapApiService.GetArea(rusherClient.Game.MapId, Difficulty.Normal, Area.CanyonOfTheMagi);
            if (canyon?.TombArea == null)
            {
                Log.Error("The map api did not name the real tomb for this game");
                return false;
            }

            if (!await TraverseTo(rusherClient, canyon.TombArea.Value))
            {
                return false;
            }

            // Walk to the orifice, not to the portal. The portal is created by the insertion, so the map
            // api has never heard of it and pathing to it returns nothing at all; the orifice is static
            // and the portal spawns a few units from it, close enough to reach from there.
            if (!await MoveToObject(rusherClient, EntityCode.HoradricOrifice))
            {
                return false;
            }

            if (!await EnterDurielsLair(rusherClient))
            {
                return false;
            }

            // A portal inside the lair, so the rushee can walk in for the kill rather than watch from the
            // tomb. Credit went to a rushee standing in the same area as Andariel, and nothing has proven
            // that it carries across an area boundary.
            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Warning("Could not open a portal inside the lair; the rushee will have to be already here");
            }

            await WaitForCompanyInTheLair(rusherClient);

            if (!await KillDuriel(rusherClient))
            {
                return false;
            }

            Log.Information("Duriel is down. Rusher quest words: {Words}", Describe(rusherClient.Game.Quests.Describe()));
            Log.Information("Holding the lair for {Seconds}s so Tyrael can be reached.", _probeConfig.HoldTombSeconds);

            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(_probeConfig.HoldTombSeconds) && rusherClient.Game.IsInGame())
            {
                await ClearTick(rusherClient, 18);
                await Task.Delay(200);
            }

            return true;
        }
        finally
        {
            if (rusherClient.Game.IsInGame())
            {
                await rusherClient.Game.LeaveGame();
            }
            rusherClient.Disconnect();
        }
    }

    private async Task<bool> JoinManualGameAsRusher(Client rusherClient)
    {
        if (!rusherClient.Connect(_config.Realm, _config.KeyOwner, _config.GameFolder))
        {
            Log.Error("Rusher failed to connect to the realm");
            return false;
        }

        var characters = await rusherClient.Login(_probeConfig.RusherUsername, _probeConfig.RusherPassword);
        var rusher = characters?.Find(c => c.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
        if (rusher == null)
        {
            Log.Error("Rusher character {Character} not found on {Account}", _probeConfig.RusherCharacter, _probeConfig.RusherUsername);
            return false;
        }

        await rusherClient.SelectCharacter(rusher);
        if (!await rusherClient.JoinGame(_probeConfig.ManualGameName, _config.GamePassword))
        {
            Log.Error("Rusher failed to join game {GameName}", _probeConfig.ManualGameName);
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((_) => rusherClient.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            Log.Error("Rusher never initialized in game {GameName}", _probeConfig.ManualGameName);
            return false;
        }

        rusherClient.Game.RequestUpdate(rusherClient.Game.Me.Id);
        ChickenService.Attach(rusherClient, _probeConfig.Chicken ?? _config.Chicken);
        AcceptPartyInvites(rusherClient);
        await InviteEveryoneToParty(rusherClient);
        await RestockRusher(rusherClient, D2NG.Core.D2GS.Act.Act.Act2);
        return true;
    }

    private async Task<bool> EnterDurielsLair(Client rusherClient)
    {
        var portal = rusherClient.Game.GetEntityByCode(EntityCode.DurielsLairPortal).FirstOrDefault();
        if (portal == null)
        {
            Log.Error("No lair portal in sight from {Location}. It only exists once a staff has been put "
                + "into the orifice in this game, and objects in sight are [{Objects}]",
                rusherClient.Game.Me.Location,
                string.Join(", ", rusherClient.Game.WorldObjects.Values
                    .Where(o => o.Type == EntityType.Object
                        && o.Location.Distance(rusherClient.Game.Me.Location) < 40)
                    .Select(o => $"{(int)o.Code}/{o.State}")));
            return false;
        }

        Log.Information("Lair portal is entity {Id} at {Location}, state {State}", portal.Id, portal.Location, portal.State);

        var entered = await GeneralHelpers.TryWithTimeout(async (attempt) =>
        {
            if (rusherClient.Game.Me.Location.Distance(portal.Location) > 5)
            {
                if (attempt % 3 == 0)
                {
                    await MovementHelpers.MoveToWorldObject(rusherClient.Game, _pathingService, _mapApiService,
                        portal, GetMovementMode(rusherClient));
                }
                else
                {
                    await rusherClient.Game.MoveToAsync(portal);
                }

                return false;
            }

            rusherClient.Game.InteractWithEntity(portal);
            return await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(100);
                return rusherClient.Game.Area == Area.DurielsLair;
            }, TimeSpan.FromSeconds(1));
        }, TimeSpan.FromSeconds(30));

        if (!entered)
        {
            Log.Error("Taking the lair portal failed, still in {Area}", rusherClient.Game.Area);
            return false;
        }

        Log.Information("{Name} is in {Area} at {Location}", rusherClient.Game.Me.Name, rusherClient.Game.Area, rusherClient.Game.Me.Location);
        return true;
    }

    /// <summary>
    /// Holds before the kill so a rushee can come through the portal, since credit has only ever been
    /// observed for a character in the same area as the boss.
    /// </summary>
    private async Task WaitForCompanyInTheLair(Client rusherClient)
    {
        var waited = await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(1000);
            return rusherClient.Game.Players.Any(p => p.Id != rusherClient.Game.Me.Id
                && p.Location.Distance(rusherClient.Game.Me.Location) < 80);
        }, TimeSpan.FromSeconds(_probeConfig.WaitForPlayersTimeoutSeconds));

        Log.Information(waited
            ? "Someone else is in the lair, killing Duriel now"
            : "Nobody arrived within {Seconds}s, killing Duriel anyway",
            _probeConfig.WaitForPlayersTimeoutSeconds);
    }

    private async Task<bool> KillDuriel(Client rusherClient)
    {
        if (!GeneralHelpers.TryWithTimeout(
            (_) => rusherClient.Game.GetNPCsByCode(NPCCode.Duriel).Count > 0,
            TimeSpan.FromSeconds(10)))
        {
            Log.Error("Rusher cannot see Duriel at {Location}", rusherClient.Game.Me.Location);
            return false;
        }

        var duriel = rusherClient.Game.GetNPCsByCode(NPCCode.Duriel).First();
        Log.Information("Duriel is entity {Id} at {Location}, {Distance:0} away",
            duriel.Id, duriel.Location, duriel.Location.Distance(rusherClient.Game.Me.Location));

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(180))
        {
            var current = rusherClient.Game.WorldObjects.GetValueOrDefault((duriel.Id, EntityType.NPC));
            if (current == null || current.State == EntityState.Dead || current.State == EntityState.Dieing)
            {
                Log.Information("Duriel is dead after {Seconds:0.0}s", timer.Elapsed.TotalSeconds);
                return true;
            }

            await _attackService.AssistPlayer(rusherClient, rusherClient.Game.Me);
            await Task.Delay(200);
        }

        Log.Error("Duriel was still alive after 180 seconds");
        return false;
    }

    private async Task<bool> RushSevenTombs(Client rusheeClient)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No rushee, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            _rusherForInvites = rusherClient;

            // No staff is only a problem if the rushee has never inserted one. A character that already
            // finished the staff quest cannot build another - the containers are empty in a game it hosts -
            // but it does not need to, because its tomb already has the way down open.
            var staff = rusheeClient.Game.Inventory.FindItemByName(ItemName.HoradricStaff);
            var staffAlreadySpent = rusheeClient.Game.Quests.IsComplete(QuestId.TheHoradricStaff);
            if (staff == null && !staffAlreadySpent)
            {
                Log.Error("Rushee is not carrying a horadric staff and has never inserted one, "
                    + "run the staff step first");
                return false;
            }

            if (staff == null)
            {
                Log.Information("Rushee has no staff but its horadric staff quest is complete, so the lair "
                    + "should already be open");
            }

            // Already through act 2, so there is nothing here to earn. Checked on the rushee rather than on
            // the game, because the game only reports settled when the host finished it, and the point of
            // the step is the host earning it.
            if (rusheeClient.Game.Quests.IsComplete(QuestId.Act2Outro)
                || rusheeClient.Game.Act > D2NG.Core.D2GS.Act.Act.Act2)
            {
                Log.Information("{Name} is already past act 2, skipping this step", _probeCharacter.Name);
                return true;
            }

            if (rusheeClient.Game.Quests.IsSettledForGame(QuestId.TheSevenTombs))
            {
                Log.Error("This game's seven tombs is already settled, so the orifice will refuse the "
                    + "staff and nobody can be credited. Host with a character that has not finished it.");
                return false;
            }

            await PrePositionRushee(rusheeClient);
            await EnsureSupplies(rusherClient, D2NG.Core.D2GS.Act.Act.Act2);

            if (!await TakeWaypointFromTown(rusherClient, Waypoint.CanyonOfTheMagi))
            {
                Log.Error("Rusher failed to take the canyon of the magi waypoint");
                return false;
            }

            var tombArea = await ResolveTombWithOrifice(rusherClient);
            if (tombArea == null)
            {
                return false;
            }

            if (!await TraverseTo(rusherClient, tombArea.Value)
                || !await MoveToObject(rusherClient, EntityCode.HoradricOrifice)
                || !await ClearAround(rusherClient, 18, TimeSpan.FromSeconds(60)))
            {
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Error("Rusher failed to open a portal in {Tomb}", tombArea);
                return false;
            }

            var rusherAsSeenByRushee = rusheeClient.Game.Players.Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
            if (rusherAsSeenByRushee == null)
            {
                Log.Error("Rushee cannot see the rusher");
                return false;
            }

            if (!await MoveRusheeToPortalSpot(rusheeClient, rusherAsSeenByRushee, tombArea.Value)
                || !await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, tombArea.Value))
            {
                Log.Error("Rushee failed to reach {Tomb}", tombArea);
                return false;
            }

            Log.Information("Rushee is in {Area} at {Location}", rusheeClient.Game.Area, rusheeClient.Game.Me.Location);

            if (!await RusheeInsertsStaff(rusherClient, rusheeClient))
            {
                return false;
            }

            if (!await EnterDurielsLair(rusherClient))
            {
                return false;
            }

            if (!await EnterDurielsLair(rusheeClient))
            {
                Log.Error("Rushee failed to follow into the lair, so the kill would credit nobody and "
                    + "would waste the staff. Stopping instead.");
                return false;
            }

            Log.Information("Both are in the lair; rushee credit before the kill: {Credited}",
                rusheeClient.Game.Quests.IsCreditedThisGame(QuestId.TheSevenTombs));

            if (!await KillDuriel(rusherClient))
            {
                return false;
            }

            if (!await VerifySevenTombsCredit(rusheeClient))
            {
                return false;
            }

            return await CompleteActTwo(rusherClient, rusheeClient, rusherAsSeenByRushee);
        }
        finally
        {
            // The game is deliberately kept open: every step of a rush runs in the same
            // one, and tearing it down here would cost a fresh game per step. RunSteps
            // closes it once at the end.
        }
    }

    /// <summary>
    /// Opens the way into Duriel's lair, by inserting the staff or by taking an entrance a previous
    /// insert already left open.
    /// </summary>
    private async Task<bool> RusheeInsertsStaff(Client rusherClient, Client rusheeClient)
    {
        var orifice = rusheeClient.Game.GetEntityByCode(EntityCode.HoradricOrifice).FirstOrDefault();
        if (orifice == null)
        {
            Log.Error("Rushee cannot see the orifice from {Location}", rusheeClient.Game.Me.Location);
            return false;
        }

        var staff = rusheeClient.Game.Inventory.FindItemByName(ItemName.HoradricStaff);
        var staffAlreadySpent = rusheeClient.Game.Quests.IsComplete(QuestId.TheHoradricStaff);
        if (staff == null && !staffAlreadySpent)
        {
            Log.Error("Rushee lost its staff somewhere between town and the tomb");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await ClearTick(rusherClient, 18);
            if (rusheeClient.Game.Me.Location.Distance(orifice.Location) <= 3)
            {
                return true;
            }

            await rusheeClient.Game.MoveToAsync(orifice);
            return false;
        }, TimeSpan.FromSeconds(20)))
        {
            Log.Error("Rushee could not reach orifice {Id} at {Location}", orifice.Id, orifice.Location);
            return false;
        }

        // Checked from on top of the orifice, because the entrance is only streamed once the rushee is
        // close enough to it.
        if (rusheeClient.Game.GetEntityByCode(EntityCode.DurielsLairPortal).Count > 0)
        {
            Log.Information("The lair is already open for this rushee, no staff to insert");
            return true;
        }

        if (staff == null)
        {
            Log.Error("Rushee has no staff and no lair entrance appeared at the orifice either");
            return false;
        }

        rusheeClient.Game.InteractWithEntity(orifice);
        await Task.Delay(500);
        rusheeClient.Game.InteractWithEntity(orifice);
        await Task.Delay(500);

        rusheeClient.Game.RemoveItemFromContainer(staff);
        if (!GeneralHelpers.TryWithTimeout(
            (_) => rusheeClient.Game.CursorItem?.Id == staff.Id,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Could not get the staff onto the cursor, so it cannot be inserted");
            return false;
        }

        Log.Information("Inserting the staff into orifice {Id}", orifice.Id);
        rusheeClient.Game.InsertHoradricStaff(orifice, staff);

        var inserted = await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await ClearTick(rusherClient, 18);
            await Task.Delay(300);
            return rusheeClient.Game.GetEntityByCode(EntityCode.DurielsLairPortal).Count > 0;
        }, TimeSpan.FromSeconds(30));

        if (!inserted)
        {
            Log.Error("No lair portal appeared after inserting. Staff still held: {Held}. The orifice "
                + "refuses a staff when the game's seven tombs is settled.",
                rusheeClient.Game.Inventory.FindItemByName(ItemName.HoradricStaff) != null);
            return false;
        }

        Log.Information("The staff opened the lair");
        return true;
    }

    private static async Task<bool> VerifySevenTombsCredit(Client rusheeClient)
    {
        var died = rusheeClient.Game.Me.Life == 0;
        rusheeClient.Game.RequestQuestData();

        var progressed = await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(500);
            return rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs) != 0;
        }, TimeSpan.FromSeconds(10));

        // A dead character receives no quest pushes, so an empty word here may only mean it cannot hear.
        // Resurrect and ask again before believing it was not credited.
        if (!progressed && died)
        {
            Log.Information("Nothing readable while dead, resurrecting and asking again");
            rusheeClient.Game.Resurrect();
            await Task.Delay(2000);
            rusheeClient.Game.RequestQuestData();
            progressed = await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                await Task.Delay(500);
                return rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs) != 0;
            }, TimeSpan.FromSeconds(10));
        }

        Log.Information("Rushee was {State} at the kill; seven tombs now 0x{Word:X4}, credited bit {Credited}, "
            + "awaiting reward {Awaiting}",
            died ? "dead" : "alive",
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs),
            rusheeClient.Game.Quests.IsCreditedThisGame(QuestId.TheSevenTombs),
            rusheeClient.Game.Quests.IsAwaitingReward(QuestId.TheSevenTombs));

        if (!progressed)
        {
            Log.Error("Duriel died with the rushee in the lair and its seven tombs word did not move");
            return false;
        }

        if (died)
        {
            Log.Information("Resurrecting the rushee now that the state has been read");
            rusheeClient.Game.Resurrect();
        }

        return true;
    }

    /// <summary>
    /// Finishes act 2 for the rushee: Tyrael in the lair, then Jerhyn, then Meshif for the crossing to
    /// act 3.
    /// </summary>
    private async Task<bool> CompleteActTwo(Client rusherClient, Client rusheeClient, Player rusherAsSeenByRushee)
    {
        // Resurrecting puts the rushee back in town, so if it died it has to be ferried back in before it
        // can reach Tyrael. The rusher is still standing in the lair.
        // Ask where it actually is rather than what it reports. Area is resolved from the tile being stood
        // in and lags an area change, so after a resurrect it still claimed the lair while the character
        // was standing in Lut Gholein next to Warriv.
        rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
        await Task.Delay(500);
        var inLair = await _pathingService.IsNavigatablePointInArea(
            rusheeClient.Game.MapId, Difficulty.Normal, Area.DurielsLair, rusheeClient.Game.Me.Location);

        if (!inLair)
        {
            Log.Information("Rushee is at {Location}, which is not in the lair, sending it back in",
                rusheeClient.Game.Me.Location);
            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Error("Rusher could not open a portal back into the lair");
                return false;
            }

            if (!await MoveRusheeToPortalSpot(rusheeClient, rusherAsSeenByRushee, Area.DurielsLair)
                || !await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, Area.DurielsLair))
            {
                Log.Error("Rushee could not get back into the lair for Tyrael");
                return false;
            }

            Log.Information("Rushee is back in {Area} at {Location}",
                rusheeClient.Game.Area, rusheeClient.Game.Me.Location);
        }

        if (_probeConfig.HoldAtTyraelSeconds > 0)
        {
            await HoldGameForManualTyrael(rusherClient, rusheeClient);
        }

        if (!await TalkToTyrael(rusheeClient))
        {
            Log.Warning("Tyrael did not work out; carrying on to Jerhyn and Meshif to see how far this gets");
        }

        await WalkRusheeTo(rusheeClient, rusherClient.Game.Me.Location);

        if (!await _townManagementService.CreateTownPortal(rusherClient))
        {
            Log.Error("Rusher could not open a portal out of the lair");
            return false;
        }

        var townArea = WayPointHelpers.MapTownArea(rusheeClient.Game.Act);
        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(250);
            return rusheeClient.Game.GetEntityByCode(EntityCode.TownPortal)
                .Any(t => t.TownPortalArea == townArea && t.TownPortalOwnerId == rusherAsSeenByRushee.Id);
        }, TimeSpan.FromSeconds(15)))
        {
            Log.Error("Rushee never saw a portal out of the lair");
            return false;
        }

        if (!await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, townArea))
        {
            Log.Error("Rushee failed to get back to {Town}", townArea);
            return false;
        }

        if (!await ReachJerhynViaPalace(rusherClient, rusheeClient, rusherAsSeenByRushee))
        {
            Log.Warning("The palace route did not work out; trying Meshif anyway");
        }

        return await TravelWithMeshif(rusheeClient);
    }

    private async Task HoldGameForManualTyrael(Client rusherClient, Client rusheeClient)
    {
        if (!await _townManagementService.CreateTownPortal(rusherClient))
        {
            Log.Warning("Could not open a portal in the lair; join and walk in instead");
        }

        Log.Information("=== HOLDING {Seconds}s FOR A MANUAL TYRAEL CAPTURE ===", _probeConfig.HoldAtTyraelSeconds);
        Log.Information("Join the current game with password {Password} on account {Account}, take the blue "
            + "portal in town down to the lair, walk to Tyrael and talk to him once",
            _config.GamePassword, _probeConfig.Username);

        var deadline = DateTime.UtcNow.AddSeconds(_probeConfig.HoldAtTyraelSeconds);
        while (DateTime.UtcNow < deadline && rusheeClient.Game.IsInGame())
        {
            await Task.Delay(5000);
            rusheeClient.Game.RequestQuestData();
            await Task.Delay(500);
            var word = rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs);
            Log.Information("Holding, {Left:F0}s left, rushee seven tombs 0x{Word:X4}",
                (deadline - DateTime.UtcNow).TotalSeconds, word);
        }

        Log.Information("=== HOLD OVER ===");
    }

    private async Task<bool> TalkToTyrael(Client rusheeClient)
    {
        var lair = await _mapApiService.GetArea(rusheeClient.Game.MapId, Difficulty.Normal, rusheeClient.Game.Area);
        var tyraelSpot = lair?.Npcs?.GetValueOrDefault((int)NPCCode.TyraelAct3)?.FirstOrDefault();
        if (tyraelSpot == null)
        {
            Log.Error("The map api does not place Tyrael in {Area}", rusheeClient.Game.Area);
            return false;
        }

        var navigable = await _pathingService.IsNavigatablePointInArea(
            rusheeClient.Game.MapId, Difficulty.Normal, rusheeClient.Game.Area, tyraelSpot);
        Log.Information("Tyrael should be at {Spot}, navigable {Navigable}, walking from {Location}",
            tyraelSpot, navigable, rusheeClient.Game.Me.Location);

        var approach = await FindApproachTo(rusheeClient, tyraelSpot);
        if (approach == null)
        {
            Log.Error("No walkable approach to Tyrael at {Spot}", tyraelSpot);
            return false;
        }

        var toApproach = await _pathingService.GetPathToLocation(rusheeClient.Game, approach, MovementMode.Walking);
        Log.Information("Approaching Tyrael via {Approach}, path of {Steps} steps", approach, toApproach.Count);
        if (toApproach.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rusheeClient.Game, toApproach, MovementMode.Walking);
        }

        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            if (rusheeClient.Game.GetNPCsByCode(NPCCode.TyraelAct3).Count > 0)
            {
                return true;
            }

            await rusheeClient.Game.MoveToAsync(approach);
            await Task.Delay(300);
            return rusheeClient.Game.GetNPCsByCode(NPCCode.TyraelAct3).Count > 0;
        }, TimeSpan.FromSeconds(20)))
        {
            Log.Error("Tyrael not in sight from {Location}. NPCs in sight: {Npcs}",
                rusheeClient.Game.Me.Location,
                string.Join(", ", rusheeClient.Game.WorldObjects.Values
                    .Where(o => o.Type == EntityType.NPC)
                    .Select(o => $"{o.NPCCode}({(int)o.NPCCode})")
                    .Distinct()));
            return false;
        }

        var tyrael = NPCHelpers.GetUniqueNPC(rusheeClient.Game, NPCCode.TyraelAct3);
        if (tyrael == null)
        {
            Log.Error("Tyrael is not reachable at {Location} in {Area}",
                rusheeClient.Game.Me.Location, rusheeClient.Game.Area);
            return false;
        }

        await rusheeClient.Game.MoveToAsync(tyrael);
        rusheeClient.Game.InitiateEntityChat(tyrael);
        await Task.Delay(1000);

        var offered = rusheeClient.Game.GetOfferedMessages(tyrael);
        Log.Information("Tyrael advertises {Offered}; seven tombs before 0x{Word:X4}, sending 0x{Message:X4}",
            offered.Count > 0 ? string.Join(", ", offered.Select(m => $"0x{m:X2}")) : "nothing",
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs),
            TyraelsSevenTombsMessage);

        rusheeClient.Game.SendQuestMessage(tyrael, TyraelsSevenTombsMessage);
        await Task.Delay(1500);

        await Task.Delay(500);
        rusheeClient.Game.TerminateEntityChat(tyrael);

        Log.Information("After Tyrael: seven tombs 0x{Word:X4}, act 2 outro 0x{Outro:X4}",
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs),
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act2Outro));

        return true;
    }

    private async Task<Point> FindApproachTo(Client client, Point target)
    {
        var offsets = new List<Point> { target };
        foreach (var radius in new[] { 8, 16, 24, 32 })
        {
            foreach (var (dx, dy) in new[] { (0, radius), (0, -radius), (radius, 0), (-radius, 0),
                (radius, radius), (radius, -radius), (-radius, radius), (-radius, -radius) })
            {
                offsets.Add(new Point((ushort)(target.X + dx), (ushort)(target.Y + dy)));
            }
        }

        foreach (var candidate in offsets)
        {
            if (await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, Difficulty.Normal, client.Game.Area, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<bool> TalkToJerhyn(Client rusheeClient)
    {
        var jerhynPath = await _pathingService.GetPathToNPC(rusheeClient.Game, NPCCode.Jerhyn, MovementMode.Walking);
        if (jerhynPath.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rusheeClient.Game, jerhynPath, MovementMode.Walking);
        }
        else
        {
            Log.Information("The map api does not list Jerhyn in town, looking for him where he stands");
            var seen = rusheeClient.Game.GetNPCsByCode(NPCCode.Jerhyn).FirstOrDefault();
            if (seen != null)
            {
                await rusheeClient.Game.MoveToAsync(seen);
            }
        }

        var jerhyn = NPCHelpers.GetUniqueNPC(rusheeClient.Game, NPCCode.Jerhyn);
        if (jerhyn == null)
        {
            Log.Warning("Jerhyn is not in sight at {Location}; carrying on to Meshif anyway",
                rusheeClient.Game.Me.Location);
            return true;
        }

        await rusheeClient.Game.MoveToAsync(jerhyn);
        rusheeClient.Game.InitiateEntityChat(jerhyn);
        await Task.Delay(1000);

        Log.Information("Jerhyn advertises {Offered}, sending {Count} observed messages",
            rusheeClient.Game.GetOfferedMessages(jerhyn) is { Count: > 0 } o
                ? string.Join(", ", o.Select(m => $"0x{m:X2}")) : "nothing",
            JerhynMessages.Length);

        foreach (var message in JerhynMessages)
        {
            rusheeClient.Game.SendQuestMessage(jerhyn, message);
            await Task.Delay(400);
        }

        await Task.Delay(800);

        await Task.Delay(500);
        rusheeClient.Game.TerminateEntityChat(jerhyn);

        Log.Information("After Jerhyn: seven tombs 0x{Word:X4}, act 2 outro 0x{Outro:X4}",
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs),
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act2Outro));
        return true;
    }

    private async Task<bool> TravelWithMeshif(Client rusheeClient)
    {
        var path = await _pathingService.GetPathToNPC(rusheeClient.Game, NPCCode.Meshif, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rusheeClient.Game, path, MovementMode.Walking);
        }

        var meshif = NPCHelpers.GetUniqueNPC(rusheeClient.Game, NPCCode.Meshif);
        if (meshif == null)
        {
            Log.Error("Meshif is not in sight at {Location}", rusheeClient.Game.Me.Location);
            return false;
        }

        await rusheeClient.Game.MoveToAsync(meshif);
        rusheeClient.Game.InitiateEntityChat(meshif);
        await Task.Delay(750);

        foreach (var message in MeshifMessages)
        {
            rusheeClient.Game.SendQuestMessage(meshif, message);
            await Task.Delay(400);
        }

        await Task.Delay(500);
        rusheeClient.Game.TravelWithNpc(meshif);

        if (!GeneralHelpers.TryWithTimeout((_) => rusheeClient.Game.Act == Act.Act3, TimeSpan.FromSeconds(15)))
        {
            Log.Error("Meshif did not take the rushee to act 3, still in {Area} with seven tombs 0x{Word:X4}",
                rusheeClient.Game.Area, rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs));
            return false;
        }

        Log.Information("Rushee is in {Area}: act 2 rushed end to end", rusheeClient.Game.Area);
        return true;
    }

    /// <summary>
    /// Act 3 for the rushee: the rusher opens the durance, Mephisto dies with the rushee standing in the
    /// room, and both take the red portal to act 4.
    /// </summary>
    /// <remarks>
    /// No quest items and no orifice. Act 3 needs only the Durance of Hate level 2 waypoint, which the
    /// rusher has, and the rushee has to be in the room for the kill: that is the rule Duriel established.
    /// </remarks>
    private async Task<bool> RushMephisto(Client rusheeClient)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No rushee, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            _rusherForInvites = rusherClient;

            // Already in act 4, so there is nothing for this step to do and the durance waypoint it would
            // take is beside the point.
            if (rusheeClient.Game.Quests.IsComplete(QuestId.Act3Outro)
                || rusheeClient.Game.Act > Act.Act3)
            {
                Log.Information("{Name} is already past act 3, skipping this step", _probeCharacter.Name);
                return true;
            }

            var rusheeCanFollow = rusheeClient.Game.Act == Act.Act3;
            if (!rusheeCanFollow)
            {
                Log.Warning("Rushee is in {Act}, not act 3, so it cannot follow: a portal from the durance "
                    + "opens into Kurast Docks and nowhere else. Running the rusher's half as a dry run.",
                    rusheeClient.Game.Act);
            }

            await EnsureSupplies(rusherClient, D2NG.Core.D2GS.Act.Act.Act3);

            if (!await TakeWaypointFromTown(rusherClient, Waypoint.DuranceOfHateLevel2))
            {
                Log.Error("Rusher failed to take the durance of hate level 2 waypoint");
                return false;
            }

            if (!await TraverseTo(rusherClient, Area.DuranceOfHateLevel3))
            {
                return false;
            }

            var staging = await MephistoStagingSpot(rusherClient);
            if (staging != null && !await MoveTo(rusherClient, staging, GetMovementMode(rusherClient)))
            {
                Log.Warning("Could not reach the staging spot {Spot}, opening the portal where standing", staging);
            }

            if (!await ClearAround(rusherClient, 20, TimeSpan.FromSeconds(90)))
            {
                Log.Error("Could not clear the durance staging spot, not bringing the rushee in");
                return false;
            }

            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Error("Rusher failed to open a portal in the durance");
                return false;
            }

            if (rusheeCanFollow)
            {
                var rusherAsSeenByRushee = rusheeClient.Game.Players.Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
                if (rusherAsSeenByRushee == null)
                {
                    Log.Error("Rushee cannot see the rusher");
                    return false;
                }

                if (!await MoveRusheeToPortalSpot(rusheeClient, rusherAsSeenByRushee, Area.DuranceOfHateLevel3)
                    || !await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, Area.DuranceOfHateLevel3))
                {
                    Log.Error("Rushee failed to reach the durance");
                    return false;
                }

                Log.Information("Rushee is in {Area} at {Location}; guardian before the kill 0x{Word:X4}",
                    rusheeClient.Game.Area, rusheeClient.Game.Me.Location,
                    rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheGuardian));
            }

            if (!await MoveToNamedNpc(rusherClient, NPCCode.Mephisto))
            {
                return false;
            }

            if (rusheeCanFollow)
            {
                await StageRusheeAtRedPortal(rusherClient, rusheeClient);
            }

            if (!await KillNamedBoss(rusherClient, NPCCode.Mephisto, TimeSpan.FromSeconds(180)))
            {
                return false;
            }

            if (!rusheeCanFollow)
            {
                var seen = rusherClient.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
                Log.Information("Dry run done: Mephisto dead and the red portal is {Portal}",
                    seen == null ? "not in sight" : $"entity {seen.Id} at {seen.Location}");
                return seen != null;
            }

            rusheeClient.Game.RequestQuestData();
            await Task.Delay(1500);
            Log.Information("After Mephisto: rushee guardian 0x{Word:X4}, act 3 outro 0x{Outro:X4}, {State}",
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheGuardian),
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act3Outro),
                rusheeClient.Game.Me.Life == 0 ? "dead" : "alive");

            if (rusheeClient.Game.Me.Life == 0)
            {
                rusheeClient.Game.Resurrect();
                await Task.Delay(1000);
            }

            rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
            await Task.Delay(500);

            var inDurance = await _pathingService.IsNavigatablePointInArea(
                rusheeClient.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel3, rusheeClient.Game.Me.Location);

            if (!inDurance)
            {
                Log.Information("Rushee is at {Location}, which is not in the durance, sending it back in "
                    + "for the red portal", rusheeClient.Game.Me.Location);

                var rusherForReturn = rusheeClient.Game.Players.Find(
                    p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));

                if (rusherForReturn == null
                    || !await _townManagementService.CreateTownPortal(rusherClient)
                    || !await MoveRusheeToPortalSpot(rusheeClient, rusherForReturn, Area.DuranceOfHateLevel3)
                    || !await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherForReturn, Area.DuranceOfHateLevel3))
                {
                    Log.Error("Rushee could not get back into the durance for the red portal");
                    return false;
                }

                Log.Information("Rushee is back in {Area} at {Location}",
                    rusheeClient.Game.Area, rusheeClient.Game.Me.Location);
            }

            return await TakeRedPortalToActFour(rusherClient, rusheeClient);
        }
        finally
        {
            // The game is deliberately kept open: every step of a rush runs in the same
            // one, and tearing it down here would cost a fresh game per step. RunSteps
            // closes it once at the end.
        }
    }

    private async Task<Area?> ResolveTombWithOrifice(Client client)
    {
        var canyon = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, Area.CanyonOfTheMagi);
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
            var map = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, tomb);
            if (map?.Objects?.ContainsKey((int)EntityCode.HoradricOrifice) == true)
            {
                Log.Information("The orifice is in {Tomb}{Note}", tomb,
                    canyon?.TombArea == tomb ? "" : $", though the canyon reported {canyon?.TombArea}");
                return tomb;
            }
        }

        Log.Error("No tomb in this game has an orifice, canyon reported {Reported}", canyon?.TombArea);
        return null;
    }

    private async Task<bool> MoveToNamedNpc(Client client, NPCCode npcCode)
    {
        var movementMode = GetMovementMode(client);
        var path = await _pathingService.GetPathToNPC(client.Game, npcCode, movementMode);
        if (path.Count == 0)
        {
            Log.Error("The map api does not place {Npc} in {Area}", npcCode, client.Game.Area);
            return false;
        }

        if (!await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode))
        {
            Log.Error("Failed to reach {Npc}, stopped at {Location}", npcCode, client.Game.Me.Location);
            return false;
        }

        Log.Information("At {Npc} in {Area} at {Location}", npcCode, client.Game.Area, client.Game.Me.Location);
        return true;
    }

    private async Task<bool> KillNamedBoss(Client rusherClient, NPCCode npcCode, TimeSpan limit)
    {
        if (!GeneralHelpers.TryWithTimeout(
            (_) => rusherClient.Game.GetNPCsByCode(npcCode).Count > 0,
            TimeSpan.FromSeconds(10)))
        {
            Log.Error("Rusher cannot see {Npc} at {Location}", npcCode, rusherClient.Game.Me.Location);
            return false;
        }

        var boss = rusherClient.Game.GetNPCsByCode(npcCode).First();
        Log.Information("{Npc} is entity {Id} at {Location}, {Distance:0} away",
            npcCode, boss.Id, boss.Location, boss.Location.Distance(rusherClient.Game.Me.Location));

        if (boss.State is EntityState.Dead or EntityState.Dieing)
        {
            Log.Warning("{Npc} was already dead before the fight started, so nobody watched it die", npcCode);
            return true;
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < limit)
        {
            var current = rusherClient.Game.WorldObjects.GetValueOrDefault((boss.Id, EntityType.NPC))
                ?? rusherClient.Game.GetNPCsByCode(npcCode).FirstOrDefault(n => n.Id == boss.Id);
            if (current == null || current.State == EntityState.Dead || current.State == EntityState.Dieing)
            {
                Log.Information("{Npc} is dead after {Seconds:0.0}s", npcCode, timer.Elapsed.TotalSeconds);
                return true;
            }

            await AttackTarget(rusherClient, current);
        }

        Log.Error("{Npc} was still alive after {Seconds:0}s", npcCode, limit.TotalSeconds);
        return false;
    }

    /// <summary>
    /// The infernal gate is in the level from the start and only becomes usable once Mephisto dies, but the
    /// server streams it to a client once, when that client first comes into range. A rushee waiting at the
    /// durance entrance never gets it and then cannot take it, so walk it over while the rusher is in place.
    /// </summary>
    private async Task StageRusheeAtRedPortal(Client rusherClient, Client rusheeClient)
    {
        var portal = rusherClient.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
        if (portal == null)
        {
            Log.Information("The rusher cannot see the red portal yet, leaving the rushee where it is");
            return;
        }

        var mode = GetMovementMode(rusheeClient);
        var path = await _pathingService.GetPathToLocation(rusheeClient.Game, portal.Location, mode);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rusheeClient.Game, path, mode);
        }

        rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
        await Task.Delay(500);

        var seen = rusheeClient.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
        Log.Information("Rushee staged at {Location} for the red portal at {Portal}, and {Sees}",
            rusheeClient.Game.Me.Location, portal.Location,
            seen == null ? "still cannot see it" : $"can see entity {seen.Id}");
    }

    private async Task<bool> TakeRedPortalToActFour(Client rusherClient, Client rusheeClient)
    {
        if (!await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(500);
            return rusherClient.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).Count > 0;
        }, TimeSpan.FromSeconds(30)))
        {
            Log.Error("No red portal appeared after Mephisto died");
            return false;
        }

        var portalSpot = rusherClient.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal)
            .FirstOrDefault()?.Location;

        foreach (var client in new[] { rusheeClient, rusherClient })
        {
            var portal = client.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
            if (portal == null && portalSpot != null)
            {
                Log.Information("{Name} cannot see the red portal yet, walking to {Spot} to pick it up",
                    client.Game.Me.Name, portalSpot);

                var toSpot = await _pathingService.GetPathToLocation(client.Game, portalSpot, GetMovementMode(client));
                if (toSpot.Count > 0)
                {
                    await MovementHelpers.TakePathOfLocations(client.Game, toSpot, GetMovementMode(client));
                }

                client.Game.RequestUpdate(client.Game.Me.Id);
                await Task.Delay(700);
                portal = client.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault();
            }

            if (portal == null)
            {
                Log.Error("{Name} cannot see the red portal from {Location}", client.Game.Me.Name, client.Game.Me.Location);
                return false;
            }

            Log.Information("Red portal is entity {Id} at {Location} for {Name}, who is at {Me}",
                portal.Id, portal.Location, client.Game.Me.Name, client.Game.Me.Location);

            var went = await GeneralHelpers.TryWithTimeout(async (attempt) =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);

                if (client.Game.Me.Location.Distance(portal.Location) > 5)
                {
                    if (attempt % 3 == 0)
                    {
                        await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, _mapApiService,
                            portal, GetMovementMode(client));
                    }
                    else
                    {
                        await client.Game.MoveToAsync(portal);
                    }

                    return false;
                }

                client.Game.InteractWithEntity(portal);
                await Task.Delay(1500);

                client.Game.RequestUpdate(client.Game.Me.Id);
                await Task.Delay(400);
                return await _pathingService.IsNavigatablePointInArea(
                    client.Game.MapId, Difficulty.Normal, Area.ThePandemoniumFortress, client.Game.Me.Location);
            }, TimeSpan.FromSeconds(30));

            if (!went)
            {
                Log.Error("{Name} failed to take the red portal, still in {Area}", client.Game.Me.Name, client.Game.Area);
                return false;
            }

            Log.Information("{Name} is in {Area}", client.Game.Me.Name, client.Game.Area);
        }

        if (rusheeClient.Game.Act != Act.Act4)
        {
            Log.Error("Rushee is in {Act} rather than act 4", rusheeClient.Game.Act);
            return false;
        }

        Log.Information("Rushee reached {Area}: act 3 rushed end to end", rusheeClient.Game.Area);
        return true;
    }

    /// <summary>
    /// Walks the rushee to Jerhyn, who stands in the palace courtyard in Lut Gholein once Duriel is dead.
    /// </summary>
    /// <remarks>
    /// He is not in the map api's npc list, so the position is the one a client was observed standing next
    /// to him at. The courtyard may be shut by a guard depending on quest state, in which case the way in
    /// is to exit Harem level 1 instead.
    /// </remarks>
    private async Task<bool> ReachJerhynViaPalace(Client rusherClient, Client rusheeClient, Player rusherAsSeenByRushee)
    {
        var toJerhyn = await _pathingService.GetPathToLocation(rusheeClient.Game, JerhynCourtyard, MovementMode.Walking);
        Log.Information("Walking to Jerhyn's courtyard {Spot} from {Location}, path of {Steps} steps",
            JerhynCourtyard, rusheeClient.Game.Me.Location, toJerhyn.Count);
        if (toJerhyn.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(rusheeClient.Game, toJerhyn, MovementMode.Walking);
        }

        var talked = await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(500);
            return rusheeClient.Game.GetNPCsByCode(NPCCode.Jerhyn).Count > 0;
        }, TimeSpan.FromSeconds(10)) && await TalkToJerhynIfSeen(rusheeClient);

        if (!talked)
        {
            Log.Warning("Jerhyn not reached at {Location}, {Distance:0} from the courtyard. NPCs here: {Npcs}",
                rusheeClient.Game.Me.Location,
                rusheeClient.Game.Me.Location.Distance(JerhynCourtyard),
                string.Join(", ", rusheeClient.Game.WorldObjects.Values
                    .Where(o => o.Type == EntityType.NPC)
                    .Select(o => $"{o.NPCCode}({(int)o.NPCCode})")
                    .Distinct()));
        }

        Log.Information("Talked to Jerhyn {Talked}; seven tombs 0x{Word:X4}, act 2 outro 0x{Outro:X4}",
            talked,
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs),
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act2Outro));
        return talked;
    }

    private async Task<bool> HopWithEscort(Client rusherClient, Client rusheeClient, Area hop)
    {
        var from = rusherClient.Game.Area;
        var rusherWent = false;
        for (var attempt = 1; attempt <= AreaHopAttempts && !rusherWent; attempt++)
        {
            rusherWent = await TryHop(rusherClient, hop, attempt);
        }

        if (!rusherWent)
        {
            Log.Error("Rusher could not get from {From} into {Hop}", from, hop);
            return false;
        }

        if (hop != Area.LutGholein)
        {
            await ClearAround(rusherClient, 20, TimeSpan.FromSeconds(45));
        }

        var rusheeWent = false;
        for (var attempt = 1; attempt <= AreaHopAttempts && !rusheeWent; attempt++)
        {
            await ClearTick(rusherClient, 20);
            rusheeWent = await TryHop(rusheeClient, hop, attempt);
        }

        if (!rusheeWent)
        {
            Log.Error("Rushee could not follow from {From} into {Hop}", from, hop);
            return false;
        }

        Log.Information("Both moved into {Hop}", hop);
        return true;
    }

    private static async Task<bool> TalkToJerhynIfSeen(Client rusheeClient)
    {
        var jerhyn = rusheeClient.Game.GetNPCsByCode(NPCCode.Jerhyn).FirstOrDefault();
        if (jerhyn == null)
        {
            return false;
        }

        Log.Information("Jerhyn is entity {Id} at {Location} in {Area}",
            jerhyn.Id, jerhyn.Location, rusheeClient.Game.Area);

        await rusheeClient.Game.MoveToAsync(jerhyn);
        rusheeClient.Game.InitiateEntityChat(jerhyn);
        await Task.Delay(1000);

        var offered = rusheeClient.Game.GetOfferedMessages(jerhyn);
        Log.Information("Jerhyn advertises {Offered}",
            offered.Count > 0 ? string.Join(", ", offered.Select(m => $"0x{m:X2}")) : "nothing");

        foreach (var message in JerhynMessages)
        {
            rusheeClient.Game.SendQuestMessage(jerhyn, message);
            await Task.Delay(750);
        }

        rusheeClient.Game.TerminateEntityChat(jerhyn);

        rusheeClient.Game.RequestQuestData();
        await Task.Delay(1000);

        Log.Information("After Jerhyn: seven tombs 0x{Word:X4}, act 2 outro 0x{Outro:X4}",
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs),
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act2Outro));
        return true;
    }

    /// <summary>
    /// Jerhyn and Meshif only, for a character that already has Duriel dead and just needs the rest of the
    /// completion chain. Needs no staff and no boss kill.
    /// </summary>
    private async Task<bool> RushJerhyn(Client rusheeClient)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No rushee, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            _rusherForInvites = rusherClient;

            Log.Information("Starting seven tombs 0x{Word:X4}, act 2 outro 0x{Outro:X4}",
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs),
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act2Outro));

            var rusherAsSeenByRushee = rusheeClient.Game.Players.Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
            if (rusherAsSeenByRushee == null)
            {
                Log.Error("Rushee cannot see the rusher");
                return false;
            }

            if (!await ReachJerhynViaPalace(rusherClient, rusheeClient, rusherAsSeenByRushee))
            {
                Log.Warning("The palace route did not work out; trying Meshif anyway");
            }

            return await TravelWithMeshif(rusheeClient);
        }
        finally
        {
            // The game is deliberately kept open: every step of a rush runs in the same
            // one, and tearing it down here would cost a fresh game per step. RunSteps
            // closes it once at the end.
        }
    }

    /// <summary>
    /// The five seals in the order the rusher visits them, left pair last so the portal the rushee takes
    /// ends up at the left anchor.
    /// </summary>
    private static readonly EntityCode[] ChaosSeals =
    [
        EntityCode.RightSeal1,
        EntityCode.RightSeal2,
        EntityCode.TopSeal,
        EntityCode.LeftSeal1,
        EntityCode.LeftSeal2
    ];

    /// <summary>
    /// The seals worth waiting at. Opening the first of the left pair wakes nothing, so a kill phase there
    /// only spends the stale-target timeout before moving on - the infector comes from the second one.
    /// </summary>
    private static readonly HashSet<EntityCode> SealsThatWakeABoss =
    [
        EntityCode.RightSeal1,
        EntityCode.RightSeal2,
        EntityCode.TopSeal,
        EntityCode.LeftSeal2
    ];

    /// <summary>
    /// No standoff: CSBot's measured offsets are used unmodified.
    /// </summary>
    /// <remarks>
    /// Standing further back suits a ranged rusher and is worth revisiting for nightmare, but it is wrong
    /// for a nova sorceress, who has to be in the pack to do anything at all. The version tried here also
    /// validated a candidate by asking whether one cell was walkable rather than whether it could be
    /// reached, and produced a pocket the rusher teleported into and could not path out of. If it comes
    /// back, validate with a walking path to the candidate, the way the crossing search does.
    /// </remarks>
    private const int SealAnchorStandoff = 0;

    /// <summary>
    /// How far frozen orb actually reaches. Casting past this does nothing whatsoever.
    /// </summary>
    /// <remarks>
    /// This is what made real seal bosses look like stale records. One sat 32 units from the anchor while
    /// 151 RightSkillOnUnit packets went out at it, and the server sent not one NPCState in reply, because
    /// nothing ever reached it - and NPCState is the only packet that moves LifePercentage, so the bot saw
    /// an unchanging 100% and wrote it off. Diablo never spawned, because the boss was alive the whole time.
    /// </remarks>
    private const double OrbEffectiveRange = 20.0;

    /// <summary>Where to stand when closing on a target that will not come: inside orb range, no closer.</summary>
    private const double OrbEngageRange = 15.0;

    /// <summary>Teleport sends nothing within this range, so anything inside it counts as arrived.</summary>
    private const double TeleportNoOpRange = 10.0;


    private static readonly TimeSpan SealKillLimit = TimeSpan.FromSeconds(75);

    private static readonly TimeSpan DiabloKillLimit = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Static field is worth casting on Diablo down to here, and frozen orb does more below it.
    /// </summary>
    /// <remarks>
    /// <c>ClassHelpers.CanStaticEntity</c> cannot decide this: it returns true at every life percentage
    /// for a classic character, which is what the rusher is, so it would static Diablo from full to dead
    /// and never switch.
    /// </remarks>
    private const double DiabloStaticUntilLife = 50.0;

    /// <summary>Frozen orb's cast delay on 1.09. Static field fills the gaps rather than waiting.</summary>
    private static readonly TimeSpan OrbCooldown = TimeSpan.FromSeconds(1);

    private const int OrbManaCost = 30;

    private const int BlizzardManaCost = 35;

    private const int NovaManaCost = 30;

    /// <summary>Nova is centred on the caster, so the target has to be this close for it to land.</summary>
    private const double NovaRange = 10.0;

    /// <summary>How close static field is worth casting from, as CowBot uses it.</summary>
    private const double StaticFieldRange = 20.0;

    /// <summary>
    /// Mana kept back for the attack that actually does the damage. Every cast costs mana, so a cheap
    /// filler spammed in the gaps can price the main attack out on its own.
    /// </summary>
    private const int FillerManaReserve = 60;

    /// <summary>At or below this many potions of either kind the rusher makes a town trip.</summary>
    private const int PotionsBeforeRestockTrip = 1;

    private const int MaxSanctuaryRejoins = 3;

    private static readonly TimeSpan SanctuaryBudget = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Act 4 for the rushee: the rusher opens the five seals alone and kills what they wake from a fixed
    /// anchor per seal, then brings the rushee in for the Diablo kill. Classic ends here.
    /// </summary>
    /// <remarks>
    /// Credit is area bound, as Duriel showed, so the rushee waits in town for the whole seal phase and
    /// only crosses for the kill. Two rules make the sanctuary survivable and both were learned by losing
    /// the rusher: never walk, and never approach a boss - teleport to the anchor and let the pack come.
    /// Dying is recoverable rather than fatal, because the rushee holds the game open and seals stay open
    /// for the life of the game, so the rusher rejoins and carries on.
    /// </remarks>
    private async Task<bool> RushDiablo(Client rusheeClient)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No rushee, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            _rusherForInvites = rusherClient;

            if (!rusherClient.Game.Me.HasSkill(Skill.Teleport))
            {
                Log.Error("Rusher {Name} has no teleport, and the chaos sanctuary cannot be done on foot",
                    rusherClient.Game.Me.Name);
                return false;
            }

            var rusheeCanFollow = await RusheeIsReadyForAct4(rusheeClient);

            await RestockRusher(rusherClient, D2NG.Core.D2GS.Act.Act.Act4);

            var budget = Stopwatch.StartNew();
            var rejoinsUsed = 0;

            async Task<bool> EnsureRusherInSanctuary()
            {
                if (!rusherClient.Game.IsInGame())
                {
                    if (rejoinsUsed++ >= MaxSanctuaryRejoins)
                    {
                        Log.Error("The rusher has spent all {Count} rejoins", MaxSanctuaryRejoins);
                        return false;
                    }

                    if (!await RejoinRusher(rusherClient, rusheeClient))
                    {
                        return false;
                    }
                }

                return await ReturnToSanctuary(rusherClient);
            }

            Point lastAnchor = null;
            for (var attempt = 0; attempt <= MaxSanctuaryRejoins && lastAnchor == null && budget.Elapsed < SanctuaryBudget; attempt++)
            {
                if (!await EnsureRusherInSanctuary())
                {
                    return false;
                }

                lastAnchor = await RunSealCircuit(rusherClient);
            }

            if (lastAnchor == null)
            {
                Log.Error("The seal circuit never finished within {Minutes} minutes", SanctuaryBudget.TotalMinutes);
                return false;
            }

            var diabloDead = false;
            var rusheeIsIn = false;
            for (var attempt = 0; attempt <= MaxSanctuaryRejoins && !diabloDead && budget.Elapsed < SanctuaryBudget; attempt++)
            {
                if (!await EnsureRusherInSanctuary())
                {
                    return false;
                }

                if (!await MoveToObject(rusherClient, EntityCode.DiabloStar))
                {
                    continue;
                }

                if (!GeneralHelpers.TryWithTimeout(
                    (_) => rusherClient.Game.GetNPCsByCode(NPCCode.Diablo).Count > 0,
                    TimeSpan.FromSeconds(60)))
                {
                    // Diablo is gated on the seal bosses being dead, so waiting at the star again cannot
                    // help - one of them is still alive and has to be gone back for.
                    Log.Error("Diablo never appeared, so a seal boss is still alive. Walking the seals again.");
                    await RunSealCircuit(rusherClient);
                    continue;
                }

                // Only now is it worth bringing the rushee over. Ferrying it in as soon as the seals looked
                // done left a level one alone at the anchor for a hundred seconds while the rusher went back
                // for a boss that had survived, and it was a corpse by the time Diablo died - and uncredited.
                if (rusheeCanFollow && !rusheeIsIn)
                {
                    if (await MoveTo(rusherClient, lastAnchor, MovementMode.Teleport)
                        && await FerryRusheeToSanctuary(rusherClient, rusheeClient, lastAnchor))
                    {
                        rusheeIsIn = true;
                    }
                    else
                    {
                        Log.Warning("The rushee could not be brought in. Killing Diablo anyway, nobody will be credited");
                        rusheeCanFollow = false;
                    }

                    if (!await MoveToObject(rusherClient, EntityCode.DiabloStar))
                    {
                        continue;
                    }
                }

                diabloDead = await KillDiablo(rusherClient);
            }

            if (!diabloDead)
            {
                return false;
            }

            if (!rusheeCanFollow)
            {
                Log.Information("Dry run done: the seals opened and Diablo died");
                return true;
            }

            // Read before resurrecting. A dead character takes no quest pushes and resurrecting moves it
            // to town, out of the area the credit depends on.
            rusheeClient.Game.RequestQuestData();
            await Task.Delay(1500);
            Log.Information("After Diablo: rushee terrors end 0x{Word:X4}, act 4 outro 0x{Outro:X4}, {State}",
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TerrorsEnd),
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act4Outro),
                rusheeClient.Game.Me.Life == 0 ? "dead" : "alive");

            if (rusheeClient.Game.Me.Life == 0)
            {
                rusheeClient.Game.Resurrect();
                await Task.Delay(2000);
            }

            await HoldGameForActFourTyrael(rusherClient, rusheeClient);
            return true;
        }
        finally
        {
            // The game is deliberately kept open: every step of a rush runs in the same
            // one, and tearing it down here would cost a fresh game per step. RunSteps
            // closes it once at the end.
        }
    }

    private async Task HoldGameForActFourTyrael(Client rusherClient, Client rusheeClient)
    {
        if (_probeConfig.HoldAfterDiabloSeconds <= 0)
        {
            return;
        }

        if (rusheeClient.Game.IsInGame())
        {
            rusheeClient.Game.CleanupCursorItem();
            await rusheeClient.Game.LeaveGame();
        }

        Log.Warning("=== HOLDING {Seconds}s FOR A MANUAL TYRAEL CAPTURE ===", _probeConfig.HoldAfterDiabloSeconds);
        Log.Warning("Join game {GameName} with password {Password} as {Character}. Diablo is dead in this "
            + "game and nowhere else, so talk to Tyrael in the Pandemonium Fortress now. The rusher is "
            + "holding the game open and will leave when the timer runs out.",
            _currentGameName, _config.GamePassword, _probeCharacter?.Name);

        var deadline = DateTime.UtcNow.AddSeconds(_probeConfig.HoldAfterDiabloSeconds);
        while (DateTime.UtcNow < deadline && rusherClient.Game.IsInGame())
        {
            await Task.Delay(5000);
        }

        Log.Information("Hold finished, letting the game go");
    }

    /// <summary>
    /// Whether the rushee can take part, checked before the seal phase rather than at the end of it.
    /// </summary>
    private async Task<bool> RusheeIsReadyForAct4(Client rusheeClient)
    {
        if (!rusheeClient.Game.IsInGame())
        {
            Log.Warning("The rushee is not in the game, running the rusher's half as a dry run");
            return false;
        }

        if (rusheeClient.Game.Me.Life == 0)
        {
            rusheeClient.Game.Resurrect();
            await Task.Delay(2000);
        }

        rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
        await Task.Delay(500);
        var inTown = await _pathingService.IsNavigatablePointInArea(rusheeClient.Game.MapId, Difficulty.Normal,
            Area.ThePandemoniumFortress, rusheeClient.Game.Me.Location);
        if (!inTown)
        {
            Log.Warning("Rushee is at {Location}, which is not the pandemonium fortress, so it cannot follow. "
                + "Running the rusher's half as a dry run.", rusheeClient.Game.Me.Location);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Walks the five seals in order. Nothing is remembered between passes: a seal that is already open is
    /// left alone, but the kill phase runs at every one, because a boss woken on an earlier pass and left
    /// alive is exactly what stops Diablo appearing.
    /// </summary>
    /// <returns>The anchor of the last seal, or null if the circuit did not finish.</returns>
    private async Task<Point> RunSealCircuit(Client rusherClient)
    {
        Point anchor = null;
        foreach (var seal in ChaosSeals)
        {
            if (!rusherClient.Game.IsInGame())
            {
                Log.Warning("The rusher left the game before {Seal}", seal);
                return null;
            }

            if (!await RestockTripIfLow(rusherClient))
            {
                return null;
            }

            anchor = await SealAnchor(rusherClient, seal);
            if (anchor == null || !await RunSeal(rusherClient, seal, anchor))
            {
                return null;
            }
        }

        return anchor;
    }

    /// <summary>
    /// Where the rusher stands to fight each seal, using the offsets CSBot measured over a lot of runs of
    /// this level: far enough out that the pack walks to it, and on ground the level actually has.
    /// </summary>
    private async Task<Point> SealAnchor(Client rusherClient, EntityCode seal)
    {
        var map = await _mapApiService.GetArea(rusherClient.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary);
        var right1 = MapPointOf(map, EntityCode.RightSeal1);
        var right2 = MapPointOf(map, EntityCode.RightSeal2);
        var top = MapPointOf(map, EntityCode.TopSeal);
        var left1 = MapPointOf(map, EntityCode.LeftSeal1);
        var left2 = MapPointOf(map, EntityCode.LeftSeal2);

        switch (seal)
        {
            case EntityCode.RightSeal1 or EntityCode.RightSeal2 when right1 != null && right2 != null:
                return await StandBackFrom(rusherClient, right1,
                    right1.X < right2.X ? right1.Add(30, -10) : right1.Add(12, -38));

            case EntityCode.TopSeal when top != null:
                var leftOfSealIsOpen = await _pathingService.IsNavigatablePointInArea(rusherClient.Game.MapId,
                    Difficulty.Normal, Area.ChaosSanctuary, top.Add(-20, 0));
                return await StandBackFrom(rusherClient, top,
                    leftOfSealIsOpen ? top.Add(-37, 31) : top.Add(0, 70));

            case EntityCode.LeftSeal1 or EntityCode.LeftSeal2 when left1 != null && left2 != null:
                return await StandBackFrom(rusherClient, left1,
                    left1.Y > left2.Y ? left1.Add(26, -21) : left1.Add(20, 40));
        }

        Log.Error("The map api does not place the seals needed to anchor {Seal}", seal);
        return null;
    }

    /// <summary>
    /// Pushes an anchor further from its seal, keeping the direction, and keeps the original whenever the
    /// further point is not ground the level actually has.
    /// </summary>
    private async Task<Point> StandBackFrom(Client rusherClient, Point seal, Point anchor)
    {
        double dx = anchor.X - seal.X;
        double dy = anchor.Y - seal.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            return anchor;
        }

        var further = OffsetOrNull(anchor,
            (int)Math.Round(dx / length * SealAnchorStandoff),
            (int)Math.Round(dy / length * SealAnchorStandoff));

        // Walkable is not the same as reachable, which is the trap GetPathToAdjacentArea already documents.
        // A stretched top seal anchor passed a plain navigability check and turned out to be a pocket the
        // rusher could teleport into and then never path out of. Asking for a walking path from the seal
        // tests real connectivity, because walking cannot cross the scenery teleport goes over.
        if (further != null
            && (await _pathingService.GetPathToLocation(rusherClient.Game.MapId, Difficulty.Normal,
                Area.ChaosSanctuary, seal, further, MovementMode.Walking)).Count > 0)
        {
            return further;
        }

        Log.Information("Keeping the close anchor {Anchor} for the seal at {Seal}, standing back is blocked",
            anchor, seal);
        return anchor;
    }

    private static Point MapPointOf(AreaMap map, EntityCode entityCode)
        => map?.Objects != null && map.Objects.TryGetValue((int)entityCode, out var points) && points.Count > 0
            ? points[0]
            : null;

    /// <summary>
    /// One seal: teleport to the anchor, teleport onto the seal, click it, teleport straight back and fight
    /// from there. The rusher is only ever off the anchor for the click.
    /// </summary>
    private async Task<bool> RunSeal(Client rusherClient, EntityCode seal, Point anchor)
    {
        Log.Information("Seal {Seal}, anchoring at {Anchor}", seal, anchor);
        if (!await MoveTo(rusherClient, anchor, MovementMode.Teleport))
        {
            Log.Error("Could not reach the anchor {Anchor} for {Seal}", anchor, seal);
            return false;
        }

        var map = await _mapApiService.GetArea(rusherClient.Game.MapId, Difficulty.Normal, Area.ChaosSanctuary);
        var sealPoint = MapPointOf(map, seal);
        if (sealPoint == null)
        {
            Log.Error("The map api does not place {Seal} in the chaos sanctuary", seal);
            return false;
        }

        if (!await OpenSeal(rusherClient, seal, sealPoint))
        {
            return false;
        }

        if (!SealsThatWakeABoss.Contains(seal))
        {
            Log.Information("{Seal} wakes nothing, moving straight on", seal);
            return true;
        }

        if (!await MoveTo(rusherClient, anchor, MovementMode.Teleport))
        {
            Log.Warning("Could not get back to the anchor after {Seal}, fighting from {Location}",
                seal, rusherClient.Game.Me.Location);
            anchor = rusherClient.Game.Me.Location;
        }

        await FightFromAnchor(rusherClient, anchor);
        return true;
    }

    /// <summary>
    /// Steps onto a seal and clicks it, leaving the state alone if an earlier pass already opened it.
    /// </summary>
    /// <remarks>
    /// The approach is pathed rather than a single teleport. One teleport covers about thirty units, and
    /// the top seal's fallback anchor sits seventy away, which failed outright every pass.
    /// </remarks>
    private async Task<bool> OpenSeal(Client rusherClient, EntityCode seal, Point sealPoint)
    {
        if (rusherClient.Game.GetEntityByCode(seal).Any(e => e.State is EntityState.Activated or EntityState.Activating))
        {
            Log.Information("{Seal} was already open from an earlier pass", seal);
            return true;
        }

        if (!await MoveTo(rusherClient, sealPoint, MovementMode.Teleport))
        {
            Log.Error("Teleporting onto {Seal} at {Point} failed from {Location}",
                seal, sealPoint, rusherClient.Game.Me.Location);
            return false;
        }

        var opened = await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var entity = rusherClient.Game.GetEntityByCode(seal).FirstOrDefault();
            if (entity == null)
            {
                await Task.Delay(200);
                return false;
            }

            if (entity.State is EntityState.Activated or EntityState.Activating)
            {
                return true;
            }

            rusherClient.Game.InteractWithEntity(entity);
            await Task.Delay(200);
            return false;
        }, TimeSpan.FromSeconds(10));

        if (!opened)
        {
            Log.Error("{Seal} did not open", seal);
            return false;
        }

        Log.Information("{Seal} is open", seal);
        return true;
    }

    /// <summary>
    /// Fights whatever a seal woke, standing on the anchor.
    /// </summary>
    /// <remarks>
    /// The phase ends as soon as a super unique near the anchor is down, which is CSBot's rule. It also
    /// makes the second seal of a pair almost free: both seals of a pair share an anchor, so the boss
    /// killed at the first one is still lying there when the second opens. Seals of different pairs are
    /// far enough apart that an old corpse never ends the wrong phase.
    /// </remarks>
    private async Task FightFromAnchor(Client rusherClient, Point anchor)
    {
        var timer = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var frozen = new Dictionary<uint, (string State, TimeSpan Since, bool Probed)>();
        var ignored = new HashSet<uint>();
        var idleTicks = 0;
        while (timer.Elapsed < SealKillLimit && rusherClient.Game.IsInGame())
        {
            var superUniques = NPCHelpers.GetNearbySuperUniques(rusherClient, anchor, 60).ToList();
            if (superUniques.Exists(w => w.State is EntityState.Dead or EntityState.Dieing))
            {
                Log.Information("Seal boss down after {Seconds:0.0}s", timer.Elapsed.TotalSeconds);
                return;
            }

            PruneStaleTargets(rusherClient, superUniques, frozen, ignored, timer.Elapsed);

            var target = superUniques.Find(w => !ignored.Contains(w.Id)
                    && w.State != EntityState.Dead && w.State != EntityState.Dieing)
                ?? NearbyHostiles(rusherClient, 40).Find(h => !ignored.Contains(h.Id));
            if (target == null)
            {
                if (rusherClient.Game.Me.Location.Distance(anchor) > 10)
                {
                    await rusherClient.Game.TeleportToLocationAsync(anchor);
                }

                if (++idleTicks > 15)
                {
                    Log.Information("Nothing left worth fighting near the anchor after {Seconds:0.0}s",
                        timer.Elapsed.TotalSeconds);
                    return;
                }

                await Task.Delay(200);
                continue;
            }

            idleTicks = 0;

            // Close to a range the orb covers rather than casting at something it cannot reach. Still a
            // standoff and never melee: the step stops on the anchor side of the target, at orb range.
            var hasRangedAttack = rusherClient.Game.Me.HasSkill(Skill.FrozenOrb)
                || rusherClient.Game.Me.HasSkill(Skill.Blizzard);
            if (hasRangedAttack && target.Location.Distance(rusherClient.Game.Me.Location) > OrbEffectiveRange)
            {
                var engageFrom = anchor.GetPointBeforePointInSameDirection(target.Location, OrbEngageRange);
                await rusherClient.Game.TeleportToLocationAsync(engageFrom ?? target.Location);
                await Task.Delay(200);
                continue;
            }

            if (timer.Elapsed - lastReport > TimeSpan.FromSeconds(15))
            {
                lastReport = timer.Elapsed;
                Log.Information("Fighting {Npc} id {Id} on {Life:0}% life, {Distance:0} from the anchor, "
                    + "rusher on {Mana} of {MaxMana} mana",
                    target.NPCCode, target.Id, target.LifePercentage, target.Location.Distance(anchor),
                    rusherClient.Game.Me.Mana, rusherClient.Game.Me.MaxMana);
            }

            await AttackTarget(rusherClient, target);
        }

        if (timer.Elapsed >= SealKillLimit)
        {
            Log.Warning("The kill phase hit its {Seconds}s limit", SealKillLimit.TotalSeconds);
        }
    }

    /// <summary>
    /// Drops targets the server no longer really has, so a phantom cannot hold a kill phase open.
    /// </summary>
    /// <remarks>
    /// Same rule <see cref="ClearAround"/> uses, and the same failure it was written for: an oblivion
    /// knight sat seventeen units from the left seal anchor at exactly 100% life for the full fifty second
    /// limit while a hundred and thirty four attacks went out at it. Unchanged position, life and state
    /// through an update request means the record is stale, not that the monster is tough.
    /// </remarks>
    /// <returns>The ids written off on this tick, so the caller can decide whether to believe it.</returns>
    private static List<uint> PruneStaleTargets(Client client, List<WorldObject> candidates,
        Dictionary<uint, (string State, TimeSpan Since, bool Probed)> frozen, HashSet<uint> ignored, TimeSpan now)
    {
        var wroteOff = new List<uint>();
        foreach (var candidate in candidates)
        {
            if (ignored.Contains(candidate.Id))
            {
                continue;
            }

            var state = $"{candidate.Location}/{candidate.LifePercentage:0.###}/{candidate.State}";
            if (!frozen.TryGetValue(candidate.Id, out var seen) || seen.State != state)
            {
                frozen[candidate.Id] = (state, now, false);
                continue;
            }

            var still = now - seen.Since;
            if (!seen.Probed && still > StaleBlockerProbeAfter)
            {
                client.Game.RequestUpdate(candidate.Id);
                frozen[candidate.Id] = (state, seen.Since, true);
                continue;
            }

            if (seen.Probed && still > StaleBlockerIgnoreAfter)
            {
                ignored.Add(candidate.Id);
                wroteOff.Add(candidate.Id);
                Log.Warning("Ignoring {Npc} id {Id} at {Location}: unchanged at {Life:0}% for {Seconds:0}s "
                    + "through an update request",
                    candidate.NPCCode, candidate.Id, candidate.Location, candidate.LifePercentage, still.TotalSeconds);
            }
        }

        return wroteOff;
    }

    /// <summary>
    /// Hits a target with whatever the character actually has.
    /// </summary>
    /// <remarks>
    /// This used to assume frozen orb or blizzard, which is one particular rusher's build. A level 99
    /// sorceress with neither stood in a seal boss pack casting nothing at all for eighty seconds.
    /// <para>
    /// Nova is centred on the caster, so it needs closing to about ten units and is cast on our own
    /// location - the same shape <c>CowBot</c> uses for its nova sorceresses, including preferring static
    /// field while the target is still healthy enough for it to be worth casting. Note that
    /// <c>AttackService</c> gates nova behind skill level twenty, which excludes characters that can use
    /// it perfectly well, so it is not delegated to for that case.
    /// </para>
    /// </remarks>
    private async Task AttackTarget(Client client, WorldObject target)
    {
        var me = client.Game.Me;
        var distance = target.Location.Distance(me.Location);

        if (me.HasSkill(Skill.FrozenOrb) && me.Mana > OrbManaCost)
        {
            client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, target);
            await Task.Delay(200);
            return;
        }

        if (me.HasSkill(Skill.Blizzard) && me.Mana > BlizzardManaCost)
        {
            client.Game.UseRightHandSkillOnEntity(Skill.Blizzard, target);
            await Task.Delay(200);
            return;
        }

        // Pure nova, no static. Static is only worth casting to soften a big health pool, and a rush wants
        // the pack dead rather than halved: mixing the two just slowed every wave down.
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

        if (me.HasSkill(Skill.StaticField) && distance < StaticFieldRange
            && ClassHelpers.CanStaticEntity(client, target.LifePercentage))
        {
            client.Game.UseRightHandSkillOnEntity(Skill.StaticField, target);
            await Task.Delay(200);
            return;
        }

        await _attackService.AssistPlayer(client, client.Game.Me);
    }

    /// <summary>
    /// Static field while Diablo is above half life, frozen orb below it, and static again whenever orb is
    /// on cooldown, so no cast window is spent idle.
    /// </summary>
    private static async Task<bool> KillDiablo(Client rusherClient)
    {
        var diablo = rusherClient.Game.GetNPCsByCode(NPCCode.Diablo).FirstOrDefault();
        if (diablo == null)
        {
            Log.Error("Diablo is not in sight from {Location}", rusherClient.Game.Me.Location);
            return false;
        }

        Log.Information("Diablo is entity {Id} at {Location}", diablo.Id, diablo.Location);
        var timer = Stopwatch.StartNew();
        var orbTimer = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var filledThisCooldown = false;
        while (timer.Elapsed < DiabloKillLimit && rusherClient.Game.IsInGame())
        {
            var current = rusherClient.Game.WorldObjects.GetValueOrDefault((diablo.Id, EntityType.NPC));
            if (current == null || current.State is EntityState.Dead or EntityState.Dieing)
            {
                Log.Information("Diablo is dead after {Seconds:0.0}s", timer.Elapsed.TotalSeconds);
                return true;
            }

            var me = rusherClient.Game.Me;
            if (me.HasSkill(Skill.ShiverArmor) && !me.Effects.ContainsKey(EntityEffect.Shiverarmor))
            {
                rusherClient.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, me.Location);
                await Task.Delay(100);
                continue;
            }

            var distance = current.Location.Distance(me.Location);
            if (distance > 15)
            {
                await rusherClient.Game.TeleportToLocationAsync(current.Location);
                continue;
            }

            var staticPhase = current.LifePercentage > DiabloStaticUntilLife;
            if (timer.Elapsed - lastReport > TimeSpan.FromSeconds(5))
            {
                lastReport = timer.Elapsed;
                Log.Information("Diablo on {Life:0}% life, {Distance:0} away; rusher {Mana}/{MaxMana} mana, "
                    + "phase {Phase}, orb ready {OrbReady}, has orb {HasOrb}",
                    current.LifePercentage, distance, me.Mana, me.MaxMana,
                    staticPhase ? "static" : "orb", orbTimer.Elapsed > OrbCooldown, me.HasSkill(Skill.FrozenOrb));
            }

            if (staticPhase && me.HasSkill(Skill.StaticField))
            {
                rusherClient.Game.UseRightHandSkillOnEntity(Skill.StaticField, current);
                await Task.Delay(100);
                continue;
            }

            if (me.HasSkill(Skill.FrozenOrb) && me.Mana > OrbManaCost && orbTimer.Elapsed > OrbCooldown)
            {
                rusherClient.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, current);
                orbTimer.Restart();
                filledThisCooldown = false;
                await Task.Delay(250);
                continue;
            }

            // One static per orb cooldown, not one per loop, and a single cast rather than a repeat hold.
            // A repeat hold cancels the orb that is mid cast, which is why a fight showing seventeen orb
            // packets still looked like pure static field.
            if (!filledThisCooldown && me.HasSkill(Skill.StaticField) && me.Mana > FillerManaReserve)
            {
                rusherClient.Game.UseRightHandSkillOnEntity(Skill.StaticField, current);
                filledThisCooldown = true;
            }

            await Task.Delay(100);
        }

        Log.Error("Diablo was still alive after {Seconds:0}s", timer.Elapsed.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Opens the one portal of the run at the last seal's anchor and brings the rushee through it.
    /// </summary>
    private async Task<bool> FerryRusheeToSanctuary(Client rusherClient, Client rusheeClient, Point anchor)
    {
        if (!await _townManagementService.CreateTownPortal(rusherClient))
        {
            Log.Error("Rusher failed to open the portal at {Anchor}", anchor);
            return false;
        }

        var rusherAsSeenByRushee = rusheeClient.Game.Players
            .Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
        if (rusherAsSeenByRushee == null)
        {
            Log.Error("Rushee cannot see the rusher");
            return false;
        }

        if (!await MoveRusheeToPortalSpot(rusheeClient, rusherAsSeenByRushee, Area.ChaosSanctuary)
            || !await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, Area.ChaosSanctuary))
        {
            Log.Error("Rushee failed to take the portal into the sanctuary");
            return false;
        }

        rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
        await Task.Delay(500);
        if (!await _pathingService.IsNavigatablePointInArea(rusheeClient.Game.MapId, Difficulty.Normal,
            Area.ChaosSanctuary, rusheeClient.Game.Me.Location))
        {
            Log.Error("Rushee is at {Location}, which is not in the sanctuary", rusheeClient.Game.Me.Location);
            return false;
        }

        Log.Information("Rushee waits at {Location}, {Distance:0} from the anchor; terrors end 0x{Word:X4}",
            rusheeClient.Game.Me.Location,
            rusheeClient.Game.Me.Location.Distance(anchor),
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.TerrorsEnd));
        return true;
    }

    /// <summary>
    /// A town trip on the rusher's own portal when the belt is nearly out, taken before leaving the game
    /// is forced. Coming back is the waypoint route rather than the portal, which is the same path a
    /// rejoin takes and is already proven.
    /// </summary>
    private async Task<bool> RestockTripIfLow(Client rusherClient)
    {
        var health = InventoryHelpers.GetTotalHealthPotions(rusherClient.Game);
        var mana = InventoryHelpers.GetTotalManaPotions(rusherClient.Game);
        if (health > PotionsBeforeRestockTrip && mana > PotionsBeforeRestockTrip)
        {
            return true;
        }

        Log.Information("Rusher is down to {Health} health and {Mana} mana potions, going to town", health, mana);
        if (!await _townManagementService.CreateTownPortal(rusherClient)
            || !await _townManagementService.TakeTownPortalToTown(rusherClient))
        {
            Log.Warning("Could not get to town to restock, carrying on with what is in the belt");
            return true;
        }

        await RestockRusher(rusherClient, D2NG.Core.D2GS.Act.Act.Act4);
        return await ReturnToSanctuary(rusherClient);
    }

    /// <summary>
    /// Gets the rusher back into the chaos sanctuary from wherever it is, and does nothing if it is
    /// already there.
    /// </summary>
    private async Task<bool> ReturnToSanctuary(Client rusherClient)
    {
        rusherClient.Game.RequestUpdate(rusherClient.Game.Me.Id);
        await Task.Delay(500);
        if (await _pathingService.IsNavigatablePointInArea(rusherClient.Game.MapId, Difficulty.Normal,
            Area.ChaosSanctuary, rusherClient.Game.Me.Location))
        {
            return true;
        }

        if (!await TakeWaypointFromTown(rusherClient, Waypoint.RiverOfFlame))
        {
            Log.Error("Rusher failed to take the river of flame waypoint");
            return false;
        }

        return await TraverseTo(rusherClient, Area.ChaosSanctuary);
    }

    /// <summary>
    /// Rejoins the game the rushee is holding open, after the rusher died or chickened out of it.
    /// </summary>
    /// <remarks>
    /// Seals stay open for the life of the game, so this costs the trip back rather than the run. Two
    /// things do not survive leaving: the gear, which is on the corpse and comes back in town, and the
    /// party. The rushees invite here rather than the rusher, because they may already be a party of
    /// several and the rejoining rusher has to be taken into it.
    /// </remarks>
    private async Task<bool> RejoinRusher(Client rusherClient, Client rusheeClient)
    {
        if (string.IsNullOrEmpty(_currentGameName))
        {
            Log.Error("No game name was recorded, so there is nothing to rejoin");
            return false;
        }

        if (!rusheeClient.Game.IsInGame())
        {
            Log.Error("The rushee is not holding {GameName} open, so the seals are gone with it", _currentGameName);
            return false;
        }

        Log.Information("Rusher is rejoining {GameName}", _currentGameName);
        if (rusherClient.Game.IsInGame())
        {
            await rusherClient.Game.LeaveGame();
        }

        if (!rusherClient.IsRealmConnected() && !await rusherClient.RejoinMCP())
        {
            Log.Error("Rusher failed to get back onto the realm");
            return false;
        }

        if (!await rusherClient.JoinGame(_currentGameName, _config.GamePassword))
        {
            Log.Error("Rusher failed to rejoin game {GameName}", _currentGameName);
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((_) => rusherClient.Game.Me != null, TimeSpan.FromSeconds(15)))
        {
            Log.Error("Rusher never initialized after rejoining {GameName}", _currentGameName);
            return false;
        }

        rusherClient.Game.RequestUpdate(rusherClient.Game.Me.Id);
        rusherClient.Game.CleanupCursorItem();

        if (!await GeneralHelpers.PickupCorpseIfExists(rusherClient, _pathingService))
        {
            Log.Error("Rusher could not pick its corpse up, so it has no gear to fight with");
            return false;
        }

        await RestockRusher(rusherClient, D2NG.Core.D2GS.Act.Act.Act4);
        if (!await EnsurePartied(rusherClient, rusheeClient, rusheeInvites: true))
        {
            return false;
        }

        KeepInvitingNewcomers(rusherClient);
        return true;
    }

    /// <summary>
    /// Moves to a point, falling back to a direct move when the pathfinder has nothing to offer.
    /// </summary>
    /// <summary>
    /// Moves to a point, treating anything within ten units as arrived.
    /// </summary>
    /// <remarks>
    /// <c>TeleportToLocation</c> returns true without sending anything inside ten units, so a character
    /// that stops exactly ten units short reports success while never arriving, and the caller loops until
    /// it times out. That behaviour is in <c>Game.cs</c>, and it stranded the rusher at (7927, 5287) - ten
    /// units from its anchor - on every retry.
    /// </remarks>
    private async Task<bool> MoveTo(Client client, Point destination, MovementMode movementMode)
    {
        client.Game.RequestUpdate(client.Game.Me.Id);
        await Task.Delay(200);

        if (client.Game.Me.Location.Distance(destination) <= TeleportNoOpRange)
        {
            return true;
        }

        return await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            var path = await _pathingService.GetPathToLocation(client.Game, destination, movementMode);
            if (path.Count == 0)
            {
                return movementMode == MovementMode.Teleport
                    ? await client.Game.TeleportToLocationAsync(destination)
                    : await client.Game.MoveToAsync(destination);
            }

            return await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode)
                || client.Game.Me.Location.Distance(destination) <= TeleportNoOpRange;
        }, TimeSpan.FromSeconds(20));
    }

    /// <summary>How far each step to the screen left moves the durance staging spot.</summary>
    private const int MephistoStagingStep = 6;

    private const int MephistoStagingSteps = 8;

    /// <summary>Kept outside the clear radius, so staging never kills Mephisto before the rushee arrives.</summary>
    private const double MephistoStagingClearance = 30.0;

    /// <summary>
    /// Where the rusher opens the portal in the durance: on the line from the infernal gate back to
    /// Mephisto, at the middle and then stepped to the screen left until it is out of his line of sight.
    /// </summary>
    /// <remarks>
    /// Computed rather than written down, because the durance re-rolls every game. Left is the screen
    /// direction: the game draws x to the lower right and y to the lower left, so one step of -x and +y
    /// moves along the screen's horizontal. Candidates have to clear Mephisto by more than the radius the
    /// rusher then clears, because clearing around a boss kills the boss - which is how an act 3 dry run
    /// once reported Mephisto dead after 0.0s and would have credited nobody.
    /// </remarks>
    private async Task<Point> MephistoStagingSpot(Client rusherClient)
    {
        var map = await _mapApiService.GetArea(rusherClient.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel3);
        var mephisto = map?.Npcs != null && map.Npcs.TryGetValue((int)NPCCode.Mephisto, out var npcs) && npcs.Count > 0
            ? npcs[0]
            : null;
        var gate = MapPointOf(map, EntityCode.PandemoniumFortressPortal)
            ?? rusherClient.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault()?.Location;

        if (mephisto == null || gate == null)
        {
            Log.Information("The durance map data has no {Missing}, staging at the entrance instead",
                mephisto == null ? "Mephisto" : "infernal gate");
            return null;
        }

        var middle = gate.Lerp(mephisto, 0.5f);
        for (var step = 0; step <= MephistoStagingSteps; step++)
        {
            var candidate = OffsetOrNull(middle, -step * MephistoStagingStep, step * MephistoStagingStep);
            if (candidate == null || candidate.Distance(mephisto) < MephistoStagingClearance)
            {
                continue;
            }

            if (!await _pathingService.IsNavigatablePointInArea(rusherClient.Game.MapId, Difficulty.Normal,
                    Area.DuranceOfHateLevel3, candidate)
                || await _attackService.IsInLineOfSight(rusherClient, candidate, mephisto))
            {
                continue;
            }

            Log.Information("Staging the durance portal at {Spot}: gate {Gate}, Mephisto {Mephisto}, {Distance:0} clear",
                candidate, gate, mephisto, candidate.Distance(mephisto));
            return candidate;
        }

        Log.Information("Nothing between the gate and Mephisto is both reachable and out of his sight, "
            + "staging at the entrance instead");
        return null;
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
    /// Measures where town portals land in the Rogue Encampment, once per act 1 town layout.
    /// </summary>
    /// <remarks>
    /// Far cheaper than watching a rush do it. Taking your own portal back to town puts you standing on
    /// the town side of it, so the character's own position after stepping through <em>is</em> the portal
    /// spot - no second client, no sweep and no Andariel kill. Each game rolls a layout at random, so the
    /// loop just keeps making games until every layout it sees has been recorded. Nothing in the map data
    /// marks the spot: no object in any town sits within six units of a measured portal, so it has to be
    /// observed live.
    /// </remarks>
    private async Task<bool> MeasureActOnePortalSpots()
    {
        var client = new Client();
        var measured = new Dictionary<(int Dx, int Dy), (int Dx, int Dy)>();
        var seen = new Dictionary<(int Dx, int Dy), int>();
        try
        {
            if (!client.Connect(_config.Realm, _config.KeyOwner, _config.GameFolder))
            {
                Log.Error("Failed to connect");
                return false;
            }

            var characters = await client.Login(_probeConfig.RusherUsername, _probeConfig.RusherPassword);
            var rusher = characters?.Find(c => c.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
            if (rusher == null)
            {
                Log.Error("Rusher {Character} not found", _probeConfig.RusherCharacter);
                return false;
            }

            var games = _probeConfig.PortalMeasurementGames > 0 ? _probeConfig.PortalMeasurementGames : 18;
            for (var attempt = 1; attempt <= games; attempt++)
            {
                if (!await MeasureOnePortalSpot(client, rusher, measured, seen, attempt, games))
                {
                    Log.Warning("Measurement game {Attempt} did not produce a spot, carrying on", attempt);
                }

                if (client.Game.IsInGame())
                {
                    await client.Game.LeaveGame();
                }

                if (!client.IsRealmConnected() && !await client.RejoinMCP())
                {
                    Log.Error("Lost the realm connection after game {Attempt}", attempt);
                    break;
                }
            }
        }
        finally
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }
            client.Disconnect();
        }

        Log.Information("=== ACT 1 PORTAL SPOTS, {Measured} of {Seen} layouts seen ===",
            measured.Count, seen.Count);
        foreach (var (variant, count) in seen)
        {
            var offset = measured.TryGetValue(variant, out var o) ? $"({o.Dx}, {o.Dy})" : "NOT MEASURED";
            Log.Information("  [({VarDx}, {VarDy})] = {Offset}   seen {Count} times",
                variant.Dx, variant.Dy, offset, count);
        }

        return measured.Count > 0;
    }

    private async Task<bool> MeasureOnePortalSpot(Client client, Character rusher,
        Dictionary<(int Dx, int Dy), (int Dx, int Dy)> measured, Dictionary<(int Dx, int Dy), int> seen,
        int attempt, int games)
    {
        await client.SelectCharacter(rusher);
        var gameName = NextGameName();
        if (!await client.CreateGame(_config.Difficulty, gameName, _config.GamePassword, _config.GameDescriptions[0]))
        {
            Log.Warning("Could not create game {GameName}", gameName);
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((_) => client.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            Log.Warning("Character never initialized in {GameName}", gameName);
            return false;
        }

        var town = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, Area.RogueEncampment);
        var stash = town?.Objects?.GetValueOrDefault((int)EntityCode.Stash)?.FirstOrDefault();
        var waypoint = town?.Objects?.GetValueOrDefault((int)EntityCode.WaypointAct1)?.FirstOrDefault();
        if (stash == null || waypoint == null)
        {
            Log.Warning("The map api has no stash or waypoint for this Rogue Encampment");
            return false;
        }

        var variant = (waypoint.X - stash.X, waypoint.Y - stash.Y);
        seen[variant] = seen.GetValueOrDefault(variant) + 1;
        Log.Information("Game {Attempt} of {Games}: act 1 layout {Variant}{Known}",
            attempt, games, variant, measured.ContainsKey(variant) ? " (already measured)" : "");
        if (measured.ContainsKey(variant))
        {
            return true;
        }

        // The rusher lives in act 4, so it has to be in act 1 town before an act 1 waypoint is reachable.
        if (client.Game.Act != D2NG.Core.D2GS.Act.Act.Act1
            && !await _townManagementService.SwitchAct(client, D2NG.Core.D2GS.Act.Act.Act1))
        {
            Log.Warning("Could not switch to act 1");
            return false;
        }

        if (!await _townManagementService.TakeWaypoint(client, Waypoint.CatacombsLevel2))
        {
            Log.Warning("Could not take the catacombs 2 waypoint");
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(client)
            || !await _townManagementService.TakeTownPortalToTown(client))
        {
            Log.Warning("Could not open a portal and step back through it");
            return false;
        }

        client.Game.RequestUpdate(client.Game.Me.Id);
        await Task.Delay(750);
        var spot = client.Game.Me.Location;
        var offset = (spot.X - waypoint.X, spot.Y - waypoint.Y);
        measured[variant] = offset;
        Log.Information("ADD TO ActOnePortalFromWaypoint: [({VarDx}, {VarDy})] = ({Dx}, {Dy})   "
            + "portal {Spot}, waypoint {Waypoint}, stash {Stash}",
            variant.Item1, variant.Item2, offset.Item1, offset.Item2, spot, waypoint, stash);
        return true;
    }

    /// <summary>
    /// Takes a waypoint, getting back to town first if the character is not already there.
    /// </summary>
    /// <remarks>
    /// Every step of a rush now shares one game, so a step begins wherever the last one finished - which
    /// is often a dungeon. <c>TakeWaypoint</c> needs the town waypoint object and returns false instantly
    /// without one, which read as "the rusher does not have that waypoint" and was not the problem at all.
    /// </remarks>
    private async Task<bool> TakeWaypointFromTown(Client client, Waypoint waypoint)
    {
        if (!client.Game.IsInTown()
            && (!await _townManagementService.CreateTownPortal(client)
                || !await _townManagementService.TakeTownPortalToTown(client)))
        {
            Log.Error("Could not get {Name} to town before taking the {Waypoint} waypoint",
                client.Game.Me.Name, waypoint);
            return false;
        }

        return await _townManagementService.TakeWaypoint(client, waypoint);
    }

    /// <summary>How long the rusher spends clearing waves before Baal is written off as not coming.</summary>
    private static readonly TimeSpan WaveClearLimit = TimeSpan.FromMinutes(8);

    private static readonly TimeSpan ThroneClearLimit = TimeSpan.FromMinutes(3);

    /// <summary>How far the rusher may drift off the kill spot before it goes back.</summary>
    private const double HoldSpotTolerance = 15.0;

    /// <summary>How far in front of the throne to stand, so the waves arrive in reach.</summary>
    private const short ThroneStandOff = 25;

    /// <summary>How far out a wave monster is still worth going to. Ranged ones never close on their own.</summary>
    private const double WaveEngageRange = 40.0;

    /// <summary>How far out to look for loot after a fight.</summary>
    private const double PickupRadius = 40.0;

    private const int MaxPicksPerSweep = 8;

    /// <summary>
    /// Act 5 for the rushee: the rusher clears the throne room until Baal appears, then the rushee comes
    /// across for the kill.
    /// </summary>
    /// <remarks>
    /// The rushee needs no waypoint of its own - it rides the rusher's portal out of Harrogath, which was
    /// verified by hand on a character that had only just arrived in act 5. Baal only spawns once the five
    /// waves are dead, so the rushee waits in town for all of that and is ferried in at the end, the same
    /// shape act 4 uses and for the same reason: credit is area bound, not proximity bound.
    /// </remarks>
    private async Task<bool> RushBaal(Client rusheeClient)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No rushee, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            _rusherForInvites = rusherClient;

            if (rusheeClient.Game.Quests.IsComplete(QuestId.EveOfDestruction))
            {
                Log.Information("{Name} has already finished Baal, skipping this step", _probeCharacter.Name);
                return true;
            }

            if (!rusherClient.Game.Me.HasSkill(Skill.Teleport))
            {
                Log.Error("Rusher has no teleport, and the worldstone keep cannot be done on foot");
                return false;
            }

            var rusheeCanFollow = await RusheeIsReadyForActFive(rusheeClient);
            await RestockRusher(rusherClient, D2NG.Core.D2GS.Act.Act.Act5);

            if (!await TakeWaypointFromTown(rusherClient, Waypoint.TheWorldStoneKeepLevel2)
                || !await TraverseTo(rusherClient, Area.ThroneOfDestruction))
            {
                Log.Error("Rusher failed to reach the throne of destruction");
                return false;
            }

            // Stage at the entrance first, which is the offset BaalBot uses from the portal Baal opens.
            var staging = await _pathingService.GetPathToObjectWithOffset(rusherClient.Game,
                EntityCode.BaalPortal, 27, 65, MovementMode.Teleport);
            if (staging.Count > 0)
            {
                await MovementHelpers.TakePathOfLocations(rusherClient.Game, staging, MovementMode.Teleport);
            }

            // The waves spawn at the throne, not at the entrance. Holding the entrance offset meant they
            // never came within reach and the run sat there watching nothing for a minute.
            var killSpot = await ThroneKillSpot(rusherClient) ?? rusherClient.Game.Me.Location;
            Log.Information("Rusher is at the throne, {Location}, clearing waves", killSpot);

            // Opened early and left there. The ferry casts its own later, but having one up from the start
            // costs nothing and lets a person walk in and watch at any point.
            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Warning("Could not open an early portal at the throne, carrying on");
            }

            if (!await ClearThroneRoom(rusherClient))
            {
                return false;
            }

            if (!await MoveTo(rusherClient, killSpot, MovementMode.Teleport))
            {
                Log.Warning("Could not get back to the kill spot, holding where standing");
                killSpot = rusherClient.Game.Me.Location;
            }

            if (!await HoldKillSpotUntilBaal(rusherClient, killSpot))
            {
                return false;
            }

            await PickupNearby(rusherClient, PickupRadius);

            if (!await TakeBaalPortal(rusherClient))
            {
                return false;
            }

            if (rusheeCanFollow && !await FerryRusheeToChamber(rusherClient, rusheeClient))
            {
                Log.Warning("The rushee could not be brought in. Killing Baal anyway, nobody will be credited");
                rusheeCanFollow = false;
            }

            if (!await KillNamedBoss(rusherClient, NPCCode.Baal, TimeSpan.FromSeconds(300)))
            {
                return false;
            }

            await PickupNearby(rusherClient, PickupRadius);

            if (!rusheeCanFollow)
            {
                Log.Information("Dry run done: the waves cleared and Baal died");
                return true;
            }

            await LogActFiveQuests(rusheeClient, "after");

            rusheeClient.Game.RequestQuestData();
            await Task.Delay(1500);
            Log.Information("After Baal: rushee eve of destruction 0x{Word:X4}, act 5 outro 0x{Outro:X4}, {State}",
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.EveOfDestruction),
                rusheeClient.Game.Quests.GetCharacterFlags(QuestId.Act5Outro),
                rusheeClient.Game.Me.Life == 0 ? "dead" : "alive");

            if (rusheeClient.Game.Me.Life == 0)
            {
                rusheeClient.Game.Resurrect();
            }

            return true;
        }
        catch (Exception e)
        {
            Log.Error(e, "Act 5 failed");
            return false;
        }
    }

    private async Task<bool> RusheeIsReadyForActFive(Client rusheeClient)
    {
        if (!rusheeClient.Game.IsInGame())
        {
            Log.Warning("The rushee is not in the game, running the rusher's half as a dry run");
            return false;
        }

        if (rusheeClient.Game.Me.Life == 0)
        {
            rusheeClient.Game.Resurrect();
            await Task.Delay(2000);
        }

        await LogActFiveQuests(rusheeClient, "before");

        rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
        await Task.Delay(500);
        if (!await _pathingService.IsNavigatablePointInArea(rusheeClient.Game.MapId, Difficulty.Normal,
            Area.Harrogath, rusheeClient.Game.Me.Location))
        {
            Log.Warning("Rushee is at {Location}, which is not Harrogath, so it cannot follow. Running the "
                + "rusher's half as a dry run.", rusheeClient.Game.Me.Location);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Clears whatever is already in the throne room, chasing it down, so the waves start on empty ground.
    /// </summary>
    private async Task<bool> ClearThroneRoom(Client rusherClient)
    {
        var timer = Stopwatch.StartNew();
        var quiet = 0;
        while (timer.Elapsed < ThroneClearLimit && rusherClient.Game.IsInGame())
        {
            var target = NearbyHostiles(rusherClient, 50).FirstOrDefault();
            if (target == null)
            {
                if (++quiet > 10)
                {
                    Log.Information("Throne room clear after {Seconds:0}s", timer.Elapsed.TotalSeconds);
                    return true;
                }

                await Task.Delay(300);
                continue;
            }

            quiet = 0;
            await AttackTarget(rusherClient, target);
        }

        Log.Warning("Throne room was still busy after {Seconds:0}s, holding the kill spot anyway",
            timer.Elapsed.TotalSeconds);
        return true;
    }

    /// <summary>
    /// Holds the kill spot until Baal appears, which is how BaalBot fights the waves.
    /// </summary>
    /// <remarks>
    /// The waves walk to the character, so there is nothing to chase and chasing is actively harmful: an
    /// earlier version leashed to this spot while also closing on anything out of nova range, and the two
    /// fought each other - the rusher teleported between the spot and a monster twenty four units away,
    /// over and over, casting nothing. Here only what comes within reach is attacked.
    /// </remarks>
    private async Task<bool> HoldKillSpotUntilBaal(Client rusherClient, Point killSpot)
    {
        var timer = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        while (timer.Elapsed < WaveClearLimit && rusherClient.Game.IsInGame())
        {
            // The waves are over when Baal gets up: NPCCode.BaalThrone is removed, which is the signal
            // BaalBot uses. EntityCode.BaalPortal is no good for this - it is streamed from the start of
            // the level and only becomes usable later, the same trick the act 3 infernal gate plays, so
            // testing for it ended the wave phase instantly at zero seconds.
            if (rusherClient.Game.GetNPCsByCode(NPCCode.BaalThrone).Count == 0)
            {
                Log.Information("Baal has left his throne after {Seconds:0}s of waves", timer.Elapsed.TotalSeconds);
                return true;
            }

            await PickupNearby(rusherClient, PickupRadius);

            if (!await RestockTripIfLow(rusherClient))
            {
                return false;
            }

            // Target first, leash second. A wave holds ranged casters that never walk into nova range, so
            // waiting on the spot for them to come is waiting for ever - six of them sat between ten and
            // forty units away for a minute while the rusher stood still. AttackTarget closes; the leash
            // only pulls back once there is nothing left nearby, which is also what stops the two fighting.
            var nearby = NearbyHostiles(rusherClient, WaveEngageRange);
            if (timer.Elapsed - lastReport > TimeSpan.FromSeconds(20))
            {
                lastReport = timer.Elapsed;
                Log.Information("Waves: {Near} within {Range}, nearest {Distance:0} away, {Seconds:0}s elapsed",
                    nearby.Count, WaveEngageRange,
                    nearby.Count == 0 ? 0 : nearby[0].Location.Distance(rusherClient.Game.Me.Location),
                    timer.Elapsed.TotalSeconds);
            }

            if (nearby.Count == 0)
            {
                if (rusherClient.Game.Me.Location.Distance(killSpot) > HoldSpotTolerance)
                {
                    await MoveTo(rusherClient, killSpot, MovementMode.Teleport);
                }

                await Task.Delay(200);
                continue;
            }

            await AttackTarget(rusherClient, nearby[0]);
        }

        Log.Error("Baal never appeared after {Minutes} minutes of waves", WaveClearLimit.TotalMinutes);
        return false;
    }

    private async Task<bool> FerryRusheeToChamber(Client rusherClient, Client rusheeClient)
    {
        if (!await _townManagementService.CreateTownPortal(rusherClient))
        {
            Log.Error("Rusher failed to open the portal in the worldstone chamber");
            return false;
        }

        var rusherAsSeenByRushee = rusheeClient.Game.Players
            .Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
        if (rusherAsSeenByRushee == null)
        {
            Log.Error("Rushee cannot see the rusher");
            return false;
        }

        if (!await MoveRusheeToPortalSpot(rusheeClient, rusherAsSeenByRushee, Area.TheWorldStoneChamber)
            || !await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, Area.TheWorldStoneChamber))
        {
            Log.Error("Rushee failed to take the portal into the worldstone chamber");
            return false;
        }

        rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
        await Task.Delay(500);
        if (!await _pathingService.IsNavigatablePointInArea(rusheeClient.Game.MapId, Difficulty.Normal,
            Area.TheWorldStoneChamber, rusheeClient.Game.Me.Location))
        {
            Log.Error("Rushee is at {Location}, which is not the worldstone chamber", rusheeClient.Game.Me.Location);
            return false;
        }

        Log.Information("Rushee is in the worldstone chamber at {Location}; eve of destruction 0x{Word:X4}",
            rusheeClient.Game.Me.Location,
            rusheeClient.Game.Quests.GetCharacterFlags(QuestId.EveOfDestruction));
        return true;
    }

    /// <summary>
    /// Where to stand for the waves: just short of Baal's throne, which is where they arrive.
    /// </summary>
    private async Task<Point> ThroneKillSpot(Client rusherClient)
    {
        var throne = await GeneralHelpers.TryWithTimeout(
            (_) => Task.FromResult(rusherClient.Game.GetNPCsByCode(NPCCode.BaalThrone).Count > 0),
            TimeSpan.FromSeconds(5))
            ? rusherClient.Game.GetNPCsByCode(NPCCode.BaalThrone).First().Location
            : null;
        if (throne == null)
        {
            Log.Warning("Baal's throne is not in sight, holding where standing");
            return null;
        }

        var spot = OffsetOrNull(throne, 0, ThroneStandOff);
        if (spot != null && await _pathingService.IsNavigatablePointInArea(rusherClient.Game.MapId,
            Difficulty.Normal, Area.ThroneOfDestruction, spot))
        {
            Log.Information("Throne at {Throne}, holding the waves at {Spot}", throne, spot);
            return spot;
        }

        Log.Information("Throne at {Throne}, standing on it since the offset is blocked", throne);
        return throne;
    }

    /// <summary>
    /// Follows Baal into the worldstone chamber through the portal the last wave opens.
    /// </summary>
    /// <remarks>
    /// The portal is created during play, so it is not in the map data and cannot be pathed to as an
    /// object. It is reached the way every other streamed object in this bot is - alternating
    /// <c>MoveToWorldObject</c> with a direct move - and arrival is confirmed by position, never by
    /// <c>Game.Area</c>.
    /// </remarks>
    private async Task<bool> TakeBaalPortal(Client rusherClient)
    {
        var portal = rusherClient.Game.GetEntityByCode(EntityCode.BaalPortal).FirstOrDefault();
        if (portal == null)
        {
            Log.Error("Baal's portal is not in sight from {Location}", rusherClient.Game.Me.Location);
            return false;
        }

        var entered = await GeneralHelpers.TryWithTimeout(async (attempt) =>
        {
            rusherClient.Game.RequestUpdate(rusherClient.Game.Me.Id);
            if (await _pathingService.IsNavigatablePointInArea(rusherClient.Game.MapId, Difficulty.Normal,
                Area.TheWorldStoneChamber, rusherClient.Game.Me.Location))
            {
                return true;
            }

            if (attempt % 3 == 0)
            {
                await MovementHelpers.MoveToWorldObject(rusherClient.Game, _pathingService, _mapApiService,
                    portal, GetMovementMode(rusherClient));
            }
            else if (rusherClient.Game.Me.Location.Distance(portal.Location) > 5)
            {
                await rusherClient.Game.MoveToAsync(portal.Location);
            }
            else
            {
                rusherClient.Game.InteractWithEntity(portal);
            }

            await Task.Delay(400);
            return false;
        }, TimeSpan.FromSeconds(40));

        if (!entered)
        {
            Log.Error("Could not follow Baal into the worldstone chamber, stopped at {Location}",
                rusherClient.Game.Me.Location);
            return false;
        }

        Log.Information("In the worldstone chamber at {Location}", rusherClient.Game.Me.Location);
        return true;
    }

    /// <summary>
    /// Picks up anything on the floor worth keeping, plus gold.
    /// </summary>
    /// <remarks>
    /// The rush had no pickit at all, so an entire act 1 to act 5 run left every drop where it fell. Uses
    /// the same decision the farming bots use on the ground, <c>Pickit.ShouldPickupItem</c>. Not
    /// <c>ShouldKeepItem</c>: that answers a question about the inventory, where an unidentified item is
    /// kept regardless of the rules so it can be identified later, which on the ground means picking up
    /// every white drop in the game. It did, and the junk filled the rusher up until restocking failed
    /// and it could not leave town.
    ///
    /// Standing on an item is not optional: picking from four units away was refused eighty eight times
    /// in a row at the viper altar.
    /// </remarks>
    private static async Task PickupNearby(Client client, double radius)
    {
        if (!client.Game.IsInGame())
        {
            return;
        }

        var wanted = client.Game.Items.Values
            .Where(i => i.Ground
                && i.Location != null
                && i.Location.Distance(client.Game.Me.Location) < radius
                && (i.IsGold || Pickit.ShouldPickupItem(client.Game, i, true)))
            .OrderBy(i => i.Location.Distance(client.Game.Me.Location))
            .Take(MaxPicksPerSweep)
            .ToList();

        foreach (var item in wanted)
        {
            if (!item.IsGold && client.Game.Inventory.FindFreeSpace(item) == null)
            {
                continue;
            }

            var picked = await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                if (client.Game.Me.Location.Distance(item.Location) > 2)
                {
                    await client.Game.MoveToAsync(item.Location);
                    return false;
                }

                client.Game.PickupItem(item);
                await Task.Delay(200);
                return item.IsGold || client.Game.Inventory.FindItemById(item.Id) != null;
            }, TimeSpan.FromSeconds(6));

            if (picked)
            {
                Log.Information("Picked up {Amount} {Name}", item.Amount, item.Name);
            }
        }
    }

    /// <summary>
    /// Dumps the rushee's whole act 5 quest chain.
    /// </summary>
    /// <remarks>
    /// Standing alive in the Worldstone Chamber when Baal dies is not enough on its own: it happened, and
    /// EveOfDestruction stayed 0x0000. The chain is logged so the prerequisite that is actually missing can
    /// be seen rather than guessed at. RiteOfPassage is the one to look at first - the Ancients are the one
    /// quest in the game that cannot be rushed, because every character has to pass them personally.
    /// </remarks>
    private static async Task LogActFiveQuests(Client rusheeClient, string when)
    {
        rusheeClient.Game.RequestQuestData();
        await Task.Delay(1000);

        var quests = new[]
        {
            QuestId.Act5Intro, QuestId.SiegeOnHarrogath, QuestId.RescueOnMountArreat, QuestId.PrisonOfIce,
            QuestId.BetrayalOfHarrogath, QuestId.RiteOfPassage, QuestId.EveOfDestruction, QuestId.Act5Outro,
        };

        var dump = string.Join(", ", quests.Select(q =>
            $"{q} 0x{rusheeClient.Game.Quests.GetCharacterFlags(q):X4}"));
        Log.Information("Rushee act 5 quests {When} Baal: {Dump}", when, dump);
    }

    private static bool LogUnknownStep(string step)
    {
        Log.Error("Unknown probe step {Step}, expected login, create, quests or death", step);
        return false;
    }

    private async Task<bool> Login(Client client)
    {
        if (!client.Connect(_config.Realm, _config.KeyOwner, _config.GameFolder))
        {
            Log.Error("Connecting to realm failed");
            return false;
        }

        var characters = await client.Login(_probeConfig.Username, _probeConfig.Password);
        if (characters == null)
        {
            Log.Error("Logging on as {Account} failed", _probeConfig.Username);
            return false;
        }

        _characters = characters;
        Log.Information("Logged on as {Account} with {Count} characters: {Characters}",
            _probeConfig.Username,
            characters.Count,
            string.Join(", ", characters.Select(c => $"{c.Name} ({c.Class} {c.Level}{(c.IsExpansion ? " exp" : "")}{(c.IsHardCore ? " hc" : "")})")));
        return true;
    }

    /// <summary>
    /// Creates a character with a free name built from the configured prefix. Quest flags are
    /// permanent per character, so every measurement of a quest needs one that has never done it.
    /// </summary>
    private async Task<Character> CreateThrowawayCharacter(Client client)
    {
        if (!Enum.TryParse<CharacterClass>(_probeConfig.CharacterClass, true, out var characterClass))
        {
            Log.Error("Unknown character class {Class}", _probeConfig.CharacterClass);
            return null;
        }

        var flags = _probeConfig.Expansion ? CharacterFlags.Expansion : CharacterFlags.None;
        var candidates = _probeConfig.CharacterNames ?? [];

        // Carry on with a character that already exists: a rushed character keeps its quests, so
        // testing act 2 does not mean rushing act 1 again first.
        if (_probeConfig.ReuseExistingCharacter)
        {
            var existing = _characters.Find(c => !string.IsNullOrWhiteSpace(_probeConfig.Character)
                    && c.Name.Equals(_probeConfig.Character, StringComparison.OrdinalIgnoreCase))
                ?? _characters.Find(IsProbeOwnedName);
            if (existing != null)
            {
                _probeCharacter = existing;
                Log.Information("Reusing {Name} ({Class} level {Level}) instead of creating a character",
                    existing.Name, existing.Class, existing.Level);
                return existing;
            }
        }

        if (candidates.Count == 0)
        {
            Log.Error("No characterNames configured to create a throwaway character with");
            return null;
        }

        // The realm answers 0x14 both for a name already in use and for one it considers invalid,
        // digits included, so a rejection just means moving on to the next candidate.
        foreach (var name in candidates)
        {
            var created = await client.CreateCharacter(name, characterClass, flags);
            if (created != null)
            {
                _createdCharacters.Add(name);
                _probeCharacter = created;
                Log.Information("Created throwaway character {Name} ({Class}, {Flags})", name, characterClass, flags);
                return created;
            }

            Log.Debug("Name {Name} unavailable, trying the next one", name);
        }

        Log.Error("Every configured name is unavailable: {Names}", string.Join(", ", candidates));
        return null;
    }

    /// <summary>
    /// Joins a game with the most recently created character and dumps its quest state, which for a
    /// brand new character should be empty.
    /// </summary>
    private async Task<bool> ReadQuestState(Client client)
    {
        if (!await EnterGameWithProbeCharacter(client))
        {
            return false;
        }

        client.Game.RequestQuestData();
        if (!GeneralHelpers.TryWithTimeout(
            (_) => client.Game.Quests.CharacterWords.Any(w => w != 0) || client.Game.Me != null,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("No quest state arrived");
            return false;
        }

        Log.Information("Character quests: {Quests}", Describe(client.Game.Quests.Describe()));
        Log.Information("Game quests: {Quests}", Describe(client.Game.Quests.Describe(game: true)));
        Log.Information("Andariel credited: {Credited}, complete: {Complete}",
            client.Game.Quests.IsCreditedThisGame(QuestId.SistersToTheSlaughter),
            client.Game.Quests.IsComplete(QuestId.SistersToTheSlaughter));
        return true;
    }

    private static string Describe(string quests) => string.IsNullOrEmpty(quests) ? "(all zero)" : quests;

    /// <summary>
    /// The death and recovery loop as a rush actually performs it, with two clients: the rusher takes
    /// a waypoint into a busy area and opens a portal there, the throwaway character walks through and
    /// is killed, the rusher then clears around the corpse, and the character resurrects, comes back
    /// through the portal and loots itself.
    /// </summary>
    /// <remarks>
    /// A level one character cannot walk out of town at all - the map api reports no exit points for a
    /// town to wilderness edge - so arriving by portal is not a shortcut here, it is the only way.
    /// </remarks>
    private async Task<bool> DieAndRecover(Client rusheeClient)
    {
        if (!Enum.TryParse<Area>(_probeConfig.DeathArea, true, out var deathArea)
            || !Enum.TryParse<Waypoint>(_probeConfig.DangerousWaypoint, true, out var waypoint))
        {
            Log.Error("Unknown death area {Area} or waypoint {Waypoint}", _probeConfig.DeathArea, _probeConfig.DangerousWaypoint);
            return false;
        }

        if (_probeCharacter == null)
        {
            Log.Error("No throwaway character to kill, add the create step first");
            return false;
        }

        var rusherClient = SharedRusher();
        try
        {
            if (!await JoinRusherAndRushee(rusherClient, rusheeClient))
            {
                return false;
            }

            Log.Information("Rusher taking waypoint to {Waypoint}", waypoint);
            if (!await TakeWaypointFromTown(rusherClient, waypoint))
            {
                Log.Error("Rusher failed to take the waypoint");
                return false;
            }

            // The waypoint end of a level is usually empty, which the first run of this test proved by
            // leaving a level one character unharmed there for two minutes. Walk into something first.
            await MoveTowardsMonsters(rusherClient, 70);

            if (!await _townManagementService.CreateTownPortal(rusherClient))
            {
                Log.Error("Rusher failed to open a portal in {Area}", deathArea);
                return false;
            }

            Log.Information("Portal open in {Area} at {Location}", rusherClient.Game.Area, rusherClient.Game.Me.Location);

            var rusherAsSeenByRushee = rusheeClient.Game.Players.Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
            if (rusherAsSeenByRushee == null)
            {
                Log.Error("Rushee cannot see the rusher in the game, so it cannot find the portal");
                return false;
            }

            if (!await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, deathArea))
            {
                Log.Error("Rushee failed to take the portal to {Area}", deathArea);
                return false;
            }

            Log.Information("Rushee arrived in {Area} at {Location} with {Life} life",
                rusheeClient.Game.Area, rusheeClient.Game.Me.Location, rusheeClient.Game.Me.Life);

            // Standing on the portal is safe, so the rushee has to walk to whatever the rusher found.
            await MoveTowardsMonsters(rusheeClient, 40);

            if (!await WaitForDeath(rusheeClient, deathArea))
            {
                return false;
            }

            var corpseLocation = rusheeClient.Game.Me.Location;
            Log.Information("Rushee died at {Location}, corpse id {CorpseId}", corpseLocation, rusheeClient.Game.Me.CorpseId);

            await ClearAroundCorpse(rusherClient, corpseLocation);

            rusheeClient.Game.Resurrect();
            if (!GeneralHelpers.TryWithTimeout((_) => rusheeClient.Game.Me.Life > 0, TimeSpan.FromSeconds(10)))
            {
                Log.Error("Resurrect did not bring the rushee back");
                return false;
            }

            Log.Information("Rushee resurrected in {Area} with {Life} life", rusheeClient.Game.Area, rusheeClient.Game.Me.Life);

            if (!await _townManagementService.TakeTownPortalToArea(rusheeClient, rusherAsSeenByRushee, deathArea))
            {
                Log.Error("Rushee failed to get back through the portal to reach its corpse");
                return false;
            }

            if (!await GeneralHelpers.PickupCorpseIfExists(rusheeClient, _pathingService))
            {
                Log.Error("Corpse recovery failed");
                return false;
            }

            Log.Information("Corpse recovered, rushee is whole again in {Area}", rusheeClient.Game.Area);
            return true;
        }
        finally
        {
            // The game is deliberately kept open: every step of a rush runs in the same
            // one, and tearing it down here would cost a fresh game per step. RunSteps
            // closes it once at the end.
        }
    }

    private async Task<bool> JoinRusherAndRushee(Client rusherClient, Client rusheeClient)
    {
        // Already together from an earlier step: stay put. A rush is one game from act 1 to act 4, and
        // making a new one per step costs a create, two joins and a party handshake every time.
        if (rusherClient.Game.IsInGame() && rusheeClient.Game.IsInGame())
        {
            Log.Information("Carrying on in {GameName}", _currentGameName);
            return true;
        }

        if (string.IsNullOrEmpty(_probeConfig.RusherUsername))
        {
            Log.Error("No rusher account configured");
            return false;
        }

        if (!rusherClient.Connect(_config.Realm, _config.KeyOwner, _config.GameFolder))
        {
            Log.Error("Rusher failed to connect to the realm");
            return false;
        }

        var rusherCharacters = await rusherClient.Login(_probeConfig.RusherUsername, _probeConfig.RusherPassword);
        var rusher = rusherCharacters?.Find(c => c.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));
        if (rusher == null)
        {
            Log.Error("Rusher character {Character} not found on {Account}", _probeConfig.RusherCharacter, _probeConfig.RusherUsername);
            return false;
        }

        // Classic and expansion characters cannot share a game, so a mismatch here would fail later
        // in a much more confusing way.
        if (rusher.IsExpansion != _probeConfig.Expansion)
        {
            Log.Error("Rusher {Rusher} is {RusherMode} but the throwaway character is {RusheeMode}, they cannot share a game",
                rusher.Name,
                rusher.IsExpansion ? "expansion" : "classic",
                _probeConfig.Expansion ? "expansion" : "classic");
            return false;
        }

        Log.Information("Rusher is {Rusher} ({Class} level {Level})", rusher.Name, rusher.Class, rusher.Level);
        var gameName = NextGameName();

        // Get the rushee back onto the realm first: an earlier step may have left it in a game, and
        // playing a game drops the realm connection that joining another one needs.
        if (rusheeClient.Game.IsInGame())
        {
            Log.Debug("Rushee is still in a game, leaving it before joining the rusher");
            await rusheeClient.Game.LeaveGame();
        }

        // SelectCharacter talks to the realm, so it only works while that connection is alive.
        // RejoinMCP re-selects the character it was already logged on with.
        if (rusheeClient.IsRealmConnected())
        {
            await rusheeClient.SelectCharacter(_probeCharacter);
        }
        else if (!await rusheeClient.RejoinMCP())
        {
            Log.Error("Rushee failed to get back onto the realm");
            return false;
        }

        // The character being rushed has to create the game. A game inherits the quest state of
        // whoever made it, so hosting with a character that already finished the quest leaves nothing
        // to complete: two runs hosted by the level 90 rusher killed Andariel and credited nobody,
        // and the game array showed her quest as 0x8000 before a blow was struck.
        var created = false;
        for (var attempt = 0; attempt < 5 && !created; attempt++)
        {
            created = await rusheeClient.CreateGame(_config.Difficulty, gameName, _config.GamePassword, _config.GameDescriptions[0]);
            if (!created)
            {
                Log.Information("Game {GameName} could not be created, trying the next number", gameName);
                gameName = NextGameName();
            }
        }

        if (!created)
        {
            Log.Error("Rushee failed to create a game");
            return false;
        }

        Log.Information("Game {GameName} created by the rushee, password {Password}", gameName, _config.GamePassword);

        await rusherClient.SelectCharacter(rusher);
        if (!await rusherClient.JoinGame(gameName, _config.GamePassword))
        {
            Log.Error("Rusher failed to join game {GameName}", gameName);
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout(
            (_) => rusherClient.Game.Me != null && rusheeClient.Game.Me != null,
            TimeSpan.FromSeconds(10)))
        {
            Log.Error("One of the clients never initialized in game {GameName}", gameName);
            return false;
        }

        rusheeClient.Game.RequestUpdate(rusheeClient.Game.Me.Id);
        rusherClient.Game.RequestUpdate(rusherClient.Game.Me.Id);

        // Keep both characters drinking. Every other bot attaches this and the probe never did, which is
        // why a rusher sorceress stalled a sand maggot queen at 16% life for four minutes: it had run out
        // of mana in the maggot lair and had no mechanism to drink its way back.
        ChickenService.Attach(rusherClient, _probeConfig.Chicken ?? _config.Chicken);

        rusheeClient.Game.CleanupCursorItem();
        rusherClient.Game.CleanupCursorItem();

        // Both clients accept whatever invite arrives, so a person joining can party with either one.
        AcceptPartyInvites(rusheeClient);
        AcceptPartyInvites(rusherClient);

        _currentGameName = gameName;
        if (!await EnsurePartied(rusherClient, rusheeClient))
        {
            return false;
        }

        KeepInvitingNewcomers(rusherClient);
        return true;
    }

    /// <summary>
    /// Puts the two clients in one party and confirms it happened.
    /// </summary>
    /// <remarks>
    /// A party does not survive leaving the game, so this runs again after every rejoin. Verifying is the
    /// point: an invite that is never accepted looks identical to success from the sender's side, and a
    /// run that assumed it had worked killed Andariel for nobody.
    /// </remarks>
    /// <param name="rusheeInvites">
    /// Which side sends. Normally the rusher invites, but a rusher rejoining an existing game has to be
    /// invited into the party the rushees are already in, which may hold several of them.
    /// </param>
    private async Task<bool> EnsurePartied(Client rusherClient, Client rusheeClient, bool rusheeInvites = false)
    {
        var rusheeAsSeenByRusher = rusherClient.Game.Players.Find(p => p.Name.Equals(_probeCharacter.Name, StringComparison.OrdinalIgnoreCase));
        if (rusheeAsSeenByRusher == null)
        {
            Log.Error("Rusher cannot see the rushee in game {GameName}", _currentGameName);
            return false;
        }

        // Invite until the party actually exists. Quest credit needs a shared party, and an invite
        // that is never accepted looks identical to success from the sender's side: a run that
        // assumed it worked killed Andariel for nobody.
        var rusherAsSeenByRusheeForParty = rusheeClient.Game.Players
            .Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));

        var partied = await GeneralHelpers.TryWithTimeout(async (attempt) =>
        {
            // Resolved again each round: looking it up once meant a rusher the rushee could not see yet
            // stayed null for the whole loop, taking the other invite direction with it.
            rusherAsSeenByRusheeForParty ??= rusheeClient.Game.Players
                .Find(p => p.Name.Equals(_probeConfig.RusherCharacter, StringComparison.OrdinalIgnoreCase));

            if (rusherClient.Game.IsInPartyWith(rusheeAsSeenByRusher)
                || (rusherAsSeenByRusheeForParty != null && rusheeClient.Game.IsInPartyWith(rusherAsSeenByRusheeForParty)))
            {
                return true;
            }

            // Lead with the side that should invite, but fall back to inviting from both after a couple
            // of seconds. A one sided invite stalls for ever whenever the acceptance does not come back
            // as an AssignPlayerToParty, and that is not rare: a rushee once accepted twenty two invites
            // in a row while both party ids stayed empty.
            if (attempt % 4 == 0)
            {
                var bothDirections = attempt >= 8;

                if ((rusheeInvites || bothDirections) && rusherAsSeenByRusheeForParty != null)
                {
                    rusheeClient.Game.InvitePlayer(rusherAsSeenByRusheeForParty);
                }

                if (!rusheeInvites || bothDirections)
                {
                    rusherClient.Game.InvitePlayer(rusheeAsSeenByRusher);
                }
            }

            await Task.Delay(250);
            return false;
        }, TimeSpan.FromSeconds(25));

        if (!partied)
        {
            Log.Error("Rusher and rushee never ended up in the same party, party ids {Rusher} and {Rushee}",
                rusherClient.Game.Me.PartyId, rusheeAsSeenByRusher.PartyId);
            return false;
        }

        Log.Information("Both clients are in game {GameName} and partied as {PartyId}",
            _currentGameName, rusherClient.Game.Me.PartyId);
        return true;
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

            var inviter = client.Game.Players.Find(p => p.Id == notification.EntityId);
            if (inviter != null)
            {
                Log.Information("{Character} accepting a party invite from {Inviter}", client.Game.Me?.Name, inviter.Name);
                client.Game.AcceptInvite(inviter);
            }
        });
    }

    /// <summary>
    /// Keeps inviting anyone who turns up for as long as the rusher is in the game.
    /// </summary>
    /// <remarks>
    /// A person watching a run joins partway through, long after the one invite sweep at the start. Party
    /// membership is also what quest credit needs, so a late joiner that is never invited both cannot see
    /// what the bot is doing on the automap and gets nothing from the kills.
    /// </remarks>
    private static void KeepInvitingNewcomers(Client rusherClient)
    {
        _ = Task.Run(async () =>
        {
            while (rusherClient.Game.IsInGame())
            {
                try
                {
                    await InviteEveryoneToParty(rusherClient);
                }
                catch (Exception e)
                {
                    Log.Debug(e, "Invite sweep failed, trying again shortly");
                }

                await Task.Delay(5000);
            }
        });
    }

    /// <summary>
    /// Invites everyone in the game who is not already in the party, which includes people who joined
    /// to watch. A rush has to do this anyway: the design allows manually played characters alongside
    /// the bot driven ones, and they need the same party membership to get quest credit.
    /// </summary>
    private static async Task InviteEveryoneToParty(Client rusherClient)
    {
        foreach (var player in rusherClient.Game.Players.Where(p => p.Id != rusherClient.Game.Me.Id))
        {
            if (rusherClient.Game.IsInPartyWith(player))
            {
                continue;
            }

            Log.Information("Inviting {Player} to the party", player.Name);
            rusherClient.Game.InvitePlayer(player);
            await Task.Delay(500);
        }
    }

    private async Task<bool> WaitForDeath(Client rusheeClient, Area deathArea)
    {
        Log.Information("Waiting for the rushee to be killed in {Area}", deathArea);
        var timer = Stopwatch.StartNew();
        while (rusheeClient.Game.Me.Life > 0 && timer.Elapsed < TimeSpan.FromSeconds(_probeConfig.DeathTimeoutSeconds))
        {
            await Task.Delay(500);
        }

        if (rusheeClient.Game.Me.Life > 0)
        {
            Log.Warning("Rushee survived {Seconds} seconds in {Area}, so there is nothing to recover from",
                _probeConfig.DeathTimeoutSeconds, deathArea);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Has the rusher kill whatever is standing around the corpse, so the rushee can come back for it
    /// without dying again on arrival.
    /// </summary>
    private async Task ClearAroundCorpse(Client rusherClient, Point corpseLocation)
    {
        Log.Information("Rusher clearing around the corpse at {Location}", corpseLocation);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            var monsters = NPCHelpers.GetNearbyNPCs(rusherClient, corpseLocation, 10, 20).ToList();
            if (monsters.Count == 0)
            {
                Log.Information("Nothing left within 20 of the corpse");
                return;
            }

            await MoveToLocation(rusherClient, corpseLocation);
            foreach (var monster in monsters)
            {
                await _attackService.AssistPlayer(rusherClient, rusherClient.Game.Me);
                if (await IsMonsterDead(rusherClient, monster))
                {
                    continue;
                }
            }

            await Task.Delay(200);
        }

        Log.Warning("Gave up clearing around the corpse, the rushee may die again on the way in");
    }

    private static Task<bool> IsMonsterDead(Client client, WorldObject monster)
    {
        var current = client.Game.WorldObjects.GetValueOrDefault((monster.Id, EntityType.NPC));
        return Task.FromResult(current == null || current.State == EntityState.Dead || current.State == EntityState.Dieing);
    }

    /// <summary>
    /// Walks or teleports towards the nearest monster, so that a test which needs a character to be
    /// attacked does not sit in an empty corner waiting for one to wander past.
    /// </summary>
    private async Task<bool> MoveTowardsMonsters(Client client, int searchDistance)
    {
        var monster = NPCHelpers.GetNearbyNPCs(client, client.Game.Me.Location, 1, searchDistance).FirstOrDefault();
        if (monster == null)
        {
            Log.Warning("{Character} found no monsters within {Distance}", client.Game.Me.Name, searchDistance);
            return false;
        }

        Log.Information("{Character} moving towards {Monster} at {Location}",
            client.Game.Me.Name, monster.NPCCode, monster.Location);
        await MoveToLocation(client, monster.Location);
        return true;
    }

    private async Task MoveToLocation(Client client, Point location)
    {
        var movementMode = client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
        if (client.Game.Me.Location.Distance(location) <= 10)
        {
            return;
        }

        var path = await _pathingService.GetPathToLocation(client.Game, location, movementMode);
        await MovementHelpers.TakePathOfLocations(client.Game, path, movementMode);
    }

    private async Task<bool> EnterGameWithProbeCharacter(Client client)
    {
        if (_probeCharacter == null)
        {
            Log.Error("No character created in this run to play with, add the create step first");
            return false;
        }

        await client.SelectCharacter(_probeCharacter);

        // CreateGame joins the game it made, so joining again would write to the realm connection it
        // just tore down on the way to the game server.
        var gameName = NextGameName();
        if (!await client.CreateGame(_config.Difficulty, gameName, _config.GamePassword, _config.GameDescriptions[0]))
        {
            Log.Error("Creating game {GameName} failed", gameName);
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((_) => client.Game.Me != null, TimeSpan.FromSeconds(5)))
        {
            Log.Error("Joining game {GameName} did not initialize the character", gameName);
            return false;
        }

        client.Game.RequestUpdate(client.Game.Me.Id);
        if (!GeneralHelpers.TryWithTimeout(
            (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
            TimeSpan.FromSeconds(5)))
        {
            Log.Error("Character location never initialized");
            return false;
        }

        Log.Information("In game {GameName} as {Character} in {Area} at {Location}",
            gameName, _probeCharacter.Name, client.Game.Area, client.Game.Me.Location);
        return true;
    }

    /// <summary>
    /// Deletes the characters this run created, refusing unless the account is explicitly listed as
    /// deletable. Deletion has no undo, so it is gated on both the account and on the character
    /// having been created by this run.
    /// </summary>
    private async Task CleanUp(Client client)
    {
        if (_createdCharacters.Count == 0 || !_probeConfig.CleanUpCreatedCharacters || !IsDeletionAllowed())
        {
            return;
        }

        if (client.Game.IsInGame())
        {
            await client.Game.LeaveGame();
        }

        if (!client.IsRealmConnected())
        {
            // Playing a game drops the realm connection, so there is nothing to delete over. The
            // cleanup step of the next run picks these up by name instead.
            Log.Information("Realm connection is gone, leaving {Characters} for the next run's cleanup step",
                string.Join(", ", _createdCharacters));
            return;
        }

        foreach (var name in _createdCharacters.ToList())
        {
            if (!IsProbeOwnedName(name))
            {
                Log.Warning("Refusing to delete {Name}: it is not one of the configured probe names", name);
                continue;
            }

            await client.DeleteCharacter(name);
        }
    }

    private bool IsProbeOwnedName(Character character) => IsProbeOwnedName(character.Name);

    /// <summary>
    /// Whether a name is one the probe is allowed to create and destroy. Anything else is off limits,
    /// however much it looks like a test character.
    /// </summary>
    private bool IsProbeOwnedName(string name)
        => _probeConfig.CharacterNames?.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Deletion has no undo, so it is refused unless the account is explicitly listed as deletable.
    /// </summary>
    private bool IsDeletionAllowed()
    {
        if (_probeConfig.DeletableAccounts.Any(a => a.Equals(_probeConfig.Username, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        Log.Warning("Deleting characters on {Account} is not allowed, add it to deletableAccounts to enable it",
            _probeConfig.Username);
        return false;
    }
}
