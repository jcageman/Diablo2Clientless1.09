using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Quest;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Act 3: Khalim's eye, brain and heart from their chests, the flail from the High Council in Travincal, the
/// four transmuted into Khalim's Will, the Compelling Orb smashed with it to open the Durance, and Mephisto,
/// whose red portal leads to act 4. The rush bot never needed this: its rusher opens the way in its own game.
/// </summary>
public sealed partial class LevelingBot
{
    /// <summary>objects.txt 405-407: the three chests, one each in the Spider Cavern, Flayer Dungeon 3 and Sewers 2.</summary>
    private static readonly EntityCode[] KhalimChests = [(EntityCode)405, (EntityCode)406, (EntityCode)407];
    private const EntityCode SewerLever = (EntityCode)367;
    private const EntityCode SewerStairs = (EntityCode)366;

    private static readonly NPCCode[] HighCouncil = [NPCCode.CouncilMember1, NPCCode.CouncilMember2, NPCCode.CouncilMember3];

    private IReadOnlyList<LevelingStep> ActThreeSteps =>
    [
        WaypointStep(Waypoint.SpiderForest, 20),
        new("Khalim's Eye", member => InActThree(member) && NeedsPart(ItemName.KhalimsEye, member), party =>
            FetchFromKhalimChest(party, Area.SpiderCavern, ItemName.KhalimsEye), Gate: 21),
        WaypointStep(Waypoint.FlayerJungle, 21),
        new("Khalim's Brain", member => InActThree(member) && NeedsPart(ItemName.KhalimsBrain, member), party =>
            FetchFromKhalimChest(party, Area.FlayerDungeonLevel3, ItemName.KhalimsBrain), Gate: 22),
        WaypointStep(Waypoint.LowerKurast, 22),
        WaypointStep(Waypoint.KurastBazaar, 23),
        new("Khalim's Heart", member => InActThree(member) && NeedsPart(ItemName.KhalimsHeart, member), KhalimsHeart, Gate: 23),
        WaypointStep(Waypoint.UpperKurast, 24),
        WaypointStep(Waypoint.Travincal, 24),
        new("Khalim's Flail", member => InActThree(member) && NeedsPart(ItemName.KhalimsFlail, member), KhalimsFlail, Gate: 25),
        new("Khalim's Will", member => InActThree(member) && !HasFinished(member, QuestId.KhalimsWill) && !member.Game.Me.AllowedWaypoints.Contains(Waypoint.DuranceOfHateLevel2), KhalimsWill, Gate: 25),
        new("Mephisto", member => InActThree(member) && !HasFinished(member, QuestId.TheGuardian), Mephisto, Gate: 25),
    ];

    private static bool InActThree(PartyMember member) => member.Game.Act == Act.Act3;

    /// <summary>A part counts as held once it is in the Will: after the transmute none of the four exist, and every part
    /// step ran again from Khalim's Eye.</summary>
    private bool NeedsPart(ItemName part, PartyMember member) => NeedsWill(member) && !LeaderHolds(ItemName.KhalimsWill) && !LeaderHolds(part);

    private static bool NeedsWill(PartyMember member) => !HasFinished(member, QuestId.KhalimsWill) && !HasFinished(member, QuestId.TheGuardian);

    private async Task<bool> FetchFromKhalimChest(Party party, Area area, ItemName item)
    {
        var map = await mapApiService.GetArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty, area);
        var chest = KhalimChests.FirstOrDefault(code => map?.Objects?.ContainsKey((int)code) == true);
        if (chest == default)
        {
            Log.Warning("No Khalim chest in the map of {Area}", area);
            chest = KhalimChests[0];
        }

