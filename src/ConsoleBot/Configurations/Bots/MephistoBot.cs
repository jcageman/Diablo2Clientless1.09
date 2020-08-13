using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using D2NG.Core;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Services.Pathing;

namespace ConsoleBot.Configurations.Bots
{
    public class MephistoBot : BaseBotConfiguration, IBotConfiguration
    {
        private readonly IPathingService _pathingService;

        public MephistoBot(BotConfiguration config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService) : base(config, externalMessagingClient)
        {
            _pathingService = pathingService;
        }

        public async Task<int> Run()
        {

            /*
                        var path = await _pathingService.GetPathFromWaypointToArea(1256081602, Difficulty.Normal, Area.DuranceOfHateLevel2, Waypoint.DuranceOfHateLevel2.ToEntityCode(), Area.DuranceOfHateLevel3, MovementMode.Teleport);
            return 1;
            var path = await _pathingService.GetPathToObject(1184271221, 0, Area.KurastDocks, new Point(5116, 5168), Waypoint.KurastDocks.ToEntityCode(), MovementMode.Teleport);
            
            */
            /*

            */
            var client = new Client();
            _externalMessagingClient.RegisterClient(client);
            return await CreateGameLoop(client);
        }

        protected override async Task<bool> RunSingleGame(Client client)
        {
            Log.Information("In game");
            client.Game.RequestUpdate(client.Game.Me.Id);
            if (!GeneralHelpers.TryWithTimeout(
                (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
                TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            if (client.Game.Me.Class != CharacterClass.Sorceress)
            {
                throw new NotSupportedException("Only sorceress is supported on Mephisto");
            }

            /*
            while (client.Game.Players.Count < 2)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
            */

            client.Game.CleanupCursorItem();
            CleanupPotionsInBelt(client.Game);

            var unidentifiedItemCount = client.Game.Inventory.Items.Count(i => !i.IsIdentified) +
                                        client.Game.Cube.Items.Count(i => !i.IsIdentified);
            if (unidentifiedItemCount > 10)
            {
                Log.Information($"Visiting Deckard Cain with {unidentifiedItemCount} unidentified items");

                var pathDecardCain = await _pathingService.GetPathToNPC(client.Game.MapId, Difficulty.Normal, client.Game.Area, client.Game.Me.Location, NPCCode.DeckardCainAct3, MovementMode.Teleport);
                if (!TeleportViaPath(client, pathDecardCain))
                {
                    Log.Warning($"Teleporting to Deckard Cain failed at {client.Game.Me.Location}");
                    return false;
                }

                NPCHelpers.IdentifyItemsAtDeckardCain(client.Game);

                var pathStash = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, client.Game.Area, client.Game.Me.Location, EntityCode.Stash, MovementMode.Teleport);
                if (!TeleportViaPath(client, pathStash))
                {
                    Log.Warning($"Teleporting failed at location {client.Game.Me.Location}");
                }

                InventoryHelpers.StashItemsToKeep(client.Game);
            }

            var healingPotionsInBelt = client.Game.Belt.NumOfHealthPotions();
            var manaPotionsInBelt = client.Game.Belt.NumOfManaPotions();
            if (healingPotionsInBelt < 4
                || manaPotionsInBelt < 4
                || client.Game.Inventory.Items.FirstOrDefault(i => i.Name == "Tome of Town Portal")?.Amount < 5
                || client.Game.Me.Life < 500
                || client.Game.Inventory.Items.Count(i => i.IsIdentified) + client.Game.Cube.Items.Count(i => i.IsIdentified) > 4)
            {
                Log.Information($"Visiting Ormus");

                var pathOrmus = await _pathingService.GetPathToNPC(client.Game.MapId, Difficulty.Normal, client.Game.Area, client.Game.Me.Location, NPCCode.Ormus, MovementMode.Teleport);
                if (!TeleportViaPath(client, pathOrmus))
                {
                    Log.Warning($"Teleporting to Ormus failed at {client.Game.Me.Location}");
                    return false;
                }

                if (!NPCHelpers.SellItemsAndRefreshPotionsAtOrmus(client.Game))
                {
                    Log.Warning($"Refreshing potions at Ormus failed at {client.Game.Me.Location}");
                    return false;
                }
            }

            bool shouldGamble = client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.GoldInStash] > 7_000_000;
            if (shouldGamble)
            {
                Log.Information($"Gambling items at Alkor");
                var pathAlkor = await _pathingService.GetPathToNPC(client.Game.MapId, Difficulty.Normal, client.Game.Area, client.Game.Me.Location, NPCCode.Alkor, MovementMode.Teleport);
                if (!TeleportViaPath(client, pathAlkor))
                {
                    Log.Warning($"Teleporting to alkor failed at {client.Game.Me.Location}");
                    return false;
                }

                var alkor = NPCHelpers.GetUniqueNPC(client.Game, NPCCode.Alkor);
                if (alkor == null)
                {
                    return false;
                }

                NPCHelpers.GambleItems(client.Game, alkor);
            }

            var path1 = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, client.Game.Area, client.Game.Me.Location, EntityCode.WaypointAct3, MovementMode.Teleport);
            if (!TeleportViaPath(client, path1))
            {
                Log.Warning($"Teleporting failed at location {client.Game.Me.Location}");
            }

            var waypoint = client.Game.GetEntityByCode(EntityCode.WaypointAct3).Single();
            Log.Information("Taking waypoint to DuranceOfHateLevel2");
            GeneralHelpers.TryWithTimeout((_) =>
            {

                client.Game.TakeWaypoint(waypoint, Waypoint.DuranceOfHateLevel2);
                return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == Waypoint.DuranceOfHateLevel2.ToArea(), TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5));

            var path2 = await _pathingService.GetPathFromWaypointToArea(client.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel2, Waypoint.DuranceOfHateLevel2, Area.DuranceOfHateLevel3, MovementMode.Teleport);
            if (!TeleportViaPath(client, path2))
            {
                Log.Warning($"Teleporting to DuranceOfHateLevel3 warp failed at location {client.Game.Me.Location}");
            }

            var warp = client.Game.GetNearestWarp();
            if (warp == null || warp.Location.Distance(client.Game.Me.Location) > 20)
            {
                Log.Warning($"Warp not close enough at location {warp?.Location} while at location {client.Game.Me.Location}");
                return false;
            }

            Log.Information($"Taking warp to Durance 3");
            if (!GeneralHelpers.TryWithTimeout((_) => client.Game.TakeWarp(warp),
                TimeSpan.FromSeconds(2)))
            {
                Log.Warning($"Taking warp failed at location {client.Game.Me.Location} to warp at location {warp.Location}");
                return false;
            }

            var path3 = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel3, client.Game.Me.Location, new Point(17566, 8070), MovementMode.Teleport);
            Log.Information($"Teleporting to Mephisto");
            if (!TeleportViaPath(client, path3))
            {
                Log.Warning($"Teleporting to Mephisto failed at location {client.Game.Me.Location}");
            }

