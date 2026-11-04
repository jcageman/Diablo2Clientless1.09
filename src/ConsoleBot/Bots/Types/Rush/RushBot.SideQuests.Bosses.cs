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
/// The side quests won by a boss kill or by fetching a quest item. Who gets the credit, from D2MOO:
/// Radament, Izual and Shenk credit everyone in the boss's room or the rooms next to it plus their party
/// members in the act; Radament drops a Book of Skill for each of them. The Horadric Malus handed to Charsi
/// credits every party member of level 8 or more; Lam Esen's tome handed to Alkor gives the stat points to
/// everyone standing in act 3 at that moment.
/// </summary>
public sealed partial class RushBot
{
    private const uint CharsiTakesMalus = 0xA3;
    private const uint AtmaRewardsRadament = 0x14E;
    private const uint AlkorTakesTome = 0x234;
    private const uint TyraelRewardsIzual = 0x2A4;
    private const double BossStandoff = 40;
    private static readonly TimeSpan SideBossKillLimit = TimeSpan.FromMinutes(2);

    /// <summary>To an area the way a player goes: the closest waypoint held, then on foot.</summary>
    private async Task<bool> ReachArea(Client client, Area area)
    {
        var act = area.MapToAct();
        if ((!client.Game.IsInTown() && !await ReturnToTown(client))
            || (client.Game.Act != act && !await _townManagementService.SwitchAct(client, act)))
        {
            return false;
        }

        var waypoint = await ClosestHeldWaypoint(client, area);
        if (waypoint != null
            && (!await TakeWaypointFromTown(client, waypoint.Value) || !await ConfirmInArea(client, waypoint.Value.ToArea())))
        {
            return false;
        }

        return await TraverseTo(client, area);
    }

    /// <summary>
    /// The creator operates a quest object the rusher stands guard over, takes the item it drops home and
    /// hands it to the NPC. Everyone else who needs the quest waits in the act whose party share counts.
    /// </summary>
    private async Task<bool> FetchQuestItem(
        Client rusher, Client creator, List<ParticipantSession> needers, Area area, EntityCode stand, ItemName item, NPCCode npc, uint message, QuestId id)
    {
        var name = creator.Game.Me.Name;
        var act = npc == NPCCode.Charsi ? Act.Act1 : area.MapToAct();
        if (!Holds(creator, item))
        {
            if (!await ReachArea(rusher, area) || !await MoveToObject(rusher, stand, area))
            {
                Log.Warning("The rusher could not reach {Stand} in {Area}", stand, area);
                await ReturnToTown(rusher);
                return false;
            }

            await ClearAround(rusher, WaypointClearRadius, WaypointClearLimit);
            if (!await _townManagementService.CreateTownPortal(rusher)
                || (creator.Game.Act != area.MapToAct() && !await _townManagementService.SwitchAct(creator, area.MapToAct()))
                || !await FerryRushee(rusher, creator, area))
            {
                Log.Warning("{Creator} could not be brought to {Stand}", name, stand);
                await ReturnToTown(rusher);
                return false;
            }

            var arrival = creator.Game.Me.Location;
            await MakeRoom(creator, 2, 3);
            var taken = await OperateAndTake(creator, stand, item, area);
            Log.Information("{Creator} {Outcome} {Item}", name, taken ? "took" : "did NOT take", item);
            await RusheeHome(rusher, creator, arrival);
            await ReturnToTown(rusher);
            if (!taken)
            {
                return false;
            }
        }

        var moved = await Task.WhenAll(needers.Select(session => MoveRusheeToAct(session.Client, act)));
        if (!moved.All(ok => ok) || !creator.Game.IsInTown())
        {
            Log.Warning("Not everyone who needs {Item} reached act {Act}", item, act);
        }

        await SayToNpc(creator, npc, message);
        await RefreshQuestData(needers);
        Log.Information("{Item} handed to {Npc} by {Creator}: {States}", item, npc, name,
            string.Join(", ", needers.Select(session => $"{session.Client.Game.Me.Name} 0x{session.Client.Game.Quests.GetCharacterFlags(id):X4}")));
        return HasKillCredit(creator, id);
    }

