using ConsoleBot.Attack;
using ConsoleBot.Chicken;
using ConsoleBot.Escape;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Core.D2GS.Quest;
using D2NG.Leveling.Plans;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// A party that levels itself from level 1, without help from anything outside it. See
/// docs/leveling-bot.md for the agreed design.
/// </summary>
public sealed partial class LevelingBot(
    IOptions<BotConfiguration> botConfiguration,
    IOptions<LevelingConfiguration> levelingConfiguration,
    IPathingService pathingService,
    IMapApiService mapApiService,
    IAttackService attackService,
    ConsoleBot.Mule.IMuleService muleService) : IBotInstance
{
    private readonly BotConfiguration _bot = botConfiguration.Value;
    private readonly LevelingConfiguration _config = levelingConfiguration.Value;
    private readonly AreaTraveler _traveler = new(pathingService, mapApiService);
    private readonly LevelingCombat _combat = new(pathingService, mapApiService, attackService);
    private LevelingGear _gear;
    private readonly Dictionary<string, D2NG.Core.MCP.CharacterProgression> _progressions = new(StringComparer.OrdinalIgnoreCase);
    private Difficulty _difficulty = Difficulty.Normal;

    private Difficulty GoalDifficulty => _config.Until switch
    {
        LevelingGoal.Hell => Difficulty.Hell,
        LevelingGoal.Nightmare => Difficulty.Nightmare,
        _ => Difficulty.Normal
    };

    /// <summary>The hardest difficulty every member has opened, never past the goal's.</summary>
    private Difficulty PartyDifficulty()
    {
        var lowest = _config.Party.Select(member => _progressions.TryGetValue(member.Character, out var progression)
            ? (progression.HasCompletedHell ? Difficulty.Hell : progression.CurrentDifficulty)
            : Difficulty.Normal).Min();
        return lowest > GoalDifficulty ? GoalDifficulty : lowest;
    }
    private int _gameNumber;
    private string _gameName;
    private bool _hasRun;

    public bool RunsOnce => true;

    public string GetName() => "leveling";

    public async Task Run()
    {
        if (_hasRun)
        {
            return;
        }

        _hasRun = true;
        StrangerEscape.FleeOnWhisper = true;
        // The party opens every door it reaches, so its paths may run through them.
        D2NG.Navigation.Extensions.AreaMapExtensions.DoorsArePassable = true;
        _config.Validate();
        _gameNumber = _config.StartingGameNumber;
        var catalog = SkillCatalog.Load(GameDataLocation.Resolve(GameDataLocation.SkillsFile));
        var plans = LoadPlans(catalog);
        LoadStepFailures();
        LevelingGamble.StateFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(FailuresFile) ?? ".", "gamble-state.json");
        _gemStateFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(FailuresFile) ?? ".", "gem-mule-state.json");
        LoadGemsLeftByMules();
        _shieldStateFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(FailuresFile) ?? ".", "shield-state.json");
        LoadShieldSettled();
        // A shield bearer looks at the start of a run, not only after a game: the first look waited out a Diablo run, and a
        // restart after a failure is the quickest way to its shield. One its last game saw settled does not look: a Normal game
        // puts it in act 1 of the next Hell game wherever it left Normal, and the paladin took the whole party through acts 1
        // to 3 after every restart.
        foreach (var bearer in _config.Party.Where(member => plans[member.Plan].Shield && !_shieldSettled.Contains(member.Character)))
        {
            _shieldBuyers.Add(bearer.Character);
        }
        _gear = new LevelingGear(new D2NG.Core.ItemData.ItemRequirements(D2NG.Core.ItemData.ItemAffixTable.Load(GameDataLocation.Resolve(GameDataLocation.ItemAffixesFile))));
        var requirements = new D2NG.Core.ItemData.ItemRequirements(D2NG.Core.ItemData.ItemAffixTable.Load(GameDataLocation.Resolve(GameDataLocation.ItemAffixesFile)));
        LevelingCombat.WeaponSpeed = item => requirements.BaseOf(item)?.Speed;
        LevelingGems.WieldsTwoHanded = member => _gear.IsTwoHandedWeaponWorn(member);
        LevelingLoot.WornOrKeptByAnyone = (party, item) => _gear.WantedByAnyone(party, item) || _gear.WantedLater(party, item);
        LevelingLoot.TeleportAlong = TeleportAlong;
        LevelingCombat.AreaMonsterLevel = member => AreaLevels?.MonsterLevel(member.Game.Area, member.Game.Difficulty, member.Game.ClientCharacter?.IsExpansion == true);
        LevelingGear.WalkTo = async (member, point) =>
        {
            using var cancel = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, point, MovementMode.Walking, cancel.Token);
        };
        LevelingGear.StashOnMap = async member =>
        {
            var town = await mapApiService.GetArea(member.Game.MapId, AreaTraveler.MapDifficulty, member.Game.Act.MapTownArea());
            return town?.Objects?.GetValueOrDefault((int)EntityCode.Stash)?.FirstOrDefault();
        };
        var members = new List<PartyMember>();
        try
        {
            var failuresInARow = 0;
            while (!StrangerEscape.StopRequested && !StopFileFound())
            {
                try
                {
                    members = await StartGame(plans, catalog);
                }
                catch (Exception exception) when (!StrangerEscape.StopRequested && ++failuresInARow < MaxStartFailures)
                {
                    // The realm restarts its game servers now and then (the administrator announces it in chat),
                    // which drops every client. Waiting it out beats ending a long leveling session.
                    Log.Warning(exception, "Starting a game failed ({Failures} in a row); waiting {Seconds}s before the next", failuresInARow, RecoveryWait.TotalSeconds);
                    await CloseClients(members);
                    await Task.Delay(RecoveryWait);
                    continue;
                }

                var party = new Party(members, _combat, _traveler, pathingService, mapApiService) { Area = members.First(member => member.IsLeader).Game.Act.MapTownArea() };
                _party = party;
                party.BetweenStretches = () => KeepPartyUp(party);
                party.LootBeforeLeaving = () => LootBeforeLeaving(party);
                party.RestockInTown = () => VisitTown(party);
                party.GoHomeAlone = ComeHomeAlone;
                party.DuringFight = () => KeepPartyUpInFight(party);
                // Hell's act 4 is where a member's chicken pulled the others down after it, 154 of 200 chickens.
                party.HoldsForTheShort = () => _difficulty == D2NG.Core.D2GS.Enums.Difficulty.Hell && party.Area.MapToAct() == Act.Act4;
                party.StopRequested = StopFileFound;
                PointSpender.Wanted = member => _gear.StatsWanted(party, member);
                await CheckPlansMatch(members);
                await Task.WhenAll(members.Select(PointSpender.Spend));
                LevelingLoot.Reset();
                _searchedBodies.Clear();
                _combat.ForgetGame();
                LevelingBuffs.ForgetGame();
                _sweptThisGame.Clear();
                _cubeChestOpened = false;
                await party.GatherWhereMostAre();
                party.StartFollowing();
                await VisitTown(party);
                await party.LearnWaypoints();
                try
                {
                    var outcome = await PlayGame(party);
                    failuresInARow = 0;
                    Log.Information("Game {Game} ended: {Outcome}", _gameName, outcome);
                    foreach (var member in members.Where(member => member.InGame))
                    {
                        Log.Information("{State}", PointSpender.Describe(member));
                    }

                    if (outcome == GameOutcome.GoalReached)
                    {
                        Log.Information("The party reached its goal: {Goal}", _config.Until);
                        return;
                    }

                    if (outcome == GameOutcome.Stuck)
                    {
                        throw new InvalidOperationException($"The party made no progress in {_gameName}; stopping rather than repeating it");
                    }
                }
                catch (Exception exception) when (!StrangerEscape.StopRequested && IsConnectionLoss(exception) && ++failuresInARow < MaxStartFailures)
                {
                    Log.Warning(exception, "The game was lost mid-play ({Failures} in a row); waiting {Seconds}s before a new one", failuresInARow, RecoveryWait.TotalSeconds);
                    await party.StopFollowing();
                    await CloseClients(members);
                    await Task.Delay(RecoveryWait);
                }
                finally
                {
                    await party.StopFollowing();
                    NoteGemMules(members);
                    NoteShieldShopping(members);
                    await CloseClients(members);
                }
            }
        }
        finally
        {
            await CloseClients(members);
        }
    }

    private readonly HashSet<string> _gemMuleDue = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _gemMuledBefore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a member held after its last mule pass: the gems no mule takes (plain ones, perfect skulls, the shield's
    /// diamonds) stay, and counting them again would send it to the mules after every game.
    /// </summary>
    private readonly Dictionary<string, int> _gemsLeftByMules = new(StringComparer.OrdinalIgnoreCase);

    private string _gemStateFile;

    /// <summary>
    /// Kept across runs: forgotten at a restart, the paladin's 46 gems no mule takes sent him to the gem mules after his
    /// first game of every run, a mule game that moved nothing.
    /// </summary>
    private void LoadGemsLeftByMules()
    {
        try
        {
            if (_gemStateFile != null && System.IO.File.Exists(_gemStateFile)
                && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(System.IO.File.ReadAllText(_gemStateFile)) is { } saved)
            {
                foreach (var (character, left) in saved)
                {
                    _gemsLeftByMules[character] = left;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not read {File}; gem mule passes count from zero", _gemStateFile);
        }
    }

    private void SaveGemsLeftByMules()
    {
        try
        {
            if (_gemStateFile != null)
            {
                System.IO.File.WriteAllText(_gemStateFile, System.Text.Json.JsonSerializer.Serialize(_gemsLeftByMules));
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not write {File}", _gemStateFile);
        }
    }

    /// <summary>The perfect diamonds the shields still needed when the last game ended; a mule pass leaves that many.</summary>
    private int _perfectDiamondsOwed;

    /// <summary>The shield's collector when the last game ended: only it keeps perfect diamonds from the gem mules.</summary>
    private string _diamondKeeper;

    /// <summary>
    /// Three for each missing shield, on the collector alone: kept on every member, the party held six perfect diamonds
    /// for the sorceress's one shield.
    /// </summary>
    private int KeptPerfectDiamonds(string character)
        => string.Equals(character, _diamondKeeper, StringComparison.OrdinalIgnoreCase) ? Math.Min(_perfectDiamondsOwed, 3) : 0;

    private void NoteGemMules(IEnumerable<PartyMember> members)
    {
        _perfectDiamondsOwed = LevelingGems.PerfectDiamondsOwed(members);
        _diamondKeeper = LevelingGems.Collector(members.Where(member => member.InGame && member.Game.Me != null))?.Name;
        foreach (var member in members.Where(member => member.InGame && member.Game.Me != null))
        {
            var held = LevelingGems.GemsHeld(member.Game);
            if (_gemMuledBefore.Remove(member.Name))
            {
                _gemsLeftByMules[member.Name] = held;
                SaveGemsLeftByMules();
                continue;
            }

            if (held >= _gemsLeftByMules.GetValueOrDefault(member.Name) + LevelingGems.MuleGemsFrom && _gemMuleDue.Add(member.Name))
            {
                Log.Information("{Character} holds {Count} gems: they go to the gem mules before its next game", member.Name, held);
            }
        }
    }

    /// <summary>The gems go out in a mule game of their own, before the member joins the party's next game.</summary>
    private async Task MuleGems(Client client, string character)
    {
        _gemMuledBefore.Add(character);
        try
        {
            var muled = await muleService.MuleItemsForClient(client, item => LevelingGems.GemsToMule(client.Game, KeptPerfectDiamonds(character))(item));
            Log.Information("{Character} gem mule pass {Outcome}", character, muled ? "done" : "FAILED");
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "{Character} gem mule pass failed", character);
        }

        // A pass that failed part way can leave the member in the mule game, and the party's game is joined from the realm.
        if (client.Game.IsInGame())
        {
            await client.Game.LeaveGame();
            await client.RejoinMCP();
        }
    }

    /// <summary>The members that shop for their shield before they join the next game: the ones that will wear it.</summary>
    private readonly HashSet<string> _shieldBuyers = new(StringComparer.OrdinalIgnoreCase);

    private int _shieldShops;

    private int _shopGames;

    private int _gamesSinceShieldShop = ShieldShopEvery;

    /// <summary>Shopping trips a run spends on the shield at most: each looks at up to twenty of Fara's stocks.</summary>
    private const int MaxShieldShops = 3;

    /// <summary>Games played between two shopping trips.</summary>
    private const int ShieldShopEvery = 2;

    /// <summary>Shield bearers whose last look found their diamond shield worn, or a two-hander in their hands.</summary>
    private readonly HashSet<string> _shieldSettled = new(StringComparer.OrdinalIgnoreCase);

    private string _shieldStateFile;

    private void LoadShieldSettled()
    {
        try
        {
            if (System.IO.File.Exists(_shieldStateFile)
                && System.Text.Json.JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(_shieldStateFile)) is { } saved)
            {
                _shieldSettled.UnionWith(saved);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not read {File}; every shield bearer looks for its shield", _shieldStateFile);
        }
    }

    private void NoteShieldSettled(PartyMember member, bool settled)
    {
        if (!(settled ? _shieldSettled.Add(member.Name) : _shieldSettled.Remove(member.Name)))
        {
            return;
        }

        try
        {
            System.IO.File.WriteAllText(_shieldStateFile, System.Text.Json.JsonSerializer.Serialize(_shieldSettled.Order().ToList()));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not write {File}", _shieldStateFile);
        }
    }

    private void NoteShieldShopping(IEnumerable<PartyMember> members)
    {
        _gamesSinceShieldShop++;
        var inGame = members.Where(member => member.InGame && member.Game.Me != null).ToList();
        foreach (var bearer in inGame.Where(member => member.Plan.Shield))
        {
            NoteShieldSettled(bearer, !LevelingGems.NeedsShieldBought(bearer));
        }

        if (LevelingGems.Collector(inGame) is { } collector && LevelingGems.NeedsShieldBought(collector) && LevelingGems.PerfectHeld(inGame) >= 3
            && _shieldShops < MaxShieldShops && _gamesSinceShieldShop >= ShieldShopEvery && _shieldBuyers.Add(collector.Name))
        {
            Log.Information("The party holds the perfect diamonds and {Character} no shield to set them in: it shops for one in Normal before the next game", collector.Name);
        }
    }

    /// <summary>
    /// The shield's wearer shops alone, in a Normal game of its own, before it joins the party's next one: the whole party
    /// went into a Normal game for it, and the next Hell game started in act 1 for all four. Alone it is also the only one
    /// to leave Lut Gholein between looks, and a merchant restocks only once no player is left in its town.
    /// </summary>
    private async Task ShopForShieldAlone(Client client, LevelingMember account, BuildPlan plan, SkillCatalog catalog)
    {
        _gamesSinceShieldShop = 0;
        // A number of its own every time: the sorceress could not make "lvlps1" a second after the paladin had left it.
        var game = $"{_bot.GameNamePrefix}s{++_shopGames}";
        try
        {
            if (!await client.CreateGame(Difficulty.Normal, game, GamePassword, _bot.GameDescriptions[0])
                || !GeneralHelpers.TryWithTimeout(_ => client.Game.Me != null, TimeSpan.FromSeconds(10)))
            {
                Log.Warning("{Character} could not make the shopping game {Game}", account.Character, game);
                return;
            }

            var member = new PartyMember(client, account, plan, catalog) { IsLeader = true, Progression = _progressions.GetValueOrDefault(account.Character) };
            var alone = new Party([member], _combat, _traveler, pathingService, mapApiService) { Area = client.Game.Act.MapTownArea() };
            // Asked at the start of a run too, before anything in the game is known: whether it still wants the shield is only
            // seen here, with its items loaded.
            if (!LevelingGems.NeedsShieldBought(member))
            {
                NoteShieldSettled(member, true);
                Log.Information("{Character} needs no shield bought: it has its diamond shield or one to set the diamonds in, or no hand free for one", account.Character);
                return;
            }

            _shieldShops++;
            var parked = await ParkForShield(member);
            await alone.LearnWaypoints();
            var bought = await ShopForShield(alone, member);
            Log.Information("{Character} shopped in Normal for the diamond shield ({Count} of {Max}): {Outcome}", account.Character, _shieldShops, MaxShieldShops,
                bought ? "bought one" : "none on offer");
            await TakeBackParked(member, parked);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "{Character} could not shop for the shield in {Game}", account.Character, game);
        }
        finally
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
                await Task.Delay(TimeSpan.FromSeconds(2));
                await client.RejoinMCP();
            }
        }
    }

    /// <summary>A shield's room in the bag: two cells wide, three high.</summary>
    private static readonly Item ShieldRoom = new() { Width = 2, Height = 3 };

    /// <summary>
    /// Everything but the tomes, the cube and the diamonds into the stash while the member shops, when its bag has no room for
    /// a shield: full of potions, unidentified rings and gear, the paladin walked past Fara's three-socket Large Shield and
    /// Kite Shield in one trip.
    /// </summary>
    private static async Task<List<Item>> ParkForShield(PartyMember member)
    {
        var game = member.Game;
        if (game.Inventory.FindFreeSpace(ShieldRoom) != null)
        {
            return [];
        }

        var parked = game.Inventory.Items.Where(item => item.Name is not (ItemName.HoradricCube or ItemName.TomeOfTownPortal or ItemName.TomeofIdentify)
            && !LevelingGems.IsDiamond(item) && !LevelingGear.IsQuestItem(item)).ToList();
        if (parked.Count == 0 || !await LevelingGear.WalkToStash(member))
        {
            return [];
        }

        var result = InventoryHelpers.StashItemsAndGold(game, parked, 0);
        Log.Information("{Character} put {Count} items in its stash to make room for the shield: {Result}", member.Name, parked.Count, result);
        return parked;
    }

    /// <summary>What was parked comes back into the bag as far as it fits; the rest stays in the stash.</summary>
    private static async Task TakeBackParked(PartyMember member, List<Item> parked)
    {
        var game = member.Game;
        var stashed = parked.Where(item => game.Stash.FindItemById(item.Id) != null).ToList();
        if (stashed.Count == 0 || !await LevelingGear.WalkToStash(member))
        {
            return;
        }

        var result = InventoryHelpers.MoveStashItemsToInventory(game, stashed);
        Log.Information("{Character} took {Count} parked items back out of its stash: {Result}", member.Name, stashed.Count, result);
    }

    private const int MaxStartFailures = 4;
    private static readonly TimeSpan RecoveryWait = TimeSpan.FromSeconds(90);

    private static bool IsConnectionLoss(Exception exception)
        => exception is IOException or System.Net.Sockets.SocketException or ObjectDisposedException
            || exception.InnerException is IOException or System.Net.Sockets.SocketException;

    private bool _stopFileSeen;

    private bool StopFileFound()
    {
        var stopFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_bot.LogFile)) ?? ".", "stop.txt");
        if (!_stopFileSeen && File.Exists(stopFile))
        {
            File.Delete(stopFile);
            _stopFileSeen = true;
            Log.Warning("Stop file found: the party leaves after this stretch");
            // Every walk refuses from here on while the step runs out: in the trace that reads as a pathing failure.
            foreach (var member in _party?.Members.Where(member => member.InGame) ?? [])
            {
                D2NG.Core.Tracing.TraceEvents.Decision(member.Game, "stop requested", ("source", "stop file"));
            }
        }

        return _stopFileSeen;
    }

    private Dictionary<string, BuildPlan> LoadPlans(SkillCatalog catalog)
    {
        var folder = _config.PlansFolder
            ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(GameDataLocation.ConfiguredDirectory ?? GameDataLocation.DefaultDirectory) ?? ".", "leveling-plans");
        var plans = new Dictionary<string, BuildPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _config.Party.Select(member => member.Plan).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var plan = BuildPlan.Load(System.IO.Path.Combine(folder, name + ".json"));
            var problems = PlanCheck.Problems(plan, catalog);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"Build plan {name} is not usable: {string.Join("; ", problems)}");
            }

            plans[name] = plan;
        }

        return plans;
    }

    /// <summary>1.09 has no respec: a character whose points already left its plan stops the bot.</summary>
    private static async Task CheckPlansMatch(List<PartyMember> members)
    {
        await Task.Delay(1000);
        foreach (var member in members)
        {
            if (member.Plan.Class != member.Game.Me.Class)
            {
                throw new InvalidOperationException($"{member.Name} is a {member.Game.Me.Class}, its plan {member.Plan.Name} is for a {member.Plan.Class}");
            }

            var deviations = PointSpender.Deviations(member);
            if (deviations.Count > 0)
            {
                throw new InvalidOperationException($"{member.Name} has left its plan {member.Plan.Name}: {string.Join("; ", deviations)}");
            }

            Log.Information("{State} on plan {Plan}", PointSpender.Describe(member), member.Plan.Name);
        }
    }

    private async Task<List<PartyMember>> StartGame(Dictionary<string, BuildPlan> plans, SkillCatalog catalog)
    {
        var leaderAccount = _config.LeaderMember;
        await ReadProgressions();
        _difficulty = PartyDifficulty();
        Log.Information("The party plays {Difficulty}: {Progressions}", _difficulty,
            string.Join(", ", _progressions.Select(pair => $"{pair.Key} {pair.Value.CurrentDifficulty} act {pair.Value.CurrentAct}")));
        var ordered = _config.Party.OrderBy(member => member == leaderAccount ? 0 : 1).ToList();
        var members = new List<PartyMember>();
        foreach (var account in ordered)
        {
            var client = await LoginAndSelect(account);
            if (_gemMuleDue.Remove(account.Character))
            {
                await MuleGems(client, account.Character);
            }

            if (_shieldBuyers.Remove(account.Character))
            {
                await ShopForShieldAlone(client, account, plans[account.Plan], catalog);
            }

            var member = new PartyMember(client, account, plans[account.Plan], catalog)
            {
                IsLeader = account == leaderAccount,
                Progression = _progressions.GetValueOrDefault(account.Character)
            };
            members.Add(member);
            AcceptPartyInvites(client);
            if (member.IsLeader)
            {
                InviteJoiningPlayers(client);
                await CreateGame(client);
            }
            else
            {
                await JoinGame(member);
            }

            ChickenService.Attach(client, account.Chicken ?? _bot.Chicken, stayForCorpse: true);
            client.Game.CleanupCursorItem();
        }

        await Task.Delay(1500);
        var leader = members.First(member => member.IsLeader);
        foreach (var player in leader.Game.Players.Where(player => player.Id != leader.Game.Me.Id && !leader.Game.IsInPartyWith(player)))
        {
            leader.Game.InvitePlayer(player);
        }

        Log.Information("Party is in game {Game} (password {Password}): {Members}", _gameName, GamePassword, string.Join(", ", members.Select(member => member.Name)));
        return members;
    }

    private bool _joining;
    private bool _joinUsed;

    private string GamePassword => _joining ? _config.JoinPassword ?? "" : _bot.GamePassword;

    private async Task CreateGame(Client host)
    {
        // A game to join is used once: the party goes into it instead of making its own, and the next game is its own again.
        if (!string.IsNullOrEmpty(_config.JoinGame) && !_joinUsed)
        {
            _joinUsed = true;
            _joining = true;
            _gameName = _config.JoinGame;
            _gear.ForgetGame();
            _browsedThisGame.Clear();
            _gambledThisGame.Clear();
            Log.Information("Joining the existing game {Game} instead of creating one", _gameName);
            if (await host.JoinGame(_gameName, GamePassword) && GeneralHelpers.TryWithTimeout(_ => host.Game.Me != null, TimeSpan.FromSeconds(10)))
            {
                return;
            }

            Log.Warning("Could not join {Game}; creating our own", _gameName);
        }

        _joining = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            _gameName = $"{_bot.GameNamePrefix}{_gameNumber++}";
            _gear.ForgetGame();
            _browsedThisGame.Clear();
            _gambledThisGame.Clear();
            if (await host.CreateGame(_difficulty, _gameName, GamePassword, _bot.GameDescriptions[0]))
            {
                if (!GeneralHelpers.TryWithTimeout(_ => host.Game.Me != null, TimeSpan.FromSeconds(10)))
                {
                    throw new InvalidOperationException($"The leader never initialized in {_gameName}");
                }

                return;
            }

            Log.Warning("Could not create {Game}; trying the next number", _gameName);
            await Task.Delay(1000);
        }

        throw new InvalidOperationException("The leader could not create a game");
    }

    private async Task JoinGame(PartyMember member)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await member.Client.JoinGame(_gameName, GamePassword)
                && GeneralHelpers.TryWithTimeout(_ => member.Game.Me != null, TimeSpan.FromSeconds(10)))
            {
                return;
            }

            Log.Warning("{Character} failed to join {Game} on attempt {Attempt}", member.Name, _gameName, attempt);
            await Task.Delay(TimeSpan.FromSeconds(15));
            await member.Client.RejoinMCP();
        }

        throw new InvalidOperationException($"{member.Name} could not join {_gameName}");
    }

    /// <summary>Every member's progression from the character list, read before the leader creates the game.</summary>
    private async Task ReadProgressions()
    {
        foreach (var account in _config.Party)
        {
            var client = new Client();
            try
            {
                if (client.Connect(_bot.Realm, _bot.KeyOwner, _bot.GameFolder))
                {
                    var characters = await client.Login(account.Username, account.Password);
                    var character = characters?.FirstOrDefault(candidate => candidate.Name.Equals(account.Character, StringComparison.OrdinalIgnoreCase));
                    if (character != null && D2NG.Core.MCP.CharacterProgression.TryCreate(character, out var progression))
                    {
                        _progressions[account.Character] = progression;
                    }
                }
            }
            finally
            {
                client.Disconnect();
                await Task.Delay(300);
            }
        }
    }

    private async Task<Client> LoginAndSelect(LevelingMember account)
    {
        var client = new Client();
        if (!client.Connect(_bot.Realm, _bot.KeyOwner, _bot.GameFolder))
        {
            throw new InvalidOperationException($"Could not connect {account.Character} to the realm");
        }

        StrangerEscape.Watch(client);
        var characters = await client.Login(account.Username, account.Password);
        var character = characters?.FirstOrDefault(candidate => candidate.Name.Equals(account.Character, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Character {account.Character} was not found on its account");
        await client.SelectCharacter(character);
        return client;
    }

    /// <summary>
    /// Between stretches of walking: points get spent, a member that dropped out comes back, and a dead
    /// one is brought back to life in town.
    /// </summary>
    /// <summary>A boss's drop lands with its death; a second pass takes what the first made room for.</summary>
    private static readonly TimeSpan DropLands = TimeSpan.FromMilliseconds(600);

    private const int LootPassesBeforeLeaving = 2;

    private static readonly TimeSpan SweepBeforeHome = TimeSpan.FromSeconds(20);

    private static async Task LootBeforeLeaving(Party party)
    {
        await Task.Delay(DropLands);
        for (var pass = 0; pass < LootPassesBeforeLeaving && party.Leader.InGame && !party.Leader.Game.IsInTown(); pass++)
        {
            await LevelingLoot.Collect(party);
        }

        if (party.Leader.InGame && !party.Leader.Game.IsInTown())
        {
            await LevelingLoot.Sweep(party, LevelingLoot.WholeSweep, SweepBeforeHome);
        }
    }

    private async Task KeepPartyUp(Party party)
    {
        foreach (var member in party.Members)
        {
            // Leaving at that moment its player is already gone while the game still counts as joined: a chicken between
            // the two stopped the whole bot with a null reference.
            if (!member.Client.Game.IsInGame() || member.Game.Me == null)
            {
                if (!_rejoiningInFight.ContainsKey(member.Name))
                {
                    await Rejoin(member, party);
                }

                continue;
            }

            if (member.Game.Me.Life <= 0 && ChickenService.DeathLocation(member.Client) != null)
            {
                // Softcore: back to life in town, then the follow loop walks it to the party, where the corpse
                // is picked up below. Every death is logged as the failure it would be in hardcore.
                Log.Warning("DEATH {Character} (level {Level}) died at {Location} in {Area}", member.Name, member.Level, ChickenService.DeathLocation(member.Client), party.Area);
                member.Game.Resurrect();
                GeneralHelpers.TryWithTimeout(_ => member.Game.Me.Life > 0, TimeSpan.FromSeconds(10));
                member.Mode = FollowMode.Off;
                party.RegroupRequested = true;
                continue;
            }

            // In town too: a corpse left in an earlier game lies there, and while it was skipped the amazon put on new gear
            // and the game refused the body until everything was taken off again.
            if (member.Game.Me.CorpseId != null)
            {
                await GeneralHelpers.PickupCorpseIfExists(member.Client, pathingService);
            }

            await ClearCursor(member);
            // Act 4's Burning Souls take a third of a member's life in a blink: a chicken at 25% left more than once with
            // nothing left at all. Higher there, except while a nearly dead boss holds the party in.
            if (!party.Finishing)
            {
                Chicken.ChickenService.OverrideLifeChicken(member.Client, member.Game.Act == Act.Act4 ? ActFourChicken : null);
            }

            await PointSpender.Spend(member);
            await LevelingBuffs.KeepUp(member);
            await LevelingBuffs.KeepUpForParty(member, party);
        }

        await FindItemOnBodies(party);
        LevelingLoot.Note(party);
        await LevelingLoot.Collect(party);
        RestockWhenDry(party);
        await TownTripTogether(party);
    }

    private volatile bool _inTownTrip;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _rejoiningInFight = new();

    /// <summary>
    /// In a fight: members who left the game rejoin in the background - the leader fights on meanwhile - and the
    /// restock and the trip to town together are checked.
    /// </summary>
    private async Task KeepPartyUpInFight(Party party)
    {
        RejoinInBackground(party);
        LevelingLoot.Note(party);

        foreach (var member in party.Members.Where(member => member.InGame && member.Game.Me != null))
        {
            await LevelingBuffs.KeepUpForParty(member, party);
        }

        RestockWhenDry(party);
        await TownTripTogether(party);
    }

    /// <summary>Members out of the game rejoin in the background, one rejoin per member at a time.</summary>
    private void RejoinInBackground(Party party)
    {
        foreach (var member in party.Members.Where(member => !member.Client.Game.IsInGame()))
        {
            if (_rejoiningInFight.TryAdd(member.Name, true))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Rejoin(member, party);
                    }
                    finally
                    {
                        _rejoiningInFight.TryRemove(member.Name, out _);
                    }
                });
            }
        }
    }

    /// <summary>
    /// Down to half the party in the field outside a boss fight - the boss fight runs its own - the party goes to town through the
    /// leader's portal, waits there until everyone is back and ready, and goes back in together to the same spot.
    /// </summary>
    private async Task TownTripTogether(Party party)
    {
        if (!party.TownTripRequested || party.InBossFight || _inTownTrip || !party.Leader.InGame || party.Leader.Game.IsInTown())
        {
            return;
        }

        _inTownTrip = true;
        try
        {
            party.TownTripRequested = false;
            Log.Information("Only {Count} of the party are left in {Area}; everyone goes to town and comes back together",
                party.Members.Count(member => member.InGame && !member.Game.IsInTown()), party.Area);
            // Not back: the step the trip interrupted cannot go on from town - the Diablo step walked to its seals from the
            // Pandemonium Fortress for eight minutes - so it ends as one that sent the party home.
            if (!await party.FleeToTown(async () =>
                {
                    await VisitTown(party);
                    // A member out of the game is brought back while the party waits for it: nothing else rejoined one in
                    // town, and the party waited out the full ninety seconds for the sorceress and went back without her.
                    RejoinInBackground(party);
                    await party.WaitForEveryoneInTown(Party.EveryoneInTownWait);
                }))
            {
                party.RegroupRequested = true;
            }
        }
        finally
        {
            _inTownTrip = false;
        }
    }

    /// <summary>
    /// An item left on the cursor - an equip or a sale the server refused with no room to put it back - makes the
    /// server ignore the character's moves and interactions: the leader stood frozen in town through a whole step.
    /// </summary>
    private static async Task ClearCursor(PartyMember member)
    {
        var game = member.Game;
        if (game.CursorItem is not { } held)
        {
            return;
        }

        if (game.Inventory.FindFreeSpace(held) is { } space)
        {
            game.InsertItemIntoContainer(held, space, D2NG.Core.D2GS.Items.ItemContainer.Inventory);
        }
        else
        {
            game.DropItem(held);
        }

        await Task.Delay(300);
        Log.Warning("{Character} had {Item} on its cursor; {Outcome}", member.Name, held.Name, game.CursorItem == null ? "put away" : "still held");
    }

    private const double ActFourChicken = 0.35;

    private DateTime _lastRestock = DateTime.MinValue;

    /// <summary>
    /// A member down to its last healing potion outside town sends everyone home to restock, through the
    /// regroup: in Jail 1 three members chickened within a minute of the amazon's belt running dry.
    /// </summary>
    private void RestockWhenDry(Party party)
    {
        if (party.RegroupRequested)
        {
            return;
        }

        // The same with the leader among those in town: the paladin and the barbarian had chickened in Hell's River of
        // Flame, the amazon and the sorceress fought Urdar on alone, and the amazon died. Those still out go home through
        // their own portals and the party regroups, to go back in together.
        var inField = party.Members.Where(member => member.InGame && member.Game.Me != null && !member.Game.IsInTown()).ToList();
        var leaderOut = !party.Leader.InGame || party.Leader.Game.Me == null || party.Leader.Game.IsInTown();
        if (leaderOut && party.Members.Count >= 2 && inField.Count > 0 && inField.Count * 2 <= party.Members.Count)
        {
            Log.Information("Only {Count} of the party are left out with the leader in town; they go home too and the party goes back together",
                inField.Count);
            foreach (var member in inField.Where(member => _restockingAlone.TryAdd(member.Name, true)))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        member.Mode = FollowMode.Off;
                        await ComeHomeAlone(member);
                    }
                    finally
                    {
                        member.Mode = FollowMode.Follow;
                        _restockingAlone.TryRemove(member.Name, out _);
                    }
                });
            }

            party.RegroupRequested = true;
            return;
        }

        // Half the party or less still in the field - the others chickened, died or went to restock - goes on badly, in a
        // boss fight and anywhere else: everyone goes to town, waits until all are ready and goes back in together.
        if (party.Members.Count >= 2 && !party.TownTripRequested && !_inTownTrip && party.Leader.InGame && !party.Leader.Game.IsInTown()
            && party.Members.Count(member => member.InGame && !member.Game.IsInTown()) * 2 <= party.Members.Count)
        {
            party.TownTripRequested = true;
            return;
        }

        if (DateTime.UtcNow - _lastRestock < TimeSpan.FromMinutes(2))
        {
            return;
        }

        // Only members with the party count: one walking back alone used its potions on the way, and calling
        // everyone home for it threw away the step they were in.
        var dry = party.Members.FirstOrDefault(member => member.InGame && !member.Game.IsInTown()
            && (member.IsLeader || member.Game.Me.Location.Distance(party.Leader.Game.Me.Location) <= 60)
            && InventoryHelpers.GetTotalHealthPotions(member.Game) <= 1 && member.Game.Belt.GetRejuvenationPotions().Count == 0);
        if (dry == null)
        {
            return;
        }

        _lastRestock = DateTime.UtcNow;
        // One member out of potions goes on its own and comes back; the others fight on.
        if (!dry.IsLeader)
        {
            if (_restockingAlone.TryAdd(dry.Name, true))
            {
                Log.Information("{Character} is down to {Potions} healing potions; it restocks in town on its own", dry.Name, InventoryHelpers.GetTotalHealthPotions(dry.Game));
                _ = Task.Run(() => RestockAlone(dry, party));
            }

            return;
        }

        // The leader out of potions takes everyone to town and back to the same spot.
        Log.Information("{Character} is down to {Potions} healing potions; the party goes to town to restock and comes back", dry.Name, InventoryHelpers.GetTotalHealthPotions(dry.Game));
        party.TownTripRequested = true;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _restockingAlone = new();

    /// <summary>Through its own portal to town, restocked, and back to the party the way a chickened member comes back.</summary>
    private async Task RestockAlone(PartyMember member, Party party)
    {
        try
        {
            member.Mode = FollowMode.Off;
            await ComeHomeAlone(member);
            if (member.Game.IsInTown())
            {
                await ReturnToParty(member, party);
            }
        }
        finally
        {
            member.Mode = FollowMode.Follow;
            _restockingAlone.TryRemove(member.Name, out _);
        }
    }

    private async Task Rejoin(PartyMember member, Party party)
    {
        Log.Warning("{Character} is out of {Game}; rejoining", member.Name, _gameName);
        for (var attempt = 1; attempt <= 3 && !member.Client.Game.IsInGame(); attempt++)
        {
            try
            {
                if (await member.Client.RejoinMCP() && await member.Client.JoinGame(_gameName, GamePassword))
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "{Character} threw while rejoining", member.Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        if (!member.Client.Game.IsInGame() || !GeneralHelpers.TryWithTimeout(_ => member.Game.Me != null, TimeSpan.FromSeconds(10)))
        {
            Log.Warning("{Character} could not rejoin {Game}", member.Name, _gameName);
            return;
        }

        member.Game.CleanupCursorItem();
        party.ForgetStuck(member);
        LevelingBuffs.Rejoined(member);
        foreach (var other in party.Members.Where(other => other != member && other.InGame))
        {
            var seen = other.Game.Players.FirstOrDefault(player => player.Name.Equals(member.Game.Me.Name, StringComparison.OrdinalIgnoreCase));
            if (seen != null && !other.Game.IsInPartyWith(seen))
            {
                other.Game.InvitePlayer(seen);
            }
        }

        member.Mode = FollowMode.Off;
        if (member.IsLeader)
        {
            if (party.LeaderAway)
            {
                _ = Task.Run(() => ReturnLeader(member, party));
                return;
            }

            // The party goes where the leader is: everyone comes home to it.
            party.RegroupRequested = true;
            return;
        }

        if (party.Leader.Game.IsInTown() || party.HomeBound)
        {
            // Back in town with the leader: it is with the party already. Calling a regroup here looped - the
            // regroup sends stragglers to rejoin, and each rejoin called the next regroup. It restocks first: the party's
            // own visit may be over, and it left with what its chicken left it.
            await Safely(member, "restock", async () =>
            {
                await HealAndSell(member, party);
                await Buy(member);
            });
            member.Mode = FollowMode.Follow;
            return;
        }

        // A follower that chickened restocks and comes back on its own: calling everyone home cost the step it
        // was in every time, and the amazon chickens most games.
        _ = Task.Run(() => ReturnToParty(member, party));
    }

    /// <summary>The leader restocks after its chicken and goes back to the others through a portal one of them opens.</summary>
    private async Task ReturnLeader(PartyMember leader, Party party)
    {
        try
        {
            await Safely(leader, "restock", async () =>
            {
                await HealAndSell(leader, party);
                await Buy(leader);
            });

            if (await party.BringLeaderBack())
            {
                leader.Mode = FollowMode.Off;
                Log.Information("{Leader} is back with the party in {Area} through a portal", leader.Name, party.Area);
                party.LeaderAway = false;
                return;
            }

            Log.Information("{Leader} found no way back to {Area} through a portal; the party regroups", leader.Name, party.Area);
            party.RegroupRequested = true;
            party.LeaderAway = false;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "{Leader} could not get back to the party", leader.Name);
            party.RegroupRequested = true;
            party.LeaderAway = false;
        }
    }

    /// <summary>How many times a member back in town looks for a portal to the party before taking the waypoint.</summary>
    private const int PortalLooks = 6;

    private async Task ReturnToParty(PartyMember member, Party party)
    {
        try
        {
            await Safely(member, "restock", async () =>
            {
                await HealAndSell(member, party);
                await Buy(member);
            });

            var game = member.Game;
            // The party is on its way into town to go back in together: wait for it here rather than going back alone.
            if (party.TownTripRequested || party.HomeBound || party.Leader.Game.IsInTown())
            {
                return;
            }

            // The fast way back after a chicken: the leader opens a portal where the party is, and the member waits a
            // little for it at the town's portal spot before falling back to the waypoint.
            if (game.IsInTown() && party.Area != game.Act.MapTownArea())
            {
                // The leader opens it within one fight upkeep, five seconds; the walk back from the waypoint is far longer.
                for (var look = 0; look < PortalLooks; look++)
                {
                    if (await party.TakePortalInto(member, party.Area))
                    {
                        Log.Information("{Character} took a town portal back to the party in {Area}", member.Name, party.Area);
                        return;
                    }

                    await Task.Delay(1500);
                }

                Log.Information("{Character} found no portal to the party in {Area}; going by waypoint", member.Name, party.Area);
            }

            WorldObject town = null;
            if (Party.WaypointIn(game.Act.MapTownArea()) is { } townWaypoint)
            {
                // A member that just rejoined stands where town put it, usually out of sight of the waypoint.
                var path = await pathingService.GetPathToObject(game.MapId, AreaTraveler.MapDifficulty, game.Act.MapTownArea(), game.Me.Location,
                    townWaypoint.ToEntityCode(), MovementMode.Walking);
                if (path.Count > 0)
                {
                    await MovementHelpers.TakePathOfLocations(game, path, MovementMode.Walking);
                }

                GeneralHelpers.TryWithTimeout(_ => (town = game.GetEntityByCode(townWaypoint.ToEntityCode()).FirstOrDefault()) != null, TimeSpan.FromSeconds(3));
            }

            if (town != null && game.Me.AllowedWaypoints.IsEmpty)
            {
                // A rejoined member knows none of its waypoints until a menu has listed them.
                await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, town, MovementMode.Walking);
                for (var attempt = 0; attempt < 3 && game.ReadWaypoints(town) == null; attempt++)
                {
                    await game.MoveToAsync(town);
                    await Task.Delay(300);
                }
            }

            var route = await _traveler.Route(member.Client, party.Area);
            var waypoint = route.Reverse().Select(Party.WaypointIn)
                .FirstOrDefault(candidate => candidate is { } held && game.Me.AllowedWaypoints.Contains(held));
            if (waypoint == null || town == null)
            {
                Log.Information("{Character} walks back to the party in {Area}: {Reason}", member.Name, party.Area,
                    town == null ? "the town waypoint is not in sight" : "no held waypoint on the way");
            }
            else
            {
                await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, town, MovementMode.Walking);
                var taken = game.TakeWaypoint(town, waypoint.Value);
                if (taken)
                {
                    // The client's area lags the jump: handed over at once, the follow loop pathed from town into the
                    // Blood Moor while the amazon stood in the Catacombs. Wait until both position and area agree.
                    await _traveler.ConfirmInArea(member.Client, waypoint.Value.ToArea());
                    GeneralHelpers.TryWithTimeout(_ => game.Area == waypoint.Value.ToArea(), TimeSpan.FromSeconds(5));
                }

                Log.Information("{Character} {Outcome} the waypoint to {Waypoint} on its way back to the party in {Area}", member.Name,
                    taken ? "took" : "could NOT take", waypoint.Value, party.Area);
            }
        }
        finally
        {
            // The follow loop walks it the rest of the way.
            party.ForgetStuck(member);
            member.Mode = FollowMode.Follow;
        }
    }

    private static async Task CloseClients(List<PartyMember> members)
    {
        foreach (var member in members)
        {
            try
            {
                if (member.Client.Game.IsInGame())
                {
                    await member.Client.Game.LeaveGame();
                }

                member.Client.Disconnect();
            }
            catch (Exception exception)
            {
                Log.Debug("Closing {Character}: {Reason}", member.Name, exception.Message);
            }
        }

        members.Clear();
    }

    private static void AcceptPartyInvites(Client client)
    {
        client.OnReceivedPacketEvent(InComingPacket.EventMessage, packet =>
        {
            var notification = new EventNotifyPacket(packet);
            // The server's own word that the character died: a burst to zero life reads like a chicken otherwise, and the
            // paladin's death in the Outer Steppes went down in the log as one.
            if (notification.EventType == EventType.WasKilledBy && string.Equals(notification.CharacterName, client.Game.Me?.Name, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("DEATH: {Character} was killed in {Area} at {Location}", client.Game.Me?.Name, client.Game.Area, client.Game.Me?.Location);
                return;
            }

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

    private static bool HasFinished(PartyMember member, QuestId quest)
        => member.Game.Quests.IsComplete(quest) || member.Game.Quests.IsAcknowledged(quest);
}

public enum GameOutcome
{
    GoalReached,
    NextGame,
    Stuck
}
