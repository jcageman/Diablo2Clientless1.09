using ConsoleBot.Helpers;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Leveling;

/// <summary>
/// A sorceress with Teleport goes ahead and the party follows through her portal: it cleared Maggot Lair's halls for
/// twenty minutes to reach the Staff of Kings, which earned it next to nothing. She does not fight the chest's guards
/// alone - she stops short of them and the party comes through to kill.
/// </summary>
public sealed partial class LevelingBot
{
    private const double StopShortOfChest = 10;

    /// <summary>True when the party stands at <paramref name="container"/> in <paramref name="area"/>, brought there by her portal.</summary>
    private async Task<bool> TeleportAhead(Party party, Area area, EntityCode container)
    {
        var sorceress = party.Members.FirstOrDefault(member => member.InGame && member.SkillLevel(Skill.Teleport) > 0 && member.Game.IsInTown());
        // Everyone in game: a member still rejoining after a death is left out of which waypoints all hold, and the
        // party then set off for one she did not have.
        if (sorceress == null || party.Members.Any(member => !member.InGame || !member.Game.IsInTown()))
        {
            return false;
        }

        var game = sorceress.Game;
        Log.Information("{Character} teleports ahead to the {Container} in {Area}; the party follows through her portal", sorceress.Name, container, area);
        sorceress.Mode = FollowMode.Off;
        try
        {
            if (!await BattleOrdersOnTheWay(party, sorceress, area) && !await GoAlone(sorceress, area))
            {
                Log.Information("{Character} could not get to {Area} alone; the party walks", sorceress.Name, area);
                await ComeHomeAlone(sorceress);
                return false;
            }

            var path = await pathingService.GetPathToObject(game.MapId, AreaTraveler.MapDifficulty, area, game.Me.Location, container, MovementMode.Teleport);
            if (path.Count > 0)
            {
                var end = path[^1];
                var shortOf = path.TakeWhile(point => point.Distance(end) > StopShortOfChest).ToList();
                await MovementHelpers.TakePathOfLocations(game, shortOf.Count > 0 ? shortOf : path, MovementHelpers.PreferredMovement(game));
            }

            if (!await Party.CastPortalFor(sorceress))
            {
                await ComeHomeAlone(sorceress);
                return false;
            }

            // She keeps fighting and moving while the others come through: standing still at the portal beside the
            // chest's guards for the seconds that takes is how she would die.
            var arriving = Task.WhenAll(party.Members.Where(member => member != sorceress && member.InGame)
                .Select(member => ThroughHerPortal(party, member, area)));
            while (!arriving.IsCompleted && sorceress.InGame)
            {
                if (!await _combat.FightTick(sorceress, sorceress.Game.Me.Location, Party.EngageRadius))
                {
                    await Task.Delay(200);
                }
            }

            var arrived = await arriving;
            if (!arrived.All(done => done))
            {
                Log.Information("Not everyone came through {Character}'s portal into {Area}; the party walks", sorceress.Name, area);
                return false;
            }

            party.Area = area;
            return true;
        }
        finally
        {
            foreach (var follower in party.Followers)
            {
                follower.Mode = FollowMode.Follow;
            }
        }
    }

    /// <summary>
    /// A few looks, not one: her portal takes a moment to show in town, and the others gave up after two seconds in
    /// Kurast Docks and walked all three levels of the Flayer Dungeon instead.
    /// </summary>
    private static async Task<bool> ThroughHerPortal(Party party, PartyMember member, Area area)
    {
        // Ten looks: in Hell Kurast Docks the leader, still on his way home through his own portal, gave up after four
        // while the others stood in Travincal - and the walk that followed pulled all of them out of the game.
        for (var look = 0; look < 10; look++)
        {
            if (await party.TakePortalInto(member, area))
            {
                return true;
            }

            await Task.Delay(2000);
        }

        Log.Information("{Character} found no portal into {Area} in town", member.Name, area);
        return false;
    }

