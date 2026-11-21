using ConsoleBot.Escape;
using ConsoleBot.Helpers;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Quest;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <param name="Needed">Whether this member still needs the step; the step runs while any member does.</param>
/// <param name="NightmareGate">The level the step waits for in Nightmare when Gate plus the usual offset does not fit it.</param>
public sealed record LevelingStep(string Name, Func<PartyMember, bool> Needed, Func<Party, Task<bool>> Run, int Gate = 1, int? NightmareGate = null);

public sealed partial class LevelingBot
{
    private const int MaxFailureLevels = 1;

    private const int HellStepLevel = 50;
    private const int HellMaxFailureLevels = 10;
    private const uint AkaraRewardsDen = 0x4C;
    private const int DenSweepSpacing = 25;
    private static readonly TimeSpan DenLimit = TimeSpan.FromMinutes(15);

    private Dictionary<string, int> _stepFailures = [];

    /// <summary>
    /// Failures count per difficulty: seven Normal Andariel failures, never cleared because the party's kill was recorded
    /// apart from the step, raised the Nightmare Andariel gate by a level.
    /// </summary>
    private string FailureKey(LevelingStep step) => $"{_difficulty}:{step.Name}";

    /// <summary>Beside the plans: the failure counts outlive a restart, or every new build spent two more attempts at a boss before farming.</summary>
    private string FailuresFile => System.IO.Path.Combine(_config.PlansFolder
        ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(D2NG.Core.GameDataLocation.ConfiguredDirectory ?? D2NG.Core.GameDataLocation.DefaultDirectory) ?? ".", "leveling-plans"),
        "step-failures.json");

    private void LoadStepFailures()
    {
        try
        {
            if (System.IO.File.Exists(FailuresFile))
            {
                _stepFailures = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(System.IO.File.ReadAllText(FailuresFile)) ?? [];
                Log.Information("Step failures carried over: {Failures}", string.Join(", ", _stepFailures.Select(pair => $"{pair.Key} {pair.Value}")));
            }
        }
        catch (Exception exception) when (exception is System.IO.IOException or System.Text.Json.JsonException)
        {
            Log.Warning("Could not read {File}: {Reason}", FailuresFile, exception.Message);
        }
    }

    /// <summary>
    /// Bosses the party killed on its own, per difficulty. A boss another player killed for it completes the quest,
    /// but the party has to show it can do it before moving on.
    /// </summary>
    private HashSet<string> _partyBeat = [];

    private string BeatFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(FailuresFile) ?? ".", "party-beat.json");

    private bool PartyBeat(string boss)
    {
        if (_partyBeat.Count == 0 && System.IO.File.Exists(BeatFile))
        {
            try
            {
                _partyBeat = System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(System.IO.File.ReadAllText(BeatFile)) ?? [];
            }
            catch (Exception exception) when (exception is System.IO.IOException or System.Text.Json.JsonException)
            {
                Log.Warning("Could not read {File}: {Reason}", BeatFile, exception.Message);
            }
        }

        return _partyBeat.Contains($"{_difficulty}:{boss}");
    }

    private void RecordPartyBeat(string boss)
    {
        _partyBeat.Add($"{_difficulty}:{boss}");
        try
        {
            System.IO.File.WriteAllText(BeatFile, System.Text.Json.JsonSerializer.Serialize(_partyBeat));
        }
        catch (System.IO.IOException exception)
        {
            Log.Warning("Could not write {File}: {Reason}", BeatFile, exception.Message);
        }
    }

    /// <summary>
    /// No other player stood near the party at the kill. Someone in the game watching from town or a portal is no help;
    /// counting everyone in the game threw away the party's first Andariel kill because the user was there.
    /// </summary>
    private static bool PartyAlone(Party party)
    {
        var leaderAt = party.Leader.Game.Me.Location;
        return party.Leader.Game.Players
            .Where(player => party.Members.All(member => member.Name != player.Name))
            .All(player => player.Location == null || player.Location.Distance(leaderAt) > OutsideHelpRange || (player.Area is { } area && area != party.Area));
    }

    private const double OutsideHelpRange = 50;

    private void SaveStepFailures()
    {
        try
        {
            System.IO.File.WriteAllText(FailuresFile, System.Text.Json.JsonSerializer.Serialize(_stepFailures));
        }
        catch (System.IO.IOException exception)
        {
            Log.Warning("Could not write {File}: {Reason}", FailuresFile, exception.Message);
        }
    }
    private readonly HashSet<string> _gateWaived = [];

    private readonly HashSet<Area> _sweptThisGame = [];

    private IReadOnlyList<LevelingStep> Steps => [CubeForEveryone, .. ActOneSteps, .. ActTwoSteps, .. ActThreeSteps, .. ActFourSteps];

    private async Task<GameOutcome> PlayGame(Party party)
    {
        var started = DateTime.UtcNow;
        var progressed = false;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(_config.MaxGameMinutes) && !StrangerEscape.StopRequested && !StopFileFound())
        {
            // The game server dropped the game: nobody is in it, and every check below would answer for no one.
            if (!party.Members.Any(member => member.InGame))
            {
                Log.Warning("Nobody is in {Game} any more; starting a new game", _gameName);
                return GameOutcome.NextGame;
            }

            // A regroup left pending makes every walk stop at once: farming then failed spot after spot in two
            // seconds each, the party never moving, because nothing in between went home for it.
            if (party.RegroupRequested)
            {
                await Regroup(party);
                continue;
            }

            RefreshQuests(party);
            if (GoalReached(party))
            {
                return GameOutcome.GoalReached;
            }

            var step = Steps.FirstOrDefault(candidate => party.Members.Any(candidate.Needed));
            var lowest = party.Members.Where(member => member.InGame).Min(member => member.Level);
            if (step == null)
            {
                if (party.Members.All(member => HasFinished(member, QuestId.TerrorsEnd)) && _difficulty < GoalDifficulty)
                {
                    Log.Information("Diablo is done on {Difficulty}; the next game is on the next difficulty", _difficulty);
                    return GameOutcome.NextGame;
                }

                if (lowest < _config.TargetLevel && await Farm(party, _config.TargetLevel))
                {
                    progressed = true;
                    continue;
                }

                return progressed ? GameOutcome.NextGame : GameOutcome.Stuck;
            }

            // A step that keeps failing is above the party for now: each failure asks for two more levels first,
            // which the farming gives it, rather than skipping a step everything after it depends on.
            var baseGate = _difficulty == D2NG.Core.D2GS.Enums.Difficulty.Nightmare && step.NightmareGate is { } nightmare ? nightmare : step.Gate + GateOffset(_difficulty);
            // In Hell every step opens from 50: farming Hell act 1 at 34k experience a minute took three hours a level past 50,
            // and the later acts' monsters are worth far more per kill. Failures still ask for more, as everywhere.
            if (_difficulty == D2NG.Core.D2GS.Enums.Difficulty.Hell)
            {
                baseGate = HellStepLevel;
            }
            // In Hell a level per failure, up to ten: every step opens at 50 there, and capped at one Andariel's chickens
            // sent the party back at 51 game after game without the farming in between that makes it stronger.
            var failures = _stepFailures.GetValueOrDefault(FailureKey(step));
            var gate = baseGate + (_difficulty == D2NG.Core.D2GS.Enums.Difficulty.Hell ? Math.Min(HellMaxFailureLevels, failures) : Math.Min(MaxFailureLevels, 2 * failures));
            if (lowest < gate && !_gateWaived.Contains(step.Name))
            {
                if (await Farm(party, gate))
                {
                    progressed = true;
                    continue;
                }

                // A farm cut short by a trip home is not a farm with nowhere to go: waiving the gate for it sent the
                // party straight back to Andariel when the barbarian ran dry on the way to Catacombs 3.
                if (party.RegroupRequested)
                {
                    await Regroup(party);
                    progressed = true;
                    continue;
                }

                if (!progressed)
                {
                    // Nothing to farm even in a fresh game: the gate is a margin, not a wall.
                    Log.Information("No farming spot for level {Level}; taking {Step} below its gate {Gate}", lowest, step.Name, gate);
                    _gateWaived.Add(step.Name);
                }
                else
                {
                    return GameOutcome.NextGame;
                }
            }

            Log.Information("Step {Step} for {Members}", step.Name, string.Join(", ", party.Members.Where(step.Needed).Select(member => member.Name)));
            var stepStarted = DateTime.UtcNow;
            var stepMembers = party.Members.Where(member => member.InGame && step.Needed(member)).ToList();
            foreach (var member in stepMembers)
            {
                D2NG.Core.Tracing.TraceEvents.Step(member.Game, step.Name, D2NG.Core.Tracing.StepPhase.Start, _stepFailures.GetValueOrDefault(FailureKey(step)) + 1);
            }

            // With nobody left in the game every "all members finished" check is true for no one.
            var done = await step.Run(party) && party.Members.Any(member => member.InGame);
            if (StopFileFound())
            {
                return GameOutcome.NextGame;
            }

            if (party.RegroupRequested)
            {
                // A step that sends the party home - out of potions, the leader chickened - is above it as surely
                // as one that fails: counting it raises the gate, so the party farms first rather than walking
                // back into the Catacombs to drink its belt dry again.
                _stepFailures[FailureKey(step)] = _stepFailures.GetValueOrDefault(FailureKey(step)) + 1;
                _gateWaived.Remove(step.Name);
                CountDiabloAttempt(step, killed: false);
                party.FledFrom = party.Area;
                Log.Information("{Step} sent the party home ({Count} times now)", step.Name, _stepFailures[FailureKey(step)]);
                SaveStepFailures();
                await Regroup(party);
                continue;
            }

            Log.Information("TIMING step {Step} {Outcome} in {Seconds:0}s", step.Name, done ? "ok" : "FAILED", (DateTime.UtcNow - stepStarted).TotalSeconds);
            foreach (var member in stepMembers.Where(member => member.InGame))
            {
                D2NG.Core.Tracing.TraceEvents.Step(member.Game, step.Name, D2NG.Core.Tracing.StepPhase.End, _stepFailures.GetValueOrDefault(FailureKey(step)) + 1,
                    done ? D2NG.Core.Tracing.StepOutcome.Ok : D2NG.Core.Tracing.StepOutcome.Failed, duration: DateTime.UtcNow - stepStarted);
            }
            CountDiabloAttempt(step, done);
            if (done)
            {
                progressed = true;
                _stepFailures.Remove(FailureKey(step));
                SaveStepFailures();
                // Diablo is dead in this game: another kill needs a new one.
                if (step.Name == "Diablo" && MoreDiabloKills)
                {
                    return GameOutcome.NextGame;
                }
            }
            else
            {
                _stepFailures[FailureKey(step)] = _stepFailures.GetValueOrDefault(FailureKey(step)) + 1;
                _gateWaived.Remove(step.Name);
                SaveStepFailures();
                // A failed step leaves the map in an unknown state; the next attempt gets a fresh game.
                return GameOutcome.NextGame;
            }
        }

        return GameOutcome.NextGame;
    }

    /// <summary>
    /// A member is back in town after a chicken or a death: everyone goes home, restocks, and the step is
    /// taken up again together. Not a failure of the step - nothing about it went wrong.
    /// </summary>
    private async Task Regroup(Party party)
    {
        Log.Information("Regrouping in town: {Waiting} waited there", string.Join(", ", party.Members.Where(member => member.Mode == FollowMode.Off).Select(member => member.Name)));
        party.RegroupRequested = false;
        await party.GoHome();

        // One still out there would walk home alone through whatever sent the others back; leaving the game puts
        // it in town in a few seconds, and KeepPartyUp rejoins it.
        // By distance from the leader, who is in town by now: right after a rejoin the client's area still lags.
        foreach (var member in party.Members.Where(member => member.InGame && !member.IsLeader && party.Leader.Game.IsInTown()
            && member.Game.Me.Location.Distance(party.Leader.Game.Me.Location) > 60))
        {
            Log.Information("{Character} is outside town at the regroup; it rejoins in town", member.Name);
            await member.Game.LeaveGame();
        }

        await KeepPartyUp(party);
        party.RegroupRequested = false;

        // A leader that chickened was out of the game at the first GoHome, which left the party's area at the
        // level it fled: the next step then thought it stood in Catacombs 2 while the leader was in town.
        await party.GoHome();
        foreach (var member in party.Members)
        {
            member.Mode = FollowMode.Follow;
        }

        await VisitTown(party);

        // A member that rejoined knows none of its waypoints until it opens a waypoint menu again.
        await party.LearnWaypoints();
    }

    /// <summary>Diablo kills made this run once his quest was done, and whether an attempt failed or sent the party home.</summary>
    private int _diabloKills;

    private bool _diabloTroubled;

    /// <summary>
    /// What <see cref="LevelingConfiguration.DiabloKills"/> asks for; a single kill becomes two once an attempt went wrong, to see
    /// the fix hold. A larger number stays as it is: a restart for the last of five turned it into two more.
    /// </summary>
    private int DiabloKillsWanted => _config.DiabloKills == 1 && _diabloTroubled ? 2 : _config.DiabloKills;

    /// <summary>Whether <see cref="LevelingConfiguration.DiabloKills"/> asks for another kill in the goal's difficulty.</summary>
    private bool MoreDiabloKills => _difficulty == GoalDifficulty && _diabloKills < DiabloKillsWanted;

    private void CountDiabloAttempt(LevelingStep step, bool killed)
    {
        if (step.Name != "Diablo" || _config.DiabloKills == 0)
        {
            return;
        }

        _diabloKills += killed ? 1 : 0;
        _diabloTroubled |= !killed;
        Log.Information("Diablo runs: {Kills} of {Wanted} kills made", _diabloKills, DiabloKillsWanted);
    }

    private bool GoalReached(Party party) => _config.Until switch
    {
        LevelingGoal.DenOfEvil => party.Members.All(member => HasFinished(member, QuestId.DenOfEvil)),
        LevelingGoal.ActOne => party.Members.All(member => HasFinished(member, QuestId.SistersToTheSlaughter)),
        LevelingGoal.ActTwo => party.Members.All(member => HasFinished(member, QuestId.TheSevenTombs)),
        LevelingGoal.ActThree => party.Members.All(member => HasFinished(member, QuestId.TheGuardian)),
        _ => _difficulty == GoalDifficulty && party.Members.All(member => HasFinished(member, QuestId.TerrorsEnd))
            && party.Members.Where(member => member.InGame).All(member => member.Level >= _config.TargetLevel) && !MoreDiabloKills
    };

    /// <summary>The quest gates are Normal's; a difficulty up the monsters are 25 levels stronger, less what gear makes up.</summary>
    private static int GateOffset(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Nightmare => 20,
        Difficulty.Hell => 40,
        _ => 0
    };

    private static void RefreshQuests(Party party)
    {
        foreach (var member in party.Members.Where(member => member.InGame))
        {
            member.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1.5));
        }
    }

    /// <summary>
    /// Den of Evil completes when every monster in it is dead, and the credit reaches every party member
    /// in act 1 (D2MOO A1Q1.cpp). The party walks there, sweeps the cave until the credit shows, walks
    /// home and collects the skill point from Akara.
    /// </summary>
    private async Task<bool> DenOfEvil(Party party)
    {
        if (!await party.TravelTo(Area.DenOfEvil))
        {
            return false;
        }

        var lastAsked = DateTime.MinValue;
        var credited = await party.Sweep(DenSweepSpacing, () =>
        {
            if (DateTime.UtcNow - lastAsked > TimeSpan.FromSeconds(10))
            {
                lastAsked = DateTime.UtcNow;
                party.Leader.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
            }

            return Task.FromResult(DenCredited(party.Leader));
        }, DenLimit, hunt: true);
        Log.Information("Den of Evil sweep {Outcome}", credited ? "earned the credit" : "ended WITHOUT the credit");

        if (!await party.TravelTo(Area.RogueEncampment))
        {
            return false;
        }

        foreach (var member in party.Members.Where(member => member.InGame && !HasFinished(member, QuestId.DenOfEvil)))
        {
            await SayToNpc(member, NPCCode.Akara, AkaraRewardsDen);
            member.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(2));
            Log.Information("{Character} {Outcome} the Den of Evil", member.Name, HasFinished(member, QuestId.DenOfEvil) ? "finished" : "did NOT finish");
        }

        await Task.WhenAll(party.Members.Select(PointSpender.Spend));
        await VisitTown(party);
        return party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.DenOfEvil));
    }

    private static bool DenCredited(PartyMember member)
        => HasFinished(member, QuestId.DenOfEvil) || member.Game.Quests.IsAwaitingReward(QuestId.DenOfEvil)
            || member.Game.Quests.IsCreditedThisGame(QuestId.DenOfEvil);

    private async Task SayToNpc(PartyMember member, NPCCode code, uint message)
    {
        var game = member.Game;
        var path = await pathingService.GetPathToNPC(game, code, MovementMode.Walking);
        if (path.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(game, path, MovementMode.Walking);
        }

        WorldObject npc = null;
        GeneralHelpers.TryWithTimeout(_ => (npc = NPCHelpers.GetUniqueNPC(game, code)) != null, TimeSpan.FromSeconds(5));
        if (npc == null)
        {
            Log.Warning("{Character} cannot see {Npc}", member.Name, code);
            return;
        }

        await game.MoveToAsync(npc);
        game.InteractWithNPC(npc);
        await Task.Delay(200);
        game.InitiateEntityChat(npc);
        GeneralHelpers.TryWithTimeout(_ => game.GetOfferedMessages(npc).Contains(message), TimeSpan.FromSeconds(2));
        Log.Information("{Npc} offers {Offered} to {Character}; saying 0x{Message:X4}", code,
            string.Join(", ", game.GetOfferedMessages(npc).Select(offered => $"0x{offered:X4}")), member.Name, message);
        game.SendQuestMessage(npc, message);
        await Task.Delay(500);
        game.TerminateEntityChat(npc);
        await Task.Delay(300);
    }
}
