using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Cows
{
    internal class CowManager
    {
        private readonly IPathingService _pathingService;
        private readonly List<Client> _killingClients;
        private readonly ConcurrentDictionary<uint, AliveMonster> _aliveMonsters = new ConcurrentDictionary<uint, AliveMonster>();
        private readonly ConcurrentDictionary<uint, DeadMonster> _monstersAvailableForCorpseExplosion = new ConcurrentDictionary<uint, DeadMonster>();
        private readonly ConcurrentDictionary<Point, Point> _usedClusters = new ConcurrentDictionary<Point, Point>();
        private readonly ConcurrentDictionary<Point, Point> _busyClusters = new ConcurrentDictionary<Point, Point>();
        private readonly ConcurrentDictionary<Point, Point> _cowClusters = new ConcurrentDictionary<Point, Point>();
        private readonly ConcurrentDictionary<uint, Item> _pickitItemsOnGround = new ConcurrentDictionary<uint, Item>();
        private readonly ConcurrentDictionary<uint, Item> _pickitPotionsOnGround = new ConcurrentDictionary<uint, Item>();
        private bool IsActive = false;
        public CowManager(IPathingService pathingService, List<Client> killingclients, List<Client> listeningClients)
        {
            _pathingService = pathingService;
            _killingClients = killingclients;

            var allClients = new List<Client>();
            allClients.AddRange(killingclients);
            allClients.AddRange(listeningClients);
            foreach (var client in allClients)
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


        internal class Line
        {
            public Point StartPoint { get; set; }

            public Point EndPoint { get; set; }
        }

        public static double DotProduct((double X, double Y)a, (double X, double Y) b)
        {
            return (a.X * b.X) + (a.Y * b.Y);
        }

        public static double MinimumDistanceToLineSegment(Point p,
            Line line)
        {
            var v = line.StartPoint;
            var w = line.EndPoint;

            double lengthSquared = v.DistanceSquared(w);

            if (lengthSquared == 0.0)
                return p.Distance(v);

            double t = Math.Max(0, Math.Min(1, DotProduct(p.Substract((v.X, v.Y)), w.Substract((v.X, v.Y))) / lengthSquared));

            short dX = (short)(((double)w.X - v.X)*t);
            short dY = (short)(((double)w.Y - v.Y)*t);
            var projection = v;
            projection = projection.Add(dX, dY);

            return p.Distance(projection);
        }

        public async Task<bool> IsInLineOfSight(Client client,  Point toLocation)
        {
            return await IsInLineOfSight(client, client.Game.Me.Location, toLocation);
        }

        public async Task<bool> IsInLineOfSight(Client client, Point fromLocation, Point toLocation)
        {
            var directDistance = fromLocation.Distance(toLocation);
            if(directDistance == 0)
            {
                return true;
            }

            var path = await _pathingService.GetPathToLocation(client.Game, toLocation, MovementMode.Walking);
            if(path.Count == 0)
            {
                return true;
            }

            var line = new Line
            {
                StartPoint = fromLocation,
                EndPoint = toLocation
            };

            var pointsOutside = false;
            if (path.Count() > 1)
            {
                pointsOutside = ((double)path.Count(p => MinimumDistanceToLineSegment(p, line) >= 4.1)) / path.Count > 0.15;
            }
            return !pointsOutside;
        }

        public async Task<bool> IsVisitable(Client client, Point point)
        {
            var path = await _pathingService.GetPathToLocation(client.Game, point, MovementMode.Walking);
            return path.Count != 0;
        }

        private Task HandleItemDrop(Game game, Item item)
        {
            if(!item.Ground)
            {
                return Task.CompletedTask;
            }

            if (Pickit.Pickit.ShouldPickupItem(game, item))
            {
                _pickitItemsOnGround.TryAdd(item.Id, item);
            }
            else if(item.Name == ItemName.SuperHealingPotion || item.Name == ItemName.SuperManaPotion)
            {
                _pickitPotionsOnGround.TryAdd(item.Id, item);
            }
            
            return Task.CompletedTask;
        }

        private void UpdateNPCLife(uint entityId, byte lifePercentage)
        {
            if(lifePercentage == 0)
            {
                if(_aliveMonsters.TryRemove(entityId, out var aliveCow))
                {
                    _monstersAvailableForCorpseExplosion.TryAdd(entityId, new DeadMonster
                    {
                        Id = entityId,
                        Location = aliveCow.Location
                    });
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

        private void HandleNPCMove(uint entityId, Point location)
        {
            if (_aliveMonsters.TryGetValue(entityId, out var cow))
            {
                cow.Location = location;
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
                    _monstersAvailableForCorpseExplosion.TryAdd(packet.EntityId, new DeadMonster
                    {
                        Id = packet.EntityId,
                        Location = packet.Location
                    });
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

        public bool CastCorpseExplosion(Client client)
        {
            if(client.Game.Me.Class != CharacterClass.Necromancer || !client.Game.Me.HasSkill(Skill.CorpseExplosion))
            {
                throw new InvalidOperationException();
            }

            var nearbyAliveMonsters = _aliveMonsters.Values.Where(c => c.Location.Distance(client.Game.Me.Location) < 20).OrderBy(c => c.Location.Distance(client.Game.Me.Location)).Take(20);
            foreach(var nearbyMonster in nearbyAliveMonsters)
            {
                var firstMatch = _monstersAvailableForCorpseExplosion.Values.FirstOrDefault(c => c.Location.Distance(nearbyMonster.Location) < 10);
                if(firstMatch != null)
                {
                    if(client.Game.WorldObjects.TryGetValue((firstMatch.Id, EntityType.NPC), out var cow))
                    {
                        bool result = client.Game.UseRightHandSkillOnEntity(Skill.CorpseExplosion, cow);
                        _monstersAvailableForCorpseExplosion.TryRemove(cow.Id, out var _);
                        return result;
                    }
                }
            }

            return false;
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
            if (Pickit.Pickit.ShouldPickupItem(client.Game, item) && (client.Game.Items.FirstOrDefault(i => i.Id == item.Id)?.Ground ?? false))
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

        public List<Item> GetNearbyPotions(Client client, bool healthPotions, int nofPotions, int distance)
        {
            var resultPickitList = new List<Item>();
            var listItems = _pickitPotionsOnGround.Values.Where(i => client.Game.Me.Location.Distance(i.Location) < distance).ToList();
            foreach (var tryItem in listItems)
            {
                var isHealingPotion = tryItem.Name == ItemName.SuperHealingPotion;
                if (isHealingPotion != healthPotions)
                {
                    continue;
                }

                if (resultPickitList.Count == nofPotions)
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
