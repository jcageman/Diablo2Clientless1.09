using ConsoleBot.Helpers;
using ConsoleBot.Escape;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Quest;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Rush;

/// <summary>
/// The side quests, done for several characters at once. A quest only runs in a game whose creator still
/// needs it: the server switches a quest off for the whole game when its creator has finished it (D2MOO
/// A3Q4.cpp, PlayerStartedGame). So each game does the quests its creator needs, everyone who needs them
/// too gets them through the party, and the next game is created by someone with quests left.
/// </summary>
public sealed partial class RushBot
{
    private const int MaxPlayersInGame = 8;
    private const int SideQuestTries = 3;
    private const int MaxSideWorkGames = 12;
    private const uint MeshifTakesFigurine = 0x211;
    private const uint AkaraRewardsDen = 0x4C;
    private const int DenSweepSpacing = 25;
    private const double DenSweepClearRadius = 30;
    private static readonly TimeSpan DenClearLimit = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan DenFirstKillLimit = TimeSpan.FromSeconds(90);
    private const double FirstKillSearchRadius = 120;
    private const uint AlkorTakesBird = 0x216;
    private const uint AlkorGivesPotion = 0x21A;
    private static readonly TimeSpan UniqueSearchLimit = TimeSpan.FromMinutes(3);

    /// <summary>The act 3 levels searched for the figurine's unique, nearest the docks first.</summary>
    private static readonly Area[] GoldenBirdSearchRoute = [Area.SpiderForest, Area.GreatMarsh, Area.FlayerJungle];

    /// <summary>
    /// The order a game works through its creator's quests, and the quest word each one is read from. The
    /// Golden Bird comes before anything else in act 3: the figurine goes to the first unique that comes into
    /// being there, and a trip to the Ruined Temple first could spend it.
    /// </summary>
    private static readonly (RushSideQuest Quest, QuestId Id)[] AllSideQuests =
    [
        (RushSideQuest.DenOfEvil, QuestId.DenOfEvil),
        (RushSideQuest.Cain, QuestId.TheSearchForCain),
        (RushSideQuest.CharsiImbue, QuestId.ToolsOfTheTrade),
        (RushSideQuest.Radament, QuestId.RadamentsLair),
        (RushSideQuest.GoldenBird, QuestId.TheGoldenBird),
        (RushSideQuest.LamEsen, QuestId.LamEsensTome),
        (RushSideQuest.Izual, QuestId.TheFallenAngel),
        (RushSideQuest.Shenk, QuestId.SiegeOnHarrogath),
        (RushSideQuest.Anya, QuestId.PrisonOfIce),
        (RushSideQuest.Ancients, QuestId.RiteOfPassage),
    ];

    private IEnumerable<(RushSideQuest Quest, QuestId Id)> SideQuestOrder =>
        AllSideQuests.Where(entry => _expansion || entry.Quest is not (RushSideQuest.Shenk or RushSideQuest.Anya or RushSideQuest.Ancients));

    /// <summary>The quest items an NPC takes from the player when a quest message is said.</summary>
    private static readonly HashSet<ItemName> HandedInQuestItems =
        [ItemName.HoradricMalus, ItemName.ScrollofInifuss, ItemName.KeytotheCairnStones, ItemName.AJadeFigurine, ItemName.TheGoldenBird, ItemName.LamEsensTome, ItemName.MalahsPotion];

    /// <summary>
    /// Malah only offers the Scroll of Resistance in a game where Anya has been freed, so a reward pending from
    /// an earlier game waits until the Anya quest has run in this one.
    /// </summary>
    private bool _anyaFreedThisGame;

    /// <summary>Who has been through Anya's reward once this game; once is all it takes when it works at all.</summary>
    private readonly HashSet<string> _anyaRewardTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each character still needed when its last game ended, by name.</summary>
    private readonly Dictionary<string, HashSet<RushSideQuest>> _openSideQuests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Games each character has created for a quest without finishing it, per difficulty.</summary>
    private readonly Dictionary<(string Name, Difficulty Difficulty, RushSideQuest Quest), int> _sideQuestTries = [];

    private bool _waypointsHandedOut;

