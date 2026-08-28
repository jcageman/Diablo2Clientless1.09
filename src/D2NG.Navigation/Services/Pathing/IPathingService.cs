using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace D2NG.Navigation.Services.Pathing;

public interface IPathingService
{
    Task<bool> IsNavigatablePointInArea(uint mapId, Difficulty difficulty, Area area, Point currentLocation);
    Task<List<Point>> GetPathToLocation(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
        Point toLocation, MovementMode movementMode);

    Task<List<Point>> GetPathToArea(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
        Area toArea, MovementMode movementMode);

    Task<List<Point>> GetPathToObject(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
        EntityCode entityCode, MovementMode movementMode);

    Task<List<Point>> GetPathToObjectWithOffset(uint mapId, Difficulty difficulty, Area area, Point fromLocation, EntityCode entityCode, short xOffset, short yOffset, MovementMode movementMode);

    Task<List<Point>> GetPathToNPC(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
        NPCCode npcCode, MovementMode movementMode);

    Task<List<Point>> GetPathFromWaypointToArea(uint mapId, Difficulty difficulty, Area area, Waypoint waypoint,
        Area toArea, MovementMode movementMode);

    /// <summary>
    /// Path into a neighbouring area, whether or not the map data offers an exit to aim at. Where there is no exit the path ends by stepping across the
    /// shared boundary, so following it lands in the other area. The crossing step is an ordinary point
    /// in the path, taken in the caller's movement mode - teleported over by a sorceress, walked by a
    /// level one.
    /// </summary>
    Task<List<Point>> GetPathToAdjacentArea(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
        Area toArea, MovementMode movementMode);

    /// <summary>
    /// The chain of areas to walk through to get from one area to another, found over the adjacency the
    /// map api reports. Lets a route be asked for by destination instead of being spelled out, and
    /// works without any waypoint.
    /// </summary>
    Task<List<Area>> GetAreaRoute(uint mapId, Difficulty difficulty, Area fromArea, Area toArea);
}
