using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Navigation.Services.MapApi;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Baal
{
    internal class BaalManager
    {
        private readonly List<Client> _killingClients;
        private readonly IMapApiService mapApiService;
        private readonly ConcurrentDictionary<uint, AliveMonster> _aliveMonsters = new ConcurrentDictionary<uint, AliveMonster>();
        private readonly ConcurrentDictionary<uint, Item> _pickitItemsOnGround = new ConcurrentDictionary<uint, Item>();
        private readonly ConcurrentDictionary<uint, Item> _pickitPotionsOnGround = new ConcurrentDictionary<uint, Item>();
        private Point LeftTopThroneRoom;
        private Point RightBottomThroneRoom;
        public BaalManager(List<Client> killingclients, List<Client> listeningClients, IMapApiService mapApiService)
        {
            _killingClients = killingclients;
            this.mapApiService = mapApiService;
            var allClients = new List<Client>();
            allClients.AddRange(killingclients);
            allClients.AddRange(listeningClients);
            foreach (var client in allClients)
            {
                client.OnReceivedPacketEvent(InComingPacket.AssignNPC2, p => HandleAssignNPC(new AssignNpcPacket(p)));
                client.OnReceivedPacketEvent(InComingPacket.AssignNPC1, p => HandleAssignNPC(new AssignNpcPacket(p)));
                client.OnReceivedPacketEvent(InComingPacket.NPCState, p => HandleNPCStateChange(new NpcStatePacket(p)));
                client.OnReceivedPacketEvent(InComingPacket.NPCMove, p => { var packet = new NPCMovePacket(p); HandleNPCMove(packet.EntityId, packet.Location); });
                client.OnReceivedPacketEvent(InComingPacket.NPCStop, p => { var packet = new NPCStopPacket(p); HandleNPCMove(packet.EntityId, packet.Location, packet.LifePercentage); });
                client.OnReceivedPacketEvent(InComingPacket.NPCMoveToTarget, p => { var packet = new NPCMoveToTargetPacket(p); HandleNPCMove(packet.EntityId, packet.Location); });
                client.OnReceivedPacketEvent(InComingPacket.NPCHit, p => { var packet = new NpcHitPacket(p); UpdateNPCLife(packet.EntityId, packet.LifePercentage); });
                client.Game.OnWorldItemEvent(i => HandleItemDrop(client.Game, i));
            }
        }

        public async Task Initialize()
        {
            var mapId = _killingClients.First().Game.MapId;
            var areaMap = await mapApiService.GetArea(mapId, Difficulty.Normal, D2NG.Core.D2GS.Act.Area.ThroneOfDestruction);
            var baalPortal = areaMap.Objects[(int)EntityCode.BaalPortal][0];
            LeftTopThroneRoom = baalPortal.Add(-35, -20);
            RightBottomThroneRoom = baalPortal.Add(35, 90);
        }

        private Task HandleItemDrop(Game game, Item item)
        {
            if(!item.Ground)
            {
                return Task.CompletedTask;
            }

            if (Pickit.Pickit.ShouldPickupItem(game, item, false))
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
            else if(item.Name == ItemName.SuperHealingPotion
                || item.Name == ItemName.SuperManaPotion
                || item.Name == ItemName.RejuvenationPotion
                || item.Name == ItemName.FullRejuvenationPotion)
            {
                _pickitPotionsOnGround.TryAdd(item.Id, item);
            }
            
            return Task.CompletedTask;
        }

        private void UpdateNPCLife(uint entityId, double lifePercentage)
        {
            if(lifePercentage == 0)
            {
                if(_aliveMonsters.TryRemove(entityId, out var _))
                {
                }
            }
            else
            {
                if(_aliveMonsters.TryGetValue(entityId, out var monster))
                {
                    monster.LifePercentage = lifePercentage;
                }
                
            }
        }

        private void HandleNPCMove(uint entityId, Point location, double? lifePercentage = null)
        {
            if (_aliveMonsters.TryGetValue(entityId, out var monster))
            {
                monster.Location = location;
                if(lifePercentage.HasValue)
                {
                    monster.LifePercentage = lifePercentage.Value;
                }
            }
        }

        private void HandleAssignNPC(AssignNpcPacket packet)
        {
            if(packet.UniqueCode == NPCCode.Guard
                || packet.UniqueCode == NPCCode.BaalThrone
                || packet.UniqueCode == NPCCode.BaalTentacle1
                || packet.UniqueCode == NPCCode.BaalTentacle2
                || packet.UniqueCode == NPCCode.BaalTentacle3
                || packet.UniqueCode == NPCCode.BaalTentacle4
                || packet.UniqueCode == NPCCode.BaalTentacle5)
            {
                return;
            }

            if(packet.UniqueCode != NPCCode.Baal)
            {
                if (packet.Location.X < LeftTopThroneRoom.X || packet.Location.X > RightBottomThroneRoom.X)
                {
                    return;
                }

                if (packet.Location.Y < LeftTopThroneRoom.Y || packet.Location.Y > RightBottomThroneRoom.Y)
                {
                    return;
                }
            }

            _aliveMonsters.AddOrUpdate(packet.EntityId, (newMonsterId => new AliveMonster
            {
                Id = packet.EntityId,
                Location = packet.Location,
                NPCCode = packet.UniqueCode,
                MonsterEnchantments = packet.MonsterEnchantments
            }), (existingMonsterId, existingMonster) => existingMonster);
        }

        private void HandleNPCStateChange(NpcStatePacket packet)
        {
            if(packet.EntityState == EntityState.Dead || packet.EntityState == EntityState.Dieing)
            {
                if (_aliveMonsters.TryRemove(packet.EntityId, out var _))
                {
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
     
        public List<AliveMonster> GetNearbyAliveMonsters(Client client, double distance, int numberOfMonsters)
        {
            return GetNearbyAliveMonsters(client.Game.Me.Location, distance, numberOfMonsters);
        }

        public List<AliveMonster> GetNearbyAliveMonsters(Point location, double distance, int numberOfMonsters)
        {
            return _aliveMonsters.Values.Where(c => c.Location.Distance(location) < distance).OrderBy(c => c.Location.Distance(location)).Take(numberOfMonsters).ToList();
        }

        public void PutItemOnPickitList(Client client, Item item)
        {
            if (Pickit.Pickit.ShouldPickupItem(client.Game, item, false)
                && client.Game.Items.TryGetValue(item.Id, out var newItem)
                && newItem.Ground)
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
        }

        public void PutPotionOnPickitList(Client client, Item item)
        {
            if (item.IsPotion && item.Ground)
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
        }

        public List<Item> GetNearbyPotions(Client client, HashSet<ItemName> items, int nofPotions, int distance)
        {
            var resultPickitList = new List<Item>();
            var listItems = _pickitPotionsOnGround.Values.Where(i => client.Game.Me.Location.Distance(i.Location) < distance).ToList();
            foreach (var tryItem in listItems)
            {
                if (!items.Contains(tryItem.Name))
                {
                    continue;
                }

                if (resultPickitList.Count >= nofPotions)
                {
                    break;
                }

                if (_pickitPotionsOnGround.TryRemove(tryItem.Id, out var item))
                {
                    resultPickitList.Add(item);
                }
            }

            return resultPickitList;
        }

        public List<Item> GetPickitList(Client client, double distance)
        {
            var resultPickitList = new List<Item>();
            var listItems = _pickitItemsOnGround.Values.Where(i => client.Game.Me.Location.Distance(i.Location) < distance).ToList();
            foreach(var tryItem in listItems)
            {
                if(resultPickitList.Count > 2)
                {
                    break;
                }

                if(_pickitItemsOnGround.TryRemove(tryItem.Id, out var item))
                {
                    resultPickitList.Add(item);
                }
            }

            return resultPickitList;
        }
    }
}
