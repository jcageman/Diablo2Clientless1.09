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
                var speedItems = this.Items.Where(i => i.Value.Action == Action.Equip && i.Value.EntityType == EntityType.Player && i.Value.PlayerId == Me?.Id).Select(i => i.Value.GetValueOfStatType(StatType.FasterRunWalk));
                if (speedItems.Any())
                {
                    walkingSpeedIncreasedMultiplier += speedItems.Aggregate((agg, frw) => agg + frw) / (double)100;
                }

                var increasedSpeedSkill = Me.Skills.GetValueOrDefault(Skill.IncreasedSpeed, 0);
                if (increasedSpeedSkill > 0)
                {
                    walkingSpeedIncreasedMultiplier += (13 + 4 * increasedSpeedSkill) / (double)100;
                }

                var vigorSkill = Me.Skills.GetValueOrDefault(Skill.Vigor, 0);
                if (vigorSkill > 0)
                {
                    walkingSpeedIncreasedMultiplier += (13 + 2.5 * vigorSkill) / 100;
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

        internal double WalkingSpeedMultiplier
        {

            get { return _walkingSpeedMultiplier.Value; }
        }

        internal void AddExperience(AddExpPacket addExpPacket)
            => Me.Experience += addExpPacket.Experience;

        internal void PlayerAssign(AssignPlayerPacket packet)
        {
            if (packet.Name == ClientCharacter.Name && packet.Class == ClientCharacter.Class)
            {
                if (Me == null)
                {
                    Me = new Self(packet);
                }

                Me.Location = packet.Location;
                Me.Area = Act.Area;
                Me.Act = Act.Act;
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

        internal void AssignMerc(AssignMercPacket packet)
        {
            var player = Players.Where(p => p.Id == packet.PlayerEntityId).FirstOrDefault();
            if (player != null)
            {
                player.MercId = packet.MercEntityId;
            }

            if (packet.PlayerEntityId == Me.Id)
            {
                Me.MercId = packet.MercEntityId;
            }
        }

        internal void PlayerLeave(PlayerLeftGamePacket packet)
        {
            Players.RemoveAll(p => p.Id == packet.Id);
        }

        internal void AddEntityEffect(AddEntityEffectPacket packet)
        {
            if (packet.EntityType == EntityType.Player)
            {
                var player = Players.Where(p => p.Id == packet.EntityId).FirstOrDefault();
                if (player != null)
                {
                    player.AddEffect(packet);
                }

                if (packet.EntityId == Me.Id)
                {
                    Me.AddEffect(packet);
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
                    player.Effects.TryAdd(packet.Effect, packet.Effect);
                }

                if (packet.EntityId == Me.Id)
                {
                    Me.Effects.TryAdd(packet.Effect, packet.Effect);
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
                    player.UpdateEffects(packet.EntityEffects);
                }

                if (packet.EntityId == Me.Id)
                {
                    Me.UpdateEffects(packet.EntityEffects);
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

            if (Me?.Id == packet.PlayerId)
            {
                Me.CorpseId = packet.CorpseId;
            }

            var corpse = Players.Where(p => p.Id == packet.CorpseId).FirstOrDefault();
            if (corpse != null && !packet.CorpseAdded)
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
            if (packet.EntityType != EntityType.Player)
            {
                return;
            }

            if (packet.EntityId == Me.Id)
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

        internal void ReassignPlayer(EntityType entityType, uint unitId, Point location)
        {
            if (entityType == EntityType.Player)
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
        }

        internal void UpdatePlayerPartyInfo(AllyPartyInfoPacket packet)
        {
            if (packet.EntityType == EntityType.Player)
            {
                if (packet.EntityId == Me.Id)
                {
                    Me.Area = packet.Area;
                    Me.Act = packet.Area.MapToAct();
                    Me.LifePercentage = packet.LifePercentage;
                }

                foreach (var player in Players)
                {
                    if (packet.EntityId == player.Id)
                    {
                        player.Area = packet.Area;
                        player.Act = packet.Area.MapToAct();
                        player.LifePercentage = packet.LifePercentage;
                        break;
                    }
                }
            }
        }

        internal void UpdateSummonInfo(PetActionPacket packet)
        {
            if (Me.Id == packet.PlayerId)
            {
                if (packet.AddingSummon)
                {
                    Me.Summons.Add(new Summon { Id = packet.SummonId, NPCCode = packet.UniqueCode });
                }
                else
                {
                    Me.Summons.RemoveAll(s => s.Id == packet.SummonId);
                }
            }

            foreach (var player in Players)
            {
                if (packet.PlayerId == player.Id)
                {
                    if (packet.AddingSummon)
                    {
                        player.Summons.Add(new Summon { Id = packet.SummonId, NPCCode = packet.UniqueCode });
                    }
                    else
                    {
                        player.Summons.RemoveAll(s => s.Id == packet.SummonId);
                    }
                    break;
                }
            }
        }

        internal void SetAttribute(BaseAttributePacket baseAttribute)
            => Me.UpdateAttribute(baseAttribute.Attribute, baseAttribute.Value);

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
            Me.Mana = packet.Mana;
            Me.Stamina = packet.Stamina;
        }

        internal void UpdateSelf(LifeManaUpdatePacket packet)
        {
            Me.Location = packet.Location;
            Me.Life = packet.Life;
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

                    if (item.EntityType == EntityType.Player)
                    {
                        if(item.PlayerId == Me?.Id)
                        {
                            if (item.Classification == ClassificationType.Belt)
                            {
                                Belt.UpdateBeltRows(item.BeltRows);
                            }
                            Me.EquipItem(item);
                        }

                    }
                    break;
                case Action.Unequip:
                    if (item.EntityType == EntityType.Player)
                    {
                        if (item.PlayerId == Me?.Id)
                        {
                            if (item.Classification == ClassificationType.Belt)
                            {
                                Belt.UpdateBeltRows(1);
                            }
                            Me.UnequipItem(item);
                        }
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
                    Belt.Remove(item);
                    break;
                case Action.ToCursor:
                    CursorItem = item;
                    break;
                case Action.UpdateStats:
                    UpdateItemStatsInContainer(item);
                    break;
                case Action.DropToGround:
                    if (CursorItem?.Id == item.Id)
                    {
                        CursorItem = null;
                    }
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
                    if (Stash.Remove(item))
                    {
                        CursorItem = item;
                    }
                    break;
                case ContainerType.Stash2:
                    if (Stash.Remove(item))
                    {
                        CursorItem = item;
                    }
                    break;
                case ContainerType.Belt:
                    if (Belt.Remove(item))
                    {
                        CursorItem = item;
                    }
                    break;
                case ContainerType.Cube:
                    if (Cube.Remove(item))
                    {
                        CursorItem = item;
                    }
                    break;
                case ContainerType.Inventory:
                    if(Inventory.Remove(item))
                    {
                        CursorItem = item;
                    }
                    
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
                    if (Belt.Width <= item.Location.X)
                    {
                        Belt.UpdateBeltRows((uint)item.Location.X + 1);
                    }
                    if (Belt.Height <= item.Location.Y)
                    {
                        Belt.UpdateBeltRows((uint)item.Location.Y + 1);
                    }
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

        internal void UseStackableItem(UseStackableItemPacket packet)
        {
            var item = Inventory.FindItemById(packet.ItemId);
            if(item != null &&
                ( item.Classification == ClassificationType.HealthPotion
                || item.Classification == ClassificationType.ManaPotion
                || item.Classification == ClassificationType.RejuvenationPotion))
            {
                Inventory.Remove(item);
            }
        }

        internal void UpdateItemStats(UpdateItemStatsPacket packet)
        {
            var item = Me.Equipment.Values.FirstOrDefault(i => i.Id == packet.ItemId);
            if(item != null)
            {
                if (packet.UpdateType == 70
                   && (item.Classification == ClassificationType.Javelin || item.Classification == ClassificationType.AmazonJavelin))
                {
                    item.Amount = packet.Amount;
                }
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
                    foreach (var player in Players.Where(p => p.CorpseId == packet.EntityId))
                    {
                        Players.RemoveAll(p => p.Id == packet.EntityId);
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
