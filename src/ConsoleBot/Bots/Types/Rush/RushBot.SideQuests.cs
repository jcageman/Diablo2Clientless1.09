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
/// The side quests a rush can pick up on the way, or do on their own for a rushee already at the
/// destination. Each one skips itself on the rushee's quest state, so a run does only what is open.
/// </summary>
public sealed partial class RushBot
{
    private const double UniqueSearchHop = 25;
    private const uint MeshifTakesFigurine = 0x211;
    private const uint AlkorTakesBird = 0x216;
    private const uint AlkorGivesPotion = 0x21A;
    private static readonly TimeSpan UniqueSearchLimit = TimeSpan.FromMinutes(3);

    /// <summary>The act 3 levels searched for the figurine's unique, nearest the docks first.</summary>
    private static readonly Area[] GoldenBirdSearchRoute = [Area.SpiderForest, Area.GreatMarsh, Area.FlayerJungle];

    private async Task<bool> RunSideQuests(Client rusher, List<ParticipantSession> rushees)
    {
        foreach (var session in rushees)
        {
            // A rushee that fights shops first, in whatever town it starts the side work in.
            if (_rushConfiguration.RusheeFights && session.Client.Game.IsInTown())
            {
                await RestockFighter(session.Client, session.Entry.Account, session.Client.Game.Act);
            }

            if (_rushConfiguration.DoesSideQuest(RushSideQuest.GoldenBird))
            {
                await SideQuest("golden-bird", session.Client, () => GoldenBird(rusher, session.Client));
            }
        }

        return true;
    }

    /// <summary>A side quest that goes wrong is logged and the next one tried; none of them is the rush.</summary>
    private async Task SideQuest(string step, Client rushee, Func<Task<bool>> quest)
    {
        try
        {
            await Timed($"{step}-{rushee.Game.Me.Name}", quest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Warning(exception, "Side quest {Step} failed for {Rushee}", step, rushee.Game.Me.Name);
            await ReturnToTown(rushee);
        }
    }

    /// <summary>
    /// The Jade Figurine drops from the first unique monster that comes into being in act 3 once the quest
    /// is armed, and only when its killer - a hireling counts as its owner - has not finished the quest
    /// (D2MOO QUESTS_SetJadeFigurineBoss and A3Q4.cpp). There is no roll: the right kill always drops it,
    /// and the wrong one costs the figurine for the whole game.
    /// </summary>
    private async Task<bool> GoldenBird(Client rusher, Client rushee)
    {
        var name = rushee.Game.Me.Name;
        if (HasFinished(rushee, QuestId.TheGoldenBird))
        {
            Log.Information("{Rushee} has finished the Golden Bird: 0x{Flags:X4}", name, rushee.Game.Quests.GetCharacterFlags(QuestId.TheGoldenBird));
            return true;
        }

        // Half done from an earlier game - the figurine or the bird carried, or the reward waiting at Alkor -
        // needs only the town part.
        if (rushee.Game.Quests.IsAwaitingReward(QuestId.TheGoldenBird)
            || Holds(rushee, ItemName.AJadeFigurine)
            || Holds(rushee, ItemName.TheGoldenBird))
        {
            return await TurnInGoldenBird(rushee);
        }

        if (!_rushConfiguration.RusheeFights)
        {
            Log.Information("Skipping the Golden Bird for {Rushee}: only its own kill drops the figurine and rusheeFights is off", name);
            return true;
        }

        if (!await MoveRusheeToAct(rushee, Act.Act3)
            || (rusher.Game.Act != Act.Act3 && !await _townManagementService.SwitchAct(rusher, Act.Act3)))
        {
            Log.Warning("The Golden Bird needs both {Rushee} and the rusher in act 3", name);
            return false;
        }

        // The rusher only opens the way and leaves: its hireling must not be anywhere near the unique.
        var start = GoldenBirdSearchRoute[0];
        if (!await TakeWaypointFromTown(rusher, Waypoint.SpiderForest)
            || !await ConfirmInArea(rusher, start)
            || !await _townManagementService.CreateTownPortal(rusher)
            || !await _townManagementService.TakeTownPortalToTown(rusher)
            || !await FerryRushee(rusher, rushee, start))
        {
            Log.Warning("{Rushee} could not be brought to {Area} for the Golden Bird", name, start);
            return false;
        }

        var arrival = rushee.Game.Me.Location;
        var unique = await FindFirstUnique(rushee);
        if (unique == null)
        {
            Log.Warning("{Rushee} found no unique monster for the Golden Bird", name);
            await RusheeHome(rusher, rushee, arrival);
            return false;
        }

        Log.Information("{Rushee} found the unique {Npc} at {Location} for the Golden Bird", name, unique.NPCCode, unique.Location);
        var killLimit = TimeSpan.FromSeconds(_rushConfiguration.RusheeKillLimitSeconds);
        var killed = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (unique.State is EntityState.Dead or EntityState.Dieing)
            {
                return true;
            }

            await AttackTarget(rushee, unique);
            return unique.State is EntityState.Dead or EntityState.Dieing;
        }, killLimit);
        if (!killed)
        {
            Log.Information("{Rushee} could not kill {Npc} within {Seconds}s; the Golden Bird stays open", name, unique.NPCCode, killLimit.TotalSeconds);
            await RusheeHome(rusher, rushee, arrival);
            return false;
        }

