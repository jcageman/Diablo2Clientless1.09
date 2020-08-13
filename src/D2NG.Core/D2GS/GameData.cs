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
        }

        public Character ClientCharacter { get; }
        public GameFlags Flags { get; }
        public ActData Act { get; }
        public Self Me { get; private set; }

        public Container Stash { get; } = new Stash();

        public Container Inventory { get; } = new Inventory();

        public Container Cube { get; } = new Cube();

        public Belt Belt { get; } = new Belt();

        public Item CursorItem { get; internal set; }

        public List<Player> Players { get; internal set; } = new List<Player>();

        public ConcurrentDictionary<uint, Item> Items { get; private set; } = new ConcurrentDictionary<uint, Item>();

        internal void AddExperience(AddExpPacket addExpPacket)
            => Me.Experience += addExpPacket.Experience;

        internal void PlayerAssign(AssignPlayerPacket packet)
        {
            if (packet.Name == ClientCharacter.Name && packet.Class == ClientCharacter.Class)
            {
                Me = new Self(packet);
            }

            var existingIndex = Players.FindIndex(p => p.Name == packet.Name);
            if (existingIndex >= 0)
            {
                Players[existingIndex] = new Player(packet);
            }
            else
            {
                Players.Add(new Player(packet));
            }
        }

        internal void PlayerJoin(PlayerInGamePacket packet)
        {
            if (packet.Name == ClientCharacter.Name && packet.Class == ClientCharacter.Class && Me == null)
            {
                Me = new Self(packet);
            }

            var existingIndex = Players.FindIndex(p => p.Name == packet.Name);
            if (existingIndex < 0)
            {
                Players.Add(new Player(packet));
            }
        }

        internal void PlayerLeave(PlayerLeftGamePacket packet)
        {
            Players.RemoveAll(p => p.Id == packet.Id);
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

        internal void ReassignPlayer(ReassignPlayerPacket packet)
        {
            if (packet.UnitId == Me.Id)
            {
                Me.Location = packet.Location;
            }

            foreach (var player in Players)
            {
                if (packet.UnitId == player.Id)
                {
                    player.Location = packet.Location;
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
        }

        private void RemoveFromContainer(Item item)
        {
            CursorItem = item;
            switch (item.Container)
            {
                case ContainerType.Stash:
                    Stash.Remove(item);
                    break;
                case ContainerType.Stash2:
                    Stash.Remove(item);
                    break;
                case ContainerType.Belt:
                    Belt.Remove(item);
                    break;
                case ContainerType.Cube:
                    Cube.Remove(item);
                    break;
                case ContainerType.Inventory:
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
