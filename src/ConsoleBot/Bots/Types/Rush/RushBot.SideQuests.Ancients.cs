using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Quest;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Rush;

/// <summary>
/// Rite of Passage (redivivus A5Q5). Operating the altar on Arreat Summit asks a question (message 0x4E22);
/// answering it wakes the three Ancients. When the last one dies, every player of level (difficulty + 1) x 20
/// or more standing on the summit is credited, and so are their party members of that level anywhere in
/// act 5. Portals onto the summit stop working once the fight is on, so everyone who fights is there first.
/// </summary>
public sealed partial class RushBot
{
    private const uint AncientsAltarQuestion = 0x4E22;
    private static readonly TimeSpan AncientsFightLimit = TimeSpan.FromMinutes(5);

    private async Task<bool> Ancients(Client rusher, Client creator, List<ParticipantSession> needers)
    {
        var name = creator.Game.Me.Name;
        var fighters = needers.Select(session => session.Client).ToList();
        if (!fighters.Contains(creator))
        {
            fighters.Insert(0, creator);
        }

        // Everyone in the game helps: the credit goes to whoever stands on the summit, whoever lands the blow, and
        // three Hell Ancients with their immunities outlasted the two who needed them and the rusher for five
        // minutes.
        fighters.AddRange(_gameRushees.Select(session => session.Client)
            .Where(client => !fighters.Contains(client) && client.Game.IsInGame()));

        await BuffBeforeFight(rusher, Area.ArreatSummit, fighters);
        var inActFive = await Task.WhenAll(fighters.Select(fighter => MoveRusheeToAct(fighter, Act.Act5)));
        if (!inActFive[fighters.IndexOf(creator)])
        {
            Log.Warning("{Creator} could not reach Harrogath for the Ancients", name);
            return false;
        }

        fighters = fighters.Where((_, index) => inActFive[index]).ToList();
        if (!await ReachArea(rusher, Area.ArreatSummit) || !await MoveToObject(rusher, EntityCode.AncientsAltar, Area.ArreatSummit))
        {
            Log.Warning("The rusher could not reach the Ancients' altar");
            await ReturnToTown(rusher);
            return false;
        }

        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            await ReturnToTown(rusher);
            return false;
        }

        var portalSpot = rusher.Game.Me.Location;
        var arrived = await Task.WhenAll(fighters.Select(fighter => FerryRushee(rusher, fighter, Area.ArreatSummit)));
        var present = fighters.Where((_, index) => arrived[index]).ToList();
        if (!present.Contains(creator))
        {
            Log.Warning("{Creator} did not reach Arreat Summit", name);
            await Task.WhenAll(present.Select(fighter => ReturnToTown(fighter)));
            await ReturnToTown(rusher);
            return false;
        }

        var woken = false;
        for (var attempt = 0; attempt < 3 && !woken; attempt++)
        {
            var altar = creator.Game.GetEntityByCode(EntityCode.AncientsAltar)
                .OrderBy(candidate => candidate.Location.Distance(creator.Game.Me.Location))
                .FirstOrDefault();
            if (altar == null)
            {
                await MoveToObject(creator, EntityCode.AncientsAltar, Area.ArreatSummit);
                continue;
            }

            await creator.Game.MoveToAsync(altar);
            creator.Game.InteractWithEntity(altar);
            await Task.Delay(600);
            creator.Game.SendQuestMessage(altar, AncientsAltarQuestion);
            woken = await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(250);
                return NearbyHostiles(creator, 60).Count > 0;
            }, TimeSpan.FromSeconds(10));
            Log.Information("{Creator} touched the Ancients' altar: the Ancients {Outcome}", name, woken ? "woke" : "did NOT wake");
        }

        var deadline = DateTime.Now + AncientsFightLimit;
        var done = false;
        var nextCheck = DateTime.Now;
        while (woken && DateTime.Now < deadline && !done)
        {
            await Task.WhenAll(present.Append(rusher).Select(async fighter =>
            {
                var target = NearbyHostiles(fighter, 50).FirstOrDefault();
                if (target != null)
                {
                    await AttackUpClose(fighter, target);
                }
                else
                {
                    await Task.Delay(200);
                }
            }));
            if (DateTime.Now >= nextCheck)
            {
                creator.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
                done = HasFinished(creator, QuestId.RiteOfPassage);
                nextCheck = DateTime.Now + TimeSpan.FromSeconds(3);
            }
        }

        Log.Information("Ancients {Outcome} with {Fighters}", done ? "defeated" : "NOT defeated",
            string.Join(", ", present.Select(fighter => fighter.Game.Me.Name)));
        await Task.WhenAll(present.Select(fighter => RusheeHome(rusher, fighter, portalSpot)));
        await ReturnToTown(rusher);
        await RefreshQuestData(needers);
        return done;
    }
}
