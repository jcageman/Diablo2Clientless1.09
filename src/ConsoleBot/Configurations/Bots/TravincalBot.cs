using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Serilog;
using Action = D2NG.Core.D2GS.Items.Action;
using Attribute = D2NG.Core.D2GS.Players.Attribute;

namespace ConsoleBot.Configurations.Bots
{
    public class TravincalBot : BaseBotConfiguration, IBotConfiguration
    {
        private static bool FullInventoryReported = false;

        public TravincalBot(BotConfiguration config, IExternalMessagingClient externalMessagingClient)
        : base(config, externalMessagingClient)
        {
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

            InventoryHelpers.MoveInventoryItemsToCube(client.Game);
            client.Game.CleanupCursorItem();
            CleanupPotionsInBelt(client.Game);

            if (client.Game.Act != Act.Act3 && client.Game.Act != Act.Act4)
            {
                Log.Information("Starting location is not Act 3 or 4, not supported for now");
                return false;
            }

            Log.Information("Walking to wp");
            if (client.Game.Act == Act.Act3 && !WalkToAct3WpFromStart(client.Game))
            {
                Log.Information("Walk to waypoint failed");
                return false;
            }

            if (client.Game.Act == Act.Act3)
            {
                TransmutePerfectSkulls(client.Game);
            }

            Act4RepairAndGamble(client.Game);

            Log.Information("Taking travincal wp");
            if (!MoveToWaypointViaNearestWaypoint(client.Game, Waypoint.Travincal))
            {
                Log.Information("Taking trav waypoint failed");
                return false;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);

            Log.Information("Doing bo");
            BarbBo(client.Game);

            var initialLocation = client.Game.Me.Location;

            Log.Information("Walking to council members");
            if (!WalkToCouncilMembers(client.Game, initialLocation))
            {
                Log.Information("Walk to council members failed");
                return false;
            }

            Log.Information("Kill council members");
            if (!KillCouncilMembers(client.Game, initialLocation))
            {
                Log.Information("Kill council members failed");
                return false;
            }

            Log.Information("Using find item");
            if (!UseFindItemOnCouncilMembers(client.Game, initialLocation))
            {
                Log.Information("Finditem failed");
                return false;
            }

            Log.Information("Picking up left over items");
            if (!PickupNearbyItems(client.Game, initialLocation, 200))
            {
                Log.Information("Pickup nearby items 1 failed");
            }

            if (!PickupNearbyItems(client.Game, initialLocation, 200))
            {
                Log.Information("Pickup nearby items 2 failed");
            }

            Log.Information("Moving to town");
            if (!MoveToA3Town(client.Game))
            {
                Log.Information("Move to town failed");
                return false;
            }

            Log.Information("Identifying items");
            if (!NPCHelpers.IdentifyItemsAtDeckardCain(client.Game))
            {
                Log.Information("Identify items failed");
                return false;
            }

            Log.Information("Stashing items to keep");
            var stashResult = InventoryHelpers.StashItemsToKeep(client.Game);
            if (stashResult != MoveItemResult.Succes)
            {
                if (stashResult == MoveItemResult.NoSpace && !FullInventoryReported)
                {
                    await _externalMessagingClient.SendMessage($"bot inventory is full");
                    FullInventoryReported = true;
                }

                Log.Information("Stashing items failed");
                return false;
            }

            Log.Information("Walking to ormus");
            if (!WalkToOrmus(client.Game))
            {
                Log.Information("Walking to ormus failed");
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

        private bool WalkToOrmus(Game game)
        {
            var points = new List<Point>()
                {
                    new Point(5151, 5068),
                    new Point(5147, 5085),
                    new Point(5138, 5096),
                    new Point(5126, 5094),
                };
            return WalkPathOfLocations(game, points);
        }

        private bool Act4RepairAndGamble(Game game)
        {
            bool shouldRepair = game.Items.Any(i => i.Action == Action.Equip && i.MaximumDurability > 0 && ((double)i.Durability / i.MaximumDurability) < 0.2);
            bool shouldGamble = game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.GoldInStash] > 7_000_000;
            if (!shouldRepair && !shouldGamble)
            {
                return true;
            }

            if (!MoveToA4Npcs(game))
            {
                return false;
            }

            if (shouldRepair)
            {
                var halbu = NPCHelpers.GetUniqueNPC(game, NPCCode.Halbu);
                if (halbu == null)
                {
                    return false;
                }
                NPCHelpers.RepairItems(game, halbu);
            }

            if (shouldGamble)
            {
                var jamella = NPCHelpers.GetUniqueNPC(game, NPCCode.JamellaAct4);
                if (jamella == null)
                {
                    return false;
                }

                NPCHelpers.GambleItems(game, jamella);
            }

            if (!MoveToA4Waypoint(game))
            {
                return false;
            }

            if (!MoveToWaypointViaNearestWaypoint(game, Waypoint.KurastDocks))
            {
                Log.Debug("Taking kurast docks waypoint failed");
                return false;
            }

            return true;
        }

        private bool MoveToA4Waypoint(Game game)
        {
            var points = new List<Point>()
                    {
                        new Point(5087, 5044),
                        new Point(5078, 5042),
                        new Point(5061, 5040),
                        new Point(5046, 5037),
                        new Point(5043, 5018),

                    };

            return WalkPathOfLocations(game, points);
        }

        private bool MoveToA4Npcs(Game game)
        {
            if (!MoveToWaypointViaNearestWaypoint(game, Waypoint.ThePandemoniumFortress))
            {
                Log.Information("Taking pandemonium waypoint failed");
                return false;
            }

            var points = new List<Point>()
                    {
                        new Point(5046, 5037),
                        new Point(5061, 5040),
                        new Point(5078, 5042),
                        new Point(5087, 5044),
                    };

            return WalkPathOfLocations(game, points);
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

        private bool PickupNearbyItems(Game game, Point initialLocation, double distance)
        {
            var pickupItems = game.Items.Where(i =>
            {
                return i.Ground && PenalizedWalkingDistance(game, initialLocation, i.Location) < distance && Pickit.Pickit.ShouldPickupItem(i);
            }).OrderBy(n => PenalizedWalkingDistance(game, initialLocation, n.Location));

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

                if (!MoveToCorrectPlaceInTravBuilding(game, initialLocation, item.Location))
                {
                    return false;
                }

                GeneralHelpers.TryWithTimeout((retryCount =>
                {
                    if (game.Me.Location.Distance(item.Location) >= 5)
                    {
                        game.MoveTo(item.Location);
                    }

                    return game.Me.Location.Distance(item.Location) < 5;
                }), TimeSpan.FromSeconds(3));

                if (game.Me.Location.Distance(item.Location) < 5)
                {
                    game.PickupItem(item);
                }
            }

            InventoryHelpers.MoveInventoryItemsToCube(game);
            return true;
        }

        private bool UseFindItemOnCouncilMembers(Game game, Point initialLocation)
        {
            List<WorldObject> councilMembers = GetCouncilMembers(game);
            var nearestMembers = councilMembers.OrderBy(n => PenalizedWalkingDistance(game, initialLocation, n.Location));

            foreach (var nearestMember in nearestMembers)
            {
                PickupNearbyItems(game, initialLocation, 5);
                bool result = GeneralHelpers.TryWithTimeout((retryCount) =>
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

                    MoveToCorrectPlaceInTravBuilding(game, initialLocation, nearestMember.Location);
                    return GeneralHelpers.TryWithTimeout((retryCount) =>
                    {
                        if (!game.IsInGame())
                        {
                            return false;
                        }

                        if (nearestMember.Location.Distance(game.Me.Location) > 5)
                        {
                            game.MoveTo(nearestMember);
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



        private bool KillCouncilMembers(Game game, Point initialLocation)
        {
            var startTime = DateTime.Now;
            List<WorldObject> aliveMembers;
            do
            {
                List<WorldObject> councilMembers = GetCouncilMembers(game);
                aliveMembers = councilMembers
                    .Where(n => n.State != EntityState.Dead)
                    .OrderBy(n => PenalizedWalkingDistance(game, initialLocation, n.Location))
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

                    if (!MoveToCorrectPlaceInTravBuilding(game, initialLocation, nearest.Location))
                    {
                        Log.Information("Couldn't move to right location in trav building");
                        continue;
                    }

                    var distanceToNearest = nearest.Location.Distance(game.Me.Location);
                    if (nearest.Location.Distance(game.Me.Location) > 15)
                    {
                        game.MoveTo(nearest);
                    }
                    else
                    {

                        var wwDirection = game.Me.Location.GetPointPastPointInSameDirection(nearest.Location, 6);
                        if (game.Me.Location.Equals(nearest.Location))
                        {
                            if (game.Me.Location.X - initialLocation.X > 100)
                            {
                                //Log.Information($"same location, wwing to left");
                                wwDirection = new Point((ushort)(game.Me.Location.X - 6), game.Me.Location.Y);
                            }
                            else
                            {
                                //Log.Information($"same location, wwing to right");
                                wwDirection = new Point((ushort)(game.Me.Location.X + 6), game.Me.Location.Y);
                            }
                        }
                        //Log.Information($"player loc: {game.Me.Location}, nearest: {nearest.Location} ww destination: {wwDirection}  ");
                        game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                        Thread.Sleep((int)(distanceToNearest * 80 + 400));
                    }
                }
            } while (aliveMembers.Any());

            return true;
        }

        private double PenalizedWalkingDistance(Game game, Point initialLocation, Point location)
        {
            var distance = location.Distance(game.Me.Location);
            var MeDeltaY = game.Me.Location.Y - initialLocation.Y;
            var NearestDeltaY = game.Me.Location.Y - initialLocation.Y;
            var MeDeltaX = game.Me.Location.X - initialLocation.X;
            if (MeDeltaY < -78 && NearestDeltaY > -80 && (MeDeltaX < 97 || MeDeltaX > 104))
            {
                distance += 40;
            }
            else if (MeDeltaY > -80 && NearestDeltaY < -78 && (MeDeltaX < 97 || MeDeltaX > 104))
            {
                distance += 40;
            }
            return distance;
        }

        private bool MoveToCorrectPlaceInTravBuilding(Game game, Point initialLocation, Point targetLocation)
        {
            return GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                if (!game.IsInGame())
                {
                    return false;
                }

                var MeDeltaY = game.Me.Location.Y - initialLocation.Y;
                var NearestDeltaY = targetLocation.Y - initialLocation.Y;
                Point insideBuilding = new Point((ushort)(initialLocation.X + 100), (ushort)(initialLocation.Y - 85));
                Point outsideBuilding = new Point((ushort)(initialLocation.X + 100), (ushort)(initialLocation.Y - 75));
                if (MeDeltaY < -78 && NearestDeltaY > -80)
                {
                    //Log.Information($"Moving outside building");
                    game.MoveTo(insideBuilding);
                    game.MoveTo(outsideBuilding);
                    return game.Me.Location.Distance(outsideBuilding) < 5;
                }
                else if (MeDeltaY > -80 && NearestDeltaY < -78)
                {
                    //Log.Information($"Moving inside building");
                    game.MoveTo(outsideBuilding);
                    game.MoveTo(insideBuilding);
                    return game.Me.Location.Distance(insideBuilding) < 5;
                }

                return true;

            }, TimeSpan.FromSeconds(4));
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
            return GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                game.MoveTo(townportal);

                game.InteractWithObject(townportal);
                return GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    return game.Area == Area.KurastDocks;
                }, TimeSpan.FromSeconds(1));
            }, TimeSpan.FromSeconds(3.5));
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

        private bool WalkToCouncilMembers(Game game, Point initialLocation)
        {
            var travpoints = new List<(short, short)>()
                    {
                        (10, 5),
                        (21, 5),
                        (19, -15),
                        (29, -24),
                        (44, -25),
                        (61, -25),
                        (76, -25),
                        (103, -25),
                        (103, -37),
                        (100, -52),
                        (100, -63),
                    }.Select(p => new Point((ushort)(initialLocation.X + p.Item1), (ushort)(initialLocation.Y + p.Item2))).ToList();
            return WalkPathOfLocations(game, travpoints);
        }

        private bool WalkToAct3WpFromStart(Game game)
        {
            var points = new List<Point>()
                    {
                        new Point(5131, 5163),
                        new Point(5133, 5145),
                        new Point(5133, 5125),
                        new Point(5132, 5106),
                        new Point(5133, 5092),
                    };

            var result = WalkPathOfLocations(game, points);
            if (!result)
            {
                return false;
            }

            var healingPotionsInBelt = game.Belt.NumOfHealthPotions();
            var manaPotionsInBelt = game.Belt.NumOfManaPotions();
            if (healingPotionsInBelt < 6
                || manaPotionsInBelt < 6
                || game.Inventory.Items.FirstOrDefault(i => i.Name == "Tome of Town Portal")?.Amount < 5
                || game.Me.Life < 500)
            {
                if (!NPCHelpers.SellItemsAndRefreshPotionsAtOrmus(game))
                {
                    return false;
                }

                WalkPathOfLocations(game, new List<Point> { new Point(5138, 5096) });
            }

            var points2 = new List<Point>()
                    {
                        new Point(5148, 5090),
                        new Point(5149, 5087),
                        new Point(5154, 5072),
                        new Point(5159, 5059)
                    };
            return WalkPathOfLocations(game, points2);
        }

        private bool WalkPathOfLocations(Game game, List<Point> points)
        {
            foreach (var point in points)
            {
                var result = GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    if (retryCount > 0)
                    {
                        Log.Debug($"Retrying");
                        game.RequestUpdate(game.Me.Id);
                    }

                    if (retryCount > 1 && !game.IsInTown() && game.Me.Class == CharacterClass.Barbarian && game.Me.HasSkill(Skill.Whirlwind))
                    {
                        Log.Debug($"Seems stuck, whirlwinding to point {point}");
                        game.UseRightHandSkillOnLocation(Skill.Whirlwind, point);
                        Thread.Sleep((int)(game.Me.Location.Distance(point) * 80 + 400));
                    }
                    else
                    {
                        Log.Debug($"Running to point {point}");
                        game.MoveTo(point);
                    }

                    return game.Me.Location.Distance(point) < 10;
                }, TimeSpan.FromSeconds(4));

                if (!result)
                {
                    return false;
                }
            }

