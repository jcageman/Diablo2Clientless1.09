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
/// Act 2: the three parts of the Horadric Staff (cube, staff, amulet), the Summoner and Horazon's journal for
/// the way into the Canyon of the Magi, the staff in the true tomb's orifice, Duriel, Tyrael, Jerhyn and
/// Meshif. Object codes, messages and the order follow the rush bot's verified act 2.
/// </summary>
public sealed partial class LevelingBot
{
    private const uint HorazonsJournalMessage = 396;
    private const uint TyraelsSevenTombsMessage = 0x012E;
    private static readonly uint[] JerhynMessages = [0x00FD, 0x016A, 0x01BA];
    private static readonly uint[] MeshifMessages = [0x00F1, 0x01C2];

    private IReadOnlyList<LevelingStep> ActTwoSteps =>
    [
        WaypointStep(Waypoint.DryHills, 14),
        WaypointStep(Waypoint.HallsOfTheDeadLevel2, 15),
        new("Horadric Cube", member => InActTwo(member) && NeedsStaff(member) && !LeaderHolds(ItemName.HoradricCube), party =>
            FetchQuestItem(party, Area.HallsOfTheDeadLevel3, EntityCode.HoradricCubeChest, ItemName.HoradricCube), Gate: 15),
        WaypointStep(Waypoint.FarOasis, 16),
        new("Staff of Kings", member => InActTwo(member) && NeedsStaff(member) && !LeaderHolds(ItemName.ShaftoftheHoradricStaff) && !LeaderHolds(ItemName.HoradricStaff), party =>
            FetchQuestItem(party, Area.MaggotLairLevel3, EntityCode.StaffOfKingsChest, ItemName.ShaftoftheHoradricStaff), Gate: 17),
        WaypointStep(Waypoint.LostCity, 17),
        new("Amulet of the Viper", member => InActTwo(member) && NeedsStaff(member) && !LeaderHolds(ItemName.TopoftheHoradricStaff) && !LeaderHolds(ItemName.HoradricStaff), AmuletOfTheViper, Gate: 18),
        new("Horadric Staff", member => InActTwo(member) && NeedsStaff(member) && !LeaderHolds(ItemName.HoradricStaff), AssembleStaff),
        new("Summoner", member => InActTwo(member) && !member.Game.Me.AllowedWaypoints.Contains(Waypoint.CanyonOfTheMagi) && !HasFinished(member, QuestId.TheSevenTombs), Summoner, Gate: 20),
        new("Duriel", member => InActTwo(member) && !SevenTombsSettled(member), Duriel, Gate: 20),
        // Also after an act 2 farm from act 3: the sail back is what returns the party to act 3, whose steps the picker
        // only sees from there. Skipping it once the quest was done left the party farming act 2 aiming at level 60.
        new("To Kurast", member => _config.Until > LevelingGoal.ActTwo && InActTwo(member) && SevenTombsSettled(member), ToActThree),
    ];

    private static bool InActTwo(PartyMember member) => member.Game.Act == Act.Act2;