    /// <summary>
    /// Side work for characters already at the destination: per batch that fits in a game and per
    /// difficulty, games are created by whoever still needs the most, until nobody needs anything left.
    /// </summary>
    private async Task RunSideWork(List<RosterEntry> eligible, List<Client> clients)
    {
        var capacity = MaxPlayersInGame - 1 - (_rushConfiguration.Crew?.Count ?? 0);
        var wanted = _rushConfiguration.QuestDifficulties;
        foreach (var batch in SideWorkBatches(eligible, capacity))
        {
            foreach (var difficulty in Enum.GetValues<Difficulty>().Where(difficulty => wanted == null || wanted.Count == 0 || wanted.Contains(difficulty)))
            {
                var present = batch.Where(entry => entry.Progression.CurrentDifficulty >= difficulty).ToList();
                if (present.Count == 0)
                {
                    continue;
                }

                StopIfNoticed();
                _gameDifficulty = difficulty;
                _openSideQuests.Clear();
                if (_rushConfiguration.GivesWaypoints)
                {
                    // A second game for whatever the first could not collect: every game has its own map, and a
                    // crossing the rusher could not get past in one is usually open in the next.
                    await PrepareRusherWaypoints();
                    await PrepareRusherWaypoints();
                }

                _waypointsHandedOut = !_rushConfiguration.GivesWaypoints;
                for (var game = 0; game < MaxSideWorkGames; game++)
                {
                    StopIfNoticed();
                    var host = game == 0 ? present[0] : NextSideWorkHost(present);
                    if (host == null)
                    {
                        break;
                    }

                    var hostNeeds = _openSideQuests.GetValueOrDefault(host.Character.Name);
                    Log.Information("Side work on {Difficulty}: {Host} creates the game for {Quests}; with {Others}",
                        difficulty, host.Character.Name,
                        hostNeeds == null ? "whatever it needs" : string.Join(", ", hostNeeds),
                        string.Join(", ", present.Where(entry => entry != host).Select(entry => entry.Character.Name)));
                    try
                    {
                        await PlayGame(host, present.Where(entry => entry != host).Prepend(host).ToList(), clients, sideWorkOnly: true);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Leaving because of a stranger can surface as any failure; it must never start another game.
                        StopIfNoticed();
                        Log.Warning(exception, "Side work game created by {Host} failed", host.Character.Name);
                        foreach (var quest in hostNeeds ?? [])
                        {
                            CountSideQuestTry(host.Character.Name, quest);
                        }
                    }
                    finally
                    {
                        await CloseClients(clients);
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                }

                var open = _openSideQuests.Where(entry => entry.Value.Count > 0)
                    .Select(entry => $"{entry.Key}: {string.Join(", ", entry.Value)}").ToList();
                Log.Information("Side work on {Difficulty} done; still open: {Open}", difficulty,
                    open.Count == 0 ? "nothing" : string.Join("; ", open));
            }
        }
    }

    /// <summary>A whisper or a stranger joining ends the side work: no further game is created.</summary>
    private static void StopIfNoticed()
    {
        if (StrangerEscape.StopRequested || StrangerEscape.FleeRequested)
        {
            throw new OperationCanceledException($"Side work stopped: {StrangerEscape.Reason}");
        }
    }

    /// <summary>Splits the characters into games' worth, never two characters of one account together.</summary>
    private static List<List<RosterEntry>> SideWorkBatches(List<RosterEntry> entries, int capacity)
    {
        var batches = new List<List<RosterEntry>>();
        foreach (var entry in entries)
        {
            var batch = batches.FirstOrDefault(candidate => candidate.Count < capacity
                && candidate.All(other => !other.Account.Username.Equals(entry.Account.Username, StringComparison.OrdinalIgnoreCase)));
            if (batch == null)
            {
                batch = [];
                batches.Add(batch);
            }

            batch.Add(entry);
        }

        return batches;
    }

    /// <summary>The character with the most quests left that it may still create a game for, or null when none has.</summary>
    private RosterEntry NextSideWorkHost(List<RosterEntry> present)
    {
        return present
            .Select(entry => (Entry: entry, Open: _openSideQuests.TryGetValue(entry.Character.Name, out var open)
                ? open.Count(quest => !GivenUp(entry.Character.Name, quest))
                : 0))
            .Where(candidate => candidate.Open > 0)
            .OrderByDescending(candidate => candidate.Open)
            .Select(candidate => candidate.Entry)
            .FirstOrDefault();
    }

    private bool GivenUp(string name, RushSideQuest quest) =>
        _sideQuestTries.GetValueOrDefault((name, _gameDifficulty, quest)) >= SideQuestTries;

    private void CountSideQuestTry(string name, RushSideQuest quest) =>
        _sideQuestTries[(name, _gameDifficulty, quest)] = _sideQuestTries.GetValueOrDefault((name, _gameDifficulty, quest)) + 1;

    /// <summary>
    /// A quest is needed until its kill part is done; a reward waiting at the NPC is only collected. Radament is
    /// the exception: his skill point is the Book of Skill, which drops at his death for every credited player
    /// not carrying one, so a character with the reward pending but no book read still has to be at the kill.
    /// </summary>
    private bool NeedsSideQuest(Client client, RushSideQuest quest, QuestId id) =>
        _rushConfiguration.DoesSideQuest(quest)
        && (quest == RushSideQuest.Radament
            ? !HasFinished(client, id) && !_booksRead.Contains(client.Game.Me.Name)
            : !HasKillCredit(client, id));

    /// <summary>
    /// Whether a character can create the game a quest is done in: its own quest state decides whether the
    /// quest runs at all, and one with the reward already pending switches it off.
    /// </summary>
    private bool CanCreateSideQuest(Client client, RushSideQuest quest, QuestId id) =>
        _rushConfiguration.DoesSideQuest(quest) && !HasKillCredit(client, id);

    /// <summary>
    /// Who read a Book of Skill this game. Atma completes Radament's quest whether or not the book was read,
    /// and a completed quest drops no book again, so he is only told once the skill point is had.
    /// </summary>
    private readonly HashSet<string> _booksRead = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every rushee in the current game, needing the quest at hand or not.</summary>
    private List<ParticipantSession> _gameRushees = [];

    /// <summary>
    /// Collects the rewards already earned, then does every quest the game's creator - the first rushee -
    /// still needs, for everyone who needs it, and records what each character still needs afterwards.
    /// </summary>
    private async Task<bool> RunSideQuests(Client rusher, List<ParticipantSession> rushees)
    {
        _anyaFreedThisGame = false;
        _anyaRewardTried.Clear();
        _booksRead.Clear();
        _gameRushees = rushees;
        _sideShoutsUntil = DateTime.MinValue;

        // Everyone fights now, not only the creator: a helper that joined on an empty belt left the game out of
        // potions half way through a fight.
        if (_rushConfiguration.RusheeFights)
        {
            await Task.WhenAll(rushees.Where(session => session.Client.Game.IsInTown())
                .Select(session => RestockFighter(session.Client, session.Entry.Account, session.Client.Game.Act)));
        }

        // Scrolls of Resistance that will not read are junk at 2x2 each - the server hands one out on every ask
        // while Anya's reward is pending, and one character carried eight. Read what reads, drop the rest.
        await Task.WhenAll(rushees.Select(session => DropUnreadableScrolls(session.Client)));

        await RefreshQuestData(rushees);
        await CollectSideQuestRewards(rushees);

        var host = rushees[0];
        var hostName = host.Client.Game.Me.Name;
        foreach (var (quest, id) in SideQuestOrder)
        {
            if (!CanCreateSideQuest(host.Client, quest, id) || GivenUp(hostName, quest))
            {
                continue;
            }

            await RejoinIfChickened(rusher);
            foreach (var session in rushees)
            {
                await RejoinIfChickened(session.Client);
            }

            if (!host.Client.Game.IsInGame())
            {
                Log.Warning("{Host} is out of its game and could not come back; the rest of its quests wait for another game", hostName);
                break;
            }

            var needers = rushees.Where(session => NeedsSideQuest(session.Client, quest, id)).ToList();
            if (_rushConfiguration.RusheeFights && host.Client.Game.IsInTown())
            {
                await RestockFighter(host.Client, host.Entry.Account, host.Client.Game.Act);
            }

            Log.Information("Side quest {Quest} with {Host} as creator and killer, for {Needers}", quest, hostName,
                string.Join(", ", needers.Select(session => session.Client.Game.Me.Name)));
            CountSideQuestTry(hostName, quest);
            var done = await SideQuest(quest.ToString(), host.Client, () => quest switch
            {
                RushSideQuest.DenOfEvil => DenOfEvil(rusher, host.Client, needers),
                RushSideQuest.GoldenBird => GoldenBird(rusher, host.Client, needers),
                RushSideQuest.CharsiImbue => HoradricMalus(rusher, host.Client, needers),
                RushSideQuest.Radament => Radament(rusher, host.Client, needers),
                RushSideQuest.LamEsen => LamEsensTome(rusher, host.Client, needers),
                RushSideQuest.Izual => Izual(rusher, host.Client, needers),
                RushSideQuest.Shenk => Shenk(rusher, host.Client, needers),
                RushSideQuest.Anya => Anya(rusher, host.Client, needers),
                RushSideQuest.Ancients => Ancients(rusher, host.Client, needers),
                RushSideQuest.Cain => Cain(rusher, host.Client, needers),
                _ => Task.FromResult(false),
            });

            await RefreshQuestData(rushees);
            await CollectSideQuestRewards(rushees);
            Log.Information("Side quest {Quest} {Outcome}: {States}", quest, done ? "done" : "not done",
                string.Join(", ", needers.Select(session => $"{session.Client.Game.Me.Name} 0x{session.Client.Game.Quests.GetCharacterFlags(id):X4}")));
        }

        await RefreshQuestData(rushees);
        foreach (var session in rushees)
        {
            _openSideQuests[session.Client.Game.Me.Name] = SideQuestOrder
                .Where(entry => CanCreateSideQuest(session.Client, entry.Quest, entry.Id))
                .Select(entry => entry.Quest)
                .ToHashSet();
        }

        Log.Information("Side quests still open on {Difficulty}: {Open}", _gameDifficulty,
            string.Join("; ", rushees.Select(session => $"{session.Client.Game.Me.Name}: "
                + (_openSideQuests[session.Client.Game.Me.Name].Count == 0 ? "none" : string.Join(", ", _openSideQuests[session.Client.Game.Me.Name])))));
        return true;
    }

    /// <summary>
    /// Rewards earned in an earlier game or through the party wait at the NPC; any game will do to collect
    /// them, so every character does so at once, in parallel.
    /// </summary>
    private async Task CollectSideQuestRewards(List<ParticipantSession> rushees)
    {
        await Task.WhenAll(rushees.Select(async session =>
        {
            var client = session.Client;
            try
            {
                if (_rushConfiguration.DoesSideQuest(RushSideQuest.DenOfEvil)
                    && !HasFinished(client, QuestId.DenOfEvil) && client.Game.Quests.IsAwaitingReward(QuestId.DenOfEvil))
                {
                    await CollectDenReward(client);
                }

                if (_rushConfiguration.DoesSideQuest(RushSideQuest.Cain)
                    && !HasFinished(client, QuestId.TheSearchForCain) && client.Game.Quests.IsAwaitingReward(QuestId.TheSearchForCain))
                {
                    // Akara's reward is a ring made straight into the inventory.
                    await MakeRoom(client, 1, 1);
                    await CollectReward(client, Act.Act1, NPCCode.Akara, AkaraRewardsCain, QuestId.TheSearchForCain);
                }

                if (_rushConfiguration.DoesSideQuest(RushSideQuest.GoldenBird)
                    && !HasFinished(client, QuestId.TheGoldenBird)
                    && (client.Game.Quests.IsAwaitingReward(QuestId.TheGoldenBird)
                        || Holds(client, ItemName.AJadeFigurine) || Holds(client, ItemName.TheGoldenBird)))
                {
                    await TurnInGoldenBird(client);
                }

                if (_rushConfiguration.DoesSideQuest(RushSideQuest.Radament) && _booksRead.Contains(client.Game.Me.Name)
                    && !HasFinished(client, QuestId.RadamentsLair) && client.Game.Quests.IsAwaitingReward(QuestId.RadamentsLair))
                {
                    await CollectReward(client, Act.Act2, NPCCode.Atma, AtmaRewardsRadament, QuestId.RadamentsLair);
                }

                if (_rushConfiguration.DoesSideQuest(RushSideQuest.Izual)
                    && !HasFinished(client, QuestId.TheFallenAngel) && client.Game.Quests.IsAwaitingReward(QuestId.TheFallenAngel))
                {
                    await CollectReward(client, Act.Act4, NPCCode.TyraelAct4, TyraelRewardsIzual, QuestId.TheFallenAngel);
                }

                // A Scroll of Resistance from an earlier game was never read: its resistances come with reading it,
                // in any game.
                if (_expansion && _rushConfiguration.DoesSideQuest(RushSideQuest.Anya))
                {
                    await ReadScrollOfResistance(client);
                }

                if (_expansion && _anyaFreedThisGame && _anyaRewardTried.Add(client.Game.Me.Name)
                    && _rushConfiguration.DoesSideQuest(RushSideQuest.Anya)
                    && !HasFinished(client, QuestId.PrisonOfIce) && client.Game.Quests.IsAwaitingReward(QuestId.PrisonOfIce))
                {
                    await CollectAnyaReward(client);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Warning(exception, "Collecting side quest rewards failed for {Client}", client.Game.Me.Name);
            }
        }));
    }

    /// <summary>
    /// Den of Evil completes when every monster in the Den is dead. The credit goes to the players who
    /// killed one of them and spreads to their party members standing in act 1 (D2MOO A1Q1.cpp,
    /// MonsterKilled and SetPrimaryGoalDoneForPartyMembers). So the creator lands the first kill alone,
    /// and only then does the rusher come in and clear the rest, faster than any walker could.
    /// </summary>
    private async Task<bool> DenOfEvil(Client rusher, Client killer, List<ParticipantSession> needers)
    {
        var name = killer.Game.Me.Name;
        if (!_rushConfiguration.RusheeFights)
        {
            Log.Information("Skipping the Den of Evil: its first kill must be {Killer}'s own and rusheeFights is off", name);
            return false;
        }

        await BuffBeforeFight(rusher, Act.Act1, _gameRushees.Select(session => session.Client));
        var inActOne = await Task.WhenAll(needers.Select(session => MoveRusheeToAct(session.Client, Act.Act1)));
        if (!inActOne.All(moved => moved)
            || (rusher.Game.Act != Act.Act1 && !await _townManagementService.SwitchAct(rusher, Act.Act1))
            || (!rusher.Game.IsInTown() && !await ReturnToTown(rusher))
            || !await TraverseTo(rusher, Area.DenOfEvil)
            || !await _townManagementService.CreateTownPortal(rusher)
            || !await _townManagementService.TakeTownPortalToTown(rusher)
            || !await FerryRushee(rusher, killer, Area.DenOfEvil))
        {
            Log.Warning("{Killer} could not be brought into the Den of Evil", name);
            return false;
        }

        var arrival = killer.Game.Me.Location;
        var map = await _mapApiService.GetArea(killer.Game.MapId, Difficulty.Normal, Area.DenOfEvil);
        var sweep = DenSweepPoints(map);
        var firstKill = await LandFirstKill(killer, sweep, DenFirstKillLimit);
        Log.Information("Den of Evil: {Killer} {Outcome} the first kill", name, firstKill ? "landed" : "did NOT land");
        if (!firstKill)
        {
            await RusheeHome(rusher, killer, arrival);
            return false;
        }

        // Now the rusher may help. The creator stays and sweeps too: the Den is in act 1, so the party share
        // still reaches everyone, and its damage covers what the rusher's element cannot.
        if (!await TraverseTo(rusher, Area.DenOfEvil))
        {
            Log.Warning("The rusher could not get back into the Den of Evil to clear it");
            await RusheeHome(rusher, killer, arrival);
            return false;
        }

        // After the first kill everyone may help, through the rusher's portal from the start. The Den is in act 1,
        // so whoever sweeps still counts for the party share.
        var helpers = _gameRushees.Select(session => session.Client).Where(client => client != killer && client.Game.IsInGame()).ToList();
        var came = await Task.WhenAll(helpers.Select(helper => FerryRushee(rusher, helper, Area.DenOfEvil)));
        helpers = helpers.Where((_, index) => came[index]).ToList();
        var credited = await SweepDen([rusher, killer, .. helpers], killer, sweep, map);
        await Task.WhenAll(helpers.Append(killer).Select(sweeper => RusheeHome(rusher, sweeper, arrival)));
        Log.Information("Den of Evil cleared after {Killer}'s first kill: credit {Outcome} ({States})", name,
            credited ? "received" : "NOT received",
            string.Join(", ", needers.Select(session => $"{session.Client.Game.Me.Name} 0x{session.Client.Game.Quests.GetCharacterFlags(QuestId.DenOfEvil):X4}")));
        await ReturnToTown(rusher);
        return credited;
    }

    private async Task CollectReward(Client client, Act act, NPCCode npc, uint message, QuestId id)
    {
        if (!await MoveRusheeToAct(client, act) || !client.Game.IsInTown())
        {
            Log.Warning("{Client} could not reach {Npc} for the {Quest} reward", client.Game.Me.Name, npc, id);
            return;
        }

        await SayToNpc(client, npc, message);
        client.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(2));
        Log.Information("{Client} {Outcome} {Quest}: 0x{Flags:X4}", client.Game.Me.Name,
            HasFinished(client, id) ? "finished" : "did not finish", id, client.Game.Quests.GetCharacterFlags(id));
    }

    private async Task CollectDenReward(Client client)
    {
        if (!await MoveRusheeToAct(client, Act.Act1) || !client.Game.IsInTown())
        {
            Log.Warning("{Client} could not reach Akara for the Den of Evil reward", client.Game.Me.Name);
            return;
        }

        await SayToNpc(client, NPCCode.Akara, AkaraRewardsDen);
        client.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(2));
        Log.Information("{Client} {Outcome} the Den of Evil: 0x{Flags:X4}", client.Game.Me.Name,
            HasFinished(client, QuestId.DenOfEvil) ? "finished" : "did not finish", client.Game.Quests.GetCharacterFlags(QuestId.DenOfEvil));
    }

