using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using Microsoft.Extensions.Logging;
using Roy_T.AStar.Paths;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using D2NG.Core.D2GS.Objects;
using Roy_T.AStar.Primitives;
using Roy_T.AStar.Serialization;

namespace D2NG.Navigation.Services.Pathing
{
    public class PathingService : IPathingService
    {
        private readonly IMapApiService _mapApiService;
        private readonly ILogger<PathingService> _logger;

        public PathingService(IMapApiService mapApiService, ILogger<PathingService> logger)
        {
            _mapApiService = mapApiService;
            _logger = logger;
        }

        public async Task<List<Point>> GetPathToLocation(uint mapId, Difficulty difficulty, Area area, Point fromLocation, Point toLocation,
            MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            return GetPath(map, movementMode, fromLocation, toLocation);
        }

        public async Task<List<Point>> GetPathToArea(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
            Area toArea, MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            if (!map.AdjacentLevels.TryGetValue(toArea, out var adjacentLevel))
            {
                throw new InvalidOperationException($"Adjacent Area {toArea} does not exist for area {area}");
            }

            if (adjacentLevel.Exits.Count == 0)
            {
                throw new InvalidOperationException($"No exits for area {toArea}");
            }

            var toLocation = adjacentLevel.Exits[0];
            return GetPath(map, movementMode, fromLocation, toLocation);
        }

        public async Task<List<Point>> GetPathToObject(uint mapId, Difficulty difficulty, Area area, Point fromLocation, EntityCode entityCode, MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            if (map.Objects.TryGetValue((int)entityCode, out var objectPoints) && objectPoints.Count > 0)
            {
                return GetPath(map, movementMode, fromLocation, objectPoints.First());
            }

            return new List<Point>();
        }

        public async Task<List<Point>> GetPathToNPC(uint mapId, Difficulty difficulty, Area area, Point fromLocation, NPCCode npcCode,
            MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            if (map.Npcs.TryGetValue((int)npcCode, out var points) && points.Count > 0)
            {
                return GetPath(map, movementMode, fromLocation, points.First());
            }

            return new List<Point>();
        }

        public async Task<List<Point>> GetPathFromWaypointToArea(uint mapId, Difficulty difficulty, Area area,
            Waypoint waypoint,
            Area toArea, MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            if (!map.AdjacentLevels.TryGetValue(toArea, out var adjacentLevel))
            {
                throw new InvalidOperationException($"Adjacent Area {toArea} does not exist for area {area}");
            }

            if (adjacentLevel.Exits.Count == 0)
            {
                throw new InvalidOperationException($"No exits for area {toArea}");
            }

            var toLocation = adjacentLevel.Exits[0];

            var entityCode = (int)waypoint.ToEntityCode();

            if (map.Npcs.TryGetValue(entityCode, out var points) && points.Count > 0)
            {
                return GetPath(map, movementMode, points.First(), toLocation);
            }

            if (map.Objects.TryGetValue(entityCode, out var objectPoints) && objectPoints.Count > 0)
            {
                return GetPath(map, movementMode, objectPoints.First(), toLocation);
            }

            return new List<Point>();
        }


        private List<Point> GetPath(AreaMap map, MovementMode movementMode, Point fromLocation, Point toLocation)
        {
            if (movementMode != MovementMode.Teleport)
            {
                throw new NotImplementedException("Movement mode {movementMode} is not implemented yet");
            }

            var teleportPath = new TeleportPather(map);
            var path = teleportPath.GetTeleportPath(fromLocation - map.LevelOrigin, toLocation - map.LevelOrigin);
            return path.Points.Select(p => map.LevelOrigin + p).Skip(1).ToList();
        }
    }
}
