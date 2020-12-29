using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.CS
{
    public class CSManager
    {
        private readonly ConcurrentDictionary<uint, AliveMonster> _aliveMonsters = new ConcurrentDictionary<uint, AliveMonster>();
        private readonly ConcurrentDictionary<uint, DeadMonster> _monstersAvailableForCorpseExplosion = new ConcurrentDictionary<uint, DeadMonster>();
        private readonly ConcurrentDictionary<uint, Item> _pickitItemsOnGround = new ConcurrentDictionary<uint, Item>();
        private readonly ConcurrentDictionary<uint, Item> _pickitRevsOnGround = new ConcurrentDictionary<uint, Item>();
        public CSManager(List<Client> clients)
        {
            foreach (var client in clients)
            {
                client.OnReceivedPacketEvent(InComingPacket.AssignNPC2, p => HandleAssignNPC(new AssignNpcPacket(p)));
                client.OnReceivedPacketEvent(InComingPacket.AssignNPC1, p => HandleAssignNPC(new AssignNpcPacket(p)));
                client.OnReceivedPacketEvent(InComingPacket.NPCState, p => HandleNPCStateChange(new NpcStatePacket(p)));
                client.OnReceivedPacketEvent(InComingPacket.NPCMove, p => { var packet = new NPCMovePacket(p); HandleNPCMove(packet.EntityId, packet.Location); });
                client.OnReceivedPacketEvent(InComingPacket.NPCStop, p => { var packet = new NPCStopPacket(p); HandleNPCMove(packet.EntityId, packet.Location); });
                client.OnReceivedPacketEvent(InComingPacket.NPCMoveToTarget, p => { var packet = new NPCMoveToTargetPacket(p); HandleNPCMove(packet.EntityId, packet.Location); });
                client.OnReceivedPacketEvent(InComingPacket.NPCHit, p => { var packet = new NpcHitPacket(p); UpdateNPCLife(packet.EntityId, packet.LifePercentage); });
                client.Game.OnWorldItemEvent(i => HandleItemDrop(client.Game, i));
            }
        }

        private Task HandleItemDrop(Game game, Item item)
        {
            if (!item.Ground)
            {
                return Task.CompletedTask;
            }

            if (Pickit.Pickit.ShouldPickupItem(game, item))
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }

            if(item.Name == ItemName.RejuvenationPotion || item.Name == ItemName.FullRejuvenationPotion)
            {
                _pickitRevsOnGround.TryAdd(item.Id, item);
            }

            return Task.CompletedTask;
        }

        private void UpdateNPCLife(uint entityId, byte lifePercentage)
        {
            if (_aliveMonsters.TryGetValue(entityId, out var monster))
            {
                monster.LifePercentage = lifePercentage;
            }
        }

        private void HandleNPCMove(uint entityId, Point location)
        {
            if (_aliveMonsters.TryGetValue(entityId, out var monster))
            {
                monster.Location = location;
            }
        }

        private void HandleAssignNPC(AssignNpcPacket packet)
        {
            if (packet.UniqueCode == NPCCode.StormCaster
                || packet.UniqueCode == NPCCode.OblivionKnight
                || packet.UniqueCode == NPCCode.DoomKnight
                || packet.UniqueCode == NPCCode.VenomLord
                || packet.UniqueCode == NPCCode.Diablo)
            {
                _aliveMonsters.AddOrUpdate(packet.EntityId, (newMonsterId => new AliveMonster
                {
                    Id = packet.EntityId,
                    Location = packet.Location,
                    NPCCode = packet.UniqueCode,
                    MonsterEnchantments = packet.MonsterEnchantments
                }), (existingId, existingMonster) => existingMonster);
            }
        }

        private void HandleNPCStateChange(NpcStatePacket packet)
        {
            if (packet.EntityState == EntityState.Dead || packet.EntityState == EntityState.Dieing)
            {
                if (_aliveMonsters.TryRemove(packet.EntityId, out var _))
                {
                    _monstersAvailableForCorpseExplosion.TryAdd(packet.EntityId, new DeadMonster
                    {
                        Id = packet.EntityId,
                        Location = packet.Location
                    });
                }
            }
            else
            {
                if (_aliveMonsters.TryGetValue(packet.EntityId, out var monster))
                {
                    monster.LifePercentage = packet.LifePercentage;
                }
            }
        }

        public void ResetAliveMonsters()
        {
            _aliveMonsters.Clear();
        }

        public List<AliveMonster> GetNearbyAliveMonsters(Client client, double distance)
        {
            return GetNearbyAliveMonsters(client.Game.Me.Location, distance);
        }

        public List<AliveMonster> GetNearbyAliveMonsters(Point location, double distance)
        {
            return _aliveMonsters.Values.Where(c => c.Location.Distance(location) < distance).OrderBy(c => c.Location.Distance(location)).ToList();
        }

        public void PutItemOnPickitList(Client client, Item item)
        {
            if (Pickit.Pickit.ShouldPickupItem(client.Game, item) && (client.Game.Items.FirstOrDefault(i => i.Id == item.Id)?.Ground ?? false))
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
        }
        public void PutRejuvenationOnPickitList(Client client, Item item)
        {
            if (item.IsPotion && item.Ground)
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
        }

        public List<Item> GetPickitList(Client client, double distance)
        {
            var resultPickitList = new List<Item>();
            var listItems = _pickitItemsOnGround.Values.Where(i => client.Game.Me.Location.Distance(i.Location) < distance).ToList();
            foreach (var tryItem in listItems)
            {
                if (_pickitItemsOnGround.TryRemove(tryItem.Id, out var item))
                {
                    resultPickitList.Add(item);
                }
            }

            return resultPickitList;
        }

        public List<Item> GetRejuvenationPotionPickupList(Client client, double distance, int nofPotions)
        {
            var resultPickitList = new List<Item>();
            var listItems = _pickitRevsOnGround.Values.Where(i => client.Game.Me.Location.Distance(i.Location) < distance).ToList();
            foreach (var tryItem in listItems)
            {
                if (resultPickitList.Count >= nofPotions)
                {
                    break;
                }

                if (_pickitRevsOnGround.TryRemove(tryItem.Id, out var item))
                {
                    resultPickitList.Add(item);
                }
            }

            return resultPickitList;
        }
    }
}