    /// <summary>
    /// Done as far as the party is concerned: finished, or told to Jerhyn (0x30 set), which is where 1.09 leaves it -
    /// Meshif sails on that, and every new game starts the party in Lut Gholein again.
    /// </summary>
    private static bool SevenTombsSettled(PartyMember member)
        => HasFinished(member, QuestId.TheSevenTombs) || (member.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs) & 0x30) == 0x30;

    private static bool NeedsStaff(PartyMember member) => !HasFinished(member, QuestId.TheHoradricStaff) && !HasFinished(member, QuestId.TheSevenTombs);

    private Party _party;

    private bool LeaderHolds(ItemName name) => _party != null && QuestItems.Holds(_party.Leader.Client, name);

    /// <summary>
    /// The quest items all go to the leader, who opens the containers and later assembles and inserts the staff - or to
    /// <paramref name="taker"/>, who opens the container and picks the item up itself.
    /// </summary>
    private async Task<bool> FetchQuestItem(Party party, Area area, EntityCode container, ItemName item, PartyMember taker = null)
    {
        if (!await TeleportAhead(party, area, container) && (!await party.GoTo(area) || !await party.WalkToObject(container)))
        {
            Log.Warning("The party could not reach the {Container} in {Area}", container, area);
            return false;
        }

        var leader = taker ?? party.Leader;
        // A follower taking it walks to the chest on its own; following the leader would pull it back every step.
        if (!leader.IsLeader)
        {
            leader.Mode = FollowMode.Off;
        }

        await party.FightHere(Party.EngageRadius + 5);
        bool taken;
        try
        {
            taken = await TakeFromContainer(party, leader, container, item);
        }
        finally
        {
            if (!leader.IsLeader)
            {
                leader.Mode = FollowMode.Follow;
            }
        }

        Log.Information("{Leader} {Outcome} the {Item}", leader.Name, taken ? "has" : "did NOT get", item);
        if (taken && item == ItemName.HoradricCube && taker != null)
        {
            await PickUpOwnCubes(party);
        }

        await party.GoHome();
        await VisitTown(party);
        return taken;
    }

    private async Task<bool> TakeFromContainer(Party party, PartyMember leader, EntityCode container, ItemName item)
    {
        return await GeneralHelpers.TryWithTimeout(async attempt =>
        {
            if (QuestItems.Holds(leader.Client, item))
            {
                return true;
            }

            await party.FightHere(Party.EngageRadius);
            var onGround = leader.Game.Items.Values.FirstOrDefault(candidate => candidate.Ground && candidate.Name == item);
            if (onGround != null)
            {
                if (onGround.Location.Distance(leader.Game.Me.Location) > 2)
                {
                    await leader.Game.MoveToAsync(onGround.Location);
                }

                LevelingLoot.MakeRoomFor(leader, onGround);
                leader.Game.PickupItem(onGround);
                await Task.Delay(400);
                leader.Game.CleanupCursorItem();
                return QuestItems.Holds(leader.Client, item);
            }

            var chest = leader.Game.GetEntityByCode(container).OrderBy(entity => entity.Location.Distance(leader.Game.Me.Location)).FirstOrDefault();
            if (chest == null)
            {
                await party.WalkToObject(container);
                return false;
            }

            var reach = container == EntityCode.ViperAmuletAltar ? 4 : 2;
            // Opened already, its item out of sight: the party pulled back fifty yards from a Hell pack right after the
            // brain fell, and the chest was clicked again instead of the item being looked for where it dropped.
            if (chest.State != EntityState.Alive)
            {
                await MovementHelpers.MoveToLocation(leader.Game, pathingService, mapApiService, chest.Location, MovementMode.Walking);
                return false;
            }

            if (chest.Location.Distance(leader.Game.Me.Location) > reach)
            {
                await MovementHelpers.MoveToWorldObject(leader.Game, pathingService, mapApiService, chest, MovementMode.Walking);
            }

            leader.Game.InteractWithEntity(chest);
            await Task.Delay(600);
            return false;
        }, leader.Game.Difficulty == D2NG.Core.D2GS.Enums.Difficulty.Hell ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(60));
    }

    /// <summary>Set once the cube chest was opened in this game: it gives one cube a game.</summary>
    private bool _cubeChestOpened;

    /// <summary>A cube that never came after this many tries is not coming: the step stops asking for it.</summary>
    private const int CubeTries = 2;

    /// <summary>
    /// Every member carries a cube of its own - more room for loot between town visits, and anyone can cube - fetched from
    /// the Halls of the Dead in whatever difficulty the party is in, before anything else, one member a game. The leader's
    /// own comes first, from the staff quest.
    /// </summary>
    private LevelingStep CubeForEveryone => new("Cube for everyone", member => !_cubeChestOpened && LeaderHolds(ItemName.HoradricCube)
        && member.InGame && !QuestItems.Holds(member.Client, ItemName.HoradricCube)
        && member.Game.Me.AllowedWaypoints.Contains(Waypoint.HallsOfTheDeadLevel2)
        && _stepFailures.GetValueOrDefault($"{_difficulty}:Cube for everyone") < CubeTries, FetchOwnCube);

    /// <summary>
    /// The chest drops a cube for every player without one, all at once: the sorceress took hers and the barbarian's and
    /// the amazon's were left lying beside it. Each member still without one picks one up.
    /// </summary>
    private static async Task PickUpOwnCubes(Party party)
    {
        foreach (var member in party.Members.Where(member => member.InGame && !QuestItems.Holds(member.Client, ItemName.HoradricCube)))
        {
            var cube = member.Game.Items.Values.Where(candidate => candidate.Ground && candidate.Name == ItemName.HoradricCube)
                .OrderBy(candidate => candidate.Location.Distance(member.Game.Me.Location)).FirstOrDefault();
            if (cube == null)
            {
                Log.Information("{Character} sees no cube left by the chest", member.Name);
                continue;
            }

            var mode = member.Mode;
            member.Mode = FollowMode.Off;
            try
            {
                for (var attempt = 0; attempt < 3 && !QuestItems.Holds(member.Client, ItemName.HoradricCube); attempt++)
                {
                    if (cube.Location.Distance(member.Game.Me.Location) > 2)
                    {
                        await member.Game.MoveToAsync(cube.Location);
                    }

                    LevelingLoot.MakeRoomFor(member, cube);
                    member.Game.PickupItem(cube);
                    await Task.Delay(500);
                    member.Game.CleanupCursorItem();
                }
            }
            finally
            {
                member.Mode = mode;
            }

            Log.Information("{Character} {Outcome} the HoradricCube", member.Name, QuestItems.Holds(member.Client, ItemName.HoradricCube) ? "has" : "did NOT get");
        }
    }

    private async Task<bool> FetchOwnCube(Party party)
    {
        var taker = party.Members.FirstOrDefault(member => member.InGame && !QuestItems.Holds(member.Client, ItemName.HoradricCube));
        if (taker == null)
        {
            return true;
        }

        _cubeChestOpened = true;
        return await FetchQuestItem(party, Area.HallsOfTheDeadLevel3, EntityCode.HoradricCubeChest, ItemName.HoradricCube, taker);
    }

    /// <summary>The amulet; then Drognan and Jerhyn, whose talk is what lets the party into the palace.</summary>
    private async Task<bool> AmuletOfTheViper(Party party)
    {
        if (!await FetchQuestItem(party, Area.ClawViperTempleLevel2, EntityCode.ViperAmuletAltar, ItemName.TopoftheHoradricStaff))
        {
            return false;
        }

        foreach (var member in party.Members.Where(member => member.InGame))
        {
            await TalkThrough(member, NPCCode.Drognan);
        }

        return true;
    }

    private Task<bool> AssembleStaff(Party party)
    {
        var assembled = QuestItems.TransmuteStaff(party.Leader.Client);
        Log.Information("{Leader} {Outcome} the Horadric Staff", party.Leader.Name, assembled ? "assembled" : "could NOT assemble");
        return Task.FromResult(assembled);
    }

    /// <summary>Talks to an NPC and answers every quest message it offers, for the talks whose ids nobody captured.</summary>
    private async Task TalkThrough(PartyMember member, NPCCode code)
    {
        if (!await WalkToNpc(member, code) || NPCHelpers.GetUniqueNPC(member.Game, code) is not { } npc)
        {
            return;
        }

        var game = member.Game;
        game.InteractWithNPC(npc);
        await Task.Delay(200);
        game.InitiateEntityChat(npc);
        GeneralHelpers.TryWithTimeout(_ => game.GetOfferedMessages(npc).Count > 0, TimeSpan.FromSeconds(2));
        var offered = game.GetOfferedMessages(npc).ToList();
        foreach (var message in offered)
        {
            game.SendQuestMessage(npc, message);
            await Task.Delay(500);
        }

        game.TerminateEntityChat(npc);
        Log.Information("{Character} talked to {Npc}: answered {Messages}", member.Name, code, string.Join(", ", offered.Select(message => $"0x{message:X4}")));
        await Task.Delay(300);
    }

    private static async Task<bool> CanyonByPortal(Party party)
    {
        if (!party.Leader.Game.IsInTown())
        {
            await party.GoHome();
        }

        if (!await party.TakeWaypoint(Waypoint.CanyonOfTheMagi, leaderSuffices: true) || party.Leader.Game.IsInTown())
        {
            return false;
        }

        await party.OpenRejoinPortal();
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(60) && await party.LeaderPresent() && !party.RegroupRequested
            && party.Members.Any(member => member.InGame && member.Game.IsInTown()))
        {
            await party.FightHere(Party.EngageRadius);
            await Task.Delay(500);
        }

        await party.TouchWaypoint();
        var all = party.Members.Where(member => member.InGame).All(member => member.Game.Me.AllowedWaypoints.Contains(Waypoint.CanyonOfTheMagi));
        Log.Information("The Canyon's waypoint by portal: {Outcome}", all ? "everyone holds it" : "NOT everyone holds it");
        return all;
    }

    /// <summary>
    /// The palace cellar's portal into the Arcane Sanctuary, the Summoner at Horazon's journal, the journal
    /// read for the red portal into the Canyon of the Magi, and the canyon's waypoint for everyone.
    /// </summary>
    private async Task<bool> Summoner(Party party)
    {
        // The step is there for the Canyon's waypoint. When the leader holds it, the party goes there by waypoint and
        // the members without it come through a portal: the Summoner's arm end, reached over walkways the party
        // cannot walk, cost five attempts and a dozen chickens for one member who missed the journal's portal.
        if (party.Leader.Game.Me.AllowedWaypoints.Contains(Waypoint.CanyonOfTheMagi))
        {
            return await CanyonByPortal(party);
        }

        foreach (var member in party.Members.Where(member => member.InGame))
        {
            await TalkThrough(member, NPCCode.Jerhyn);
        }

        if (!await party.GoTo(Area.PalaceCellarLevel3) || !await party.WalkToObject(EntityCode.ArcanePortal)
            || !await party.StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.ArcanePortal).FirstOrDefault(), Area.ArcaneSanctuary))
        {
            Log.Warning("The party could not get into the Arcane Sanctuary");
            return false;
        }

        // Straight through the portal the leader's position is still the cellar's, and every path from there into the
        // Sanctuary came back empty: the step failed three times within three seconds of arriving. Wait for a
        // position that is a walkable spot of the Sanctuary first.
        for (var wait = 0; wait < 20 && !await pathingService.IsNavigatablePointInArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty,
            Area.ArcaneSanctuary, party.Leader.Game.Me.Location); wait++)
        {
            await _traveler.SyncPosition(party.Leader.Client, Area.ArcaneSanctuary);
            await Task.Delay(300);
        }

        await party.TouchWaypoint();
        if (!await party.WalkToObject(EntityCode.HorazonsJournal))
        {
            // The journal stands on a tile the map marks unwalkable and the object path has no fallback for that, so
            // it came back empty three times in a row. Walking to its map position falls back to the nearest
            // walkable point.
            var map = await mapApiService.GetArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty, Area.ArcaneSanctuary);
            var spot = map?.Objects?.GetValueOrDefault((int)EntityCode.HorazonsJournal)?.FirstOrDefault();
            if (spot != null)
            {
                await party.WalkTo(spot);
            }

            if (!party.Leader.Game.GetEntityByCode(EntityCode.HorazonsJournal).Any(journal => journal.Location.Distance(party.Leader.Game.Me.Location) <= 20))
            {
                Log.Warning("The party could not walk to Horazon's journal from {Location} (journal at {Journal})", party.Leader.Game.Me.Location, spot);
                return false;
            }
        }

        await party.KillBoss(NPCCode.Summoner, BossLimit);
        var leader = party.Leader;
        var journal = leader.Game.GetEntityByCode(EntityCode.HorazonsJournal).FirstOrDefault();
        if (journal == null)
        {
            return false;
        }

        await leader.Game.MoveToAsync(journal);
        leader.Game.InteractWithEntity(journal);
        await Task.Delay(500);
        leader.Game.SendQuestMessage(journal, HorazonsJournalMessage);
        if (!GeneralHelpers.TryWithTimeout(_ => leader.Game.GetEntityByCode(EntityCode.RedTownPortal).Count > 0, TimeSpan.FromSeconds(5)))
        {
            Log.Warning("Reading Horazon's journal opened no portal");
            return false;
        }

        if (!await party.StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.RedTownPortal)
                .OrderBy(portal => portal.Location.Distance(member.Game.Me.Location)).FirstOrDefault(), Area.CanyonOfTheMagi))
        {
            return false;
        }

        var touched = await party.TouchWaypoint();
        await party.GoHome();
        await VisitTown(party);
        return touched;
    }

    /// <summary>The true tomb is the one whose map holds the orifice; the staff goes in, Duriel dies, Tyrael and Jerhyn are told.</summary>
    private async Task<bool> Duriel(Party party)
    {
        // Duriel dead and Tyrael heard, only Jerhyn untold: telling him settles the quest without a second fight. The
        // party farmed half an hour towards another Duriel because Jerhyn had moved into his palace and was not found.
        if (party.Leader.Game.IsInTown())
        {
            foreach (var member in party.Members.Where(member => member.InGame))
            {
                // Whatever he offers is answered: the fixed messages found him but left the quest open.
                if (NPCHelpers.GetUniqueNPC(member.Game, NPCCode.Jerhyn) == null)
                {
                    using var walk = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, PalaceHaremDoor, MovementMode.Walking, walk.Token);
                }

                await TalkThrough(member, NPCCode.Jerhyn);
            }

            RefreshQuests(party);
            Log.Information("After Jerhyn, seven tombs: {States}",
                string.Join(", ", party.Members.Where(member => member.InGame).Select(member => $"{member.Name} 0x{member.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs):X4}")));
            if (party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.TheSevenTombs)))
            {
                Log.Information("Jerhyn settled the seven tombs; no Duriel fight needed");
                return true;
            }

            // After Jerhyn the flags read 0x0034, not finished: the quest closes with Meshif's passage east. Sailing
            // settles it, and a party in Kurast needs no Duriel step.
            if (_config.Until > LevelingGoal.ActTwo && party.Members.Where(member => member.InGame).All(member => (member.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs) & 0x30) == 0x30))
            {
                await ToActThree(party);
                if (party.Members.Where(member => member.InGame).All(member => member.Game.Act == Act.Act3))
                {
                    Log.Information("Meshif took the party to Kurast after Jerhyn; no Duriel fight needed");
                    return true;
                }
            }
        }

        var tomb = await TombWithOrifice(party);
        if (tomb == null || !await party.GoTo(tomb.Value) || !await party.WalkToObject(EntityCode.HoradricOrifice))
        {
            Log.Warning("The party could not reach the orifice in {Tomb}", tomb);
            return false;
        }

        await party.FightHere(Party.EngageRadius + 5);
        var leader = party.Leader;
        if (leader.Game.GetEntityByCode(EntityCode.DurielsLairPortal).Count == 0 && !await InsertStaff(party))
        {
            return false;
        }

        if (!await party.StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.DurielsLairPortal).FirstOrDefault(), Area.DurielsLair))
        {
            return false;
        }

        var killed = await party.KillBoss(NPCCode.Duriel, BossLimit);
        Log.Information("Duriel {Outcome}", killed ? "is dead" : "is NOT dead");
        foreach (var member in party.Members.Where(member => member.InGame))
        {
            await TalkToTyrael(party, member);
        }

        await party.StepThroughPortal(member => member.Game.GetEntityByCode(EntityCode.TownPortal)
            .OrderBy(portal => portal.Location.Distance(member.Game.Me.Location)).FirstOrDefault(), Area.LutGholein);
        foreach (var member in party.Members.Where(member => member.InGame))
        {
            await TalkTo(member, NPCCode.Jerhyn, JerhynMessages);
        }

        RefreshQuests(party);
        await VisitTown(party);
        return party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.TheSevenTombs));
    }

    private async Task<Area?> TombWithOrifice(Party party)
    {
        for (var tomb = Area.TalRashasTomb1; tomb <= Area.TalRashasTomb7; tomb++)
        {
            var map = await mapApiService.GetArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty, tomb);
            if (map?.Objects?.ContainsKey((int)EntityCode.HoradricOrifice) == true)
            {
                Log.Information("The orifice is in {Tomb}", tomb);
                return tomb;
            }
        }

        return null;
    }

    private static async Task<bool> InsertStaff(Party party)
    {
        var leader = party.Leader;
        var orifice = leader.Game.GetEntityByCode(EntityCode.HoradricOrifice).FirstOrDefault();
        var staff = leader.Game.Inventory.FindItemByName(ItemName.HoradricStaff);
        if (orifice == null || staff == null)
        {
            Log.Warning("{Leader} cannot insert the staff: orifice {Orifice}, staff {Staff}", leader.Name, orifice != null, staff != null);
            return false;
        }

        await leader.Game.MoveToAsync(orifice);
        leader.Game.InteractWithEntity(orifice);
        await Task.Delay(500);
        leader.Game.InteractWithEntity(orifice);
        await Task.Delay(500);
        leader.Game.RemoveItemFromContainer(staff);
        if (!GeneralHelpers.TryWithTimeout(_ => leader.Game.CursorItem?.Id == staff.Id, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        leader.Game.InsertHoradricStaff(orifice, staff);
        var opened = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await party.FightHere(18);
            await Task.Delay(300);
            return leader.Game.GetEntityByCode(EntityCode.DurielsLairPortal).Count > 0;
        }, TimeSpan.FromSeconds(30));
        Log.Information("The staff {Outcome} Duriel's lair", opened ? "opened" : "did NOT open");
        return opened;
    }

    private async Task TalkToTyrael(Party party, PartyMember member)
    {
        var lair = await mapApiService.GetArea(member.Game.MapId, AreaTraveler.MapDifficulty, Area.DurielsLair);
        var spot = lair?.Npcs?.GetValueOrDefault((int)NPCCode.TyraelAct3)?.FirstOrDefault();
        if (spot != null)
        {
            var path = await pathingService.GetPathToLocation(member.Game.MapId, AreaTraveler.MapDifficulty, Area.DurielsLair, member.Game.Me.Location, spot, MovementMode.Walking);
            if (path.Count > 0)
            {
                await MovementHelpers.TakePathOfLocations(member.Game, path, MovementMode.Walking);
            }
        }

        WorldObject tyrael = null;
        GeneralHelpers.TryWithTimeout(_ => (tyrael = NPCHelpers.GetUniqueNPC(member.Game, NPCCode.TyraelAct3)) != null, TimeSpan.FromSeconds(10));
        if (tyrael == null)
        {
            Log.Warning("{Character} cannot see Tyrael", member.Name);
            return;
        }

        await member.Game.MoveToAsync(tyrael);
        member.Game.InitiateEntityChat(tyrael);
        await Task.Delay(1000);
        member.Game.SendQuestMessage(tyrael, TyraelsSevenTombsMessage);
        await Task.Delay(1500);
        member.Game.TerminateEntityChat(tyrael);
        member.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
        Log.Information("{Character} talked to Tyrael; seven tombs 0x{Flags:X4}", member.Name, member.Game.Quests.GetCharacterFlags(QuestId.TheSevenTombs));
    }

    /// <summary>Where Jerhyn stands once he has moved into his palace: by the door to the harem, inside Lut Gholein.</summary>
    private static readonly Point PalaceHaremDoor = new(5050, 5142);

    private async Task TalkTo(PartyMember member, NPCCode code, IEnumerable<uint> messages)
    {
        if (code == NPCCode.Jerhyn && NPCHelpers.GetUniqueNPC(member.Game, code) == null && member.Game.Act == Act.Act2)
        {
            using var walk = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
            await MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, PalaceHaremDoor, MovementMode.Walking, walk.Token);
        }

        if (!await WalkToNpc(member, code) || NPCHelpers.GetUniqueNPC(member.Game, code) is not { } npc)
        {
            Log.Warning("{Character} did not find {Npc} to talk to", member.Name, code);
            return;
        }

        await member.Game.MoveToAsync(npc);
        member.Game.InitiateEntityChat(npc);
        await Task.Delay(750);
        foreach (var message in messages)
        {
            member.Game.SendQuestMessage(npc, message);
            await Task.Delay(400);
        }

        member.Game.TerminateEntityChat(npc);
        await Task.Delay(300);
    }

    /// <summary>Meshif sails every member to Kurast once the seven tombs are settled for it.</summary>
    private async Task<bool> ToActThree(Party party)
    {
        // Every new game starts the party in Lut Gholein, so this ran 92 times in one day: the walk to Meshif at the docks
        // and the sail took about 20 s, and Kurast Docks then had the walk back to its waypoint. Once everyone holds the
        // Kurast Docks waypoint it is a few steps from where reading the waypoints left them.
        if (party.Members.Where(member => member.InGame).All(member => member.Game.Me.AllowedWaypoints.Contains(Waypoint.KurastDocks))
            && await party.TakeWaypoint(Waypoint.KurastDocks))
        {
            Log.Information("The party is in Kurast Docks, by waypoint");
            return true;
        }

        foreach (var member in party.Followers)
        {
            member.Mode = FollowMode.Off;
        }

        var arrived = await Task.WhenAll(party.Members.Where(member => member.InGame).Select(async member =>
        {
            await TalkTo(member, NPCCode.Meshif, MeshifMessages);
            if (NPCHelpers.GetUniqueNPC(member.Game, NPCCode.Meshif) is not { } meshif)
            {
                return false;
            }

            member.Game.InitiateEntityChat(meshif);
            await Task.Delay(500);
            member.Game.TravelWithNpc(meshif);
            return GeneralHelpers.TryWithTimeout(_ => member.Game.Act == Act.Act3, TimeSpan.FromSeconds(15));
        }));
        foreach (var member in party.Followers)
        {
            member.Mode = FollowMode.Follow;
        }

        if (arrived.All(done => done))
        {
            party.Area = Area.KurastDocks;
            Log.Information("The party is in Kurast Docks");
            return true;
        }

        return false;
    }
}
