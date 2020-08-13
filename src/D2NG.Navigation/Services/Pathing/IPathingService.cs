using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using System.Collections.Generic;
using System.Threading.Tasks;
using D2NG.Core;
using D2NG.Core.D2GS.Objects;

namespace D2NG.Navigation.Services.Pathing
{
    public interface IPathingService
    {
        public Task<List<Point>> GetPathToLocation(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            Point toLocation, MovementMode movementMode);

        public Task<List<Point>> GetPathToArea(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            Area toArea, MovementMode movementMode);

        public Task<List<Point>> GetPathToObject(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            EntityCode entityCode, MovementMode movementMode);

        public Task<List<Point>> GetPathToNPC(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            NPCCode npcCode, MovementMode movementMode);

        public Task<List<Point>> GetPathFromWaypointToArea(uint mapId, Difficulty difficulty, Area area, Waypoint waypoint,
            Area toArea, MovementMode movementMode);
    }
}
