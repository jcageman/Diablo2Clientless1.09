using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Quest;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Rush;

/// <summary>
/// The Search for Cain (D2MOO A1Q4.cpp). The Inifuss tree in the Dark Wood drops the Scroll of Inifuss for a
/// player who needs the quest; Akara turns it into the Key to the Cairn Stones (message 112). The five stones in
/// the Stony Field take the key in an order rolled per game: a stone out of turn is ignored without resetting the
/// sequence, and one in turn lights up. After the fifth a permanent portal to Tristram opens beside the stones.
/// Operating Cain's gibbet there credits the player and every party member in act 1; Akara pays a ring (118).
/// </summary>
public sealed partial class RushBot
{
    private const uint AkaraDeciphersScroll = 0x70;
    private const uint AkaraRewardsCain = 0x76;
    private static readonly TimeSpan CairnStonesLimit = TimeSpan.FromSeconds(60);

    private static readonly EntityCode[] CairnStones =
    [
        EntityCode.CairnStoneAlpha, EntityCode.CairnStoneBeta, EntityCode.CairnStoneGamma,
        EntityCode.CairnStoneDelta, EntityCode.CairnStoneLambda,
    ];

    private async Task<bool> Cain(Client rusher, Client creator, List<ParticipantSession> needers)
    {
        var name = creator.Game.Me.Name;
        if (!Holds(creator, ItemName.KeytotheCairnStones))
        {
            await FetchQuestItem(rusher, creator, needers, Area.DarkWood, EntityCode.InifussTree, ItemName.ScrollofInifuss,
                NPCCode.Akara, AkaraDeciphersScroll, QuestId.TheSearchForCain);
            GeneralHelpers.TryWithTimeout(_ => Holds(creator, ItemName.KeytotheCairnStones), TimeSpan.FromSeconds(3));
            if (!Holds(creator, ItemName.KeytotheCairnStones))
            {
                Log.Warning("{Creator} did not get the Key to the Cairn Stones from Akara", name);
                return false;
            }
        }

        if (!await OpenTristram(rusher, creator))
        {
            return false;
        }

        return await FreeCain(rusher, creator, needers);
    }

    private static WorldObject TristramPortal(Client client) =>
        client.Game.GetEntityByCode(EntityCode.RedTownPortal).FirstOrDefault(portal => portal.TownPortalArea == Area.Tristram);

    /// <summary>The creator works the stones with the key while everyone else in the game guards it.</summary>
    private async Task<bool> OpenTristram(Client rusher, Client creator)
    {
        var name = creator.Game.Me.Name;
        await BuffBeforeFight(rusher, Act.Act1, _gameRushees.Select(session => session.Client));
        if (!await ReachArea(rusher, Area.StonyFields) || !await MoveToObject(rusher, EntityCode.CairnStoneAlpha, Area.StonyFields))
        {
            Log.Warning("The rusher could not reach the Cairn Stones");
            await ReturnToTown(rusher);
            return false;
        }

        await ClearAround(rusher, WaypointClearRadius, WaypointClearLimit);
        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            await ReturnToTown(rusher);
            return false;
        }

        var arrival = rusher.Game.Me.Location;
        var present = await FerryEveryone(rusher, creator, Area.StonyFields);
        await ReturnToTown(rusher);
        if (!present.Contains(creator))
        {
            Log.Warning("{Creator} could not be brought to the Cairn Stones", name);
            await Task.WhenAll(present.Select(member => RusheeHome(rusher, member, arrival)));
            return false;
        }

        using var guarding = new System.Threading.CancellationTokenSource();
        var guards = present.Where(member => member != creator)
            .Select(member => GuardSpot(member, arrival, guarding.Token)).ToList();

        // The last stone deletes the key; marked, its removal is not taken for a lift.
        creator.Game.ExpectItemsTaken(creator.Game.Inventory.Items.Where(item => item.Name == ItemName.KeytotheCairnStones));
        var clicks = 0;
        var opened = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (TristramPortal(creator) != null)
            {
                return true;
            }

            await ClearUpClose(creator, 12, TimeSpan.FromSeconds(5));
            var stones = CairnStones
                .Select(code => creator.Game.GetEntityByCode(code)
                    .OrderBy(stone => stone.Location.Distance(creator.Game.Me.Location))
                    .FirstOrDefault())
                .Where(stone => stone != null)
                .ToList();
            if (stones.Count < CairnStones.Length)
            {
                await MoveToObject(creator, EntityCode.CairnStoneAlpha, Area.StonyFields);
                return false;
            }

