using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.D2GS.Quest;
using D2NG.Navigation.Extensions;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// Act 4 on foot: Izual for Tyrael's two skill points, then the Chaos Sanctuary's five seals, each of the three
/// groups guarded by a boss that has to die before Diablo comes to the star, and Tyrael to close the act.
/// The rush bot's circuit teleports; a leveling party walks it together.
/// </summary>
public sealed partial class LevelingBot
{
    private const uint TyraelRewardsIzual = 0x2A4;
    private const uint TyraelClosesActFour = 0x02AC;

    /// <summary>The seals in the order the party walks them: Vizier's pair, De Seis's one, Infector's pair.</summary>
    private static readonly EntityCode[][] SealGroupsInOrder =
    [
        [EntityCode.LeftSeal1, EntityCode.LeftSeal2],
        [EntityCode.TopSeal],
        [EntityCode.RightSeal1, EntityCode.RightSeal2],
    ];

    private static readonly TimeSpan SealBossWait = TimeSpan.FromSeconds(60);

    private const double SealReach = 15;

    private static readonly TimeSpan SealBossHunt = TimeSpan.FromSeconds(120);

    /// <summary>The hunt after a seal of the group stayed shut: what came is fought, nobody waits for what may never come.</summary>
    private static readonly TimeSpan ShutSealHunt = TimeSpan.FromSeconds(20);

    private const double SealBossSearch = 70;

    private const double SealBossClose = 15;

    private IReadOnlyList<LevelingStep> ActFourSteps =>
    [
        // Act 4 asks for levels: at 26 the Burning Souls on the way to the City of the Damned chickened every member, some
        // at 0 life left.
        // Farming act 3 for it gave about 5,000 xp a minute - hours per level at 26 - so the gates stay near the party's
        // level and the chickens, rejoin portals and lightning resistance carry it through the Souls instead.
        WaypointStep(Waypoint.CityOfTheDamned, 26),
        new("Izual", member => InActFour(member) && !HasFinished(member, QuestId.TheFallenAngel), Izual, Gate: 27),
        WaypointStep(Waypoint.RiverOfFlame, 27),
        // At 29 the Chaos Sanctuary's packs took all four within half a minute, attempt after attempt. Level 30 opens
        // Whirlwind and Frozen Orb, which the barbarian and the sorceress save their points for. In Nightmare they have
        // both long before: 30 plus the offset asked for 50, over seven hours of farming from 47.
        new("Diablo", member => InActFour(member) && (!HasFinished(member, QuestId.TerrorsEnd) || MoreDiabloKills), Diablo, Gate: 30, NightmareGate: 45),
    ];

    /// <summary>In act 4, or farming in act 3 after Mephisto: there the act 4 steps were ignored and the party farmed on
    /// "aiming at 60" past the level it came for.</summary>
    private static bool InActFour(PartyMember member) => member.Game.Act == Act.Act4
        || (member.Game.Act == Act.Act3 && HasFinished(member, QuestId.TheGuardian));

    private async Task<bool> Izual(Party party)
    {
        if (!await party.GoTo(Area.PlainsOfDespair))
        {
            return false;
        }

        var map = await mapApiService.GetArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty, Area.PlainsOfDespair);
        if (map?.Npcs?.GetValueOrDefault((int)NPCCode.Izual)?.FirstOrDefault() is { } spot)
        {
            await party.WalkTo(spot);
        }

        var killed = await party.KillBoss(NPCCode.Izual, BossLimit);
        Log.Information("Izual {Outcome}", killed ? "is dead" : "is NOT dead");
        await party.GoHome();
        RefreshQuests(party);
        foreach (var member in party.Members.Where(member => member.InGame && member.Game.Quests.IsAwaitingReward(QuestId.TheFallenAngel)))
        {
            await SayToNpc(member, NPCCode.TyraelAct4, TyraelRewardsIzual);
        }