        return await FetchQuestItem(party, area, chest, item);
    }

    /// <summary>The heart's chest is on Sewers level 2, behind stairs a lever on level 1 opens.</summary>
    private async Task<bool> KhalimsHeart(Party party)
    {
        if (!await party.GoTo(Area.Sewers2Level1))
        {
            return false;
        }

        if (await party.WalkToObject(SewerLever))
        {
            var leader = party.Leader;
            var lever = leader.Game.GetEntityByCode(SewerLever).OrderBy(entity => entity.Location.Distance(leader.Game.Me.Location)).FirstOrDefault();
            if (lever != null)
            {
                await MovementHelpers.MoveToWorldObject(leader.Game, pathingService, mapApiService, lever, MovementMode.Walking);
                leader.Game.InteractWithEntity(lever);
                await Task.Delay(1000);
                Log.Information("{Leader} pulled the sewer lever", leader.Name);
            }
        }

        return await FetchFromKhalimChest(party, Area.Sewers2Level2, ItemName.KhalimsHeart);
    }

    /// <summary>The High Council in Travincal; one of them drops the flail.</summary>
    private async Task<bool> KhalimsFlail(Party party)
    {
        if (!await party.GoTo(Area.Travincal))
        {
            return false;
        }

        foreach (var council in HighCouncil)
        {
            await party.KillBoss(council, TimeSpan.FromMinutes(3));
        }

        await party.Sweep(30, () => Task.FromResult(false), TimeSpan.FromMinutes(5));
        var leader = party.Leader;
        var flail = leader.Game.Items.Values.FirstOrDefault(item => item.Ground && item.Name == ItemName.KhalimsFlail);
        if (flail != null)
        {
            await MovementHelpers.MoveToLocation(leader.Game, pathingService, mapApiService, flail.Location, MovementMode.Walking);
            LevelingLoot.MakeRoomFor(leader, flail);
            leader.Game.PickupItem(flail);
            GeneralHelpers.TryWithTimeout(_ => QuestItems.Holds(leader.Client, ItemName.KhalimsFlail), TimeSpan.FromSeconds(3));
        }

        // Whoever picked it up hands it to the leader in town, where the other parts are.
        await party.GoHome();
        await VisitTown(party);
        foreach (var member in party.Members.Where(member => member != leader && member.InGame))
        {
            if (member.Game.Inventory.FindItemByName(ItemName.KhalimsFlail) is { } held)
            {
                await LevelingGear.HandQuestItem(member, leader, held);
            }
        }

        Log.Information("{Leader} {Outcome} Khalim's Flail", leader.Name, QuestItems.Holds(leader.Client, ItemName.KhalimsFlail) ? "has" : "does NOT have");
        return QuestItems.Holds(leader.Client, ItemName.KhalimsFlail);
    }

    /// <summary>The four parts in the cube make Khalim's Will; worn as a weapon it smashes the Compelling Orb.</summary>
    private async Task<bool> KhalimsWill(Party party)
    {
        var leader = party.Leader;
        var game = leader.Game;
        if (!QuestItems.Holds(leader.Client, ItemName.KhalimsWill))
        {
            // The cube holds the loot stored on the way, and a transmute with anything besides the four parts fails.
            LevelingLoot.EmptyCube(leader);
            foreach (var part in new[] { ItemName.KhalimsEye, ItemName.KhalimsBrain, ItemName.KhalimsHeart, ItemName.KhalimsFlail })
            {
                if (!QuestItems.EnsureItemInInventory(leader.Client, part))
                {
                    Log.Warning("{Leader} is missing {Part} for Khalim's Will", leader.Name, part);
                    return false;
                }

                var item = game.Inventory.FindItemByName(part);
                var space = game.Cube.FindFreeSpace(item);
                if (space == null || InventoryHelpers.PutInventoryItemInCube(game, item, space) != MoveItemResult.Succes)
                {
                    return false;
                }
            }

            var others = game.Cube.Items.Where(item => item.Name is not (ItemName.KhalimsEye or ItemName.KhalimsBrain or ItemName.KhalimsHeart or ItemName.KhalimsFlail)).ToList();
            if (others.Count > 0)
            {
                Log.Warning("The cube still holds {Items} besides Khalim's parts", string.Join(", ", others.Select(item => item.Name)));
            }

            if (!InventoryHelpers.TransmuteItemsInCube(game, true) || game.Cube.FindItemByName(ItemName.KhalimsWill) is not { } will
                || InventoryHelpers.PutCubeItemInInventory(game, will) != MoveItemResult.Succes)
            {
                Log.Warning("Transmuting Khalim's Will failed");
                return false;
            }
        }

        if (!await party.GoTo(Area.Travincal) || !await party.WalkToObject(EntityCode.CompellingOrb))
        {
            return false;
        }

        await party.FightHere(Party.EngageRadius + 10);
        // Out of the cube first: taken for a mace it went there as loot, and the orb was struck with a Gladius six times.
        QuestItems.EnsureItemInInventory(leader.Client, ItemName.KhalimsWill);
        var willItem = game.Inventory.FindItemByName(ItemName.KhalimsWill);
        if (willItem == null)
        {
            Log.Warning("{Leader} has no Khalim's Will in its inventory to smash the orb with", leader.Name);
        }
        var weapon = game.Me.Equipment.GetValueOrDefault(D2NG.Core.D2GS.Items.DirectoryType.RightHand);
        if (willItem != null && !await LevelingGear.WearInHand(leader, willItem))
        {
            return false;
        }

        var orb = game.GetEntityByCode(EntityCode.CompellingOrb).FirstOrDefault();
        var smashed = orb != null && await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            await game.MoveToAsync(orb);
            game.InteractWithEntity(orb);
            await Task.Delay(800);
            game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
            return HasFinished(leader, QuestId.KhalimsWill) || game.Quests.IsAwaitingReward(QuestId.KhalimsWill)
                || game.Quests.IsCreditedThisGame(QuestId.KhalimsWill);
        }, TimeSpan.FromSeconds(30));
        Log.Information("The Compelling Orb {Outcome}", smashed ? "is smashed" : "is NOT smashed");
        if (weapon != null && game.Inventory.FindItemById(weapon.Id) is { } previous)
        {
            await LevelingGear.WearInHand(leader, previous);
        }

        if (!await party.GoTo(Area.DuranceOfHateLevel2))
        {
            return false;
        }

        var touched = await party.TouchWaypoint();
        await party.GoHome();
        return smashed && touched;
    }

    /// <summary>Durance level 3 from the level 2 waypoint, Mephisto, then the red portal to the Pandemonium Fortress.</summary>
    private async Task<bool> Mephisto(Party party)
    {
        if (!await party.GoTo(Area.DuranceOfHateLevel2))
        {
            return false;
        }

        if (party.Area != Area.DuranceOfHateLevel3)
        {
            // The generic crossing search fails at this border; the rush bot paths from the waypoint instead.
            var path = await pathingService.GetPathFromWaypointToArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty,
                Area.DuranceOfHateLevel2, Waypoint.DuranceOfHateLevel2, Area.DuranceOfHateLevel3, MovementMode.Walking);
            if (!await party.EnterArea(Area.DuranceOfHateLevel3, path))
            {
                return false;
            }
        }

        var killed = await party.KillBoss(NPCCode.Mephisto, BossLimit);
        Log.Information("Mephisto {Outcome}", killed ? "is dead" : "is NOT dead");
        if (!killed)
        {
            return false;
        }

        if (!await party.WalkToObject(EntityCode.PandemoniumFortressPortal)
            || !await party.StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.PandemoniumFortressPortal).FirstOrDefault(), Area.ThePandemoniumFortress))
        {
            return false;
        }

        RefreshQuests(party);
        await VisitTown(party);
        return party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.TheGuardian) || member.Game.Act == Act.Act4);
    }
}
