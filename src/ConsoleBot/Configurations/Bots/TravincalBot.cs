﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using Action = D2NG.Core.D2GS.Items.Action;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Configurations.Bots
{
    public class TravincalBot : BaseBotConfiguration, IBotConfiguration
    {
        private static bool FullInventoryReported = false;
        private readonly IPathingService _pathingService;

        public TravincalBot(BotConfiguration config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService)
        : base(config, externalMessagingClient)
        {
            _pathingService = pathingService;
        }

        public async Task<int> Run()
        {
            var client = new Client();
            _externalMessagingClient.RegisterClient(client);
            return await CreateGameLoop(client);
        }

        protected override async Task<bool> RunSingleGame(Client client)
        {
            Log.Information("In game");
            client.Game.RequestUpdate(client.Game.Me.Id);
            while (client.Game.Me.Location.X == 0 && client.Game.Me.Location.Y == 0)
            {
                Thread.Sleep(10);
            }

            if (client.Game.Me.Class != CharacterClass.Barbarian)
            {
                throw new NotSupportedException("Only barbarian is supported on travincal");
            }

            client.Game.CleanupCursorItem();
            InventoryHelpers.MoveInventoryItemsToCube(client.Game);
            CleanupPotionsInBelt(client.Game);

            /*
             *
            while (client.Game.Players.Count < 2)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
             */

            if (client.Game.Act != Act.Act3)
            {
                var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, MovementMode.Walking);
                if (!WalkPathOfLocations(client.Game, pathToTownWayPoint))
                {
                    Log.Information($"Walking to {client.Game.Act} waypoint failed");
                    return false;
                }

                if (!MoveToWaypointViaNearestWaypoint(client.Game, Waypoint.KurastDocks))
                {
                    Log.Debug("Taking kurast docks waypoint failed");
                    return false;
                }
            }

            var healingPotionsInBelt = client.Game.Belt.NumOfHealthPotions();
            var manaPotionsInBelt = client.Game.Belt.NumOfManaPotions();
            if (healingPotionsInBelt < 6
                || manaPotionsInBelt < 6
                || client.Game.Inventory.Items.FirstOrDefault(i => i.Name == "Tome of Town Portal")?.Amount < 5
                || client.Game.Me.Life < 500)
            {
                var pathOrmus = await _pathingService.GetPathToNPC(client.Game, NPCCode.Ormus, MovementMode.Walking);
                if (!WalkPathOfLocations(client.Game, pathOrmus))
                {
                    Log.Warning($"Walking to Ormus failed at {client.Game.Me.Location}");
                }

                if (!NPCHelpers.SellItemsAndRefreshPotionsAtOrmus(client.Game))
                {
                    return false;
                }
            }

            if (client.Game.Act == Act.Act3 && client.Game.Stash.Items.Count(i => i.Name == "Flawless Skull") >= 3)
            {
                var pathStash = await _pathingService.GetPathToObject(client.Game, EntityCode.Stash, MovementMode.Walking);
                if (!WalkPathOfLocations(client.Game, pathStash))
                {
                    Log.Warning($"Walking failed at location {client.Game.Me.Location}");
                }
                CubeHelpers.TransmutePerfectSkulls(client.Game);
            }

            Log.Information("Walking to wp");
            var pathToWayPoint = await _pathingService.ToTownWayPoint(client.Game, MovementMode.Walking);
            if (!WalkPathOfLocations(client.Game, pathToWayPoint))
            {
                Log.Information($"Walking to {client.Game.Act} waypoint failed");
            }

            await Act4RepairAndGamble(client.Game);

            Log.Information("Taking travincal wp");
            if (!MoveToWaypointViaNearestWaypoint(client.Game, Waypoint.Travincal))
            {
                Log.Information("Taking trav waypoint failed");
                return false;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);

            Log.Information("Doing bo");
            BarbBo(client.Game);

            Log.Information("Walking to council members");
            
            var pathToCouncil = await _pathingService.GetPathToObjectWithOffset(client.Game, EntityCode.CompellingOrb, 23, 25, MovementMode.Walking);
            if (!WalkPathOfLocations(client.Game, pathToCouncil))
            {
                Log.Information($"Walking to councile members failed");
                return false;
            }

            Log.Information("Kill council members");
            if (!await KillCouncilMembers(client.Game))
            {
                Log.Information("Kill council members failed");
                return false;
            }

            Log.Information("Using find item");
            if (!await UseFindItemOnCouncilMembers(client.Game))
            {
                Log.Information("Finditem failed");
                return false;
            }

            Log.Information("Picking up left over items");
            if (!await PickupNearbyItems(client.Game, 300))
            {
                Log.Information("Pickup nearby items 1 failed");
            }

            if (!await PickupNearbyItems(client.Game, 300))
            {
                Log.Information("Pickup nearby items 2 failed");
            }

            Log.Information("Moving to town");
            if (!MoveToA3Town(client.Game))
            {
                Log.Information("Move to town failed");
                return false;
            }

            var pathDeckardCain = await _pathingService.GetPathToNPC(client.Game, NPCCode.DeckardCainAct3, MovementMode.Walking);
            if (!WalkPathOfLocations(client.Game, pathDeckardCain))
            {
                Log.Warning($"Walking to Deckhard Cain failed at {client.Game.Me.Location}");
                return false;
            }

            Log.Information("Identifying items");
            if (!NPCHelpers.IdentifyItemsAtDeckardCain(client.Game))
            {
                Log.Information("Identify items failed");
                return false;
            }

            Log.Information("Stashing items to keep");
            var stashResult = InventoryHelpers.StashItemsToKeep(client.Game, _externalMessagingClient);
            if (stashResult != MoveItemResult.Succes)
            {
                if (stashResult == MoveItemResult.NoSpace && !FullInventoryReported)
                {
                    await _externalMessagingClient.SendMessage($"bot inventory is full");
                    FullInventoryReported = true;
                }

                Log.Information("Stashing items failed");
                
            }

            Log.Information("Walking to ormus");
            var pathOrmus2 = await _pathingService.GetPathToNPC(client.Game, NPCCode.Ormus, MovementMode.Walking);
            if (!WalkPathOfLocations(client.Game, pathOrmus2))
            {
                Log.Warning($"Walking to Ormus failed at {client.Game.Me.Location}");
                return false;
            }

            Log.Information("Selling items at ormus");
            if (!NPCHelpers.SellItemsAndRefreshPotionsAtOrmus(client.Game))
            {
                Log.Information("Selling items failed");
                return false;
            }

            Log.Information("Successfully finished game");
            return true;
        }

        private async Task<bool> Act4RepairAndGamble(Game game)
        {
            bool shouldRepair = game.Items.Any(i => i.Action == Action.Equip && i.MaximumDurability > 0 && ((double)i.Durability / i.MaximumDurability) < 0.2);
            bool shouldGamble = game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.GoldInStash] > 7_000_000;
            if (!shouldRepair && !shouldGamble)
            {
                return true;
            }

            if (!MoveToWaypointViaNearestWaypoint(game, Waypoint.ThePandemoniumFortress))
            {
                Log.Information("Taking pandemonium waypoint failed");
                return false;
            }

            if (shouldRepair)
            {
                var pathHalbu = await _pathingService.GetPathToNPC(game, NPCCode.Halbu, MovementMode.Walking);
                if (!WalkPathOfLocations(game, pathHalbu))
                {
                    Log.Warning($"Walking to Halbu failed at {game.Me.Location}");
                }

                var halbu = NPCHelpers.GetUniqueNPC(game, NPCCode.Halbu);
                if (halbu == null)
                {
                    return false;
                }
                NPCHelpers.RepairItems(game, halbu);
            }

            if (shouldGamble)
            {
                var pathJamella = await _pathingService.GetPathToNPC(game, NPCCode.JamellaAct4, MovementMode.Walking);
                if (!WalkPathOfLocations(game, pathJamella))
                {
                    Log.Warning($"Walking to Jamella failed at {game.Me.Location}");
                }

                var jamella = NPCHelpers.GetUniqueNPC(game, NPCCode.JamellaAct4);
                if (jamella == null)
                {
                    return false;
                }

                NPCHelpers.GambleItems(game, jamella);
            }

            var pathToWayPoint = await _pathingService.ToTownWayPoint(game, MovementMode.Walking);
            if (!WalkPathOfLocations(game, pathToWayPoint))
            {
                Log.Information($"Walking to {game.Act} waypoint failed");
                return false;
            }

            if (!MoveToWaypointViaNearestWaypoint(game, Waypoint.KurastDocks))
            {
                Log.Debug("Taking kurast docks waypoint failed");
                return false;
            }

            return true;
        }

        private void CleanupPotionsInBelt(Game game)
        {
            var manaPotionsInWrongSlot = game.Belt.GetManaPotionsInSlots(new List<int>() { 0, 1 });
            foreach (var manaPotion in manaPotionsInWrongSlot)
            {
                game.UseBeltItem(manaPotion);
            }

            var healthPotionsInWrongSlot = game.Belt.GetHealthPotionsInSlots(new List<int>() { 2, 3 });
            foreach (var healthPotion in healthPotionsInWrongSlot)
            {
                game.UseBeltItem(healthPotion);
            }
        }

        private async Task<bool> PickupNearbyItems(Game game, double distance)
        {
            var pickupItems = game.Items.Where(i =>
            {
                return i.Ground && game.Me.Location.Distance(i.Location) < distance && Pickit.Pickit.ShouldPickupItem(i);
            }).OrderBy(n => game.Me.Location.Distance(n.Location));

            foreach (var item in pickupItems)
            {
                if (!game.IsInGame())
                {
                    return false;
                }

                InventoryHelpers.MoveInventoryItemsToCube(game);
                if (game.Inventory.FindFreeSpace(item) == null)
                {
                    continue;
                }

                await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    if (game.Me.Location.Distance(item.Location) >= 5)
                    {
                        var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, item.Location, MovementMode.Walking);
                        WalkPathOfLocations(game, pathNearest);
                        return false;
                    }

                    return true;
                }, TimeSpan.FromSeconds(3));

                if (game.Me.Location.Distance(item.Location) < 5)
                {
                    game.PickupItem(item);
                }
            }

            InventoryHelpers.MoveInventoryItemsToCube(game);
            return true;
        }

        private async Task<bool> UseFindItemOnCouncilMembers(Game game)
        {
            List<WorldObject> councilMembers = GetCouncilMembers(game);
            var nearestMembers = councilMembers.OrderBy(n => game.Me.Location.Distance(n.Location));

            foreach (var nearestMember in nearestMembers)
            {
                await PickupNearbyItems(game, 10);
                bool result = await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    if (!game.IsInGame())
                    {
                        return false;
                    }

                    if (retryCount % 4 == 0)
                    {
                        Log.Debug($"Requesting update find item, since % 4th attempt");
                        game.RequestUpdate(game.Me.Id);
                    }

                    return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                    {
                        if (!game.IsInGame())
                        {
                            return false;
                        }

                        if (nearestMember.Location.Distance(game.Me.Location) > 5)
                        {
                            var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearestMember.Location, MovementMode.Walking);
                            WalkPathOfLocations(game, pathNearest);
                        }

                        if (nearestMember.Location.Distance(game.Me.Location) <= 5)
                        {
                            game.UseRightHandSkillOnEntity(Skill.FindItem, nearestMember);
                            Thread.Sleep(500);
                            return true;
                        }

                        return false;

                    }, TimeSpan.FromSeconds(2));
                }, TimeSpan.FromSeconds(4));

                if (!game.IsInGame())
                {
                    return false;
                }
            }

            return true;
        }



        private async Task<bool> KillCouncilMembers(Game game)
        {
            var startTime = DateTime.Now;
            List<WorldObject> aliveMembers;
            do
            {
                List<WorldObject> councilMembers = GetCouncilMembers(game);
                aliveMembers = councilMembers
                    .Where(n => n.State != EntityState.Dead)
                    .OrderBy(n => game.Me.Location.Distance(n.Location))
                    .ToList();

                var nearest = aliveMembers.FirstOrDefault();
                if (nearest != null)
                {
                    if (!game.IsInGame())
                    {
                        return false;
                    }

                    if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMinutes(2))
                    {
                        Log.Debug("Passed maximum elapsed time for killing council members");
                        return false;
                    }

                    if(game.Me.Location.Distance(nearest.Location) > 5)
                    {
                        var closeTo = game.Me.Location.GetPointBeforePointInSameDirection(nearest.Location, 6);
                        if(game.Me.Location.Distance(closeTo) > 3)
                        {
                            closeTo = nearest.Location;
                        }

                        var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearest.Location, MovementMode.Walking);
                        if (!WalkPathOfLocations(game, pathNearest))
                        {
                            Log.Warning($"Walking to Council Member failed at {game.Me.Location}");
                        }
                    }

                    var wwDirection = game.Me.Location.GetPointPastPointInSameDirection(nearest.Location, 6);
                    if (game.Me.Location.Equals(nearest.Location))
                    {
                        var pathLeft = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearest.Location.Add(-6, 0), MovementMode.Walking);
                        var pathRight = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, nearest.Location.Add(6, 0), MovementMode.Walking);
                        if (pathLeft.Count < pathRight.Count)
                        {
                            Log.Debug($"same location, wwing to left");
                            wwDirection = new Point((ushort)(game.Me.Location.X - 6), game.Me.Location.Y);
                        }
                        else
                        {
                            Log.Debug($"same location, wwing to right");
                            wwDirection = new Point((ushort)(game.Me.Location.X + 6), game.Me.Location.Y);
                        }
                    }

                    var wwDistance = game.Me.Location.Distance(wwDirection);
                    //Log.Information($"player loc: {game.Me.Location}, nearest: {nearest.Location} ww destination: {wwDirection}  ");
                    game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                    Thread.Sleep((int)((wwDistance * 50 + 300)));

                }
            } while (aliveMembers.Any());

            return true;
        }

        private List<WorldObject> GetCouncilMembers(Game game)
        {
            var councilMembers = game.GetNPCsByCode(NPCCode.CouncilMember1);
            councilMembers.AddRange(game.GetNPCsByCode(NPCCode.CouncilMember2));
            return councilMembers;
        }

        private bool MoveToA3Town(Game game)
        {
            var existingTownPortals = game.GetEntityByCode(EntityCode.TownPortal).ToHashSet();
            game.CreateTownPortal();
            var newTownPortals = game.GetEntityByCode(EntityCode.TownPortal).Where(t => !existingTownPortals.Contains(t)).ToList();
            if (!newTownPortals.Any())
            {
                return false;
            }

            var townportal = newTownPortals.First();
            if(!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                game.MoveTo(townportal);

                game.InteractWithObject(townportal);
                return GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    return game.Area == Area.KurastDocks;
                }, TimeSpan.FromSeconds(1));
            }, TimeSpan.FromSeconds(3.5)))
            {
                return false;
            }

            game.RequestUpdate(game.Me.Id);
            return true;
        }

        private void BarbBo(Game game)
        {
            game.UseRightHandSkillOnLocation(Skill.BattleCommand, game.Me.Location);
            Thread.Sleep(500);
            game.UseRightHandSkillOnLocation(Skill.BattleOrders, game.Me.Location);
            Thread.Sleep(500);
            game.UseRightHandSkillOnLocation(Skill.Shout, game.Me.Location);
            game.UseHealthPotion();
            Thread.Sleep(300);
        }
        private bool WalkPathOfLocations(Game game, List<Point> points)
        {
            if (points.Count == 0)
            {
                Log.Warning($"Walk path of length 0 found, something went wrong while client at location: {game.Me.Location}");
                return false;
            }

            var previousBackupPoint = -1;

            for (int i = 0; i < points.Count; ++i)
            {
                var point = points[i];
                if(game.Me.Location.Distance(point) > 30)
                {
                    var bestDistance = game.Me.Location.Distance(point);
                    var bestIndex = i;
                    var j = i - 1;
                    while (j > 0)
                    {
                        var newDistance = game.Me.Location.Distance(points[j]);
                        if (newDistance < bestDistance)
                        {
                            bestDistance = newDistance;
                            bestIndex = j;
                        }

                        j--;
                    }
                    Log.Debug($"Backing up to point {j} from {i}");

                    if (previousBackupPoint == bestIndex)
                    {
                        if (!game.IsInTown() && game.Me.Class == CharacterClass.Barbarian && game.Me.HasSkill(Skill.Whirlwind))
                        {
                            Log.Debug($"Seems stuck, whirlwinding to point {point}");
                            game.UseRightHandSkillOnLocation(Skill.Whirlwind, point);
                            Thread.Sleep((int)(game.Me.Location.Distance(point) * 80 + 400));
                        }
                    }

                    previousBackupPoint = bestIndex;
                    i = bestIndex;
                }
                Log.Debug($"Running to point {point}");
                game.MoveTo(point);
            }

            return true;
        }

        private bool MoveToWaypointViaNearestWaypoint(Game game, Waypoint waypoint)
        {
            WorldObject townWaypoint = null;
            GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                townWaypoint = game.GetEntityByCode(game.Act.MapTownWayPoint()).Single();
                return townWaypoint != null;
            }, TimeSpan.FromSeconds(2));

            if (townWaypoint == null)
            {
                Log.Error("No waypoint found");
                return false;
            }

            Log.Debug("Walking to waypoint");
            if(!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                while (game.Me.Location.Distance(townWaypoint.Location) > 5)
                {
                    game.MoveTo(townWaypoint);
                }
                Log.Debug("Taking waypoint");
                game.TakeWaypoint(townWaypoint, waypoint);
                return GeneralHelpers.TryWithTimeout((retryCount) => game.Area == waypoint.ToArea(), TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            game.RequestUpdate(game.Me.Id);
            return true;
        }
    }
}