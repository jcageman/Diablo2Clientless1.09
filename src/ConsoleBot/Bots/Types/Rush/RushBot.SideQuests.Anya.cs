using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
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
/// Prison of Ice (D2MOO A5Q3.cpp). Malah hands one potion per game (message 20127) to a player without one;
/// operating frozen Anya - put at the preset spot in the Frozen River - with it frees her, and the credit
/// spreads to the party members standing in act 5. Each of them then gets the Scroll of Resistance from
/// Malah (20132), reads it, and takes the rare item Anya hands out in town (20136).
/// </summary>
public sealed partial class RushBot
{
    private const uint MalahIntroducesAnya = 0x4E94;
    private const uint MalahGivesPotion = 0x4E9F;
    private const uint MalahGivesScroll = 0x4EA4;
    private const uint AnyaGivesItem = 0x4EA8;
    private const uint AnyaOpensNihlathaksPortal = 0x4EA9;
    private static readonly TimeSpan AnyaArrivesWithin = TimeSpan.FromSeconds(40);
    private static readonly (int X, int Y) AnyaTownOffset = (106, 115);

    private async Task<bool> Anya(Client rusher, Client creator, List<ParticipantSession> needers)
    {
        var name = creator.Game.Me.Name;
        var inActFive = await Task.WhenAll(needers.Select(session => MoveRusheeToAct(session.Client, Act.Act5)));
        if (!inActFive.All(moved => moved) || !creator.Game.IsInTown())
        {
            Log.Warning("Not everyone who needs Anya reached Harrogath");
            return false;
        }

        if (!Holds(creator, ItemName.MalahsPotion))
        {
            await MakeRoom(creator, 1, 1);
            await SayToNpc(creator, NPCCode.Malah, MalahIntroducesAnya);
            await SayToNpc(creator, NPCCode.Malah, MalahGivesPotion);
            GeneralHelpers.TryWithTimeout(_ => Holds(creator, ItemName.MalahsPotion), TimeSpan.FromSeconds(3));
        }

        if (!Holds(creator, ItemName.MalahsPotion))
        {
            Log.Warning("{Creator} did not get Malah's potion", name);
            return false;
        }

        if (!await ReachArea(rusher, Area.FrozenRiver)
            || !await MoveToObject(rusher, EntityCode.AnyaStartOutsideTown, Area.FrozenRiver))
        {
            Log.Warning("The rusher could not reach Anya in the Frozen River");
            await ReturnToTown(rusher);
            return false;
        }

        await ClearAround(rusher, WaypointClearRadius, WaypointClearLimit);
        if (!await _townManagementService.CreateTownPortal(rusher) || !await FerryRushee(rusher, creator, Area.FrozenRiver))
        {
            Log.Warning("{Creator} could not be brought to Anya", name);
            await ReturnToTown(rusher);
            return false;
        }

        var arrival = creator.Game.Me.Location;
        var freed = await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            await ClearTick(rusher, 30);
            var anya = creator.Game.GetEntityByCode(EntityCode.FrozenAnya)
                .OrderBy(candidate => candidate.Location.Distance(creator.Game.Me.Location))
                .FirstOrDefault();
            if (anya == null)
            {
                await MoveToObject(creator, EntityCode.AnyaStartOutsideTown, Area.FrozenRiver);
                return false;
            }

            if (creator.Game.Me.Location.Distance(anya.Location) > 3)
            {
                await creator.Game.MoveToAsync(anya);
                return false;
            }

            if (attempt % 3 == 0)
            {
                // Freeing her deletes the potion with the removal a lift gets; marked, it is not taken for a lift.
                creator.Game.ExpectItemsTaken(creator.Game.Inventory.Items.Where(item => item.Name == ItemName.MalahsPotion));
                creator.Game.InteractWithEntity(anya);
            }

            await Task.Delay(400);
            if (attempt % 3 == 2)
            {
                creator.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
            }

            return !Holds(creator, ItemName.MalahsPotion) || HasKillCredit(creator, QuestId.PrisonOfIce);
        }, TimeSpan.FromSeconds(40));
        Log.Information("{Creator} {Outcome} Anya", name, freed ? "freed" : "did NOT free");
        _anyaFreedThisGame |= freed;
        await RusheeHome(rusher, creator, arrival);
        await ReturnToTown(rusher);
        await RefreshQuestData(needers);
        return freed && HasKillCredit(creator, QuestId.PrisonOfIce);
    }

    private static async Task ReadScrollOfResistance(Client client)
    {
        var scroll = client.Game.Inventory.FindItemByName(ItemName.ScrollofResistance);
        if (scroll == null)
        {
            return;
        }

        client.Game.ActivateInventoryItem(scroll);
        var read = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await Task.Delay(100);
            return client.Game.Inventory.FindItemByName(ItemName.ScrollofResistance) == null;
        }, TimeSpan.FromSeconds(3));
        Log.Information("{Client} {Outcome} its Scroll of Resistance (item {Id} at {Location})", client.Game.Me.Name,
            read ? "read" : "could NOT read", scroll.Id, scroll.Location);
    }

    /// <summary>The scroll from Malah, read at once, then Anya's item.</summary>
    private async Task CollectAnyaReward(Client client)
    {
        var name = client.Game.Me.Name;
        if (!await MoveRusheeToAct(client, Act.Act5) || !client.Game.IsInTown())
        {
            Log.Warning("{Client} could not reach Harrogath for Anya's reward", name);
            return;
        }

        // Malah hands a fresh scroll on every ask while the reward is pending; one carried already is enough.
        await MakeRoom(client, 2, 4);
        if (!Holds(client, ItemName.ScrollofResistance))
        {
            await SayToNpc(client, NPCCode.Malah, MalahGivesScroll);
            GeneralHelpers.TryWithTimeout(_ => Holds(client, ItemName.ScrollofResistance), TimeSpan.FromSeconds(3));
        }

        var scroll = client.Game.Inventory.FindItemByName(ItemName.ScrollofResistance);
        await ReadScrollOfResistance(client);

        // Anya only comes to town some seconds after Malah's first handout, and a client sees her only near her
        // own spot: waiting by Malah, nobody ever saw her arrive.
        // She is no preset of the town map; seen live at (106, 115) from Harrogath's origin, which never moves.
        var town = await _mapApiService.GetArea(client.Game.MapId, MapDifficulty, Area.Harrogath);
        var anyaSpot = new Point((ushort)(town.LevelOrigin.X + AnyaTownOffset.X), (ushort)(town.LevelOrigin.Y + AnyaTownOffset.Y));
        await MoveTo(client, anyaSpot, MovementMode.Walking);

        await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
            }

            return client.Game.GetNPCsByCode(NPCCode.Anya).Count > 0;
        }, AnyaArrivesWithin);
        // Her item is made straight into the inventory and, without room, simply not made: a class weapon up to 2x4.
        await MakeRoom(client, 2, 4);
        await SayToNpc(client, NPCCode.Anya, AnyaGivesItem);

        // With Anya's item had, Malah closes the quest.
        await SayToNpc(client, NPCCode.Malah, MalahGivesScroll);
        client.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(2));
        Log.Information("{Client} {Outcome} Prison of Ice: 0x{Flags:X4}, scroll {Scroll}", name,
            HasFinished(client, QuestId.PrisonOfIce) ? "finished" : "did not finish", client.Game.Quests.GetCharacterFlags(QuestId.PrisonOfIce),
            scroll == null ? "not received" : client.Game.Inventory.FindItemByName(ItemName.ScrollofResistance) == null ? "read" : "still carried");
    }
}
