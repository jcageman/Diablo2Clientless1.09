using D2NG.Core.D2GS.Motion;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace D2NG.Core.D2GS.Act;

internal class ActData
{
    public Act Act { get; set; }
    public uint MapId { get; set; }
    public Area Area { get; set; }

    private readonly ConcurrentDictionary<Act, List<Tile>> _tiles = new();
    private readonly ConcurrentDictionary<Act, ConcurrentDictionary<(uint, EntityType), WorldObject>> _worldObjects
        = new();
    private readonly ConcurrentDictionary<Act, ConcurrentDictionary<EntityCode, CodeObjects>> _worldObjectsByEntityCode
        = new();

    private readonly ConcurrentDictionary<Act, ConcurrentDictionary<uint, List<WarpData>>> _warps = new();

    public List<Tile> Tiles { get => _tiles.GetOrAdd(Act, static _ => []); }

    /// <summary>What Tile.Equals compares of every tile in <see cref="Tiles"/>, which only a reveal adds to; touched by the receive thread only.</summary>
    private readonly ConcurrentDictionary<Act, HashSet<(ushort X, ushort Y, Area Area)>> _tileKeys = new();

    private HashSet<(ushort X, ushort Y, Area Area)> TileKeys => _tileKeys.GetOrAdd(Act, static _ => []);

    public ConcurrentDictionary<(uint, EntityType), WorldObject> WorldObjects { get => _worldObjects.GetOrAdd(Act, static _ => new ConcurrentDictionary<(uint, EntityType), WorldObject>()); }

    public ConcurrentDictionary<EntityCode, CodeObjects> WorldObjectsByEntityCode { get => _worldObjectsByEntityCode.GetOrAdd(Act, static _ => new ConcurrentDictionary<EntityCode, CodeObjects>()); }

    public ConcurrentDictionary<uint, List<WarpData>> Warps { get => _warps.GetOrAdd(Act, static _ => new ConcurrentDictionary<uint, List<WarpData>>()); }

    internal AreaResolver Resolver { get; init; } = new();

    private bool _lifeFromStopsAndAssigns = true;
    private bool _lifeZeroForDyingAndDead = true;

    internal ClientRules Rules
    {
        init
        {
            _lifeFromStopsAndAssigns = value.MonsterLifeFromStopsAndAssigns;
            _lifeZeroForDyingAndDead = value.LifeZeroForDyingAndDead;
        }
    }

    internal void LoadActData(ActDataPacket packet)
    {
        Act = packet.Act;
        Area = packet.Area;
        MapId = packet.MapId;
        Resolver.ActLoaded(packet.Act, packet.Area);
    }

    internal void UpdateNPCLocation(uint entityId, Point location)
    {
        var npc = WorldObjects[(entityId, EntityType.NPC)];
        npc?.Location = location;
    }

    /// <summary>
    /// A stop's position, and its life for a unit still alive. A stop stating 0 states no life: town folk and other
    /// units that take no damage stop with 0 while alive, and a death comes as an NPCState.
    /// </summary>
    internal void UpdateNPCStop(NPCStopPacket packet)
    {
        var npc = WorldObjects[(packet.EntityId, EntityType.NPC)];
        if (npc == null)
        {
            return;
        }

        npc.Location = packet.Location;
        if (_lifeFromStopsAndAssigns && packet.LifePercentage > 0 && npc.State is not (EntityState.Dead or EntityState.Dieing))
        {
            npc.LifePercentage = packet.LifePercentage;
        }
    }

    internal void UpdateObjectState(ObjectStatePacket packet)
    {
        var npc = WorldObjects[(packet.ObjectId, EntityType.Object)];
        npc.State = packet.State;
    }

    internal void UpdateNPCState(NpcStatePacket packet)
    {
        var npc = WorldObjects[(packet.EntityId, EntityType.NPC)];
        npc.State = packet.EntityState;
        if (packet.EntityState == EntityState.BeingHit || packet.EntityState == EntityState.Dead)
        {
            npc.Location = packet.Location;
        }

        npc.LifePercentage = _lifeZeroForDyingAndDead || packet.EntityState is not (EntityState.Dieing or EntityState.Dead)
            ? packet.LifePercentage
            : packet.Direction / 1.28;
    }

    internal void UpdateNPCOnHit(NpcHitPacket packet)
    {
        var entity = WorldObjects[(packet.EntityId, packet.EntityType)];
        entity.LifePercentage = packet.LifePercentage;
    }