            return true;
        }

        private bool MoveToWaypointViaNearestWaypoint(Game game, Waypoint waypoint)
        {
            WorldObject nearestWaypoint = null;
            GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                nearestWaypoint = GetNearestWaypoint(game);
                return nearestWaypoint != null;
            }, TimeSpan.FromSeconds(2));

            if (nearestWaypoint == null)
            {
                Log.Error("No waypoint found");
                return false;
            }

            Log.Debug("Walking to waypoint");
            return GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                while (game.Me.Location.Distance(nearestWaypoint.Location) > 5)
                {
                    game.MoveTo(nearestWaypoint);
                }
                Log.Debug("Taking waypoint");
                game.TakeWaypoint(nearestWaypoint, waypoint);
                return GeneralHelpers.TryWithTimeout((retryCount) => game.Area == waypoint.ToArea(), TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5));
        }
        private WorldObject GetNearestWaypoint(Game game)
        {
            var waypoints = new List<WorldObject>();
            foreach (var waypointEntityCode in EntityConstants.WayPointEntityCodes)
            {
                waypoints.AddRange(game.GetEntityByCode(waypointEntityCode));
            }

            return waypoints.SingleOrDefault();
        }

        private void TransmutePerfectSkulls(Game game)
        {
            var flawlessSkulls = game.Stash.Items.Where(i => i.Name == "Flawless Skull").ToList();
            if (flawlessSkulls.Count < 3)
            {
                return;
            }

            if (game.Cube.Items.Any())
            {
                return;
            }

            var stashes = game.GetEntityByCode(EntityCode.Stash);
            if (!stashes.Any())
            {
                return;
            }

            var stash = stashes.Single();


            bool result = GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                if (game.Me.Location.Distance(stash.Location) >= 5)
                {
                    game.MoveTo(stash);
                }
                else
                {
                    return game.OpenStash(stash);
                }

                return false;
            }, TimeSpan.FromSeconds(4));

            if (!result)
            {
                Log.Error($"Failed to open stash");
                return;
            }

            foreach (var skull in flawlessSkulls)
            {
                if (InventoryHelpers.MoveItemFromStashToInventory(game, skull) != MoveItemResult.Succes)
                {
                    break;
                }
            }

            Thread.Sleep(300);
            game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            game.ClickButton(ClickType.CloseStash);

            Log.Information($"Moved skulls to inventory for transmuting");

            var remainingSkulls = flawlessSkulls;
            while (remainingSkulls.Count() > 2)
            {
                Log.Information($"Transmuting 3 flawless skulls to perfect skull");
                var skullsToTransmute = remainingSkulls.Take(3);
                remainingSkulls = remainingSkulls.Skip(3).ToList();
                foreach (var skull in skullsToTransmute)
                {
                    var inventoryItem = game.Inventory.FindItemById(skull.Id);
                    if (inventoryItem == null)
                    {
                        Log.Error($"Skull to be transmuted not found in inventory");
                        return;
                    }
                    var freeSpace = game.Cube.FindFreeSpace(inventoryItem);
                    if (freeSpace != null)
                    {
                        InventoryHelpers.PutInventoryItemInCube(game, inventoryItem, freeSpace);
                    }
                }

                if (!InventoryHelpers.TransmuteItemsInCube(game))
                {
                    Log.Error($"Transmuting items failed");
                    return;
                }

                var newCubeItems = game.Cube.Items;
                foreach (var item in newCubeItems)
                {
                    if (InventoryHelpers.PutCubeItemInInventory(game, item) != MoveItemResult.Succes)
                    {
                        Log.Error($"Couldn't move transmuted items out of cube");
                        return;
                    }
                }
            }

            if (!game.OpenStash(stash))
            {
                Log.Error($"Opening stash failed");
                return;
            }

            var inventoryItemsToKeep = game.Inventory.Items.Where(i => Pickit.Pickit.ShouldKeepItem(i) && Pickit.Pickit.CanTouchInventoryItem(i)).ToList();
            foreach (Item item in inventoryItemsToKeep)
            {
                if (InventoryHelpers.MoveItemToStash(game, item) == MoveItemResult.Failed)
                {
                    return;
                }
            }

            Thread.Sleep(300);
            game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            game.ClickButton(ClickType.CloseStash);

            Log.Information($"Transmuting items succeeded");
        }
    }
}