    /// <summary>
    /// Operates a quest object and picks up what it drops, fighting whatever stands next to it first. Not the
    /// rush's container helper: that runs a rusher clearing tick before every attempt, and with the Smith on
    /// the rusher the creator stood on the Malus stand for forty seconds without touching it once.
    /// </summary>
    private async Task<bool> OperateAndTake(Client client, EntityCode code, ItemName itemName, Area area)
    {
        Item onGround = null;
        Item Dropped() => client.Game.Items.Values
            .Where(candidate => candidate.Ground && candidate.Name == itemName)
            .OrderBy(candidate => candidate.Location.Distance(client.Game.Me.Location))
            .FirstOrDefault();

        var opened = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            onGround = Dropped();
            if (onGround != null)
            {
                return true;
            }

            await ClearUpClose(client, 12, TimeSpan.FromSeconds(10));
            var target = client.Game.GetEntityByCode(code)
                .OrderBy(candidate => candidate.Location.Distance(client.Game.Me.Location))
                .FirstOrDefault();
            if (target == null)
            {
                await MoveToObject(client, code, area);
                return false;
            }

            if (client.Game.Me.Location.Distance(target.Location) > 3)
            {
                await client.Game.MoveToAsync(target);
            }

            client.Game.InteractWithEntity(target);
            await Task.Delay(600);
            onGround = Dropped();
            return onGround != null;
        }, TimeSpan.FromSeconds(40));
        if (!opened)
        {
            Log.Warning("{Client} got no {Item} out of {Object}", client.Game.Me.Name, itemName, code);
            return false;
        }

        return await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (Holds(client, itemName))
            {
                return true;
            }

            if (client.Game.Me.Location.Distance(onGround.Location) > 2)
            {
                await client.Game.MoveToAsync(onGround.Location);
                return false;
            }

            await MakeRoom(client, onGround.Width, onGround.Height);
            client.Game.PickupItem(onGround);
            await Task.Delay(300);
            client.Game.CleanupCursorItem();
            return Holds(client, itemName);
        }, TimeSpan.FromSeconds(15));
    }

    private Task<bool> HoradricMalus(Client rusher, Client creator, List<ParticipantSession> needers) =>
        FetchQuestItem(rusher, creator, needers, Area.Barracks, EntityCode.HoradricMalusStand, ItemName.HoradricMalus,
            NPCCode.Charsi, CharsiTakesMalus, QuestId.ToolsOfTheTrade);

    private Task<bool> LamEsensTome(Client rusher, Client creator, List<ParticipantSession> needers) =>
        FetchQuestItem(rusher, creator, needers, Area.RuinedTemple, EntityCode.LamEsensTomeStand, ItemName.LamEsensTome,
            NPCCode.Alkor, AlkorTakesTome, QuestId.LamEsensTome);

    private async Task<bool> Radament(Client rusher, Client creator, List<ParticipantSession> needers)
    {
        var killed = await BossQuest(rusher, creator, needers, Area.SewersLevel3, NPCCode.Radament,
            async () => MapPointOf(await _mapApiService.GetArea(rusher.Game.MapId, MapDifficulty, Area.SewersLevel3), EntityCode.HoradricScrollChest),
            ReadBookOfSkill);
        return killed;
    }

    private Task<bool> Izual(Client rusher, Client creator, List<ParticipantSession> needers) =>
        BossQuest(rusher, creator, needers, Area.PlainsOfDespair, NPCCode.Izual,
            async () =>
            {
                var map = await _mapApiService.GetArea(rusher.Game.MapId, MapDifficulty, Area.PlainsOfDespair);
                return map?.Npcs != null && map.Npcs.TryGetValue((int)NPCCode.Izual, out var points) && points.Count > 0 ? points[0] : null;
            },
            null);

    /// <summary>
    /// Shenk has no preset position; he stands in the Bloody Foothills near the Frigid Highlands, so the
    /// rusher comes in from that side and looks for him on the way towards Harrogath. He is matched as the one
    /// superunique of the level, not by class: he came as class 479, which the NPC table names otherwise.
    /// </summary>
    private Task<bool> Shenk(Client rusher, Client creator, List<ParticipantSession> needers) =>
        BossQuest(rusher, creator, needers, Area.BloodyFoothills, NPCCode.OverSeer, () => FindShenk(rusher), null);

    private async Task<Point> FindShenk(Client rusher)
    {
        if (!await TakeWaypointFromTown(rusher, Waypoint.FrigidHighlands)
            || !await ConfirmInArea(rusher, Area.FrigidHighlands)
            || !await TraverseTo(rusher, Area.BloodyFoothills))
        {
            return null;
        }

        var mode = GetMovementMode(rusher);
        var path = await _pathingService.GetPathToAdjacentArea(
            rusher.Game.MapId, MapDifficulty, Area.BloodyFoothills, rusher.Game.Me.Location, Area.Harrogath, mode);
        foreach (var point in path.Prepend(rusher.Game.Me.Location))
        {
            var shenk = VisibleBoss(rusher, NPCCode.OverSeer);
            if (shenk != null)
            {
                return shenk.Location;
            }

            await MovementHelpers.TakePathOfLocations(rusher.Game, [point], mode);
        }

        return VisibleBoss(rusher, NPCCode.OverSeer)?.Location;
    }

    private static WorldObject VisibleBoss(Client client, NPCCode code)
    {
        return client.Game.WorldObjects
            .Where(entry => entry.Key.Item2 == EntityType.NPC)
            .Select(entry => entry.Value)
            .Where(npc => (code == NPCCode.OverSeer ? npc.IsBoss : npc.NPCCode == code)
                && npc.State is not EntityState.Dead and not EntityState.Dieing)
            .OrderBy(npc => npc.Location.Distance(client.Game.Me.Location))
            .FirstOrDefault();
    }

    /// <summary>
    /// The rusher finds the boss and opens a portal at a distance without touching it; everyone who needs the
    /// quest comes through and they kill it together, so the kill is theirs and all stand close enough for
    /// the credit. Afterwards each does whatever the drop asks of it, and goes home.
    /// </summary>
    private async Task<bool> BossQuest(
        Client rusher, Client creator, List<ParticipantSession> needers, Area area, NPCCode boss,
        Func<Task<Point>> locate, Func<Client, Task> afterKill)
    {
        var fighters = needers.Select(session => session.Client).ToList();
        if (!fighters.Contains(creator))
        {
            fighters.Insert(0, creator);
        }

        // Everyone else in the game helps. These bosses credit whoever who still needs the quest stands in the
        // kill room or next to it, whoever lands the blow, so a weak character no longer fights them alone.
        var needing = fighters.ToHashSet();
        fighters.AddRange(_gameRushees.Select(session => session.Client)
            .Where(client => !needing.Contains(client) && client.Game.IsInGame()));

        if (boss == NPCCode.OverSeer)
        {
            if (!await ReturnToTown(rusher) || (rusher.Game.Act != Act.Act5 && !await _townManagementService.SwitchAct(rusher, Act.Act5)))
            {
                return false;
            }
        }
        else if (!await ReachArea(rusher, area))
        {
            Log.Warning("The rusher could not reach {Area} for {Boss}", area, boss);
            await ReturnToTown(rusher);
            return false;
        }

        var spot = await locate();
        if (spot == null)
        {
            Log.Warning("The rusher could not find where {Boss} is", boss);
            await ReturnToTown(rusher);
            return false;
        }

        // No fighting here at all: a Frozen Orb or Static Field aimed at the boss's escort killed Radament
        // before anyone who needed him came through, and a kill by someone who has finished the quest
        // spends it for the game. The fighters clear their own way in.
        var standoff = await StandoffPoint(rusher, area, spot, BossStandoff) ?? rusher.Game.Me.Location;
        await MoveTo(rusher, standoff, GetMovementMode(rusher));
        if (!await _townManagementService.CreateTownPortal(rusher))
        {
            await ReturnToTown(rusher);
            return false;
        }

        var arrived = await Task.WhenAll(fighters.Select(async fighter =>
            (fighter.Game.Act == area.MapToAct() || await _townManagementService.SwitchAct(fighter, area.MapToAct()))
            && await FerryRushee(rusher, fighter, area)));
        var present = fighters.Where((_, index) => arrived[index]).ToList();
        if (!present.Contains(creator))
        {
            Log.Warning("{Creator} did not make it to {Boss}", creator.Game.Me.Name, boss);
            await Task.WhenAll(present.Select(fighter => RusheeHome(rusher, fighter, standoff)));
            await ReturnToTown(rusher);
            return false;
        }

        // Out of the way, so neither it nor its hireling lands the kill.
        await ReturnToTown(rusher);
        var deadline = DateTime.Now + SideBossKillLimit;
        var kills = await Task.WhenAll(present.Select(fighter => FightBoss(fighter, boss, spot, deadline)));
        var killed = kills.Any(dead => dead);
        Log.Information("{Boss} {Outcome} by {Fighters}", boss, killed ? "killed" : "NOT killed",
            string.Join(", ", present.Select(fighter => fighter.Game.Me.Name)));

        if (killed && afterKill != null)
        {
            foreach (var fighter in present.Where(needing.Contains))
            {
                await afterKill(fighter);
            }
        }

        await Task.WhenAll(present.Select(fighter => RusheeHome(rusher, fighter, standoff)));
        await RefreshQuestData(needers);
        return killed;
    }

    private async Task<bool> FightBoss(Client fighter, NPCCode boss, Point spot, DateTime deadline)
    {
        var mode = GetMovementMode(fighter);
        WorldObject seen = null;
        while (DateTime.Now < deadline && fighter.Game.IsInGame())
        {
            if (seen != null && seen.State is EntityState.Dead or EntityState.Dieing)
            {
                return true;
            }

            // Izual's ghost (monster 406) only appears once Izual is dead, wherever the client was.
            if (boss == NPCCode.Izual && fighter.Game.WorldObjects.Values.Any(npc => npc.NPCCode == NPCCode.IzualAct4))
            {
                return true;
            }

            seen = VisibleBoss(fighter, boss) ?? seen;
            if (seen != null && seen.State is not EntityState.Dead and not EntityState.Dieing)
            {
                await AttackUpClose(fighter, seen);
                continue;
            }

            var hostile = NearbyHostiles(fighter, 20).FirstOrDefault();
            if (hostile != null)
            {
                await AttackUpClose(fighter, hostile);
                continue;
            }

            if (fighter.Game.Me.Location.Distance(spot) > 8)
            {
                await MoveTo(fighter, spot, mode);
            }
            else
            {
                await Task.Delay(300);
            }
        }

        return seen != null && seen.State is EntityState.Dead or EntityState.Dieing;
    }

    /// <summary>A walkable point the given distance from the target, on the side the client comes from.</summary>
    private async Task<Point> StandoffPoint(Client client, Area area, Point target, double distance)
    {
        var me = client.Game.Me.Location;
        var dx = (double)me.X - target.X;
        var dy = (double)me.Y - target.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            return null;
        }

        dx /= length;
        dy /= length;
        foreach (var angle in new[] { 0.0, 30.0, -30.0, 60.0, -60.0, 90.0, -90.0 })
        {
            var radians = angle * Math.PI / 180.0;
            var rx = dx * Math.Cos(radians) - dy * Math.Sin(radians);
            var ry = dx * Math.Sin(radians) + dy * Math.Cos(radians);
            var candidate = new Point((ushort)(target.X + rx * distance), (ushort)(target.Y + ry * distance));
            if (await _pathingService.IsNavigatablePointInArea(client.Game.MapId, MapDifficulty, area, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Frees a space of the given size by drinking inventory potions. A fighter restocks its inventory full of
    /// them, and the server answers a pickup without room by dropping the item straight back: every Book of
    /// Skill stayed on the floor beside Radament.
    /// </summary>
    private static async Task<bool> MakeRoom(Client client, ushort width, ushort height)
    {
        for (var drunk = 0; drunk < 16; drunk++)
        {
            if (HasRoom(client, width, height))
            {
                return true;
            }

            var potion = client.Game.Inventory.Items.FirstOrDefault(item => item.Classification
                is ClassificationType.HealthPotion or ClassificationType.ManaPotion or ClassificationType.RejuvenationPotion);
            if (potion == null)
            {
                break;
            }

            client.Game.ActivateInventoryItem(potion);
            await Task.Delay(250);
        }

        var room = HasRoom(client, width, height);
        if (!room)
        {
            Log.Warning("{Client} has no {Width}x{Height} space free in its inventory", client.Game.Me.Name, width, height);
        }

        return room;
    }

    private static bool HasRoom(Client client, ushort width, ushort height)
    {
        var inventory = client.Game.Inventory;
        for (ushort y = 0; y < inventory.Height; y++)
        {
            for (ushort x = 0; x < inventory.Width; x++)
            {
                if (inventory.SpaceIsFree(new Point(x, y), width, height))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Radament drops a Book of Skill for each player credited; each picks one up and reads it at once.</summary>
    private async Task ReadBookOfSkill(Client fighter)
    {
        var name = fighter.Game.Me.Name;
        var read = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            var carried = fighter.Game.Inventory.FindItemByName(ItemName.BookofSkill);
            if (carried != null)
            {
                fighter.Game.ActivateInventoryItem(carried);
                await Task.Delay(500);
                return fighter.Game.Inventory.FindItemByName(ItemName.BookofSkill) == null;
            }

            var book = fighter.Game.Items.Values
                .Where(item => item.Ground && item.Name == ItemName.BookofSkill)
                .OrderBy(item => item.Location.Distance(fighter.Game.Me.Location))
                .FirstOrDefault();
            if (book == null)
            {
                await Task.Delay(300);
                return false;
            }

            if (fighter.Game.Me.Location.Distance(book.Location) > 2)
            {
                await fighter.Game.MoveToAsync(book.Location);
                return false;
            }

            await MakeRoom(fighter, book.Width, book.Height);
            fighter.Game.PickupItem(book);
            await Task.Delay(300);
            fighter.Game.CleanupCursorItem();
            return false;
        }, TimeSpan.FromSeconds(15));
        Log.Information("{Fighter} {Outcome} a Book of Skill", name, read ? "read" : "did NOT read");
        if (read)
        {
            _booksRead.Add(name);
        }
    }
}
