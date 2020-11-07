using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Roy_T.AStar.Grids;
using Roy_T.AStar.Paths;
using Roy_T.AStar.Primitives;
using Roy_T.AStar.Serialization;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.Navigation.Services.Pathing
{
    public class PathingService : IPathingService
    {
        private readonly IMapApiService _mapApiService;
        private readonly IMemoryCache _cache;

        public PathingService(IMapApiService mapApiService, IMemoryCache cache)
        {
            _mapApiService = mapApiService;
            _cache = cache;
        }

        public async Task<List<Point>> GetPathToLocation(uint mapId, Difficulty difficulty, Area area, Point fromLocation, Point toLocation,
            MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            return GetPath(mapId, difficulty, area, map, movementMode, fromLocation, toLocation);
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
            return GetPath(mapId, difficulty, area, map, movementMode, fromLocation, toLocation);
        }

        public async Task<List<Point>> GetPathToObjectWithOffset(uint mapId, Difficulty difficulty, Area area, Point fromLocation, EntityCode entityCode, short xOffset, short yOffset, MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            if (map.Objects.TryGetValue((int)entityCode, out var objectPoints) && objectPoints.Count > 0)
            {
                return GetPath(mapId, difficulty, area, map, movementMode, fromLocation, objectPoints.First().Add(xOffset, yOffset));
            }

            return new List<Point>();
        }

        public async Task<List<Point>> GetPathToObject(uint mapId, Difficulty difficulty, Area area, Point fromLocation, EntityCode entityCode, MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            if (map.Objects.TryGetValue((int)entityCode, out var objectPoints) && objectPoints.Count > 0)
            {
                return GetPath(mapId, difficulty, area, map, movementMode, fromLocation, objectPoints.First());
            }

            return new List<Point>();
        }

        public async Task<List<Point>> GetPathToNPC(uint mapId, Difficulty difficulty, Area area, Point fromLocation, NPCCode npcCode,
            MovementMode movementMode)
        {
            var map = await _mapApiService.GetArea(mapId, difficulty, area);
            if (map.Npcs.TryGetValue((int)npcCode, out var points) && points.Count > 0)
            {
                return GetPath(mapId, difficulty, area, map, movementMode, fromLocation, points.First());
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
                return GetPath(mapId, difficulty, area, map, movementMode, points.First(), toLocation);
            }

            if (map.Objects.TryGetValue(entityCode, out var objectPoints) && objectPoints.Count > 0)
            {
                return GetPath(mapId, difficulty, area, map, movementMode, objectPoints.First(), toLocation);
            }

            return new List<Point>();
        }


        private List<Point> GetPath(uint mapId, Difficulty difficulty, Area area, AreaMap map, MovementMode movementMode, Point fromLocation, Point toLocation)
        {
            if (!map.TryMapToPointInMap(fromLocation, out var fromPosition) || !map.TryMapToPointInMap(toLocation, out var toPosition))
            {
                return new List<Point>();
            }

            if (movementMode == MovementMode.Teleport)
            {
                var teleportPath = new TeleportPather(map);
                var path = teleportPath.GetTeleportPath(fromLocation - map.LevelOrigin, toLocation - map.LevelOrigin);
                if (path.Found)
                {
                    return path.Points.Select(p => map.LevelOrigin + p).Skip(1).ToList();
                }
            }
            else
            {
                var grid = _cache.GetOrCreate<Grid>(Tuple.Create("pathing", mapId, difficulty, area), (cacheEntry) =>
                {
                    cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(2);
                    return map.MapToGrid();
                });
                var pathFinder = new PathFinder();
                var fromGridPosition = new GridPosition(fromPosition.X, fromPosition.Y);
                var toGridPosition = new GridPosition(toPosition.X, toPosition.Y);
                var path = pathFinder.FindPath(fromGridPosition, toGridPosition, grid);
                var endPosition = path.Edges.LastOrDefault()?.End.Position;
                if (endPosition.HasValue && map.MapToPoint(endPosition.Value) == toLocation)
                {
                    return path.Edges.Where((p, i) => i % 5 == 0 || i == path.Edges.Count - 1).Select(e => map.MapToPoint(e.End.Position)).ToList();
                }
            }

            return new List<Point>();
        }
    }
}