            // Every unlit stone once per round: the one whose turn it is lights up, the rest ignore the click,
            // so five rounds at most go through any order.
            foreach (var stone in stones.Where(stone => stone.State == EntityState.Alive))
            {
                if (creator.Game.Me.Location.Distance(stone.Location) > 3)
                {
                    await creator.Game.MoveToAsync(stone);
                }

                creator.Game.InteractWithEntity(stone);
                clicks++;
                await Task.Delay(400);
                Log.Information("{Creator} touched {Stone}: {Outcome}", name, stone.Code,
                    stone.State == EntityState.Alive ? "not its turn" : "lit");
            }

            return GeneralHelpers.TryWithTimeout(_ => TristramPortal(creator) != null,
                TimeSpan.FromSeconds(stones.All(stone => stone.State != EntityState.Alive) ? 8 : 1));
        }, CairnStonesLimit);
        Log.Information("{Creator} {Outcome} the portal to Tristram after {Clicks} stone clicks", name,
            opened ? "opened" : "did NOT open", clicks);
        await guarding.CancelAsync();
        await Task.WhenAll(guards);
        await Task.WhenAll(present.Select(member => RusheeHome(rusher, member, arrival)));
        return opened;
    }

    /// <summary>The rusher goes through the stones' portal and opens its own beside the gibbet for the rest.</summary>
    private async Task<bool> FreeCain(Client rusher, Client creator, List<ParticipantSession> needers)
    {
        var name = creator.Game.Me.Name;
        await Task.WhenAll(needers.Select(session => MoveRusheeToAct(session.Client, Act.Act1)));
        if (!await ReachArea(rusher, Area.StonyFields)
            || !await MoveToObject(rusher, EntityCode.CairnStoneAlpha, Area.StonyFields)
            || !await StepThroughObject(rusher, EntityCode.RedTownPortal, Area.Tristram)
            || !await MoveToObject(rusher, EntityCode.CainGibbet, Area.Tristram))
        {
            Log.Warning("The rusher could not reach Cain's gibbet in Tristram");
            await ReturnToTown(rusher);
            return false;
        }

        await ClearAround(rusher, WaypointClearRadius, WaypointClearLimit);
        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            await ReturnToTown(rusher);
            return false;
        }

        var arrival = rusher.Game.Me.Location;
        var present = await FerryEveryone(rusher, creator, Area.Tristram);
        await ReturnToTown(rusher);
        if (!present.Contains(creator))
        {
            Log.Warning("{Creator} could not be brought to Cain's gibbet", name);
            await Task.WhenAll(present.Select(member => RusheeHome(rusher, member, arrival)));
            return false;
        }

        using var guarding = new System.Threading.CancellationTokenSource();
        var guards = present.Where(member => member != creator)
            .Select(member => GuardSpot(member, arrival, guarding.Token)).ToList();
        var freed = await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            await ClearUpClose(creator, 12, TimeSpan.FromSeconds(10));
            var gibbet = creator.Game.GetEntityByCode(EntityCode.CainGibbet)
                .OrderBy(candidate => candidate.Location.Distance(creator.Game.Me.Location))
                .FirstOrDefault();
            if (gibbet == null)
            {
                await MoveToObject(creator, EntityCode.CainGibbet, Area.Tristram);
                return false;
            }

            if (creator.Game.Me.Location.Distance(gibbet.Location) > 3)
            {
                if (attempt % 3 == 2)
                {
                    await MovementHelpers.MoveToWorldObject(creator.Game, _pathingService, _mapApiService, gibbet, MovementMode.Walking);
                }
                else
                {
                    await creator.Game.MoveToAsync(gibbet);
                }

                return false;
            }

            creator.Game.CleanupCursorItem(force: attempt > 0);
            creator.Game.InteractWithEntity(gibbet);
            await Task.Delay(600);
            creator.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
            return HasKillCredit(creator, QuestId.TheSearchForCain);
        }, TimeSpan.FromSeconds(40));
        Log.Information("{Creator} {Outcome} Cain", name, freed ? "freed" : "did NOT free");
        await guarding.CancelAsync();
        await Task.WhenAll(guards);
        await Task.WhenAll(present.Select(member => RusheeHome(rusher, member, arrival)));
        await RefreshQuestData(needers);
        Log.Information("Cain freed by {Creator}: {States}", name,
            string.Join(", ", needers.Select(session => $"{session.Client.Game.Me.Name} 0x{session.Client.Game.Quests.GetCharacterFlags(QuestId.TheSearchForCain):X4}")));
        return freed;
    }

    /// <summary>Everyone in the game through the rusher's portal, the creator first; returns who arrived.</summary>
    private async Task<List<Client>> FerryEveryone(Client rusher, Client creator, Area area)
    {
        var party = _gameRushees.Select(session => session.Client).Where(client => client.Game.IsInGame()).ToList();
        party.Remove(creator);
        party.Insert(0, creator);
        var came = await Task.WhenAll(party.Select(member => FerryRushee(rusher, member, area)));
        return party.Where((_, index) => came[index]).ToList();
    }
}
