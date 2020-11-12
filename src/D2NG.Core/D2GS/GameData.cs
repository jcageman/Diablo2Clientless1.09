using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Core.MCP;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Core.D2GS
{
    internal class GameData
    {
        internal GameData(GameFlags gameFlags, Character clientCharacter)
        {
            if (clientCharacter == null)
            {
                throw new ArgumentNullException(nameof(clientCharacter));
            }

            Flags = gameFlags;
            Act = new ActData();
            ClientCharacter = clientCharacter;
            _walkingSpeedMultiplier = new Lazy<double>(() =>
            {
                var walkingSpeedIncreasedMultiplier = 1.0;
                var speedItems = this.Items.Where(i => i.Value.Action == Action.Equip && i.Value.PlayerId == Me?.Id).Select(i => i.Value.GetValueOfStatType(StatType.FasterRunWalk));
                if(speedItems.Any())
                {
                    walkingSpeedIncreasedMultiplier += speedItems.Aggregate((agg, frw) => agg + frw) / (double)100;
                }
                
                var increasedSpeedSkill = Me.Skills.GetValueOrDefault(Skill.IncreasedSpeed, 0);
                if (increasedSpeedSkill > 0)
                {
                    walkingSpeedIncreasedMultiplier += (13 + 4 * increasedSpeedSkill) / (double)100;
                }

                return walkingSpeedIncreasedMultiplier;
            });
        }

        private Lazy<double> _walkingSpeedMultiplier;

        public Character ClientCharacter { get; }
        public GameFlags Flags { get; }
        public ActData Act { get; }
        public Self Me { get; private set; }

        public Container Stash { get; } = new Stash();

        public Container Inventory { get; } = new Inventory();

        public Container Cube { get; } = new Cube();

        public Belt Belt { get; internal set; } = new Belt();

        public Item CursorItem { get; internal set; }

        public List<Player> Players { get; internal set; } = new List<Player>();

        public ConcurrentDictionary<uint, Item> Items { get; private set; } = new ConcurrentDictionary<uint, Item>();

        internal double WalkingSpeedMultiplier  {

            get { return _walkingSpeedMultiplier.Value; }
        }

        internal void AddExperience(AddExpPacket addExpPacket)
            => Me.Experience += addExpPacket.Experience;

        internal void PlayerAssign(AssignPlayerPacket packet)
        {
            if (packet.Name == ClientCharacter.Name && packet.Class == ClientCharacter.Class)
            {
                if(Me == null)
                {
                    Me = new Self(packet);
                }

                Me.Location = packet.Location;
            }

            var player = Players.Where(p => p.Id == packet.Id).FirstOrDefault();
            if (player == null)
            {
                Players.Add(new Player(packet));
            }
            else
            {
                player.Location = packet.Location;
            }
        }

        internal void PlayerJoin(PlayerInGamePacket packet)
        {
            if (packet.Name == ClientCharacter.Name && packet.Class == ClientCharacter.Class && Me == null)
            {
                Me = new Self(packet);
            }

            var player = Players.Where(p => p.Id == packet.Id).FirstOrDefault();
            if (player == null)
            {
                Players.Add(new Player(packet));
            }
        }

        internal void PlayerLeave(PlayerLeftGamePacket packet)
        {
            Players.RemoveAll(p => p.Id == packet.Id);
        }

        internal void AddEntityEffect(AddEntityEffectPacket packet)
        {
            if(packet.EntityType == EntityType.Player)
            {
                var player = Players.Where(p => p.Id == packet.EntityId).FirstOrDefault();
                if(player != null)
                {
                    player.Effects.Add(packet.Effect);
                }

                if (packet.EntityId == Me.Id)
                {
                    Me.Effects.Add(packet.Effect);
                }
            }
            else
            {
                Act.WorldObjects[(packet.EntityId, packet.EntityType)].Effects.Add(packet.Effect);
            }
        }

        internal void AddEntityEffect2(AddEntityEffectPacket2 packet)
        {
            if (packet.EntityType == EntityType.Player)
            {
                var player = Players.Where(p => p.Id == packet.EntityId).FirstOrDefault();
                if (player != null)
                {
                    player.Effects.Add(packet.Effect);
                }

                if (packet.EntityId == Me.Id)
                {
                    Me.Effects.Add(packet.Effect);
                }
            }
            else
            {
                Act.WorldObjects[(packet.EntityId, packet.EntityType)].Effects.Add(packet.Effect);
            }
        }

        internal void UpdateEntityEffects(UpdateEntityEffectsPacket packet)
        {
            if (packet.EntityType == EntityType.Player)
            {
                var player = Players.Where(p => p.Id == packet.EntityId).FirstOrDefault();
                if (player != null)
                {
                    player.Effects = packet.EntityEffects;
                }

                if (packet.EntityId == Me.Id)
                {
                    Me.Effects = packet.EntityEffects;
                }
            }
            else
            {
                Act.WorldObjects[(packet.EntityId, packet.EntityType)].Effects = packet.EntityEffects;
            }
        }


        internal void PlayerCorpseAssign(CorpseAssignPacket packet)
        {
            var player = Players.Where(p => p.Id == packet.PlayerId).FirstOrDefault();
            if (player != null)
            {
                player.CorpseId = packet.CorpseId;
            }

            if(Me?.Id == packet.PlayerId)
            {
                Me.CorpseId = packet.CorpseId;
            }

            var corpse = Players.Where(p => p.Id == packet.CorpseId).FirstOrDefault();
            if(corpse != null && !packet.CorpseAdded)
            {
                Players.RemoveAll(p => p.Id == packet.CorpseId);
            }
        }

        internal void EntityMove(EntityMovePacket packet)
        {
            if (packet.UnitId == Me.Id)
            {
                Me.Location = packet.MoveToLocation;
            }

            foreach (var player in Players)
            {
                if (packet.UnitId == player.Id)
                {
                    player.Location = packet.MoveToLocation;
                    break;
                }
            }
        }

        internal void PlayerStop(PlayerStopPacket packet)
        {
            if(packet.EntityType != EntityType.Player)
            {
                return;
            }

            if (packet.EntityId == Me.Id )
            {
                Me.Location = packet.Location;
            }

            foreach (var player in Players)
            {
                if (packet.EntityId == player.Id)
                {
                    player.Location = packet.Location;
                    break;
                }
            }
        }

        internal void ReassignPlayer(uint unitId, Point location)
        {
            if (unitId == Me.Id)
            {
                Me.Location = location;
            }

            foreach (var player in Players)
            {
                if (unitId == player.Id)
                {
                    player.Location = location;
                    break;
                }
            }
        }

        internal void SetAttribute(BaseAttributePacket baseAttribute)
            => Me.Attributes[baseAttribute.Attribute] = baseAttribute.Value;

        internal void SetItemSkill(SetItemSkillPacket packet)
        {
            if (packet.UnitId == Me.Id)
            {
                Me.ItemSkills[packet.Skill] = packet.Amount;
            }
        }

        internal void SetSkills(BaseSkillLevelsPacket packet)
        {
            if (packet.PlayerId == Me.Id)
            {
                foreach (var skill in packet.Skills)
                {
                    Me.Skills[skill.Key] = skill.Value;
                }
            }
        }

        internal void SetActiveSkill(SetActiveSkillPacket packet)
        {
            if (packet.UnitGid == Me.Id)
            {
                Me.ActiveSkills[packet.Hand] = packet.Skill;
            }
        }

        internal void UpdateSelf(LifeManaPotUpdatePacket packet)
        {
            Me.Location = packet.Location;
            Me.Life = packet.Life;
            Me.MaxLife = Math.Max(Me.MaxLife, Me.Life);
            Me.Mana = packet.Mana;
            Me.MaxMana = Math.Max(Me.MaxMana, Me.Mana);
            Me.Stamina = packet.Stamina;
        }

        internal void UpdateSelf(LifeManaUpdatePacket packet)
        {
            Me.Location = packet.Location;
            Me.Life = packet.Life;
            //Workaround: seems the game server remembers battle orders during initialization
            if (Act.MapId > 0)
            {
                Me.MaxLife = Math.Max(Me.MaxLife, Me.Life);
                Me.MaxMana = Math.Max(Me.MaxMana, Me.Mana);
            }

            Me.Mana = packet.Mana;

            Me.Stamina = packet.Stamina;
        }

        internal void UpdateWaypointInfo(WaypointMenuPacket packet)
        {
            Me.LastSelectedWaypointId = packet.WaypointId;
            Me.AllowedWaypoints = new ConcurrentBag<Waypoint>(packet.AllowedWaypoints);
        }

        internal void ItemUpdate(ParseItemPacket packet)
        {
            var item = packet.Item;
            Items[item.Id] = packet.Item;
            switch (item.Action)
            {
                case Action.Equip:
                    if (item.PlayerId == Me?.Id && item.Classification == ClassificationType.Belt)
                    {
                        Belt.UpdateBeltRows(item.BeltRows);
                    }
                    break;
                case Action.Unequip:
                    if (item.PlayerId == Me?.Id && item.Classification == ClassificationType.Belt)
                    {
                        Belt.UpdateBeltRows(1);
                    }
                    break;
                case Action.PutInContainer:
                    PutInContainer(item);
                    break;
                case Action.RemoveFromContainer:
                    RemoveFromContainer(item);
                    break;
                case Action.PutInBelt:
                    PutInContainer(item);
                    break;
                case Action.RemoveFromBelt:
                    RemoveFromContainer(item);
                    break;
                case Action.ToCursor:
                    CursorItem = packet.Item;
                    break;
                case Action.UpdateStats:
                    UpdateItemStatsInContainer(item);
                    break;
                default:
                    // Do nothing because we don't know
                    break;
            }
        }

        private void UpdateItemStatsInContainer(Item item)
        {
            var inventoryItem = Inventory.FindItemById(item.Id);
            if (inventoryItem != null)
            {
                Inventory.UpdateItem(inventoryItem, item);
            }

            var cubeItem = Cube.FindItemById(item.Id);
            if (cubeItem != null)
            {
                Cube.UpdateItem(cubeItem, item);
            }

            var stashItem = Stash.FindItemById(item.Id);
            if (stashItem != null)
            {
                Stash.UpdateItem(stashItem, item);
            }

            var beltItem = Belt.FindItemById(item.Id);
            if (beltItem != null)
            {
                Belt.UpdateItem(beltItem, item);
            }
        }

        private void RemoveFromContainer(Item item)
        {
            switch (item.Container)
            {
                case ContainerType.Stash:
                    CursorItem = item;
                    Stash.Remove(item);
                    break;
                case ContainerType.Stash2:
                    CursorItem = item;
                    Stash.Remove(item);
                    break;
                case ContainerType.Belt:
                    CursorItem = item;
                    Belt.Remove(item);
                    break;
                case ContainerType.Cube:
                    CursorItem = item;
                    Cube.Remove(item);
                    break;
                case ContainerType.Inventory:
                    CursorItem = item;
                    Inventory.Remove(item);
                    break;
                default:
                    // Do nothing we don't know how to handle this
                    break;
            }
        }

        private void PutInContainer(Item item)
        {
            if (CursorItem?.Id == item.Id)
            {
                CursorItem = null;
            }

            switch (item.Container)
            {
                case ContainerType.Stash:
                    Stash.Add(item);
                    break;
                case ContainerType.Stash2:
                    Stash.Add(item);
                    break;
                case ContainerType.Belt:
                    Belt.Add(item);
                    break;
                case ContainerType.Cube:
                    Cube.Add(item);
                    break;
                case ContainerType.Inventory:
                    Inventory.Add(item);
                    break;
                default:
                    // Do nothing we don't know how to handle this
                    break;
            }
        }
        internal void RemoveObject(RemoveObjectPacket packet)
        {
            switch (packet.EntityType)
            {
                case EntityType.Doorway:
                    break;
                case EntityType.Player:
                    Act.RemoveWorldObject(packet.EntityId, packet.EntityType);
                    Players.RemoveAll(p => p.Id == packet.EntityId);
                    foreach(var player in Players.Where(p => p.CorpseId == packet.EntityId))
                    {
                        player.CorpseId = null;
                    }
                    break;
                case EntityType.NPC:
                case EntityType.Object:
                    Act.RemoveWorldObject(packet.EntityId, packet.EntityType);
                    break;
                case EntityType.Missiles:
                    break;
                case EntityType.Item:
                    Items.Remove(packet.EntityId, out var value2);
                    break;
            }
        }
    }
}
