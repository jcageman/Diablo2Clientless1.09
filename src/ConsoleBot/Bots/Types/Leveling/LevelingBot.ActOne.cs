using ConsoleBot.Helpers;
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
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Act 1, Normal. Quest order with the lowest party level each step needs; below a gate the party farms
/// the areas whose monsters suit its level first. Messages and object handling follow the rush bot's
/// verified side quests (D2MOO A1Q1-A1Q6).
/// </summary>
public sealed partial class LevelingBot
{
    private const uint AkaraDeciphersScroll = 0x70;
    private const uint AkaraRewardsCain = 0x76;
    private const uint WarrivsAndarielMessage = 0xB7;
    private const ushort CairnStonesOpenedBit = 0x10;
    private static readonly TimeSpan BossLimit = TimeSpan.FromMinutes(5);

    private static readonly EntityCode[] CairnStones =
    [
        EntityCode.CairnStoneAlpha, EntityCode.CairnStoneBeta, EntityCode.CairnStoneGamma,
        EntityCode.CairnStoneDelta, EntityCode.CairnStoneLambda,
    ];

    /// <summary>
    /// Where to farm: Normal monster levels from levels.txt and the waypoint each area is reached from (none for
    /// the areas next to town). Nightmare adds 25 and Hell 50 to every monster level.
    /// </summary>
    private static readonly (Area Area, int MonsterLevel, Waypoint? From)[] Farming =
    [
        (Area.BloodMoor, 1, null), (Area.ColdPlains, 2, null), (Area.CaveLevel1, 2, null), (Area.BurialGrounds, 3, Waypoint.ColdPlains),
        (Area.StonyFields, 4, Waypoint.StonyFields), (Area.UndergroundPassageLevel1, 4, Waypoint.StonyFields), (Area.DarkWood, 5, Waypoint.DarkWood),
        (Area.BlackMarsh, 6, Waypoint.BlackMarsh), (Area.HoleLevel1, 5, Waypoint.BlackMarsh), (Area.Tristram, 6, Waypoint.StonyFields),
        (Area.TamoeHighland, 8, Waypoint.BlackMarsh), (Area.OuterCloister, 9, Waypoint.OuterCloister), (Area.Barracks, 9, Waypoint.OuterCloister),
        (Area.JailLevel1, 10, Waypoint.JailLevel1), (Area.JailLevel2, 10, Waypoint.JailLevel1), (Area.JailLevel3, 10, Waypoint.JailLevel1),
        (Area.InnerCloister, 10, Waypoint.InnerCloister), (Area.Cathedral, 11, Waypoint.InnerCloister), (Area.CatacombsLevel1, 11, Waypoint.InnerCloister),
        (Area.CatacombsLevel2, 11, Waypoint.CatacombsLevel2), (Area.CatacombsLevel3, 12, Waypoint.CatacombsLevel2),
        (Area.RockyWaste, 14, null), (Area.SewersLevel1, 13, null), (Area.DryHills, 15, Waypoint.DryHills), (Area.HallsOfTheDeadLevel1, 12, Waypoint.DryHills),
        (Area.HallsOfTheDeadLevel2, 13, Waypoint.HallsOfTheDeadLevel2), (Area.HallsOfTheDeadLevel3, 14, Waypoint.HallsOfTheDeadLevel2),
        (Area.FarOasis, 16, Waypoint.FarOasis), (Area.LostCity, 17, Waypoint.LostCity),
        (Area.ValleyOfSnakes, 18, Waypoint.LostCity), (Area.ClawViperTempleLevel1, 14, Waypoint.LostCity), (Area.AncientTunnels, 17, Waypoint.LostCity),
        (Area.TalRashasTomb1, 17, Waypoint.CanyonOfTheMagi), (Area.TalRashasTomb2, 17, Waypoint.CanyonOfTheMagi), (Area.TalRashasTomb3, 17, Waypoint.CanyonOfTheMagi),
        (Area.SpiderForest, 21, Waypoint.SpiderForest), (Area.GreatMarsh, 21, Waypoint.GreatMarsh), (Area.FlayerJungle, 22, Waypoint.FlayerJungle),
        (Area.LowerKurast, 22, Waypoint.LowerKurast), (Area.KurastBazaar, 22, Waypoint.KurastBazaar), (Area.UpperKurast, 23, Waypoint.UpperKurast),
        (Area.Travincal, 24, Waypoint.Travincal), (Area.DuranceOfHateLevel1, 25, Waypoint.Travincal),
        (Area.OuterSteppes, 26, null), (Area.PlainsOfDespair, 26, null), (Area.CityOfTheDamned, 27, Waypoint.CityOfTheDamned),
        (Area.RiverOfFlame, 27, Waypoint.RiverOfFlame), (Area.ChaosSanctuary, 28, Waypoint.RiverOfFlame),
    ];