    private static List<Point> DenSweepPoints(AreaMap map)
    {
        var points = new List<Point>();
        for (var row = DenSweepSpacing / 2; row < map.Map.Length; row += DenSweepSpacing)
        {
            for (var column = DenSweepSpacing / 2; column < map.Map[row].Length; column += DenSweepSpacing)
            {
                var x = map.LevelOrigin.X + column;
                var y = map.LevelOrigin.Y + row;
                if (map.IsWalkableWorldPoint(x, y))
                {
                    points.Add(new Point((ushort)x, (ushort)y));
                }
            }
        }

        return points;
    }

    /// <summary>
    /// The killer attacks the nearest monster until one dies, walking the sweep points to find one. Nobody
    /// else is in the level, so the first death is the killer's own (or its hireling's, which counts for it).
    /// </summary>
    private async Task<bool> LandFirstKill(Client killer, List<Point> sweep, TimeSpan limit)
    {
        var mode = GetMovementMode(killer);
        var remaining = sweep.ToList();
        var started = DateTime.Now;
        while (DateTime.Now - started < limit)
        {
            if (killer.Game.WorldObjects.Where(entry => entry.Key.Item2 == EntityType.NPC).Select(entry => entry.Value).Any(npc => !NPCHelpers.IsFriendly(npc.NPCCode)
                && npc.State is EntityState.Dead or EntityState.Dieing))
            {
                return true;
            }

            // Any monster the client knows of, not just one in reach: a walking amazon wandered the sweep points for
            // a minute without ever coming within 40 of one.
            var target = NearbyHostiles(killer, FirstKillSearchRadius).FirstOrDefault();
            if (target != null)
            {
                if (target.Location.Distance(killer.Game.Me.Location) > 25)
                {
                    await MoveTo(killer, killer.Game.Me.Location.GetPointBeforePointInSameDirection(target.Location, 15), mode);
                    continue;
                }

                await AttackUpClose(killer, target);
                await Task.Delay(100);
                continue;
            }

            if (remaining.Count == 0)
            {
                return false;
            }

            var here = killer.Game.Me.Location;
            var next = remaining.OrderBy(point => point.Distance(here)).First();
            remaining.Remove(next);
            await MoveTo(killer, next, mode);
        }

        return false;
    }

