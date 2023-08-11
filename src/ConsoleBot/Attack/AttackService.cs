using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Attack
{
    public class AttackService : IAttackService
    {
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;

        public AttackService(IPathingService pathingService, IMapApiService mapApiService)
        {
            _pathingService = pathingService;
            _mapApiService = mapApiService;
        }

        internal class Line
        {
            public Point StartPoint { get; set; }

            public Point EndPoint { get; set; }
        }

        public async Task<bool> IsInLineOfSight(Client client, Point toLocation)
        {
            return await IsInLineOfSight(client, client.Game.Me.Location, toLocation);
        }

        public async Task<bool> IsInLineOfSight(Client client, Point fromLocation, Point toLocation)
        {
            var directDistance = fromLocation.Distance(toLocation);
            if (directDistance == 0)
            {
                return true;
            }

            var clientArea = client.Game.Area;
            if (clientArea == Area.None)
            {
                return false;
            }

            var areaMap = await _mapApiService.GetArea(client.Game.MapId, Difficulty.Normal, clientArea);
            var pointsOnLine = GetPointsOnLine(fromLocation.X, fromLocation.Y, toLocation.X, toLocation.Y);
            foreach (var point in pointsOnLine)
            {
                var mapValue = areaMap.Map[point.Y - areaMap.LevelOrigin.Y][point.X - areaMap.LevelOrigin.X];
                if (!AreaMapExtensions.IsMovable(mapValue) && mapValue != 1)
                {
                    return false;
                }
            }

            return true;
        }

        public static IEnumerable<Point> GetPointsOnLine(ushort x0, ushort y0, ushort x1, ushort y1)
        {
            bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
            if (steep)
            {
                ushort t;
                t = x0; // swap x0 and y0
                x0 = y0;
                y0 = t;
                t = x1; // swap x1 and y1
                x1 = y1;
                y1 = t;
            }
            if (x0 > x1)
            {
                ushort t;
                t = x0; // swap x0 and x1
                x0 = x1;
                x1 = t;
                t = y0; // swap y0 and y1
                y0 = y1;
                y1 = t;
            }
            ushort dx = (ushort)(x1 - x0);
            ushort dy = (ushort)(Math.Abs(y1 - y0));
            ushort error = (ushort)(dx / 2);
            ushort ystep = (ushort)((y0 < y1) ? 1 : -1);
            ushort y = y0;
            for (ushort x = x0; x <= x1; x++)
            {
                yield return new Point((steep ? y : x), (steep ? x : y));
                error = (ushort)(error - dy);
                if (error < 0)
                {
                    y += ystep;
                    error += dx;
                }
            }
            yield break;
        }

        public async Task<bool> IsVisitable(Client client, Point point)
        {
            var path = await _pathingService.GetPathToLocation(client.Game, point, MovementMode.Walking);
            return path.Count != 0;
        }

        private List<Point> GetNearbyMonsters(List<Point> enemies, Point location, double distance)
        {
            return enemies.Where(p => p.Distance(location) < distance).ToList();

        }

        private async Task<Point> FindNearbySafeSpot(Client client, List<Point> enemies, Point toLocation, double minDistance = 0, double maxdistance = 30)
        {
            Point bestSpot = null;
            int spotMonsters = int.MaxValue;
            for (int i = 1; i < 5; ++i)
            {
                foreach (var (p1, p2) in new List<(short, short)> {
                    (-5,0), (5, 0), (0, -5), (0, 5), (-5, 5), (-5, -5), (5, -5), (5, 5)})
                {
                    var x = (short)(p1 * i);
                    var y = (short)(p2 * i);

                    var distance = Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2));
                    if (distance < minDistance || distance > maxdistance)
                    {
                        continue;
                    }

                    var tryLocation = toLocation.Add(x, y);
                    if (await IsVisitable(client, tryLocation) && await IsInLineOfSight(client, tryLocation, toLocation))
                    {
                        var monsters = GetNearbyMonsters(enemies, tryLocation, 5.0).Count;
                        if(monsters < spotMonsters)
                        {
                            spotMonsters = monsters;
                            bestSpot = tryLocation;
                        }
                    }
                }
            }

            return bestSpot;
        }

        public async Task<bool> MoveToNearbySafeSpot(Client client, List<Point> enemies, Point toLocation, MovementMode movementMode, double minDistance = 0, double maxDistance = 30)
        {
            var spot = await FindNearbySafeSpot(client, enemies, toLocation, minDistance, maxDistance);
            if (spot != null)
            {
                if (movementMode == MovementMode.Teleport)
                {
                    if (await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        return await client.Game.TeleportToLocationAsync(spot);
                    }, TimeSpan.FromSeconds(4)))
                    {
                        return true;
                    }
                }
                else
                {
                    var path = await _pathingService.GetPathToLocation(client.Game, spot, MovementMode.Walking);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, path, MovementMode.Walking))
                    {
                        Log.Warning($"Walking to safe spot failed at {client.Game.Me.Location}");
                        return false;
                    }

                    return true;
                }
            }
            Log.Warning($"No safe spot found for {client.Game.Me.Name} on location {client.Game.Me.Location}");
            return false;
        }

        public async Task<bool> AssistPlayer(Client client, Player player)
        {
            if (client.Game.IsInTown())
            {
                return true;
            }

            switch (client.Game.Me.Class)
            {
                case CharacterClass.Amazon:
                    return await AmazonAssist(client, player);
                case CharacterClass.Sorceress:
                    return await SorceressAssist(client, player);
                case CharacterClass.Necromancer:
                    return await NecromancerAssist(client, player);
                case CharacterClass.Paladin:
                    return await PaladinAssist(client, player);
                case CharacterClass.Barbarian:
                    return await BarbarianAssist(client, player);
                case CharacterClass.Druid:
                    break;
                case CharacterClass.Assassin:
                    break;
            }

            return true;
        }

        private async Task<bool> AmazonAssist(Client client, Player player)
        {
            var me = client.Game.Me;
            var enemies = NPCHelpers.GetNearbyNPCs(client, player.Location, 20, 40).ToList();

            if (me.Attributes[Attribute.Level] < 30 && client.Game.Difficulty > Difficulty.Normal)
            {
                return true;
            }
            else if (me.Attributes[Attribute.Level] < 26 && client.Game.Difficulty == Difficulty.Normal && client.Game.Area == Area.CowLevel)
            {
                return true;
            }

            var nearest = await GetNearestInSight(client, enemies);
            if (nearest == null)
            {
                return true;
            }

            if (me.HasSkill(Skill.MultipleShot) && me.Mana > 20 && enemies.Count > 5)
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.MultipleShot}");
                client.Game.RepeatRightHandSkillOnEntity(Skill.MultipleShot, nearest);
                await Task.Delay(200);
            }
            else if (me.HasSkill(Skill.LightningFury) && me.Mana > 20 && enemies.Count > 5)
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.LightningFury}");
                client.Game.UseRightHandSkillOnEntity(Skill.LightningFury, nearest);
                await Task.Delay(200);
            }
            else if (me.HasSkill(Skill.GuidedArrow) && me.Mana > 20 && enemies.Count < 5)
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.GuidedArrow}");
                client.Game.RepeatRightHandSkillOnEntity(Skill.GuidedArrow, nearest);
                await Task.Delay(200);
            }
            else if (client.Game.Me.Equipment.TryGetValue(DirectoryType.RightHand, out var weapon)
                && weapon.Classification == ClassificationType.Bow)
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.Attack}");
                client.Game.RepeatRightHandSkillOnEntity(Skill.Attack, nearest);
                await Task.Delay(200);
            }
            else if (me.Attributes[Attribute.Level] < 10
                && client.Game.Me.Equipment.TryGetValue(DirectoryType.RightHand, out var javalin)
                && javalin.Classification == ClassificationType.Javelin)
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.Attack}");
                await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, nearest, MovementMode.Walking);
                client.Game.LeftHandSkillHoldOnEntity(Skill.Attack, nearest);
                await Task.Delay(200);
            }

            return true;
        }

        private async Task<WorldObject> GetNearestInSight(Client client, List<WorldObject> enemies)
        {
            foreach (var enemy in enemies)
            {
                if (await IsInLineOfSight(client, enemy.Location))
                {
                    return enemy;
                }
            }

            return null;
        }

        private async Task<bool> SorceressAssist(Client client, Player player)
        {
            var enemies = NPCHelpers.GetNearbyNPCs(client, player.Location, 30, 40).ToList();

            var me = client.Game.Me;
            if (me.Mana > 10
                && me.HasSkill(Skill.FrozenArmor)
                && !me.HasSkill(Skill.ShiverArmor)
                && !client.Game.Me.Effects.ContainsKey(EntityEffect.Frozenarmor))
            {
                Log.Information($"Casting {Skill.FrozenArmor}");
                client.Game.UseRightHandSkillOnLocation(Skill.FrozenArmor, client.Game.Me.Location);
                await Task.Delay(100);
                return true;
            }
            else if (me.Mana > 10
                    && me.HasSkill(Skill.ShiverArmor)
                    && !client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor))
            {
                Log.Information($"Casting {Skill.ShiverArmor}");
                client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
                await Task.Delay(100);
                return true;
            }

            var nearest = enemies.FirstOrDefault();
            if (nearest == null)
            {
                return true;
            }

            if (me.HasSkill(Skill.Blizzard) && !me.HasSkill(Skill.FrozenOrb) && me.Mana > 35)
            {
                client.Game.UseRightHandSkillOnEntity(Skill.Blizzard, nearest);
                await Task.Delay(200);
            }
            else if (me.HasSkill(Skill.FrozenOrb) && me.Mana > 30)
            {
                if (me.Skills.GetValueOrDefault(Skill.StaticField) > 10
                    && nearest.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique)
                    && nearest.LifePercentage > 20
                    && ClassHelpers.CanStaticEntity(client, nearest.LifePercentage))
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }
                else if (me.Skills.GetValueOrDefault(Skill.StaticField) > 10 && nearest.LifePercentage > 50 && ClassHelpers.CanStaticEntity(client, nearest.LifePercentage))
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }
                else
                {
                    client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, nearest);
                }
                
                await Task.Delay(200);
            }
            else if (me.Skills.GetValueOrDefault(Skill.Nova) >= 20 && me.Mana > 30)
            {
                if(nearest.Location.Distance(client.Game.Me.Location) > 10)
                {
                    await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, nearest, client.Game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking);
                }
                if (me.Skills.GetValueOrDefault(Skill.StaticField) > 10 && nearest.LifePercentage > 50 && ClassHelpers.CanStaticEntity(client, nearest.LifePercentage))
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }
                else
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                }

                await Task.Delay(200);
            }
            else if (me.HasSkill(Skill.FireBolt)
                && !me.HasSkill(Skill.IceBolt)
                && me.Attributes[Attribute.Level] < 10
                && me.Mana > 5)
            {
                client.Game.UseRightHandSkillOnEntity(Skill.FireBolt, nearest);
                await Task.Delay(200);
            }
            else if (me.HasSkill(Skill.IceBlast)
                && me.Mana > 10)
            {
                client.Game.UseRightHandSkillOnEntity(Skill.IceBlast, nearest);
                await Task.Delay(200);
            }
            else if (me.Attributes[Attribute.Level] < 10 && client.Game.Area < Area.CowLevel)
            {
                await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, nearest, MovementMode.Walking);
                client.Game.UseRightHandSkillOnEntity(Skill.Attack, nearest);
                await Task.Delay(200);
            }

            return true;
        }

        private async Task<bool> PaladinAssist(Client client, Player player)
        {
            var enemies = NPCHelpers.GetNearbyNPCs(client, player.Location, 30, 20).ToList();

            var me = client.Game.Me;
            if (me.Mana > 20 && me.HasSkill(Skill.HolyShield) && !client.Game.Me.Effects.ContainsKey(EntityEffect.Holyshield))
            {
                Log.Information($"Casting {Skill.HolyShield}");
                client.Game.UseRightHandSkillOnLocation(Skill.HolyShield, client.Game.Me.Location);
                await Task.Delay(100);
                return true;
            }

            var conviction = me.Skills.GetValueOrDefault(Skill.Conviction);
            var enemyLightningEnhancedMultiShot = enemies.FirstOrDefault(e => e.MonsterEnchantments.Contains(MonsterEnchantment.LightningEnchanted)
                 && (e.MonsterEnchantments.Contains(MonsterEnchantment.MultiShot)));
            if (me.HasSkill(Skill.Salvation)
                && ( enemyLightningEnhancedMultiShot != null || client.Game.Players.Any(p => p.Effects.ContainsKey(EntityEffect.Convicted))))
            {
                if (!client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var currentSkill) || currentSkill != Skill.Salvation)
                {
                    Log.Information($"Changing to {Skill.Salvation} due to monster with {string.Join(",", enemyLightningEnhancedMultiShot?.MonsterEnchantments)} or convicted player");
                    client.Game.ChangeSkill(Skill.Salvation, Hand.Right);
                }
            }
            else if (player.Class == CharacterClass.Sorceress && me.HasSkill(Skill.Conviction) && me.Skills.GetValueOrDefault(Skill.BlessedHammer) < 10)
            {
                if (!client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var currentSkill) || currentSkill != Skill.Conviction)
                {
                    Log.Information($"Changing to {Skill.Conviction}");
                    client.Game.ChangeSkill(Skill.Conviction, Hand.Right);
                }
            }
            else if (me.HasSkill(Skill.Might) || me.HasSkill(Skill.Concentration) || me.HasSkill(Skill.Fanaticism))
            {
                var damageSkill = me.HasSkill(Skill.Fanaticism) ? Skill.Fanaticism : me.HasSkill(Skill.Concentration) ? Skill.Concentration : Skill.Might;
                if (!client.Game.Me.ActiveSkills.TryGetValue(Hand.Right, out var currentSkill) || currentSkill != damageSkill)
                {
                    Log.Information($"Changing from {currentSkill} to {damageSkill}");
                    client.Game.ChangeSkill(damageSkill, Hand.Right);
                }
            }

            var nearest = enemies.FirstOrDefault();
            if (nearest == null)
            {
                return true;
            }

            if (me.Attributes[Attribute.Level] < 25
                && client.Game.Difficulty == Difficulty.Normal
                && client.Game.Area != Area.CowLevel)
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.Attack}");
                await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, nearest, MovementMode.Walking);
                client.Game.LeftHandSkillHoldOnEntity(Skill.Attack, nearest);
                await Task.Delay(200);
            }
            else if (me.ActiveSkills.TryGetValue(Hand.Right, out var rightSkill)
                && (rightSkill == Skill.Fanaticism || rightSkill == Skill.Concentration || rightSkill == Skill.Might) && me.Skills.GetValueOrDefault(Skill.BlessedHammer) >= 20)
            {
                if (nearest.Location.Distance(client.Game.Me.Location) > 15)
                {
                    var goalLocation = client.Game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 3);
                    if (client.Game.Me.HasSkill(Skill.Vigor))
                    {
                        client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
                    }

                    var pathNearest = await _pathingService.GetPathToLocation(client.Game, goalLocation, MovementMode.Walking);
                    if (!await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                    {
                        Log.Warning($"Walking to Nearest failed at {client.Game.Me.Location}");
                    }
                    var damageSkill = me.HasSkill(Skill.Fanaticism) ? Skill.Fanaticism : me.HasSkill(Skill.Concentration) ? Skill.Concentration : Skill.Might;
                    client.Game.ChangeSkill(damageSkill, Hand.Right);
                }

                client.Game.ShiftHoldLeftHandSkillOnLocation(Skill.BlessedHammer, client.Game.Me.Location);
            }

            return true;
        }

        private async Task<bool> NecromancerAssist(Client client, Player player)
        {
            var me = client.Game.Me;
            if (me.Mana > 20 && me.HasSkill(Skill.BoneArmor) && !client.Game.Me.Effects.ContainsKey(EntityEffect.Bonearmor))
            {
                Log.Information($"Casting {Skill.BoneArmor}");
                client.Game.UseRightHandSkillOnLocation(Skill.BoneArmor, client.Game.Me.Location);
                await Task.Delay(100);
            }

            if (me.Mana > 40
                && me.HasSkill(Skill.Bloodgolem)
                && !me.Summons.Exists(s => s.NPCCode == NPCCode.BloodGolem))
            {
                Log.Information($"Summoning {NPCCode.BloodGolem}");
                client.Game.UseRightHandSkillOnLocation(Skill.Bloodgolem, client.Game.Me.Location);
                await Task.Delay(200);
            }

            if (me.Mana > 20
                && me.HasSkill(Skill.ClayGolem)
                && !me.HasSkill(Skill.Bloodgolem)
                && !me.Summons.Exists(s => s.NPCCode == NPCCode.ClayGolem))
            {
                Log.Information($"Summoning {NPCCode.ClayGolem}");
                client.Game.UseRightHandSkillOnLocation(Skill.ClayGolem, client.Game.Me.Location);
                await Task.Delay(200);
            }

            var enemies = NPCHelpers.GetNearbyNPCs(client, player.Location, 10, 30);
            var nearest = enemies.FirstOrDefault();
            if (nearest == null)
            {
                return true;
            }

            if (me.Mana > 10
                && me.Attributes[Attribute.Level] < 10
                && me.HasSkill(Skill.Skeletonraise)
                && !me.Summons.Exists(s => s.NPCCode == NPCCode.Skeleton))
            {
                Log.Information($"Summoning {NPCCode.Skeleton}");
                List<WorldObject> corpses = NPCHelpers.GetNearbyCorpses(client, nearest.Location, 1);
                var corpse = corpses.FirstOrDefault();
                if (corpses != null)
                {
                    client.Game.UseRightHandSkillOnEntity(Skill.Skeletonraise, corpse);
                    await Task.Delay(200);
                    return true;
                }
            }

            var cursableEnemy = enemies.FirstOrDefault(e => !e.Effects.Contains(EntityEffect.Lifetap) & !e.Effects.Contains(EntityEffect.Amplifydamage));
            if (client.Game.Area == Area.ChaosSanctuary
                && client.Game.Players.Any(p => p.Class == CharacterClass.Barbarian && p.Location.Distance(nearest.Location) < 10)
                && me.HasSkill(Skill.LifeTap)
                && enemies.Count() > 1
                && cursableEnemy != null
                && me.Mana > 15)
            {
                client.Game.UseRightHandSkillOnEntity(Skill.LifeTap, cursableEnemy);
                await Task.Delay(200);
            }
            else if (me.HasSkill(Skill.AmplifyDamage)
                && me.Mana > 5
                && cursableEnemy != null)
            {
                client.Game.UseRightHandSkillOnEntity(Skill.AmplifyDamage, cursableEnemy);
                await Task.Delay(200);
            }
            else if (me.HasSkill(Skill.CorpseExplosion) && me.Mana > 20)
            {
                foreach(var enemy in enemies)
                {
                    if(!enemy.Effects.Contains(EntityEffect.Amplifydamage))
                    {
                        continue;
                    }

                    List<WorldObject> corpses = NPCHelpers.GetNearbyCorpses(client, enemy.Location, 1);
                    var corpse = corpses.FirstOrDefault();
                    if (corpse == null)
                    {
                        continue;
                    }

                    client.Game.UseRightHandSkillOnEntity(Skill.CorpseExplosion, corpse);
                    await Task.Delay(200);
                }
            }

            return true;
        }

        private async Task<bool> BarbarianAssist(Client client, Player player)
        {
            var me = client.Game.Me;
            await ClassHelpers.CastAllShouts(client);
            var enemies = NPCHelpers.GetNearbyNPCs(client, player.Location, 1, 20);
            var nearest = enemies.FirstOrDefault();
            if (nearest == null)
            {
                return true;
            }

            if (me.Attributes[Attribute.Level] < 30 && client.Game.Difficulty > Difficulty.Normal)
            {
                return true;
            }
            else if (me.Attributes[Attribute.Level] < 26 && client.Game.Difficulty == Difficulty.Normal && client.Game.Area == Area.CowLevel)
            {
                return true;
            }

            if (me.HasSkill(Skill.Whirlwind) &&
                ((me.Attributes[Attribute.Level] > 33 && client.Game.Difficulty != Difficulty.Normal) || me.Attributes[Attribute.Level] > 40))
            {
                if(me.Mana > 30)
                {
                    return await WhirlWindEnemy(client, nearest);
                }
            }
            else if (me.HasSkill(Skill.Concentrate) && me.Mana > 5)
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.Concentrate}");
                await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, nearest, MovementMode.Walking);
                client.Game.RepeatRightHandSkillOnEntity(Skill.Concentrate, nearest);
                await Task.Delay(200);
            }
            else
            {
                Log.Information($"Attacking {nearest.NPCCode} with {Skill.Attack}");
                await MovementHelpers.MoveToWorldObject(client.Game, _pathingService, nearest, MovementMode.Walking);
                client.Game.UseRightHandSkillOnEntity(Skill.Attack, nearest);
                await Task.Delay(200);
            }

            return true;
        }

        private async Task<bool> WhirlWindEnemy(Client client, WorldObject worldObject)
        {
            if (client.Game.Me.Location.Distance(worldObject.Location) > 15)
            {
                var wwStartPoint = client.Game.Me.Location.GetPointBeforePointInSameDirection(worldObject.Location, 15);
                await MovementHelpers.MoveToLocation(client.Game, _pathingService, _mapApiService, wwStartPoint, MovementMode.Walking);
            }

            var wwDirection = client.Game.Me.Location.GetPointPastPointInSameDirection(worldObject.Location, 6);
            if (client.Game.Me.Location.Equals(worldObject.Location))
            {
                var pathLeft = await _pathingService.GetPathToLocation(client.Game, worldObject.Location.Add(-6, 0), MovementMode.Walking);
                var pathRight = await _pathingService.GetPathToLocation(client.Game, worldObject.Location.Add(6, 0), MovementMode.Walking);
                if (pathLeft.Count < pathRight.Count)
                {
                    Log.Debug($"same location, wwing to left");
                    wwDirection = new Point((ushort)(client.Game.Me.Location.X - 6), client.Game.Me.Location.Y);
                }
                else
                {
                    Log.Debug($"same location, wwing to right");
                    wwDirection = new Point((ushort)(client.Game.Me.Location.X + 6), client.Game.Me.Location.Y);
                }
            }

            //Log.Information($"player loc: {game.Me.Location}, nearest: {nearest.Location} ww destination: {wwDirection}  ");
            var distance = client.Game.Me.Location.Distance(wwDirection);;
            bool gotInWhirldWindState = false;
            var result = GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                var isInWhirldWindState = client.Game.Me.Effects.ContainsKey(EntityEffect.Skillmove) && client.Game.Me.Effects.ContainsKey(EntityEffect.Uninterruptable);
                if (!gotInWhirldWindState)
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                }
                if (!gotInWhirldWindState && isInWhirldWindState)
                {
                    gotInWhirldWindState = true;
                }
                return gotInWhirldWindState && !isInWhirldWindState;
            }, TimeSpan.FromSeconds(distance * 0.2));
            return result;
        }
    }
}
