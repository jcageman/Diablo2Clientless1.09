using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using Serilog;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Cows
{
    internal class CowManager
    {
        private readonly List<Client> _killingClients;
        private readonly IMapApiService mapApiService;
        private readonly ConcurrentDictionary<uint, AliveMonster> _aliveMonsters = new();
        private readonly ConcurrentDictionary<Point, Point> _usedClusters = new();
        private readonly ConcurrentDictionary<Point, Point> _busyClusters = new();
        private readonly ConcurrentDictionary<Point, Point> _cowClusters = new();
        private readonly ConcurrentDictionary<uint, Item> _pickitItemsOnGround = new();
        private readonly ConcurrentDictionary<uint, Item> _pickitPotionsOnGround = new();
        private bool IsActive = false;
        public CowManager(List<Client> killingclients, List<Client> listeningClients, IMapApiService mapApiService)
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

        public async Task<List<Point>> GetPossibleStartingLocations(Game game)
        {
            var result = new List<Point>();
            var areaMap = await mapApiService.GetArea(game.MapId, Difficulty.Normal, D2NG.Core.D2GS.Act.Area.CowLevel);
            var cowKing = areaMap.Npcs[(int)NPCCode.CowKing][0];
            var rows = areaMap.Map.GetLength(0);
            for (var i = 0; i < rows; i += rows / 5)
            {
                var edge1 = GetNearestLocationToEdge(areaMap.Map[i], true);
                if(edge1.HasValue)
                {
                    var option1 = areaMap.MapToPoint(i, edge1.Value);
                    if (option1.Distance(cowKing) > 150)
                    {
                        result.Add(option1);
                    }
                }

                var edge2 = GetNearestLocationToEdge(areaMap.Map[i], false);
                if(edge2.HasValue)
                {
                    var option2 = areaMap.MapToPoint(i, edge2.Value);
                    if (option2.Distance(cowKing) > 150)
                    {
                        result.Add(option2);
                    }
                }
            }

            return result.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        }

        private static int? GetNearestLocationToEdge(int[] locations, bool leftToRight)
        {
            var startX = leftToRight ? 0 : locations.Length - 1;
            int x = startX;
            int count = 0;
            while (true)
            {
                if (AreaMapExtensions.IsMovable(locations[x]))
                {
                    count++;
                    if(count > 5)
                    {
                        return x;
                    }
                }
                else
                {
                    count = 0;
                }

                if(leftToRight)
                {
                    x++;
                    if(x >= locations.Length)
                    {
                        break;
                    }
                }
                else
                {
                    x--;
                    if (x < 0)
                    {
                        break;
                    }
                }
            }

            return null;
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
                if(_aliveMonsters.TryGetValue(entityId, out var cow))
                {
                    cow.LifePercentage = lifePercentage;
                }
                
            }
        }

        private void HandleNPCMove(uint entityId, Point location, double? lifePercentage = null)
        {
            if (_aliveMonsters.TryGetValue(entityId, out var cow))
            {
                cow.Location = location;
                if(lifePercentage.HasValue)
                {
                    cow.LifePercentage = lifePercentage.Value;
                }
            }
        }

        private void HandleAssignNPC(AssignNpcPacket packet)
        {
            if (packet.UniqueCode == NPCCode.HellBovine)
            {
                _aliveMonsters.AddOrUpdate(packet.EntityId, (newCowId => new AliveMonster
                {
                    Id = packet.EntityId,
                    Location = packet.Location,
                    NPCCode = packet.UniqueCode,
                    MonsterEnchantments = packet.MonsterEnchantments
                }), (existingCowId, existingCow) => existingCow);

                var usedCluster = _usedClusters.Values.FirstOrDefault(cluster => packet.Location.Distance(cluster) < 30);
                if(usedCluster == null && _usedClusters.TryAdd(packet.Location, packet.Location))
                {
                    Log.Information($"Adding new cluster at {packet.Location}");
                    _cowClusters.TryAdd(packet.Location, packet.Location);

                    IsActive = true;
                }
            }
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
                if (_aliveMonsters.TryGetValue(packet.EntityId, out var cow))
                {
                    cow.LifePercentage = packet.LifePercentage;
                }
            }
        }
     
        public List<AliveMonster> GetNearbyAliveMonsters(Client client, double distance, int numberOfCows)
        {
            return GetNearbyAliveMonsters(client.Game.Me.Location, distance, numberOfCows);
        }

        public List<AliveMonster> GetNearbyAliveMonsters(Point location, double distance, int numberOfMonsters)
        {
            return _aliveMonsters.Values.Where(c => c.Location.Distance(location) < distance).OrderBy(c => c.Location.Distance(location)).Take(numberOfMonsters).ToList();
        }

        public void GiveUpCluster(Point cluster)
        {
            _cowClusters.TryAdd(cluster, cluster);
        }

        public Point GetNextCluster(Client client, Point previousCluster)
        {
            if(previousCluster != null)
            {
                _busyClusters.Remove(previousCluster, out var _);
            }

            var orderedByDistance = _cowClusters.Keys.OrderBy(p => client.Game.Me.Location.Distance(p));
            foreach(var cluster in orderedByDistance)
            {
                if(_cowClusters.TryRemove(cluster, out var removedCluster))
                {
                    _busyClusters.TryAdd(removedCluster, removedCluster);
                    return removedCluster;
                }
            }

            return _busyClusters.Values.FirstOrDefault();
        }

        public bool IsFinished()
        {
            if(_killingClients.All(c => !c.Game.IsInGame()))
            {
                return true;
            }

            return IsActive && !_busyClusters.Values.Any() && _cowClusters.IsEmpty;
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
                if(_pickitItemsOnGround.TryRemove(tryItem.Id, out var item))
                {
                    resultPickitList.Add(item);
                    if(resultPickitList.Count == 2)
                    {
                        break;
                    }
                }
            }

            return resultPickitList;
        }
    }
}
