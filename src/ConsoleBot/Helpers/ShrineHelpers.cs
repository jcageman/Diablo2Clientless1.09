using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Core.ObjectData;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

/// <summary>
/// Finding and taking shrines with what the server already tells us about them.
/// </summary>
/// <remarks>
/// The assign object packet carries the shrine's function in its last byte (the shrines.txt code,
/// <see cref="ShrineType"/>), so a shrine's kind is known the moment it is in view; no click is
/// needed to find out. Captured on the 1.09 realm with a real client: health shrines arrive as 2,
/// mana as 3, and the rolled ones as what they rolled (12 skill, 13 recharge, 14 stamina, 15
/// experience, 21 exploding). Taking one is the same entity interaction the bots use on chests
/// and waypoints; the server answers with the object going to state Activating (0x0E), the buff
/// state on the character (0x13, for example 137 for experience) and the shrine message (0x26).
/// </remarks>
public static class ShrineHelpers
{
    /// <summary>How long to wait for the shrine's buff to land after the click before giving up.</summary>
    private static readonly TimeSpan TakeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How far from where the shrine was first seen it is still looked for when taking it.</summary>
    private const double ShrineSearchRadius = 15;

    public static ShrineType TypeOf(WorldObject shrine) => (ShrineType)shrine.InteractionType;

    /// <summary>The shrine objects currently in view of this client.</summary>
    public static List<WorldObject> VisibleShrines(Game game, ShrineTable shrines)
    {
        return game.WorldObjects.Values
            .Where(o => o.Type == EntityType.Object && shrines.IsShrine(o.Code))
            .ToList();
    }

    /// <summary>A shrine of the wanted kind in view that has not been used yet, or null.</summary>
    public static WorldObject FindShrine(Game game, ShrineTable shrines, ShrineType type)
    {
        return VisibleShrines(game, shrines)
            .FirstOrDefault(s => TypeOf(s) == type && s.State != EntityState.Activated && s.State != EntityState.Activating);
    }

    /// <summary>
    /// Which character should get an experience shrine: the highest level one, because experience
    /// is level wide and the boost is a fraction of what its taker earns. Ties go to the first.
    /// Returns null when nobody is in the game.
    /// </summary>
    public static string PickHighestLevel(IEnumerable<(string Name, int Level)> characters)
    {
        string best = null;
        var bestLevel = int.MinValue;
        foreach (var (name, level) in characters)
        {
            if (name != null && level > bestLevel)
            {
                best = name;
                bestLevel = level;
            }
        }

        return best;
    }

    public static string PickHighestLevel(IEnumerable<Game> games)
    {
        return PickHighestLevel(games
            .Where(g => g.IsInGame() && g.Me != null)
            .Select(g => (g.Me.Name, g.Me.Attributes.TryGetValue(D2NG.Core.D2GS.Players.Attribute.Level, out var level) ? level : 0)));
    }

    /// <summary>
    /// Moves to where a shrine was seen and takes it. The shrine object itself is looked up again on
    /// arrival because the client only has objects near it; a follower that heard about the shrine
    /// from another client has never seen the object. Returns true once the buff is on the character.
    /// </summary>
    public static async Task<bool> TakeShrine(Game game,
                                              IPathingService pathingService,
                                              IMapApiService mapApiService,
                                              ShrineTable shrines,
                                              ShrineType type,
                                              Point location,
                                              EntityEffect expectedEffect)
    {
        var movementMode = game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
        if (!await MovementHelpers.MoveToLocation(game, pathingService, mapApiService, location, movementMode))
        {
            Log.Warning($"Client {game.Me.Name} could not reach the {type} shrine at {location}, standing at {game.Me.Location}");
            return false;
        }

        var shrine = VisibleShrines(game, shrines)
            .Where(s => TypeOf(s) == type && s.Location.Distance(location) <= ShrineSearchRadius)
            .OrderBy(s => s.Location.Distance(location))
            .FirstOrDefault();
        if (shrine == null)
        {
            Log.Warning($"Client {game.Me.Name} found no {type} shrine within {ShrineSearchRadius} of {location}");
            return false;
        }

        if (shrine.State == EntityState.Activated || shrine.State == EntityState.Activating)
        {
            Log.Information($"Client {game.Me.Name}: the {type} shrine at {location} has already been used");
            return false;
        }

        if (!await MovementHelpers.MoveToWorldObject(game, pathingService, mapApiService, shrine, movementMode))
        {
            return false;
        }

        game.InteractWithEntity(shrine);

        // Poll the character's own effects rather than waiting on a packet event: the reset events are
        // keyed by packet type, so any effect packet for anyone would satisfy them.
        var taken = await GeneralHelpers.TryWithTimeout(async (_) =>
        {
            await Task.Delay(100);
            return game.Me.Effects.ContainsKey(expectedEffect);
        }, TakeTimeout);

        Log.Information(taken
            ? $"Client {game.Me.Name} took the {type} shrine at {location}"
            : $"Client {game.Me.Name} clicked the {type} shrine at {location} but {expectedEffect} did not appear within {TakeTimeout.TotalSeconds}s");
        return taken;
    }
}