        // The measurement this step exists for: did the figurine drop for a killer without the quest.
        Item figurine = null;
        GeneralHelpers.TryWithTimeout(_ =>
        {
            figurine = rushee.Game.Items.Values.FirstOrDefault(item => item.Ground && item.Name == ItemName.AJadeFigurine);
            return figurine != null;
        }, TimeSpan.FromSeconds(5));
        Log.Information("Golden Bird: {Npc} died to {Rushee}; the Jade Figurine {Outcome}", unique.NPCCode, name, figurine == null ? "did NOT drop" : "dropped");
        if (figurine == null)
        {
            await RusheeHome(rusher, rushee, arrival);
            return false;
        }

        var picked = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (Holds(rushee, ItemName.AJadeFigurine))
            {
                return true;
            }

            if (rushee.Game.Me.Location.Distance(figurine.Location) > 2)
            {
                await rushee.Game.MoveToAsync(figurine.Location);
                return false;
            }

            rushee.Game.PickupItem(figurine);
            await Task.Delay(300);
            rushee.Game.CleanupCursorItem();
            return Holds(rushee, ItemName.AJadeFigurine);
        }, TimeSpan.FromSeconds(15));
        Log.Information("{Rushee} {Outcome} the Jade Figurine", name, picked ? "picked up" : "could not pick up");
        await RusheeHome(rusher, rushee, arrival);
        return picked && await TurnInGoldenBird(rushee);
    }

    /// <summary>
    /// The town half (1.14d quest word 20): Meshif turns the figurine into the bird (message 0x211), Alkor
    /// takes the bird (0x216) and then hands over the potion of life (0x21A), which is drunk at once. Each
    /// step runs only if the one before it has happened, so a rushee that stopped half way picks up there.
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
        client.Game.SendQuestMessage(npc, message);
        await Task.Delay(500);
        client.Game.TerminateEntityChat(npc);
        await Task.Delay(300);
    }

    /// <summary>
    /// Walks the search route until a unique monster is in view. The rushee does the walking so that the
    /// first unique to come into being is one it can reach before anybody else does.
    /// </summary>
    /// <summary>
    /// Back to town by the rushee's own tome, or through the rusher's portal where it came in: a character
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
            Log.Warning("{Rushee} could not get back to town after the Golden Bird", rushee.Game.Me.Name);
        }
    }

    private async Task<WorldObject> FindFirstUnique(Client client)
    {
        var mode = GetMovementMode(client);
        var started = DateTime.Now;
        var here = GoldenBirdSearchRoute[0];
        foreach (var next in GoldenBirdSearchRoute.Skip(1))
        {
            // The route says which level this is; no need to ask the client, whose area lags.
            // The adjacent-area path, not GetPathToArea: act 3's jungles meet at open borders, which the map
            // data lists without exits.
            var path = await _pathingService.GetPathToAdjacentArea(
                client.Game.MapId, Difficulty.Normal, here, client.Game.Me.Location, next, mode);
            foreach (var point in path)
            {
                var unique = VisibleUnique(client);
                if (unique != null)
                {
                    return unique;
                }

                if (DateTime.Now - started > UniqueSearchLimit)
                {
                    return null;
                }

                await MovementHelpers.TakePathOfLocations(client.Game, [point], mode);
            }

            var atBorder = VisibleUnique(client);
            if (atBorder != null)
            {
                return atBorder;
            }

            if (!await TraverseTo(client, next))
            {
                return null;
            }

            here = next;
        }

        return VisibleUnique(client);
    }

    private static WorldObject VisibleUnique(Client client)
    {
        return client.Game.WorldObjects
            .Where(entry => entry.Key.Item2 == EntityType.NPC)
            .Select(entry => entry.Value)
            .Where(npc => npc.IsBoss && npc.State is not EntityState.Dead and not EntityState.Dieing)
            .OrderBy(npc => npc.Location.Distance(client.Game.Me.Location))
            .FirstOrDefault();
    }
}