    /// <summary>
    /// The party takes the furthest waypoint on the way together, the barbarian's Battle Orders goes on her there - it
    /// does nothing cast in town - and the others go home by the same waypoint while she teleports on. False when there
    /// is no barbarian with it or the waypoint trip fails, and she then goes alone from town.
    /// </summary>
    private async Task<bool> BattleOrdersOnTheWay(Party party, PartyMember sorceress, Area area)
    {
        var barbarian = party.Members.FirstOrDefault(member => member.InGame && member.SkillLevel(Skill.BattleOrders) > 0);
        var route = await _traveler.Route(party.Leader.Client, area);
        var waypoint = route.Reverse().Select(Party.WaypointIn)
            .FirstOrDefault(candidate => candidate is { } held && party.Members.Where(member => member.InGame).All(member => member.Game.Me.AllowedWaypoints.Contains(held)));
        // Battle Orders does nothing in town: with only the town's waypoint on the way - the Pandemonium Fortress to the City
        // of the Damned - it went on her there and she chickened in the Outer Steppes. The party walks out to the first
        // area beyond town together and she gets it there.
        Area? shoutAt = waypoint is { } found && found.ToArea() != party.Leader.Game.Act.MapTownArea() ? found.ToArea()
            : route.FirstOrDefault(step => step != party.Leader.Game.Act.MapTownArea()) is var first && first != default ? first : null;
        if (barbarian == null || shoutAt == null)
        {
            Log.Information("No Battle Orders on the way to {Area}: {Reason}", area,
                barbarian == null ? "nobody has the skill" : $"no waypoint all hold on the route {string.Join(" > ", route)}");
            return false;
        }

        // She follows the party there: taken off following at the start of the teleport, she stayed at the Pandemonium
        // Fortress's edge while the others walked out, and the barbarian shouted beside her still in town.
        sorceress.Mode = FollowMode.Follow;
        if (!await party.GoTo(shoutAt.Value))
        {
            // Home first: a failed waypoint trip falls back to walking, and her portal would open in an empty town.
            Log.Information("No Battle Orders on the way to {Area}: the party did not reach {Spot}", area, shoutAt.Value);
            await Task.WhenAll(party.Members.Where(member => member.InGame).Select(ComeHomeAlone));
            party.Area = party.Leader.Game.Act.MapTownArea();
            return false;
        }

        // Both out of town by the server's word first: Battle Orders cast in town does nothing, and at the Pandemonium
        // Fortress's edge (x 5160) the client already said Outer Steppes while every cast came to nothing. A few steps on
        // towards the next area on the way puts them clearly outside.
        bool OutOfTown() => sorceress.Game.IsOutOfTownConfirmed() && barbarian.Game.IsOutOfTownConfirmed();
        await GeneralHelpers.TryWithTimeout(async _ =>
        {
            await Task.Delay(500);
            return OutOfTown();
        }, TimeSpan.FromSeconds(10));
        var onward = route.SkipWhile(step => step != shoutAt.Value).Skip(1).FirstOrDefault();
        if (!OutOfTown() && onward != default)
        {
            // The border to the next area may have no warp - the Outer Steppes into the Plains of Despair - and asking for an
            // exit there threw and stopped the bot.
            List<Point> path;
            try
            {
                path = await pathingService.GetPathToAdjacentArea(barbarian.Game.MapId, AreaTraveler.MapDifficulty, shoutAt.Value, barbarian.Game.Me.Location,
                    onward, MovementMode.Walking);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Information("No way on from {Spot} for Battle Orders: {Reason}", shoutAt.Value, exception.Message);
                path = [];
            }

            if ((path.Skip(3).FirstOrDefault() ?? path.LastOrDefault()) is { } spot)
            {
                using var walk = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                await Task.WhenAll(new[] { barbarian, sorceress }.Select(member =>
                    MovementHelpers.MoveToLocation(member.Game, pathingService, mapApiService, spot, MovementMode.Walking, walk.Token)));
            }

            await GeneralHelpers.TryWithTimeout(async _ =>
            {
                await Task.Delay(500);
                return OutOfTown();
            }, TimeSpan.FromSeconds(10));
        }

        // Cast for her, whatever the barbarian has on himself: his own Battle Orders still running, the upkeep cast none and
        // she went into Hell act 4 without it. He stands beside her for it - it reaches only those close by.
        await GeneralHelpers.TryWithTimeout(async _ =>
        {
            if (barbarian.Game.Me.Location.Distance(sorceress.Game.Me.Location) > 5)
            {
                await barbarian.Game.MoveToAsync(sorceress.Game.Me.Location);
            }

            return (await LevelingBuffs.Shout(barbarian, [sorceress])).Contains(sorceress);
        }, TimeSpan.FromSeconds(8));
        Log.Information("{Character} {Outcome} Battle Orders in {Spot}", sorceress.Name,
            sorceress.Game.Me.Effects.ContainsKey(EntityEffect.BattleOrders) ? "has" : "did NOT get", shoutAt.Value);

        sorceress.Mode = FollowMode.Off;
        // Home through portals, not the waypoint: they arrive at the town's portal spot, where hers will open - in Lut
        // Gholein the waypoint is a long walk from it.
        await Task.WhenAll(party.Members.Where(member => member != sorceress && member.InGame).Select(member =>
        {
            member.Mode = FollowMode.Off;
            return ComeHomeAlone(member);
        }));

        party.Area = party.Leader.Game.Act.MapTownArea();
        return shoutAt.Value == area || await _traveler.TraverseTo(sorceress.Client, area);
    }