    private IReadOnlyList<LevelingStep> ActOneSteps =>
    [
        new("Den of Evil", member => !HasFinished(member, QuestId.DenOfEvil), DenOfEvil),
        WaypointStep(Waypoint.ColdPlains, 2),
        WaypointStep(Waypoint.StonyFields, 5),
        WaypointStep(Waypoint.DarkWood, 6),
        new("Search for Cain", member => !HasFinished(member, QuestId.TheSearchForCain), SearchForCain, Gate: 7),
        WaypointStep(Waypoint.BlackMarsh, 8),
        WaypointStep(Waypoint.OuterCloister, 9),
        WaypointStep(Waypoint.JailLevel1, 10),
        WaypointStep(Waypoint.InnerCloister, 11),
        WaypointStep(Waypoint.CatacombsLevel2, 12),
        new("Andariel", member => !HasFinished(member, QuestId.SistersToTheSlaughter) || !PartyBeat("Andariel"), Andariel, Gate: 14),
        new("To Lut Gholein", member => _config.Until > LevelingGoal.ActOne && member.Game.Act == Act.Act1 && HasFinished(member, QuestId.SistersToTheSlaughter), ToActTwo),
    ];

    /// <summary>Warriv takes every member to Lut Gholein once Andariel is settled for it.</summary>
    private async Task<bool> ToActTwo(Party party)
    {
        if (!await party.GoHome())
        {
            return false;
        }

        foreach (var member in party.Followers)
        {
            member.Mode = FollowMode.Off;
        }

        var arrived = await Task.WhenAll(party.Members.Where(member => member.InGame).Select(async member =>
        {
            if (!await WalkToNpc(member, NPCCode.WarrivAct1) || NPCHelpers.GetUniqueNPC(member.Game, NPCCode.WarrivAct1) is not { } warriv)
            {
                return false;
            }

            member.Game.InitiateEntityChat(warriv);
            await Task.Delay(500);
            member.Game.TravelWithNpc(warriv);
            return await _traveler.ConfirmInArea(member.Client, Area.LutGholein);
        }));
        foreach (var member in party.Followers)
        {
            member.Mode = FollowMode.Follow;
        }

        if (arrived.All(done => done))
        {
            party.Area = Area.LutGholein;
            Log.Information("The party is in Lut Gholein");
            return true;
        }

        return false;
    }

    private LevelingStep WaypointStep(Waypoint waypoint, int gate)
        => new($"{waypoint} waypoint", member => (member.Game.Act == waypoint.ToArea().MapToAct()
                || (waypoint.ToArea().MapToAct() == Act.Act4 && InActFour(member)))
            // An empty list is one not read yet - a member that rejoined knows none until it opens a menu - not one without waypoints.
            && !member.Game.Me.AllowedWaypoints.IsEmpty && !member.Game.Me.AllowedWaypoints.Contains(waypoint),
            // The sorceress teleports to it and the party comes through her portal: the Flayer Jungle waypoint took twelve
            // minutes on foot.
            async party => (await TeleportAhead(party, waypoint.ToArea(), waypoint.ToEntityCode()) || await party.GoTo(waypoint.ToArea()))
                && await party.TouchWaypoint() && await party.GoHome(), Gate: gate);