        RefreshQuests(party);
        return party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.TheFallenAngel));
    }

    private async Task<bool> Diablo(Party party)
    {
        if (!await party.GoTo(Area.ChaosSanctuary))
        {
            return false;
        }

        var map = await mapApiService.GetArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty, Area.ChaosSanctuary);
        if (map?.Objects == null)
        {
            Log.Warning("No map of the Chaos Sanctuary");
            return false;
        }

        var allOpen = true;
        foreach (var group in SealGroupsInOrder)
        {
            // Before the group's first seal: the Infector of Souls came at the first right seal and died on the way to the
            // second, and counted as an earlier boss's corpse the party stood two minutes at the second seal, every run.
            var deadBefore = DeadSuperUniques(party);
            var groupOpen = true;
            foreach (var seal in group)
            {
                if (map.Objects.GetValueOrDefault((int)seal)?.FirstOrDefault() is not { } at)
                {
                    Log.Warning("{Seal} is not in the map", seal);
                    continue;
                }

                if (party.RegroupRequested)
                {
                    return false;
                }

                groupOpen &= await OpenSealOnFoot(party, seal, at);
            }

            // A seal that stayed shut may be the one its boss comes from: the hunt is cut short rather than waited out for a
            // boss that never spawns, and the party clears on - the rest of the Sanctuary is the experience left in the run.
            allOpen &= groupOpen;
            await FightSealBoss(party, deadBefore, await SealBossSpawn(party, group, map), groupOpen ? SealBossHunt : ShutSealHunt);
            if (party.RegroupRequested)
            {
                return false;
            }
        }

        // Left the game - a game server restart, a lost connection - the seals after it only failed their walks.
        if (!party.Leader.InGame)
        {
            return false;
        }

        if (!allOpen)
        {
            Log.Warning("A seal stayed shut: Diablo will not come; the run ends with the Sanctuary cleared");
            return false;
        }

        var star = map.Objects.GetValueOrDefault((int)EntityCode.DiabloStar)?.FirstOrDefault();
        if (star != null)
        {
            await party.WalkTo(star);
        }

        var appeared = await WaitFighting(party, () => party.Leader.Game.GetNPCsByCode(NPCCode.Diablo).Count > 0, SealBossWait);
        if (!appeared)
        {
            Log.Warning("Diablo did not come to the star; a seal boss is probably still alive");
            return false;
        }

        var killed = await party.KillBoss(NPCCode.Diablo, BossLimit);
        Log.Information("Diablo {Outcome}", killed ? "is dead" : "is NOT dead");
        if (!killed)
        {
            return false;
        }

        await party.GoHome();
        RefreshQuests(party);
        foreach (var member in party.Members.Where(member => member.InGame && !HasFinished(member, QuestId.TerrorsEnd)))
        {
            await SayToNpc(member, NPCCode.TyraelAct4, TyraelClosesActFour);
        }

        RefreshQuests(party);
        return party.Members.Where(member => member.InGame).All(member => HasFinished(member, QuestId.TerrorsEnd));
    }

    /// <summary>Whether the seal is open.</summary>
    private static async Task<bool> OpenSealOnFoot(Party party, EntityCode seal, Point at)
    {
        var game = party.Leader.Game;
        // Every seal of two attempts "did NOT open" without the leader taking a step toward it: the walk came back at
        // once and the click looked for a seal a hundred yards away. It is retried and each failure says why.
        for (var attempt = 1; attempt <= 3 && game.Me.Location.Distance(at) > SealReach; attempt++)
        {
            var walked = await party.WalkTo(at);
            if (game.Me.Location.Distance(at) > SealReach)
            {
                Log.Warning("Walk {Attempt} to {Seal} at {At} ended {Distance:0} away at {Location} (walk {Outcome}, regroup {Regroup}, leader in game {InGame})",
                    attempt, seal, at, game.Me.Location.Distance(at), game.Me.Location, walked ? "done" : "failed", party.RegroupRequested, party.Leader.InGame);
                if (party.RegroupRequested || !party.Leader.InGame)
                {
                    return false;
                }

                await party.SettleInArea();
            }
        }

        await party.FightHere(Party.EngageRadius);
        var opened = await TryOpen(game, seal, TimeSpan.FromSeconds(10));
        // The others try: the paladin stood 4.5 from the top seal while sixteen clicks went unanswered and the server held
        // him in place, and without its boss Diablo never came - the run was lost.
        // The sorceress's Telekinesis first, as the CS bot opens its seals: a click has the server walk the clicker onto the
        // seal, and at the top seal four members clicked fourteen times each, held four yards off, while it stayed shut.
        foreach (var member in opened ? [] : party.Followers.Where(member => member.InGame && !member.Game.IsInTown()
                         && member.Game.GetEntityByCode(seal).Any(entity => entity.Location.Distance(member.Game.Me.Location) <= SealHelpReach))
                     .OrderByDescending(member => member.SkillLevel(Skill.Telekinesis) > 0).ToList())
        {
            var mode = member.Mode;
            member.Mode = FollowMode.Off;
            try
            {
                opened = await TryOpen(member.Game, seal, TimeSpan.FromSeconds(6), telekinesis: member.SkillLevel(Skill.Telekinesis) > 0);
            }
            finally
            {
                member.Mode = mode;
            }

            if (opened)
            {
                Log.Information("{Character} opened {Seal} the leader could not", member.Name, seal);
                break;
            }
        }

        Log.Information("{Seal} {Outcome}", seal, opened ? "is open" : "did NOT open");
        return opened;
    }

    /// <summary>Members this close to a seal the leader could not open try it themselves.</summary>
    private const double SealHelpReach = 40;

    /// <summary>Telekinesis reaches a seal from where the caster stands; the CS sorceress casts it from beside the seal.</summary>
    private const double TelekinesisReach = 10;

    /// <param name="telekinesis">Telekinesis in turn with a click, as the CS bot opens seals: it works where a click is blocked.</param>
    private static async Task<bool> TryOpen(D2NG.Core.Game game, EntityCode seal, TimeSpan limit, bool telekinesis = false)
    {
        var attempt = 0;
        return await GeneralHelpers.TryWithTimeout(async _ =>
        {
            var entity = game.GetEntityByCode(seal).OrderBy(candidate => candidate.Location.Distance(game.Me.Location)).FirstOrDefault();
            if (entity == null)
            {
                await Task.Delay(300);
                return false;
            }

            if (entity.State is EntityState.Activated or EntityState.Activating)
            {
                return true;
            }

            if (telekinesis && attempt++ % 2 == 0 && game.Me.Mana > 10)
            {
                if (entity.Location.Distance(game.Me.Location) > TelekinesisReach)
                {
                    await game.MoveToAsync(entity);
                }

                game.UseRightHandSkillOnEntity(Skill.Telekinesis, entity);
                await Task.Delay(400);
                return false;
            }

            if (entity.Location.Distance(game.Me.Location) > 4)
            {
                await game.MoveToAsync(entity);
            }

            game.InteractWithEntity(entity);
            await Task.Delay(400);
            return false;
        }, limit);
    }

    /// <summary>
    /// Where the group's boss turns up when it is out of sight of the seal. De Seis spawns within a few units of one
    /// point per layout (the CS bot's 180 games): seal + (-42, 23) when the ground left of the seal is open, seal +
    /// (1, 67) when the room lies below it - 45 to 67 from the seal, past what a client sees.
    /// </summary>
    private async Task<Point> SealBossSpawn(Party party, EntityCode[] group, D2NG.Navigation.Services.MapApi.AreaMap map)
    {
        if (!group.Contains(EntityCode.TopSeal) || map.Objects.GetValueOrDefault((int)EntityCode.TopSeal)?.FirstOrDefault() is not { } seal)
        {
            return null;
        }

        var leftOpen = await pathingService.IsNavigatablePointInArea(party.Leader.Game.MapId, AreaTraveler.MapDifficulty, Area.ChaosSanctuary,
            new Point((ushort)(seal.X - 20), seal.Y));
        return leftOpen ? new Point((ushort)(seal.X - 42), (ushort)(seal.Y + 23)) : new Point((ushort)(seal.X + 1), (ushort)(seal.Y + 67));
    }

    /// <summary>The super uniques lying dead in sight of any member.</summary>
    private static HashSet<uint> DeadSuperUniques(Party party)
        => party.Members.Where(member => member.InGame)
            .SelectMany(member => NPCHelpers.GetNearbySuperUniques(member.Client, 200).Where(npc => npc.State is EntityState.Dead or EntityState.Dieing))
            .Select(npc => npc.Id).ToHashSet();

    /// <summary>
    /// The group's boss spawns when one of its seals opens; the party holds the spot and fights until it dies or the hunt
    /// runs out. A corpse that lay there before the group's first seal is an earlier boss's: the Infector's seal was called
    /// done the moment it opened.
    /// </summary>
    private static async Task FightSealBoss(Party party, HashSet<uint> deadBefore, Point spawn, TimeSpan hunt)
    {
        bool Died() => party.Members.Where(member => member.InGame).Any(member =>
            NPCHelpers.GetNearbySuperUniques(member.Client, 60).Any(npc => npc.State is EntityState.Dead or EntityState.Dieing && !deadBefore.Contains(npc.Id)));

        // The boss is gone after, not waited for: Grand Vizier of Chaos and his Storm Casters stood 35 from the seal
        // casting, outside the party's reach, until the wait ran out and the party walked on with him alive.
        var started = DateTime.UtcNow;
        var died = false;
        while (!(died = Died()) && DateTime.UtcNow - started < hunt && await party.LeaderPresent() && !party.RegroupRequested)
        {
            var leader = party.Leader.Game;
            var boss = NPCHelpers.GetNearbySuperUniques(party.Leader.Client, SealBossSearch)
                .Where(npc => npc.State is not (EntityState.Dead or EntityState.Dieing))
                .OrderBy(npc => npc.Location.Distance(leader.Me.Location)).FirstOrDefault();
            if (boss != null && boss.Location.Distance(leader.Me.Location) > SealBossClose)
            {
                var near = leader.Me.Location.GetPointBeforePointInSameDirection(boss.Location, SealBossClose - 3);
                await party.WalkTo(near, () => boss.Location.Distance(leader.Me.Location) <= SealBossClose);
            }
            else if (boss == null && spawn != null && spawn.Distance(leader.Me.Location) > SealBossClose)
            {
                // Out of sight: Lord De Seis spawned beyond the party's view of the top seal, and it stood two minutes
                // beside the seal with nothing alive in sight while he lived.
                Log.Information("No seal boss in sight; the party goes to its spawn at {Spawn}", spawn);
                await party.WalkTo(spawn, () => NPCHelpers.GetNearbySuperUniques(party.Leader.Client, SealBossSearch)
                    .Any(npc => npc.State is not (EntityState.Dead or EntityState.Dieing)) || spawn.Distance(leader.Me.Location) <= SealBossClose);
            }

            await party.FightHere(Party.EngageRadius + 10);
            await Task.Delay(300);
        }

        Log.Information("The seal boss {Outcome}", died ? "is dead" : "was NOT seen dying");
        await party.FightHere(Party.EngageRadius + 10);
    }

    private static async Task<bool> WaitFighting(Party party, Func<bool> done, TimeSpan limit)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < limit && await party.LeaderPresent() && !party.RegroupRequested)
        {
            if (done())
            {
                return true;
            }

            await party.FightHere(Party.EngageRadius + 10);
            await Task.Delay(300);
        }

        return done();
    }
}