    internal void AddNPC(AssignNpcPacket packet)
    {
        var npc = new WorldObject(EntityType.NPC, packet.EntityId, 0, packet.Location, EntityState.Alive, 0)
        {
            NPCCode = packet.UniqueCode,
            MonsterEnchantments = packet.MonsterEnchantments,
            IsBoss = packet.IsBoss,
            IsChampion = packet.IsChampion,
            IsBossMinion = packet.IsBossMinion
        };
        if (_lifeFromStopsAndAssigns && packet.LifePercentage > 0)
        {
            npc.LifePercentage = packet.LifePercentage;
        }

        if (Motion != null)
        {
            try
            {
                npc.AttachMotion(Motion, MotionUpdate.From(packet));
            }
            catch (System.Exception e)
            {
                MotionFailures.Report(e);
            }
        }

        AddWorldObject(npc);
    }

    internal MotionContext Motion { get; init; }

    internal void ApplyMotion(in MotionUpdate update)
    {
        if (update.Kind != MotionUpdateKind.None && WorldObjects.TryGetValue((update.EntityId, EntityType.NPC), out var npc))
        {
            npc.ApplyMotion(update);
        }
    }

    internal void UpdateTownPortal(TownPortalStatePacket packet)
    {
        var entity = WorldObjects[(packet.TeleportId, EntityType.Object)];
        entity.TownPortalArea = packet.Area;
    }

    internal void UpdateTownPortalOwner(PortalOwnerPacket packet)
    {
        var entity = WorldObjects[(packet.TeleportOurSideId, EntityType.Object)];
        entity.TownPortalOwnerId = packet.OwnerId;
    }

    internal void RemoveWorldObject(uint entityId, EntityType entityType)
    {
        WorldObjects.Remove((entityId, entityType), out var value);
        if (value != null)
        {
            WorldObjectsByEntityCode.GetOrAdd(value.Code, static _ => new CodeObjects()).Remove(value.Id, value.Type);
        }
    }

    internal void AddWorldObject(WorldObject obj)
    {
        WorldObjects[(obj.Id, obj.Type)] = obj;
        WorldObjectsByEntityCode.GetOrAdd(obj.Code, static _ => new CodeObjects()).Add(obj);
    }

    internal void AddWarp(AssignLevelWarpPacket packet)
    {
        Warps.GetOrAdd(packet.WarpId, []).Add(packet.AsWarpData());
    }

    private Point _lastResolvedPosition;
    private Area _lastResolvedArea = Area.None;

    /// <summary>
    /// The area a world position falls in, taken from the revealed tiles rather than from whichever
    /// reveal packet arrived last.
    /// </summary>
    /// <remarks>
    /// Reveal packets arrive for neighbouring levels as they come into view, so the area they name is
    /// not necessarily the area being stood in: approaching the underground passage entrance names the
    /// dungeon while the character is still in stony field. Tiles carry their own area and bounds, so
    /// asking which tile contains the position answers the question properly. The last answer is kept
    /// because callers read the current area in tight loops and the tile list grows as an act is
    /// explored.
    /// </remarks>
    internal Area AreaAtPosition(Point position)
    {
        if (position == null)
        {
            return Area.None;
        }

        if (_lastResolvedArea != Area.None && position.Equals(_lastResolvedPosition))
        {
            return _lastResolvedArea;
        }

        lock (Tiles)
        {
            foreach (var tile in Tiles)
            {
                if (tile.Contains(position))
                {
                    _lastResolvedPosition = position;
                    _lastResolvedArea = tile.Area;
                    return tile.Area;
                }
            }
        }

        return Area.None;
    }

    /// <summary>
    /// Records a revealed map tile, and adopts its area as the current one only when the tile is the one
    /// being stood in.
    /// </summary>
    /// <param name="position">
    /// Where the character is, or null when that is not known yet. Reveals arrive for neighbouring levels
    /// as they come into view, so taking every reveal at face value reports the area next door: standing
    /// at the underground passage entrance used to report the dungeon while still in stony field.
    /// </param>
    internal void HandleMapRevealPacket(MapRevealPacket p, Point position = null)
    {
        var keys = TileKeys;
        if (keys.Contains((p.X, p.Y, p.Area)))
        {
            if (position == null || Tile.Contains(p.X, p.Y, p.Area, position))
            {
                Area = p.Area;
                _lastResolvedArea = Area.None;
            }

            return;
        }

        var tile = new Tile(p.X, p.Y, p.Area);
        if (position == null || tile.Contains(position))
        {
            Area = p.Area;
            _lastResolvedArea = Area.None;
        }

        if (keys.Add((tile.X, tile.Y, tile.Area)))
        {
            lock (Tiles)
            {
                Tiles.Add(tile);
                foreach (var t in Tiles)
                {
                    if (t.IsNorthOf(tile))
                    {
                        tile.North = t;
                        t.South = tile;
                    }
                    if (t.IsEastOf(tile))
                    {
                        tile.East = t;
                        t.West = tile;
                    }
                    if (t.IsSouthOf(tile))
                    {
                        tile.South = t;
                        t.North = tile;
                    }
                    if (t.IsWestOf(tile))
                    {
                        tile.West = t;
                        t.East = tile;
                    }
                }
            }

            Resolver.TileRevealed(Act, tile);
        }
    }
}