            if (!GeneralHelpers.TryWithTimeout((_) => client.Game.GetNPCsByCode(NPCCode.Mephisto).Count > 0, TimeSpan.FromSeconds(2)))
            {
                Log.Warning($"Finding Mephisto failed while at location {client.Game.Me.Location}");
                return false;
            }

            var mephisto = client.Game.GetNPCsByCode(NPCCode.Mephisto).Single();
            Log.Information($"Killing Mephisto");
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    if (!client.Game.IsInGame())
                    {
                        return true;
                    }

                    if (mephisto.Location.Distance(client.Game.Me.Location) < 30)
                    {
                        client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                    }

                    Thread.Sleep(200);
                    if (retryCount % 5 == 0)
                    {
                        client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, mephisto);
                    }

                    return mephisto.LifePercentage < 30;
                },
                TimeSpan.FromSeconds(30)))
            {
                Log.Warning($"Killing Mephisto failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, mephisto);

                if (!client.Game.IsInGame())
                {
                    return true;
                }

                return GeneralHelpers.TryWithTimeout((_) => mephisto.State == EntityState.Dead,
                    TimeSpan.FromSeconds(1));
            }, TimeSpan.FromSeconds(30)))
            {
                Log.Warning($"Killing Mephisto failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!PickupNearbyItems(client))
            {
                Log.Warning($"Failed to pickup items at location {client.Game.Me.Location}");
                return false;
            }

            return true;
        }

        private static bool TeleportViaPath(Client client, List<Point> path)
        {
            foreach (var point in path)
            {
                if (!GeneralHelpers.TryWithTimeout((retryCount) => client.Game.TeleportToLocation(point),
                    TimeSpan.FromSeconds(4)))
                {
                    return false;
                }

                if (!client.Game.IsInGame())
                {
                    return false;
                }
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


        private bool PickupNearbyItems(Client client)
        {
            var pickupItems = client.Game.Items.Where(i => i.Ground && Pickit.Pickit.ShouldPickupItem(i)).OrderBy(n => n.Location.Distance(client.Game.Me.Location));
            Log.Information($"Killed Mephisto, picking up {pickupItems.Count()} items ");
            foreach (var item in pickupItems)
            {
                if (item.Location.Distance(client.Game.Me.Location) > 30)
                {
                    Log.Warning($"Skipped {item} since it's at location {item.Location}, while player at {client.Game.Me.Location}");
                    continue;
                }

                if (!client.Game.IsInGame())
                {
                    return false;
                }

                InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                if (client.Game.Inventory.FindFreeSpace(item) == null)
                {
                    Log.Warning($"Skipped {item.GetFullDescription()} since inventory is full");
                    continue;
                }

                if (!GeneralHelpers.TryWithTimeout((retryCount =>
                {
                    if (client.Game.Me.Location.Distance(item.Location) >= 5)
                    {
                        if (!client.Game.TeleportToLocation(item.Location))
                        {
                            Log.Warning($"Teleporting to item {item.GetFullDescription()} at location {item.Location} from location {client.Game.Me.Location} failed");
                            return false;
                        }
                    }
                    else
                    {
                        client.Game.PickupItem(item);
                        Thread.Sleep(50);
                        if (client.Game.Inventory.FindItemById(item.Id) == null && !item.IsGold)
                        {
                            return false;
                        }
                    }

                    return true;
                }), TimeSpan.FromSeconds(3)))
                {
                    Log.Warning($"Picking up item {item.GetFullDescription()} at location {item.Location} from location {client.Game.Me.Location} failed");
                }
            }

            return true;
        }
    }
}
