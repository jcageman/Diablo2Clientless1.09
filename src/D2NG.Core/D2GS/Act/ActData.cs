using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Incoming;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Act
{
    class ActData
    {
        public Act Act { get; set; }
        public uint MapId { get; set; }
        public Area Area { get; set; }

        private readonly ConcurrentDictionary<Act, List<Tile>> _tiles = new ConcurrentDictionary<Act, List<Tile>>();
        private readonly ConcurrentDictionary<Act, ConcurrentDictionary<(uint, EntityType), WorldObject>> _worldObjects
            = new ConcurrentDictionary<Act, ConcurrentDictionary<(uint, EntityType), WorldObject>>();
        private readonly ConcurrentDictionary<Act, ConcurrentDictionary<EntityCode, List<WorldObject>>> _worldObjectsByEntityCode
            = new ConcurrentDictionary<Act, ConcurrentDictionary<EntityCode, List<WorldObject>>>();

        private readonly ConcurrentDictionary<Act, ConcurrentDictionary<uint, List<WarpData>>> _warps = new ConcurrentDictionary<Act, ConcurrentDictionary<uint, List<WarpData>>>();

        public List<Tile> Tiles { get => _tiles.GetOrAdd(Act, new List<Tile>()); }

        public ConcurrentDictionary<(uint, EntityType), WorldObject> WorldObjects { get => _worldObjects.GetOrAdd(Act, new ConcurrentDictionary<(uint, EntityType), WorldObject>()); }

        public ConcurrentDictionary<EntityCode, List<WorldObject>> WorldObjectsByEntityCode { get => _worldObjectsByEntityCode.GetOrAdd(Act, new ConcurrentDictionary<EntityCode, List<WorldObject>>()); }

        public ConcurrentDictionary<uint, List<WarpData>> Warps { get => _warps.GetOrAdd(Act, new ConcurrentDictionary<uint, List<WarpData>>()); }

        internal void LoadActData(ActDataPacket packet)
        {
            Act = packet.Act;
            Area = packet.Area;
            MapId = packet.MapId;
        }

        internal void UpdateNPCLocation(uint entityId, Point location)
        {
            var npc = WorldObjects[(entityId, EntityType.NPC)];
            if (npc != null)
            {
                npc.Location = location;
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

            npc.LifePercentage = packet.LifePercentage;
        }

        internal void UpdateNPCOnHit(NpcHitPacket packet)
        {
            var entity = WorldObjects[(packet.EntityId, packet.EntityType)];
            entity.LifePercentage = packet.LifePercentage;
        }

        internal void AddNPC(AssignNpcPacket packet)
        {
            var npc = new WorldObject(EntityType.NPC, packet.EntityId, 0, packet.Location, EntityState.Alive, 0);
            npc.NPCCode = packet.UniqueCode;
            npc.MonsterEnchantments = packet.MonsterEnchantments;
            AddWorldObject(npc);
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
                WorldObjectsByEntityCode.AddOrUpdate(
    value.Code,
    (newCode => new List<WorldObject> { }),
    (existingCode, existingObjects) => { existingObjects.RemoveAll(o => o.Id == value.Id && o.Type == value.Type); return existingObjects; });
            }

        }

        internal void AddWorldObject(WorldObject obj)
        {
            WorldObjects[(obj.Id, obj.Type)] = obj;
            WorldObjectsByEntityCode.AddOrUpdate(obj.Code, (newCode => new List<WorldObject> { obj }), (existingCode, existingObjects) => { existingObjects.Add(obj); return existingObjects; });
        }

        internal void AddWarp(AssignLevelWarpPacket packet)
        {
            Warps.GetOrAdd(packet.WarpId, new List<WarpData>()).Add(packet.AsWarpData());
        }

        internal void HandleMapRevealPacket(MapRevealPacket p)
        {
            Area = p.Area;
            var tile = new Tile(p.X, p.Y, p.Area);
            if (!Tiles.Any(item => item.Equals(tile)))
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
            }
        }
    }
}