    /// <summary>From town to <paramref name="area"/>: the furthest held waypoint on the way, then teleporting the rest.</summary>
    private async Task<bool> GoAlone(PartyMember member, Area area)
    {
        var game = member.Game;
        if (Party.WaypointIn(game.Act.MapTownArea()) is not { } townWaypoint)
        {
            return false;
        }

        var toWaypoint = await pathingService.GetPathToObject(game.MapId, AreaTraveler.MapDifficulty, game.Act.MapTownArea(), game.Me.Location,
            townWaypoint.ToEntityCode(), MovementMode.Walking);
        if (toWaypoint.Count > 0)
        {
            await MovementHelpers.TakePathOfLocations(game, toWaypoint, MovementMode.Walking);
        }

        WorldObject town = null;
        GeneralHelpers.TryWithTimeout(_ => (town = game.GetEntityByCode(townWaypoint.ToEntityCode()).FirstOrDefault()) != null, TimeSpan.FromSeconds(3));
        if (town == null)
        {
            return false;
        }

        await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, town, MovementMode.Walking);
        if (game.Me.AllowedWaypoints.IsEmpty)
        {
            game.ReadWaypoints(town);
        }

        var route = await _traveler.Route(member.Client, area);
        var waypoint = route.Reverse().Select(Party.WaypointIn).FirstOrDefault(candidate => candidate is { } held && game.Me.AllowedWaypoints.Contains(held));
        // Only the town's own waypoint on the way - act 4's City of the Damned from the Pandemonium Fortress: she teleports
        // from town. Taking a waypoint to where she stood failed, and the party walked into the fortress gate instead.
        if (waypoint == townWaypoint)
        {
            return await _traveler.TraverseTo(member.Client, area);
        }

        if (waypoint == null || !game.TakeWaypoint(town, waypoint.Value) || !await _traveler.ConfirmInArea(member.Client, waypoint.Value.ToArea()))
        {
            return false;
        }

        return waypoint.Value.ToArea() == area || await _traveler.TraverseTo(member.Client, area);
    }

    /// <summary>Back to town through a portal of its own.</summary>
    private async Task ComeHomeAlone(PartyMember member)
    {
        var game = member.Game;
        if (game.IsInTown() || !await Party.CastPortalFor(member))
        {
            return;
        }

        WorldObject portal = null;
        GeneralHelpers.TryWithTimeout(_ => (portal = game.GetEntityByCode(EntityCode.TownPortal)
            .FirstOrDefault(candidate => candidate.TownPortalOwnerId == game.Me.Id)) != null, TimeSpan.FromSeconds(3));
        if (portal == null)
        {
            return;
        }

        for (var attempt = 0; attempt < 3 && !game.IsInTown(); attempt++)
        {
            await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, portal, MovementMode.Walking);
            game.InteractWithEntity(portal);
            await _traveler.ConfirmInArea(member.Client, game.Act.MapTownArea(), attempts: 4);
        }
    }
}