    /// <summary>
    /// Every sweeper takes the nearest sweep point nobody has taken yet and clears around it, then they hunt
    /// whatever was seen in the Den but is not dead, then - still without credit - they go over every point
    /// once more. Two sweepers bring two kinds of damage: a cold sorceress alone left the cold immune
    /// behind and the Nightmare Den never completed.
    /// </summary>
    private async Task<bool> SweepDen(List<Client> sweepers, Client creator, List<Point> sweep, AreaMap map)
    {
        var points = sweep.ToList();
        var gate = new object();
        var passes = 1;
        var hunted = new Dictionary<uint, int>();
        var started = DateTime.Now;
        var credited = false;
        Log.Information("Sweeping the Den of Evil over {Count} points with {Sweepers}", points.Count,
            string.Join(", ", sweepers.Select(sweeper => sweeper.Game.Me.Name)));

        bool InDen(Point point) =>
            point.X >= map.LevelOrigin.X && point.Y >= map.LevelOrigin.Y
            && point.Y < map.LevelOrigin.Y + map.Map.Length && point.X < map.LevelOrigin.X + map.Map[0].Length;

        async Task Sweep(Client sweeper)
        {
            var mode = GetMovementMode(sweeper);
            while (!credited && DateTime.Now - started < DenClearLimit && sweeper.Game.IsInGame())
            {
                Point next = null;
                lock (gate)
                {
                    var here = sweeper.Game.Me.Location;
                    if (points.Count == 0)
                    {
                        var straggler = sweeper.Game.WorldObjects
                            .Where(entry => entry.Key.Item2 == EntityType.NPC).Select(entry => entry.Value)
                            .Where(npc => !NPCHelpers.IsFriendly(npc.NPCCode) && InDen(npc.Location)
                                && npc.State is not EntityState.Dead and not EntityState.Dieing
                                && hunted.GetValueOrDefault(npc.Id) < 2)
                            .OrderBy(npc => npc.Location.Distance(here))
                            .FirstOrDefault();
                        if (straggler != null)
                        {
                            hunted[straggler.Id] = hunted.GetValueOrDefault(straggler.Id) + 1;
                            next = straggler.Location;
                        }
                        else if (passes < 2)
                        {
                            passes++;
                            hunted.Clear();
                            points = sweep.ToList();
                            Log.Information("Den sweep: no credit after the first pass; going over the Den again");
                        }
                    }

                    if (next == null && points.Count > 0)
                    {
                        next = points.OrderBy(point => point.Distance(here)).First();
                        points.Remove(next);
                    }
                }

                if (next == null)
                {
                    return;
                }

                await MoveTo(sweeper, next, mode);
                await ClearUpClose(sweeper, DenSweepClearRadius, TimeSpan.FromSeconds(20));
                var reached = sweeper.Game.Me.Location;
                lock (gate)
                {
                    points.RemoveAll(point => point.Distance(reached) < DenSweepSpacing * 0.8);
                }
            }
        }

        var sweeping = Task.WhenAll(sweepers.Select(Sweep));
        while (!sweeping.IsCompleted)
        {
            creator.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
            credited = HasKillCredit(creator, QuestId.DenOfEvil);
            await Task.WhenAny(sweeping, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        await sweeping;
        creator.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
        var done = HasKillCredit(creator, QuestId.DenOfEvil);
        if (!done)
        {
            var left = sweepers.SelectMany(sweeper => sweeper.Game.WorldObjects
                    .Where(entry => entry.Key.Item2 == EntityType.NPC).Select(entry => entry.Value))
                .Where(npc => !NPCHelpers.IsFriendly(npc.NPCCode) && InDen(npc.Location)
                    && npc.State is not EntityState.Dead and not EntityState.Dieing)
                .GroupBy(npc => npc.Id).Select(group => group.First())
                .Select(npc => $"{npc.NPCCode} {npc.Id} at {npc.Location} ({npc.State})")
                .ToList();
            Log.Information("Den sweep ended without credit; still alive as far as the sweepers know: {Left}",
                left.Count == 0 ? "nothing" : string.Join("; ", left));
        }

        return done;
    }

    /// <summary>Like ClearAround, but anyone who is not a sorceress closes in on each target first.</summary>
    private async Task ClearUpClose(Client client, double radius, TimeSpan limit)
    {
        var started = DateTime.Now;
        while (DateTime.Now - started < limit && client.Game.IsInGame())
        {
            var target = NearbyHostiles(client, radius).FirstOrDefault();
            if (target == null)
            {
                return;
            }

            await AttackUpClose(client, target);
            await Task.Delay(100);
        }
    }

    /// <summary>A side quest that goes wrong is logged and the next one tried; none of them is the rush.</summary>
    private async Task<bool> SideQuest(string step, Client rushee, Func<Task<bool>> quest)
    {
        try
        {
            return await Timed($"{step}-{rushee.Game.Me.Name}", quest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Warning(exception, "Side quest {Step} failed for {Rushee}", step, rushee.Game.Me.Name);
            await ReturnToTown(rushee);
            return false;
        }
    }

    /// <summary>
    /// The Jade Figurine drops from the first unique monster that comes into being in act 3, and only when
    /// its killer - a hireling counts as its owner - has not finished the quest (D2MOO
    /// QUESTS_SetJadeFigurineBoss and A3Q4.cpp). That first unique can come into being out of sight, so the
    /// creator kills every unique it meets until the figurine drops. Handing the bird to Alkor sets the
    /// reward pending for every party member, who then each collect their own potion.
    /// </summary>
    private async Task<bool> GoldenBird(Client rusher, Client killer, List<ParticipantSession> needers)
    {
        var name = killer.Game.Me.Name;
        if (!_rushConfiguration.RusheeFights)
        {
            Log.Information("Skipping the Golden Bird: only {Killer}'s own kill drops the figurine and rusheeFights is off", name);
            return false;
        }

        // The creator goes alone and first: anyone arriving in the jungle before it brings rooms - and the
        // figurine's unique - into being where it cannot see them.
        if (!await MoveRusheeToAct(killer, Act.Act3)
            || !await TakeWaypointFromTown(killer, Waypoint.SpiderForest)
            || !await ConfirmInArea(killer, GoldenBirdSearchRoute[0]))
        {
            Log.Warning("{Killer} could not reach {Area} for the Golden Bird", name, GoldenBirdSearchRoute[0]);
            await ReturnToTown(killer);
            return false;
        }

        // Everyone else who needs the bird comes through the creator's own portal and fights beside it:
        // whichever of them lands the kill, the figurine drops, and one character alone could not kill a
        // Nightmare fetish unique in a minute. Nobody who has finished the quest comes near.
        var portalSpot = killer.Game.Me.Location;
        var helpers = new List<Client>();
        var others = needers.Select(session => session.Client).Where(client => client != killer).ToList();
        if (others.Count > 0 && await _townManagementService.CreateTownPortal(killer))
        {
            var came = await Task.WhenAll(others.Select(async helper =>
                await MoveRusheeToAct(helper, Act.Act3) && await FerryRushee(killer, helper, GoldenBirdSearchRoute[0])));
            helpers = others.Where((_, index) => came[index]).ToList();
        }

        using var following = new System.Threading.CancellationTokenSource();
        var follow = FollowLeader(killer, helpers, following.Token);
        var hunt = new UniqueHunt { Deadline = DateTime.Now + UniqueSearchLimit };
        var killLimit = TimeSpan.FromSeconds(_rushConfiguration.RusheeKillLimitSeconds);
        Item figurine = null;
        while (figurine == null)
        {
            var unique = await FindUnique(killer, hunt);
            if (unique == null)
            {
                Log.Warning("{Killer} found no unique monster that dropped the figurine ({Tried} tried)", name, hunt.Tried.Count);
                await following.CancelAsync();
                await follow;
                await Task.WhenAll(helpers.Select(helper => RusheeHome(killer, helper, portalSpot)));
                await ReturnToTown(killer);
                return false;
            }

            hunt.Tried.Add(unique.Id);
            Log.Information("{Killer} found the unique {Npc} at {Location} for the Golden Bird", name, unique.NPCCode, unique.Location);
            var killed = await GeneralHelpers.TryWithTimeout(async _ =>
            {
                if (unique.State is EntityState.Dead or EntityState.Dieing)
                {
                    return true;
                }

                await Task.WhenAll(helpers.Select(helper => AttackSeen(helper, unique.Id)).Append(AttackUpClose(killer, unique)));
                return unique.State is EntityState.Dead or EntityState.Dieing;
            }, killLimit);
            if (!killed)
            {
                Log.Information("{Killer} could not kill {Npc} within {Seconds}s; the Golden Bird stays open", name, unique.NPCCode, killLimit.TotalSeconds);
                await following.CancelAsync();
                await follow;
                await Task.WhenAll(helpers.Select(helper => RusheeHome(killer, helper, portalSpot)));
                await ReturnToTown(killer);
                return false;
            }

            GeneralHelpers.TryWithTimeout(_ =>
            {
                figurine = killer.Game.Items.Values.FirstOrDefault(item => item.Ground && item.Name == ItemName.AJadeFigurine);
                return figurine != null;
            }, TimeSpan.FromSeconds(3));
            Log.Information("Golden Bird: {Npc} died to {Killer}; the Jade Figurine {Outcome}", unique.NPCCode, name, figurine == null ? "did NOT drop" : "dropped");
        }

        var picked = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (Holds(killer, ItemName.AJadeFigurine))
            {
                return true;
            }

            if (killer.Game.Me.Location.Distance(figurine.Location) > 2)
            {
                await killer.Game.MoveToAsync(figurine.Location);
                return false;
            }

            await MakeRoom(killer, figurine.Width, figurine.Height);
            killer.Game.PickupItem(figurine);
            await Task.Delay(300);
            killer.Game.CleanupCursorItem();
            return Holds(killer, ItemName.AJadeFigurine);
        }, TimeSpan.FromSeconds(15));
        Log.Information("{Killer} {Outcome} the Jade Figurine", name, picked ? "picked up" : "could not pick up");
        await following.CancelAsync();
        await follow;
        await Task.WhenAll(helpers.Select(helper => RusheeHome(killer, helper, portalSpot)));
        await ReturnToTown(killer);
        if (!picked || !await TurnInGoldenBird(killer))
        {
            return false;
        }

        // The rest of the party now has the reward pending at Alkor.
        await RefreshQuestData(needers);
        await CollectSideQuestRewards(needers.Where(session => session.Client != killer).ToList());
        return true;
    }

    /// <summary>
    /// The town half (1.14d quest word 20): Meshif turns the figurine into the bird (message 0x211), Alkor
    /// takes the bird (0x216) and then hands over the potion of life (0x21A), which is drunk at once. Each
    /// step runs only if the one before it has happened, so a character that stopped half way picks up there.
    /// </summary>
    private async Task<bool> TurnInGoldenBird(Client rushee)
    {
        var name = rushee.Game.Me.Name;
        if (!await MoveRusheeToAct(rushee, Act.Act3) || !rushee.Game.IsInTown())
        {
            Log.Warning("{Rushee} could not reach Kurast Docks to hand in the Golden Bird", name);
            return false;
        }

        if (Holds(rushee, ItemName.AJadeFigurine))
        {
            await SayToNpc(rushee, NPCCode.Meshif2, MeshifTakesFigurine);
            GeneralHelpers.TryWithTimeout(_ => Holds(rushee, ItemName.TheGoldenBird), TimeSpan.FromSeconds(5));
            Log.Information("{Rushee} {Outcome} the Golden Bird from Meshif", name, Holds(rushee, ItemName.TheGoldenBird) ? "got" : "did not get");
        }

        if (Holds(rushee, ItemName.TheGoldenBird))
        {
            await SayToNpc(rushee, NPCCode.Alkor, AlkorTakesBird);
            rushee.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(2));
        }

        if (rushee.Game.Quests.IsAwaitingReward(QuestId.TheGoldenBird))
        {
            await MakeRoom(rushee, 1, 1);
            await SayToNpc(rushee, NPCCode.Alkor, AlkorGivesPotion);
            GeneralHelpers.TryWithTimeout(_ => Holds(rushee, ItemName.PotionofLife), TimeSpan.FromSeconds(5));
        }

        // Drunk shows as the server taking it out of the inventory (item action 5). The client then believes
        // it is on the cursor, where nothing ever follows for a used item, so only the inventory counts.
        var potion = rushee.Game.Inventory.FindItemByName(ItemName.PotionofLife);
        var drunk = false;
        if (potion != null)
        {
            rushee.Game.ActivateInventoryItem(potion);
            drunk = GeneralHelpers.TryWithTimeout(
                _ => rushee.Game.Inventory.FindItemByName(ItemName.PotionofLife) == null,
                TimeSpan.FromSeconds(3));
        }

        rushee.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(2));
        var done = HasFinished(rushee, QuestId.TheGoldenBird);
        Log.Information("{Rushee} {Outcome} the Golden Bird: 0x{Flags:X4}, potion {Potion}", name,
            done ? "finished" : "did not finish", rushee.Game.Quests.GetCharacterFlags(QuestId.TheGoldenBird),
            potion == null ? "not received" : drunk ? "drunk" : "still carried");
        return done;
    }

    /// <summary>
    /// Walks to a town NPC, opens the chat and says one quest message. What the NPC offers is logged
    /// rather than followed: following Tyrael's offers once walked him into a branch that never closed.
    /// </summary>
    private async Task SayToNpc(Client client, NPCCode code, uint message)
    {
        var path = await _pathingService.GetPathToNPC(client.Game, code, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking);
        }

        var npc = await WaitForNpc(client, code);
        if (npc == null)
        {
            Log.Warning("{Client} cannot see {Npc}", client.Game.Me.Name, code);
            return;
        }

        await client.Game.MoveToAsync(npc);
        client.Game.InteractWithNPC(npc);
        await Task.Delay(200);
        client.Game.InitiateEntityChat(npc);
        GeneralHelpers.TryWithTimeout(_ => client.Game.GetOfferedMessages(npc).Contains(message), TimeSpan.FromSeconds(2));
        Log.Information("{Npc} offers {Offered} to {Client}; saying 0x{Message:X4}", code,
            string.Join(", ", client.Game.GetOfferedMessages(npc).Select(offered => $"0x{offered:X4}")), client.Game.Me.Name, message);
        client.Game.ExpectItemsTaken(client.Game.Inventory.Items.Where(item => HandedInQuestItems.Contains(item.Name)));
        client.Game.SendQuestMessage(npc, message);
        await Task.Delay(500);
        client.Game.TerminateEntityChat(npc);
        await Task.Delay(300);
    }

    /// <summary>
    /// Back to town by the character's own tome, or through the rusher's portal where it came in: a character
    /// that has played on its own for a long while can carry an empty tome.
    /// </summary>
    private async Task RusheeHome(Client rusher, Client rushee, Point arrival)
    {
        var scrolls = rushee.Game.Inventory.Items.FirstOrDefault(item => item.Name == ItemName.TomeOfTownPortal)?.Amount ?? 0;
        if (scrolls > 0 && await ReturnToTown(rushee) && rushee.Game.IsInTown())
        {
            return;
        }

        var rusherAsSeen = rushee.Game.Players.FirstOrDefault(player =>
            player.Name.Equals(rusher.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
        await MoveTo(rushee, arrival, GetMovementMode(rushee));
        if (rusherAsSeen == null
            || !await _townManagementService.TakeTownPortalToArea(rushee, rusherAsSeen, rushee.Game.Act.MapTownArea()))
        {
            Log.Warning("{Rushee} could not get back to town", rushee.Game.Me.Name);
        }
    }

    /// <summary>
    /// One attack on a monster the character has to kill itself. The shared attack logic is built for a
    /// party the monsters come to: a hammer paladin only walks closer past 15 units, while his hammers
    /// spiral out to about 5, so on his own he stood hammering the air beside a Thorned Hulk for a minute.
    /// Anyone who is not a sorceress closes in first.
    /// </summary>
    private async Task AttackUpClose(Client client, WorldObject target)
    {
        // Only the paladin and the barbarian go in close; everyone else keeps their distance and shoots or casts.
        // Walking the others up to the Hell Ancients put them in melee that took a third of their life a blow.
        var distance = target.Location.Distance(client.Game.Me.Location);
        var melee = client.Game.Me.Class is CharacterClass.Paladin or CharacterClass.Barbarian;
        if (melee && distance > CloseInRange)
        {
            await client.Game.MoveToAsync(client.Game.Me.Location.GetPointBeforePointInSameDirection(target.Location, 2));
        }
        else if (!melee && distance < KeepAwayRange && distance > 0)
        {
            var me = client.Game.Me.Location;
            var dx = (me.X - target.Location.X) / distance;
            var dy = (me.Y - target.Location.Y) / distance;
            var backOff = new Point((ushort)(target.Location.X + dx * RangedStandoff), (ushort)(target.Location.Y + dy * RangedStandoff));
            await client.Game.MoveToAsync(backOff);
        }

        await _attackService.AssistPlayer(client, client.Game.Me, [target.NPCCode], target.Location);

        // An attack can come back at once - a paladin's hammer hold is one packet - and called in a loop that
        // sent the same packet ten times in a millisecond until the server cut the connection.
        await Task.Delay(AttackPacing);
    }

    private const double CloseInRange = 4;
    private const double KeepAwayRange = 12;
    private const double RangedStandoff = 18;
    private static readonly TimeSpan AttackPacing = TimeSpan.FromMilliseconds(150);

    /// <summary>Keeps the helpers within reach of the leader, fighting whatever comes at them on the way.</summary>
    private async Task FollowLeader(Client leader, List<Client> helpers, System.Threading.CancellationToken token)
    {
        while (!token.IsCancellationRequested && helpers.Count > 0)
        {
            await Task.WhenAll(helpers.Select(async helper =>
            {
                if (helper.Game.Me.Location.Distance(leader.Game.Me.Location) > 12)
                {
                    await MoveTo(helper, leader.Game.Me.Location, GetMovementMode(helper));
                }
            }));
            try
            {
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One attack on the monster with this id, as this client sees it.</summary>
    private async Task AttackSeen(Client client, uint id)
    {
        if (client.Game.WorldObjects.TryGetValue((id, EntityType.NPC), out var target)
            && target.State is not EntityState.Dead and not EntityState.Dieing)
        {
            await AttackUpClose(client, target);
        }
    }

    private sealed class UniqueHunt
    {
        public int Leg { get; set; }
        public DateTime Deadline { get; init; }
        public HashSet<uint> Tried { get; } = [];
    }

    /// <summary>
    /// Walks on along the search route from wherever the hunt got to, until a unique monster not tried yet
    /// is in view. The killer does the walking so that the uniques it meets are ones it reaches first.
    /// </summary>
    private async Task<WorldObject> FindUnique(Client client, UniqueHunt hunt)
    {
        var mode = GetMovementMode(client);
        while (DateTime.Now < hunt.Deadline)
        {
            var unique = VisibleUnique(client, hunt.Tried);
            if (unique != null)
            {
                return unique;
            }

            if (hunt.Leg >= GoldenBirdSearchRoute.Length - 1)
            {
                return null;
            }

            // The route says which level this is; no need to ask the client, whose area lags. The
            // adjacent-area path, not GetPathToArea: act 3's jungles meet at open borders, which the map
            // data lists without exits.
            var here = GoldenBirdSearchRoute[hunt.Leg];
            var next = GoldenBirdSearchRoute[hunt.Leg + 1];
            List<Point> path;
            try
            {
                path = await _pathingService.GetPathToAdjacentArea(
                    client.Game.MapId, Difficulty.Normal, here, client.Game.Me.Location, next, mode);
            }
            catch (InvalidOperationException exception)
            {
                // Not every map strings the jungles in a row: in one the Flayer Jungle opened off the Spider
                // Forest and the Great Marsh was a dead end, and asking for the border failed the whole quest.
                Log.Information("{Client} finds no border from {Here} to {Next} ({Reason}); going round", client.Game.Me.Name, here, next, exception.Message);
                path = [];
            }
            foreach (var point in path)
            {
                unique = VisibleUnique(client, hunt.Tried);
                if (unique != null)
                {
                    return unique;
                }

                if (DateTime.Now > hunt.Deadline)
                {
                    return null;
                }

                await MovementHelpers.TakePathOfLocations(client.Game, [point], mode);
            }

            unique = VisibleUnique(client, hunt.Tried);
            if (unique != null)
            {
                return unique;
            }

            if (!await TraverseTo(client, next))
            {
                return null;
            }

            hunt.Leg++;
        }

        return null;
    }

    private static WorldObject VisibleUnique(Client client, HashSet<uint> tried)
    {
        return client.Game.WorldObjects
            .Where(entry => entry.Key.Item2 == EntityType.NPC)
            .Select(entry => entry.Value)
            .Where(npc => npc.IsBoss && !tried.Contains(npc.Id) && npc.State is not EntityState.Dead and not EntityState.Dieing)
            .OrderBy(npc => npc.Location.Distance(client.Game.Me.Location))
            .FirstOrDefault();
    }
}