    /// <summary>
    /// Farms the area of the party's act best suited to its lowest level that it can reach and has not cleared
    /// yet this game. False when there is none left, which calls for a fresh game.
    /// </summary>
    private async Task<bool> Farm(Party party, int target)
    {
        var lowest = party.Members.Where(member => member.InGame).Min(member => member.Level);
        // A new game starts a party that has finished act 3 in Kurast Docks: read as act 3 it farmed Durance at 25 instead
        // of the River of Flame at 27.
        var act = party.Area.MapToAct() == Act.Act3 && party.Members.Where(member => member.InGame).All(InActFour) ? Act.Act4 : party.Area.MapToAct();
        var difficulty = party.Leader.Game.Difficulty;
        var expansion = party.Leader.Game.ClientCharacter?.IsExpansion == true;
        var offset = DifficultyOffset(difficulty);
        var reachable = Farming
            .Select(spot => (spot.Area, MonsterLevel: AreaLevels?.MonsterLevel(spot.Area, difficulty, expansion) ?? spot.MonsterLevel + offset, spot.From))
            // Every act may farm the one before it: Nightmare act 3 starts at monster level 46, out of reach of a party at
            // 40, which then found nowhere to farm game after game. In act 4 the party stays out of the Outer Steppes, the Plains of Despair and the City of
            // the Damned: their Burning Souls took 140-390 life in a blink from members at level 26-27, a pack of ten of
            // them waiting at the City's waypoint.
            .Where(spot => spot.Area.MapToAct() <= act
                && !(act == Act.Act4 && (spot.Area is Area.OuterSteppes or Area.PlainsOfDespair or Area.ChaosSanctuary
                    || (spot.Area == Area.CityOfTheDamned && difficulty != D2NG.Core.D2GS.Enums.Difficulty.Hell)))
                // Act 4 hits harder for its level: farming the Chaos Sanctuary at 27 chickened two members within
                // five seconds, so there the party farms no higher than its own level. Its seal guards, Doom and Oblivion
                // Knights with Storm Casters, chickened all four within fourteen seconds at 28 even so. Act 3's spots keep their
                // margin: capped too, the party at 46 farmed the Spider Forest instead of Travincal at 49.
                && !_sweptThisGame.Contains(spot.Area) && spot.MonsterLevel >= lowest - FarmFloor && spot.MonsterLevel <= lowest + FarmAbove(spot.Area, difficulty))
            // Durance of Hate opens only once the Compelling Orb is smashed: the party tried to farm its first level and
            // failed the crossing from Travincal again and again.
            .Where(spot => spot.Area != Area.DuranceOfHateLevel1 || party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.KhalimsWill)))
            .Where(spot => spot.Area == Area.Tristram
                ? party.Members.All(member => (member.Game.Quests.GetCharacterFlags(QuestId.TheSearchForCain) & CairnStonesOpenedBit) != 0)
                : spot.From == null || party.AllHold(spot.From.Value))
            .OrderByDescending(spot => spot.MonsterLevel)
            .ToList();
        // The act and the one before it first; earlier acts only when neither has a spot in reach. Hell act 4 at 56 has
        // none - its own areas are capped at the party's level, act 3 starts at 71 - and without them a failed Diablo was
        // tried again at once, game after game, instead of farmed for.
        var near = reachable.Where(candidate => candidate.Area.MapToAct() == act || candidate.Area.MapToAct() == act - 1).ToList();
        if (near.Count > 0)
        {
            reachable = near;
        }

        if (reachable.Count == 0)
        {
            Log.Information("Nowhere left to farm in {Act} at level {Level} in this game", act, lowest);
            return false;
        }

        var spot = reachable[0].Area;
        Log.Information("Farming {Area} (monster level {MonsterLevel}) for the party's lowest level {Level}, aiming at {Target}", spot, reachable[0].MonsterLevel, lowest, target);
        _sweptThisGame.Add(spot);
        var arrived = spot == Area.Tristram ? await EnterTristram(party) : await party.GoTo(spot);
        if (!arrived)
        {
            // Not "nowhere to farm": this spot is done for the game and the next pick tries another. Answering
            // false here waived Andariel's gate because one crossing into Catacombs 3 failed.
            Log.Information("The party could not reach {Area} to farm; trying another spot", spot);
            return true;
        }

        await party.Sweep(SweepSpacing(spot), () => Task.FromResult(party.Members.Where(member => member.InGame).All(member => member.Level >= target)), FarmLimit);
        await party.GoHome();
        await VisitTown(party);
        return true;
    }

    /// <summary>
    /// The game's own monster levels. Classic is Normal plus 25 and 50, which the offset below reproduces; expansion is
    /// not (Blood Moor 36 in Nightmare, 67 in Hell), so an expansion party farms by this table or not at all well.
    /// </summary>
    private static readonly Lazy<D2NG.Core.LevelData.AreaLevelTable> AreaLevelsFile = new(() =>
        D2NG.Core.GameDataLocation.TryResolve(D2NG.Core.GameDataLocation.AreaLevelsFile, out var path) ? D2NG.Core.LevelData.AreaLevelTable.Load(path) : null);

    private static D2NG.Core.LevelData.AreaLevelTable AreaLevels => AreaLevelsFile.Value;

    private static int DifficultyOffset(D2NG.Core.D2GS.Enums.Difficulty difficulty) => difficulty switch
    {
        D2NG.Core.D2GS.Enums.Difficulty.Nightmare => 25,
        D2NG.Core.D2GS.Enums.Difficulty.Hell => 50,
        _ => 0
    };

    private static readonly TimeSpan FarmLimit = TimeSpan.FromMinutes(12);

    /// <summary>
    /// How far above the party's level it farms: act 4 no higher than its own level, Hell further up than elsewhere -
    /// its act 1 at four above gave 34k experience a minute, three hours a level.
    /// </summary>
    private static int FarmAbove(Area area, D2NG.Core.D2GS.Enums.Difficulty difficulty) => (area.MapToAct(), difficulty) switch
    {
        // Hell act 4: the River of Flame and the City of the Damned at 77 against a party in its late fifties - act 2's 66
        // at Far Oasis gave half the experience and weaker drops. Normal's own-level cap came from level 26 members.
        (Act.Act4, D2NG.Core.D2GS.Enums.Difficulty.Hell) => HellActFourAbove,
        (Act.Act4, _) => 0,
        (_, D2NG.Core.D2GS.Enums.Difficulty.Hell) => 8,
        _ => 4,
    };

    private const int HellActFourAbove = 20;

    /// <summary>
    /// Monsters more than five levels below a character give it far less experience: after the River of Flame the
    /// party farmed Travincal and Upper Kurast at 24 and 23 for level 30, where a new game's River was a minute away.
    /// </summary>
    private const int FarmFloor = 5;

    private static int SweepSpacing(Area area) => area is Area.BloodMoor or Area.ColdPlains or Area.StonyFields or Area.DarkWood
        or Area.BlackMarsh or Area.TamoeHighland or Area.OuterCloister or Area.RockyWaste or Area.DryHills or Area.FarOasis
        or Area.LostCity or Area.ValleyOfSnakes or Area.SpiderForest or Area.GreatMarsh or Area.FlayerJungle or Area.LowerKurast
        or Area.KurastBazaar or Area.UpperKurast or Area.OuterSteppes or Area.PlainsOfDespair or Area.CityOfTheDamned or Area.RiverOfFlame ? 40 : 25;

    /// <summary>
    /// The Search for Cain (A1Q4): the Inifuss tree drops the scroll, Akara turns it into the key, the key
    /// lights the five Cairn Stones in a per-game order, the portal they open leads to Tristram, and the
    /// leader frees Cain from his gibbet for everyone in the party in act 1. Akara pays a ring each.
    /// </summary>
    private async Task<bool> SearchForCain(Party party)
    {
        var leader = party.Leader;
        if (party.Members.Where(member => member.InGame).All(CainCredited))
        {
            return await ClaimCainReward(party);
        }

        var opened = (leader.Game.Quests.GetCharacterFlags(QuestId.TheSearchForCain) & CairnStonesOpenedBit) != 0;
        if (!opened && !Holds(leader, ItemName.KeytotheCairnStones))
        {
            if (!Holds(leader, ItemName.ScrollofInifuss) && !await FetchInifussScroll(party))
            {
                return false;
            }

            if (!await party.GoHome())
            {
                return false;
            }

            await SayToNpc(leader, NPCCode.Akara, AkaraDeciphersScroll);
            if (!GeneralHelpers.TryWithTimeout(_ => Holds(leader, ItemName.KeytotheCairnStones), TimeSpan.FromSeconds(3)))
            {
                Log.Warning("Akara did not give {Leader} the Key to the Cairn Stones", leader.Name);
                return false;
            }
        }

        if (!opened && !await OpenCairnStones(party))
        {
            return false;
        }

        if (!await EnterTristram(party))
        {
            return false;
        }

        _sweptThisGame.Add(Area.Tristram);
        if (!await party.WalkToObject(EntityCode.CainGibbet))
        {
            Log.Warning("The party could not reach Cain's gibbet");
        }

        await party.FightHere(Party.EngageRadius + 10);
        var freed = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            var gibbet = leader.Game.GetEntityByCode(EntityCode.CainGibbet).OrderBy(entity => entity.Location.Distance(leader.Game.Me.Location)).FirstOrDefault();
            if (gibbet == null)
            {
                await party.WalkToObject(EntityCode.CainGibbet);
                return false;
            }

            if (gibbet.Location.Distance(leader.Game.Me.Location) > 3)
            {
                await MovementHelpers.MoveToWorldObject(leader.Game, pathingService, mapApiService, gibbet, MovementMode.Walking);
            }

            leader.Game.InteractWithEntity(gibbet);
            await Task.Delay(600);
            leader.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
            return CainCredited(leader);
        }, TimeSpan.FromSeconds(40));
        Log.Information("The party {Outcome} Cain", freed ? "freed" : "did NOT free");

        // The reward comes first: a Tristram sweep at these levels chickens members, and a step that fails
        // after the rescue sends the next game all the way back here. Tristram is farmed from the farm table.
        if (!await LeaveTristram(party))
        {
            return false;
        }

        return await ClaimCainReward(party);
    }

    private async Task<bool> ClaimCainReward(Party party)
    {
        if (!await party.GoHome())
        {
            return false;
        }

        foreach (var member in party.Members.Where(member => member.InGame && !HasFinished(member, QuestId.TheSearchForCain)))
        {
            await SayToNpc(member, NPCCode.Akara, AkaraRewardsCain);
        }

        RefreshQuests(party);
        foreach (var member in party.Members.Where(member => member.InGame && !HasFinished(member, QuestId.TheSearchForCain)))
        {
            await TalkThrough(member, NPCCode.DeckardCainAct1);
            await TalkThrough(member, NPCCode.Akara);
            member.Game.RequestQuestDataAndWait(TimeSpan.FromSeconds(1));
            Log.Information("{Character} search for Cain flags 0x{Flags:X4} after the reward talks", member.Name,
                member.Game.Quests.GetCharacterFlags(QuestId.TheSearchForCain));
        }

        await VisitTown(party);
        return party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.TheSearchForCain) || CainCredited(member));
    }

    private static bool CainCredited(PartyMember member)
        => HasFinished(member, QuestId.TheSearchForCain) || member.Game.Quests.IsAwaitingReward(QuestId.TheSearchForCain)
            || member.Game.Quests.IsCreditedThisGame(QuestId.TheSearchForCain);

    private const int AntidotesForAndariel = 5;

    /// <summary>How far from Andariel's preset the clearing stops: outside her room, past the rooms before it.</summary>
    private const double AndarielStopShort = 40;

    /// <summary>Monsters this close to her spot are left for the fight: going back for them pulled her lair onto the party.</summary>
    private const double AndarielKeepAway = 30;

    /// <summary>
    /// Andariel is near: she is not settled for the party yet and the member is close to her level. The waypoints are
    /// no guide here, a member that just joined knows none of them until a menu lists them.
    /// </summary>
    private bool WantsAntidotes(PartyMember member)
        => member.Game.Act == Act.Act1 && member.Game.Me.Attributes.GetValueOrDefault(D2NG.Core.D2GS.Players.Attribute.Level) >= 12
            && (!HasFinished(member, QuestId.SistersToTheSlaughter) || !PartyBeat("Andariel"));

    /// <summary>A few before the fight; the rest are drunk mid-fight as a member backs off low, to clear the poison so healing works.</summary>
    private const int AntidotesBeforeTheFight = 2;

    private static void DrinkAntidotes(PartyMember member)
    {
        var antidotes = member.Game.Inventory.Items.Where(item => item.Name == ItemName.AntidotePotion).Take(AntidotesBeforeTheFight).ToList();
        foreach (var antidote in antidotes)
        {
            member.Game.ActivateInventoryItem(antidote);
            Thread.Sleep(250);
        }

        Log.Information("{Character} drank {Count} antidote potions", member.Name, antidotes.Count);
    }

    private static bool Holds(PartyMember member, ItemName name) => member.Game.Inventory.Items.Any(item => item.Name == name);

    private async Task<bool> FetchInifussScroll(Party party)
    {
        if (!await party.GoTo(Area.DarkWood) || !await party.WalkToObject(EntityCode.InifussTree))
        {
            Log.Warning("The party could not reach the Tree of Inifuss");
            return false;
        }

        var leader = party.Leader;
        await party.FightHere(Party.EngageRadius);
        for (var attempt = 0; attempt < 5 && !Holds(leader, ItemName.ScrollofInifuss); attempt++)
        {
            var tree = leader.Game.GetEntityByCode(EntityCode.InifussTree).OrderBy(entity => entity.Location.Distance(leader.Game.Me.Location)).FirstOrDefault();
            if (tree != null)
            {
                if (tree.Location.Distance(leader.Game.Me.Location) > 4)
                {
                    await MovementHelpers.MoveToWorldObject(leader.Game, pathingService, mapApiService, tree, MovementMode.Walking);
                }

                leader.Game.InteractWithEntity(tree);
                await Task.Delay(800);
            }

            var scroll = leader.Game.Items.Values.FirstOrDefault(item => item.Ground && item.Name == ItemName.ScrollofInifuss);
            if (scroll != null)
            {
                await leader.Game.MoveToAsync(scroll);
                LevelingLoot.MakeRoomFor(leader, scroll);
                leader.Game.PickupItem(scroll);
                GeneralHelpers.TryWithTimeout(_ => Holds(leader, ItemName.ScrollofInifuss), TimeSpan.FromSeconds(2));
            }
        }

        Log.Information("{Leader} {Outcome} the Scroll of Inifuss", leader.Name, Holds(leader, ItemName.ScrollofInifuss) ? "has" : "did NOT get");
        return Holds(leader, ItemName.ScrollofInifuss) && await party.TouchWaypoint();
    }

    /// <summary>Every unlit stone once per round: the one whose turn it is lights up, the others ignore it.</summary>
    private async Task<bool> OpenCairnStones(Party party)
    {
        var leader = party.Leader;
        if (!await party.GoTo(Area.StonyFields) || !await party.WalkToObject(EntityCode.CairnStoneAlpha))
        {
            return false;
        }

        await party.FightHere(Party.EngageRadius);
        leader.Game.ExpectItemsTaken(leader.Game.Inventory.Items.Where(item => item.Name == ItemName.KeytotheCairnStones));
        var opened = await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (TristramPortal(leader) != null)
            {
                return true;
            }

            await party.FightHere(12);
            foreach (var stone in CairnStones
                .Select(code => leader.Game.GetEntityByCode(code).OrderBy(entity => entity.Location.Distance(leader.Game.Me.Location)).FirstOrDefault())
                .Where(stone => stone != null && stone.State == EntityState.Alive))
            {
                leader.Game.RequestUpdate(leader.Game.Me.Id);
                await Task.Delay(150);
                if (leader.Game.Me.Location.Distance(stone.Location) > 3)
                {
                    await MovementHelpers.MoveToWorldObject(leader.Game, pathingService, mapApiService, stone, MovementMode.Walking);
                }

                leader.Game.InteractWithEntity(stone);
                await Task.Delay(400);
            }

            return GeneralHelpers.TryWithTimeout(_ => TristramPortal(leader) != null, TimeSpan.FromSeconds(3));
        }, TimeSpan.FromSeconds(90));
        Log.Information("The Cairn Stones {Outcome}", opened ? "opened the portal to Tristram" : "did NOT open");
        return opened;
    }

    private static WorldObject TristramPortal(PartyMember member)
        => member.Game.GetEntityByCode(EntityCode.RedTownPortal).FirstOrDefault(portal => portal.TownPortalArea == Area.Tristram);

    private static async Task<bool> EnterTristram(Party party)
    {
        if (!await party.GoTo(Area.StonyFields) || !await party.WalkToObject(EntityCode.CairnStoneAlpha))
        {
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout(_ => TristramPortal(party.Leader) != null, TimeSpan.FromSeconds(8)))
        {
            Log.Warning("No portal to Tristram stands at the Cairn Stones");
            return false;
        }

        return await party.StepThroughPortal(member => TristramPortal(member), Area.Tristram);
    }

    private static async Task<bool> LeaveTristram(Party party)
    {
        static WorldObject Back(PartyMember member)
            => member.Game.GetEntityByCode(EntityCode.RedTownPortal).OrderBy(portal => portal.Location.Distance(member.Game.Me.Location)).FirstOrDefault();

        // The red portal is no map preset, so a path to it only comes back while it is in sight: freed in Hell, Cain's
        // rescue failed two seconds after, on the walk back. Its last known place is tried next, and failing both the
        // party goes home by town portal for the reward instead - Stony Fields was never needed.
        if (!await party.WalkToObject(EntityCode.RedTownPortal)
            && !(Back(party.Leader) is { } seen && await party.WalkTo(seen.Location) && Back(party.Leader)?.Location.Distance(party.Leader.Game.Me.Location) < 15))
        {
            Log.Information("The party could not get back to the Tristram portal; it goes home by town portal");
            return true;
        }

        return await party.StepThroughPortal(Back, Area.StonyFields) || party.Leader.Game.Area == Area.Tristram;
    }

    /// <summary>Andariel in the fourth level of the Catacombs; her death credits every party member in act 1.</summary>
    private async Task<bool> Andariel(Party party)
    {
        // Her poison hurts most at these levels: every member carries antidotes, bought right before the walk.
        if (party.Leader.Game.IsInTown())
        {
            await Task.WhenAll(party.Members.Where(member => member.InGame && member.Game.IsInTown()
                && member.Game.Inventory.Items.Count(item => item.Name == ItemName.AntidotePotion) < AntidotesForAndariel).Select(async member =>
            {
                if (await WalkToNpc(member, NPCHelpers.GetSellNPC(member.Game.Act)) && NPCHelpers.GetUniqueNPC(member.Game, NPCHelpers.GetSellNPC(member.Game.Act)) is { } akara)
                {
                    LevelingShop.BuyAntidotes(member, akara, AntidotesForAndariel);
                }
            }));
        }

        party.Rushing = true;
        try
        {
            if (!await party.GoTo(Area.CatacombsLevel4))
            {
                return false;
            }
        }
        finally
        {
            party.Rushing = false;
        }

        // The rooms before hers are cleared first, then a trip to town buys back what that cost - full belts and
        // fresh antidotes - and the party opens her door from the cleared spot it comes back to.
        var map = await mapApiService.GetArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty, Area.CatacombsLevel4);
        // The party's own area record says Catacombs 4 after a leader rejoined in town; the leader's place decides.
        if (party.Leader.Game.IsInTown())
        {
            return false;
        }

        var cameOut = false;
        if (map?.Npcs?.GetValueOrDefault((int)NPCCode.Andarial)?.FirstOrDefault() is { } lair)
        {
            cameOut = await party.ClearTowards(lair, AndarielStopShort, NPCCode.Andarial, lair, AndarielKeepAway);
        }

        if (!cameOut)
        {
            await party.TownTrip(() => VisitTown(party));
        }

        // A leader that chickened on the way is back in town: the hunt there found nothing and logged her as alive.
        if (!party.Leader.InGame || party.Leader.Game.IsInTown() || party.RegroupRequested)
        {
            return false;
        }

        // Drunk the moment she comes close, not on entering her level: the walk to her outlasted the potions.
        party.BossInSight = _ =>
        {
            foreach (var member in party.Members.Where(member => member.InGame))
            {
                DrinkAntidotes(member);
            }
        };
        bool killed;
        try
        {
            killed = await party.KillBoss(NPCCode.Andarial, BossLimit);
        }
        finally
        {
            party.BossInSight = null;
        }

        RefreshQuests(party);
        var byParty = killed && PartyAlone(party);
        if (byParty)
        {
            RecordPartyBeat("Andariel");
        }

        Log.Information("Andariel {Outcome}{Who}: {States}", killed ? "is dead" : "is NOT dead", killed && !byParty ? " (with outside help, not counted as the party's)" : "",
            string.Join(", ", party.Members.Select(member => $"{member.Name} 0x{member.Game.Quests.GetCharacterFlags(QuestId.SistersToTheSlaughter):X4}")));
        await party.GoHome();
        foreach (var member in party.Members.Where(member => member.InGame && !HasFinished(member, QuestId.SistersToTheSlaughter)))
        {
            await SayToNpc(member, NPCCode.WarrivAct1, WarrivsAndarielMessage);
        }

        RefreshQuests(party);
        await VisitTown(party);
        return byParty && party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.SistersToTheSlaughter));
    }
}
