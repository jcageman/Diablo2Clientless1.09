using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using Microsoft.Extensions.Caching.Memory;
using Roy_T.AStar.Grids;
using Roy_T.AStar.Paths;
using Roy_T.AStar.Primitives;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.Navigation.Services.Pathing;

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

    /// <summary>
    /// Path into a neighbouring area. Uses the level exit when the map data has one and otherwise paths
    /// to the shared boundary and steps over it in whatever movement mode the caller asked for, which is the only way across the many level borders the
    /// game has no warp for - town to wilderness, or the river of flame into the chaos sanctuary.
    /// </summary>
    public async Task<List<Point>> GetPathToAdjacentArea(uint mapId, Difficulty difficulty, Area area, Point fromLocation,
        Area toArea, MovementMode movementMode)
    {
        var map = await _mapApiService.GetArea(mapId, difficulty, area);
        if (!map.AdjacentLevels.TryGetValue(toArea, out var adjacentLevel))
        {
            throw new InvalidOperationException($"Area {toArea} does not border {area}");
        }

        // Nearest is not the same as reachable. Act 1 town is walled, so the closest patch of the blood
        // moor boundary is usually on the far side of the palisade: aiming at it produced a string of
        // failed teleports into a wall. Every candidate is therefore tested by asking for a path to it.
        foreach (var exit in adjacentLevel.Exits.OrderBy(fromLocation.Distance))
        {
            // A warp tile is frequently not walkable itself, so the destination is allowed to snap here.
            // The crossing search below keeps snapping off, because it uses an empty path as its test of
            // whether a candidate is reachable and snapping would make every candidate look reachable.
            var exitPath = GetPath(mapId, difficulty, area, map, movementMode, fromLocation, exit);
            if (exitPath.Count > 0)
            {
                return exitPath;
            }
        }

        var targetMap = await _mapApiService.GetArea(mapId, difficulty, toArea);
        var crossings = map.FindCrossings(adjacentLevel, targetMap, fromLocation);
        var tried = new List<string>();
        foreach (var crossing in Thin(crossings, MinimumCandidateSeparation).Take(MaximumCandidatesToPath))
        {
            var path = GetPath(mapId, difficulty, area, map, movementMode, fromLocation, crossing.Approach, snapDestination: false);
            if (path.Count == 0)
            {
                tried.Add($"{crossing.Approach}->{crossing.Target} unreachable");
                continue;
            }

            // Walk to this side of the boundary, then step onto the other side. Following the whole list
            // ends in the target area, so callers need no special case for a border without a warp.
            path.Add(crossing.Target);
            return path;
        }

        throw new InvalidOperationException(
            $"No reachable exit or crossing from {area} into {toArea} standing at {fromLocation} using {movementMode}: "
            + $"{adjacentLevel.Exits.Count} exits, {crossings.Count} crossings, tried [{string.Join("; ", tried)}]");
    }

    /// <summary>How far apart candidate crossings have to be before both are worth pathing to.</summary>
    private const int MinimumCandidateSeparation = 10;

    /// <summary>Cap on pathing attempts, so an unreachable border fails quickly instead of grinding.</summary>
    private const int MaximumCandidatesToPath = 25;

    /// <summary>
    /// Drops candidates that sit on top of one another. Neighbouring cells of the same doorway all pass
    /// or all fail together, so pathing to each of them is wasted work.
    /// </summary>
    private static List<AreaCrossingExtensions.AreaCrossing> Thin(
        List<AreaCrossingExtensions.AreaCrossing> crossings, int separation)
    {
        var kept = new List<AreaCrossingExtensions.AreaCrossing>();
        foreach (var crossing in crossings)
        {
            if (kept.TrueForAll(k => k.Approach.Distance(crossing.Approach) >= separation))
            {
                kept.Add(crossing);
            }
        }

        return kept;
    }

    /// <summary>
    /// Breadth first search over the adjacency the map api reports, so a destination several areas away
    /// can be asked for by name. The result includes the starting area.
    /// </summary>
    public async Task<List<Area>> GetAreaRoute(uint mapId, Difficulty difficulty, Area fromArea, Area toArea)
    {
        if (fromArea == toArea)
        {
            return [fromArea];
        }

        var previous = new Dictionary<Area, Area>();
        var seen = new HashSet<Area> { fromArea };
        var queue = new Queue<Area>();
        queue.Enqueue(fromArea);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var map = await _mapApiService.GetArea(mapId, difficulty, current);
            foreach (var neighbour in map.AdjacentLevels.Keys)
            {
                if (!seen.Add(neighbour))
                {
                    continue;
                }

                previous[neighbour] = current;
                if (neighbour == toArea)
                {
                    return BuildRoute(previous, fromArea, toArea);
                }

                queue.Enqueue(neighbour);
            }
        }

        return [];
    }

    private static List<Area> BuildRoute(Dictionary<Area, Area> previous, Area fromArea, Area toArea)
    {
        var route = new List<Area> { toArea };
        var current = toArea;
        while (current != fromArea)
        {
            current = previous[current];
            route.Add(current);
        }

        route.Reverse();
        return route;
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

        return [];
    }

    public async Task<List<Point>> GetPathToObject(uint mapId, Difficulty difficulty, Area area, Point fromLocation, EntityCode entityCode, MovementMode movementMode)
    {
        var map = await _mapApiService.GetArea(mapId, difficulty, area);
        if (map.Objects.TryGetValue((int)entityCode, out var objectPoints) && objectPoints.Count > 0)
        {
            return GetPath(mapId, difficulty, area, map, movementMode, fromLocation, objectPoints.First());
        }

        return [];
    }

    public async Task<List<Point>> GetPathToNPC(uint mapId, Difficulty difficulty, Area area, Point fromLocation, NPCCode npcCode,
        MovementMode movementMode)
    {
        var map = await _mapApiService.GetArea(mapId, difficulty, area);
        if (map.Npcs.TryGetValue((int)npcCode, out var points) && points.Count > 0)
        {
            return GetPath(mapId, difficulty, area, map, movementMode, fromLocation, points.First());
        }

        return [];
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

        return [];
    }


    /// <summary>How far to look for somewhere to stand when an endpoint is off the level's grid.</summary>
    private const int StartSnapRadius = 50;

    /// <summary>Kept short, because moving the destination changes where the caller ends up.</summary>
    private const int DestinationSnapRadius = 15;

    private List<Point> GetPath(uint mapId, Difficulty difficulty, Area area, AreaMap map, MovementMode movementMode,
        Point fromLocation, Point toLocation, bool snapDestination = true)
    {
        var path = FindPath(mapId, difficulty, area, map, movementMode, fromLocation, toLocation);
        if (path.Count > 0)
        {
            return path;
        }

        return RetryFromNearestNavigablePoints(mapId, difficulty, area, map, movementMode, fromLocation, toLocation,
            snapDestination);
    }

    /// <summary>
    /// Second attempt for a path the grid could not produce, with either end moved onto the nearest cell
    /// that can be stood on.
    /// </summary>
    /// <remarks>
    /// Teleporting characters end up outside the level's grid often enough that this is routine rather
    /// than exceptional, and an endpoint the grid cannot use produces an empty path, which callers read
    /// as "unreachable" and act on by giving up or, worse, by appending to it and moving into a wall.
    /// This runs only after the honest attempt has already failed, so no path that works today changes.
    /// </remarks>
    private List<Point> RetryFromNearestNavigablePoints(uint mapId, Difficulty difficulty, Area area, AreaMap map,
        MovementMode movementMode, Point fromLocation, Point toLocation, bool snapDestination)
    {
        var from = fromLocation;
        var snappedStart = false;
        if (!map.IsNavigable(fromLocation)
            && map.TryFindNearestNavigablePoint(fromLocation, StartSnapRadius, out var nearestStart))
        {
            from = nearestStart;
            snappedStart = true;
        }

        var to = toLocation;
        var snappedDestination = false;
        if (snapDestination
            && !map.IsNavigable(toLocation)
            && map.TryFindNearestNavigablePoint(toLocation, DestinationSnapRadius, out var nearestDestination))
        {
            to = nearestDestination;
            snappedDestination = true;
        }

        if (!snappedStart && !snappedDestination)
        {
            return [];
        }

        var path = FindPath(mapId, difficulty, area, map, movementMode, from, to);
        if (path.Count == 0)
        {
            return [];
        }

        Log.Warning("Pathing in {Area} fell back to the nearest navigable points, {From} -> {To} became {SnappedFrom} -> {SnappedTo}",
            area, fromLocation, toLocation, from, to);

        if (snappedStart)
        {
            path.Insert(0, from);
        }

        return path;
    }

    private List<Point> FindPath(uint mapId, Difficulty difficulty, Area area, AreaMap map, MovementMode movementMode, Point fromLocation, Point toLocation)
    {
        if (!map.TryMapToPointInMap(fromLocation, out var fromPosition) || !map.TryMapToPointInMap(toLocation, out var toPosition))
        {
            return [];
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
            var endPosition = path.Edges.Count > 0 ? path.Edges[^1]?.End.Position : null;
            if (endPosition.HasValue && map.MapToPoint(endPosition.Value) == toLocation)
            {
                return path.Edges.Where((p, i) => i % 3 == 0 || i == path.Edges.Count - 1).Select(e => map.MapToPoint(e.End.Position)).ToList();
            }
        }

        return [];
    }

    public async Task<bool> IsNavigatablePointInArea(uint mapId, Difficulty difficulty, Area area, Point currentLocation)
    {
        var map = await _mapApiService.GetArea(mapId, difficulty, area);
        return map.IsNavigable(currentLocation);
    }
}
