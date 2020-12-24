using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace D2NG.Navigation.Services.Pathing
{
    public interface IPathingService
    {
        public Task<bool> IsNavigatablePointInArea(uint mapId, Difficulty difficulty, Area area, Point currentLocation);
        public Task<List<Point>> GetPathToLocation(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            Point toLocation, MovementMode movementMode);

        public Task<List<Point>> GetPathToArea(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            Area toArea, MovementMode movementMode);

        public Task<List<Point>> GetPathToObject(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            EntityCode entityCode, MovementMode movementMode);

        public Task<List<Point>> GetPathToObjectWithOffset(uint mapId, Difficulty difficulty, Area area, Point fromLocation, EntityCode entityCode, short xOffset, short yOffset, MovementMode movementMode);

        public Task<List<Point>> GetPathToNPC(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            NPCCode npcCode, MovementMode movementMode);

        public Task<List<Point>> GetPathFromWaypointToArea(uint mapId, Difficulty difficulty, Area area, Waypoint waypoint,
            Area toArea, MovementMode movementMode);
    }
}
