using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Services.Pathing;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace D2NG.Navigation.Extensions
{
    public static class PathingServiceExtensions
    {
        public static async Task<List<Point>> GetPathToNPC(this IPathingService pathingService, Game game, NPCCode NPCCode, MovementMode movementMode)
        {
            return await pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, game.Area, game.Me.Location, NPCCode, movementMode);
        }

        public static async Task<List<Point>> GetPathToObject(this IPathingService pathingService, Game game, EntityCode entityCode, MovementMode movementMode)
        {
            return await pathingService.GetPathToObject(game.MapId, Difficulty.Normal, game.Area, game.Me.Location, entityCode, movementMode);
        }

        public static async Task<List<Point>> GetPathToObjectWithOffset(this IPathingService pathingService, Game game, EntityCode entityCode, short xOffset, short yOffset, MovementMode movementMode)
        {
            return await pathingService.GetPathToObjectWithOffset(game.MapId, Difficulty.Normal, game.Area, game.Me.Location, entityCode, xOffset, yOffset, movementMode);
        }

        public static async Task<List<Point>> GetPathToLocation(this IPathingService pathingService, Game game, Point toLocation, MovementMode movementMode)
        {
            return await pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, game.Area, game.Me.Location, toLocation, movementMode);
        }

        public static async Task<List<Point>> GetPathToArea(this IPathingService pathingService, Game game, Area toArea, MovementMode movementMode)
        {
            return await pathingService.GetPathToArea(game.MapId, Difficulty.Normal, game.Area, game.Me.Location, toArea, movementMode);
        }

        public static async Task<List<Point>> ToTownWayPoint(this IPathingService pathingService, Game game, MovementMode movementMode)
        {
            switch (game.Act)
            {
                case Act.Act1:
                    return await pathingService.GetPathToObject(game.MapId, Difficulty.Normal, Area.RogueEncampment, game.Me.Location, EntityCode.WaypointAct1, movementMode);
                case Act.Act2:
                    return await pathingService.GetPathToObject(game.MapId, Difficulty.Normal, Area.LutGholein, game.Me.Location, EntityCode.WaypointAct2, movementMode);
                case Act.Act3:
                    return await pathingService.GetPathToObject(game.MapId, Difficulty.Normal, Area.KurastDocks, game.Me.Location, EntityCode.WaypointAct3, movementMode);
                case Act.Act4:
                    return await pathingService.GetPathToObject(game.MapId, Difficulty.Normal, Area.ThePandemoniumFortress, game.Me.Location, EntityCode.WaypointAct4, movementMode);
                case Act.Act5:
                    return await pathingService.GetPathToObject(game.MapId, Difficulty.Normal, Area.Harrogath, game.Me.Location, EntityCode.WaypointAct5, movementMode);
            }

            throw new InvalidOperationException("WalkToTownWayPoint executed an invalid operation");
        }
    }
}
