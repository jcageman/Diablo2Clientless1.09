using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.TownManagement
{
    public class TownManagementService : ITownManagementService
    {
        private readonly IPathingService _pathingService;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private static int isAnyClientGambling = 0;

        public TownManagementService(IPathingService pathingService, IExternalMessagingClient externalMessagingClient)
        {
            _pathingService = pathingService;
            _externalMessagingClient = externalMessagingClient;
        }

        public async Task<bool> TakeWaypoint(Client client, Waypoint waypoint)
        {
            var movementMode = GetMovementMode(client.Game);
            var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTownWayPoint, movementMode))
            {
                Log.Information($"Walking to {client.Game.Act} waypoint failed");
                return false;
            }

            WorldObject townWaypoint = null;
            GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).Single();
                return townWaypoint != null;
            }, TimeSpan.FromSeconds(2));

            if (townWaypoint == null)
            {
                Log.Error("No waypoint found");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                while (client.Game.Me.Location.Distance(townWaypoint.Location) > 5)
                {
                    if(client.Game.Me.HasSkill(Skill.Teleport))
                    {
                        await client.Game.TeleportToLocationAsync(townWaypoint.Location);
                    }
                    else
                    {
                        await client.Game.MoveToAsync(townWaypoint);
                    }
                    
                }
                client.Game.TakeWaypoint(townWaypoint, waypoint);
                return GeneralHelpers.TryWithTimeout((retryCount) => client.Game.Area == waypoint.ToArea(), TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
                var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, waypoint.ToArea(), client.Game.Me.Location);
                return isValidPoint;
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Checking whether moved to area failed");
                return false;
            }

            return true;
        }

        public async Task<bool> TakeTownPortalToArea(Client client, Player player, Area area)
        {
            var portal = client.Game.GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(t => t.TownPortalArea == area && t.TownPortalOwnerId == player.Id);
            if(portal == null)
            {
                return false;
            }

            var movementMode = GetMovementMode(client.Game);
            var pathToPortal = await _pathingService.GetPathToLocation(client.Game, portal.Location, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPortal, movementMode))
            {
                Log.Information($"Moving to {portal.Location} failed");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }

                client.Game.InteractWithEntity(portal);
                return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(50);
                    return client.Game.Area == area;
                }, TimeSpan.FromSeconds(0.5));
            }, TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }

                await Task.Delay(100);

                return await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, area, client.Game.Me.Location);
            }, TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            return true;
        }

        public bool CreateTownPortal(Client client)
        {
            if (!GeneralHelpers.TryWithTimeout((retryCount) =>
            {
                return client.Game.CreateTownPortal();
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Failed to create town portal");
                return false;
            }

            return true;
        }

        public async Task<bool> TakeTownPortalToTown(Client client)
        {
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (!GeneralHelpers.TryWithTimeout((_) =>
                {
                    return client.Game.CreateTownPortal();
                }, TimeSpan.FromSeconds(1.0)))
                {
                    return false;
                }

                if (!await GeneralHelpers.TryWithTimeout(async (_) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.1));
                    return client.Game.GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(t => t.TownPortalOwnerId == client.Game.Me.Id) != null;
                }, TimeSpan.FromSeconds(0.5)))
                {
                    return false;
                }

                return true;
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Failed to create or find town portal");
                return false;
            }

            var townportal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            var townArea = client.Game.Act.MapTownArea();
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                client.Game.MoveTo(townportal);

                client.Game.InteractWithEntity(townportal);
                return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(50);
                    return client.Game.Area == townArea;
                }, TimeSpan.FromSeconds(1));
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Moving to town failed");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                client.Game.RequestUpdate(client.Game.Me.Id);
                var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, townArea, client.Game.Me.Location);
                return isValidPoint;
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error("Checking whether in town failed");
                return false;
            }

            return true;
        }

        public async Task<bool> PerformTownTasks(Client client, TownManagementOptions options)
        {
            var game = client.Game;
            var movementMode = GetMovementMode(game);
            game.CleanupCursorItem();
            InventoryHelpers.CleanupPotionsInBelt(game);

            if(client.Game.Me.Class == CharacterClass.Paladin && client.Game.Me.HasSkill(Skill.Vigor))
            {
                client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
            }

            if (!await GeneralHelpers.PickupCorpseIfExists(client, _pathingService))
            {
                Log.Error($"{client.Game.Me.Name} failed to pickup corpse");
                return false;
            }

            if (client.Game.Act != options.Act)
            {
                var targetTownArea = WayPointHelpers.MapTownArea(options.Act);
                var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTownWayPoint, movementMode))
                {
                    Log.Warning($"Teleporting to {client.Game.Act} waypoint failed");
                    return false;
                }

                var townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).Single();
                Log.Information($"Taking waypoint to {targetTownArea}");
                if (!GeneralHelpers.TryWithTimeout((_) =>
                {

                    client.Game.TakeWaypoint(townWaypoint, options.Act.MapTownWayPoint());
                    return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == targetTownArea, TimeSpan.FromSeconds(2));
                }, TimeSpan.FromSeconds(5)))
                {
                    Log.Information($"Moving to {options.Act} failed");
                    return false;
                }
            }

            var townArea = WayPointHelpers.MapTownArea(game.Act);

            if (!await IdentifyItems(game, movementMode))
            {
                return false;
            }

            if (!await RefreshAndSellItems(game, movementMode, options))
            {
                return false;
            }

            if (!await RepairItems(game, movementMode))
            {
                return false;
            }

            if(InventoryHelpers.HasAnyItemsToStash(client.Game))
            {
                var pathStash = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, townArea, game.Me.Location, EntityCode.Stash, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(game, pathStash, movementMode))
                {
                    Log.Warning($"{movementMode} failed at location {game.Me.Location}");
                }

                var stashItemsResult = InventoryHelpers.StashItemsToKeep(game, _externalMessagingClient);
                if (stashItemsResult != Enums.MoveItemResult.Succes)
                {
                    Log.Warning($"Stashing items failed with result {stashItemsResult}");
                }
            }

            if (CubeHelpers.AnyGemsToTransmuteInStash(client.Game))
            {
                var pathStash = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, townArea, game.Me.Location, EntityCode.Stash, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(game, pathStash, movementMode))
                {
                    Log.Warning($"{movementMode} failed at location {game.Me.Location}");
                }
                CubeHelpers.TransmuteGems(client.Game);
            }

            if (!await GambleItems(client, movementMode))
            {
                return false;
            }

            return true;
        }

        private static MovementMode GetMovementMode(Game game)
        {
            return game.Me.HasSkill(Skill.Teleport) ? MovementMode.Teleport : MovementMode.Walking;
        }

        private async Task<bool> IdentifyItems(Game game, MovementMode movementMode)
        {
            var unidentifiedItemCount = game.Inventory.Items.Count(i => !i.IsIdentified) +
        game.Cube.Items.Count(i => !i.IsIdentified);
            if (unidentifiedItemCount > 6)
            {
                Log.Information($"Visiting Deckard Cain with {unidentifiedItemCount} unidentified items");
                var deckhardCainCode = NPCHelpers.GetDeckardCainForAct(game.Act);

                var deckardCain = NPCHelpers.GetUniqueNPC(game, deckhardCainCode);
                var pathDeckardCain = new List<Point>();
                if (deckardCain != null)
                {
                    pathDeckardCain = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, deckardCain.Location, movementMode);
                }
                else
                {
                    pathDeckardCain = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, deckhardCainCode, movementMode);
                }

                if (!await MovementHelpers.TakePathOfLocations(game, pathDeckardCain, movementMode))
                {
                    Log.Warning($"Client {game.Me.Name} {movementMode} to deckard cain failed at {game.Me.Location}");
                    return false;
                }

                return NPCHelpers.IdentifyItemsAtDeckardCain(game);
            }

            return true;
        }

        private async Task<bool> RefreshAndSellItems(Game game, MovementMode movementMode, TownManagementOptions options)
        {
            var sellItemCount = game.Inventory.Items.Count(i => !Pickit.Pickit.ShouldKeepItem(game, i)) + game.Cube.Items.Count(i => !Pickit.Pickit.ShouldKeepItem(game, i));
            if (NPCHelpers.ShouldRefreshCharacterAtNPC(game) || sellItemCount > 5 || options.ItemsToBuy?.Count > 0)
            {
                var sellNpc = NPCHelpers.GetSellNPC(game.Act);
                Log.Information($"Client {game.Me.Name} moving to {sellNpc} for refresh and selling {sellItemCount} items");
                var pathSellNPC = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, sellNpc, movementMode);
                if (pathSellNPC.Count > 0 && await MovementHelpers.TakePathOfLocations(game, pathSellNPC, movementMode))
                {
                    var uniqueNPC = NPCHelpers.GetUniqueNPC(game, sellNpc);
                    if (uniqueNPC == null)
                    {
                        Log.Warning($"Client {game.Me.Name} Did not find {sellNpc} at {game.Me.Location}");
                        return false;
                    }

                    if (!NPCHelpers.SellItemsAndRefreshPotionsAtNPC(game, uniqueNPC, options.ItemsToBuy))
                    {
                        Log.Warning($"Client {game.Me.Name} Selling items and refreshing potions failed at {game.Me.Location}");
                        return false;
                    }
                }
                else
                {
                    Log.Warning($"Client {game.Me.Name} {movementMode} to {sellNpc} failed at {game.Me.Location}");
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> RepairItems(Game game, MovementMode movementMode)
        {
            if (NPCHelpers.ShouldGoToRepairNPC(game))
            {
                var repairNPC = NPCHelpers.GetRepairNPC(game.Act);
                Log.Information($"Client {game.Me.Name} moving to {repairNPC} for repair/arrows");
                var pathRepairNPC = repairNPC == NPCCode.Hratli ? await _pathingService.GetPathToObject(game, EntityCode.Hratli, movementMode)
                 : await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, repairNPC, movementMode);
                if (pathRepairNPC.Count > 0 && await MovementHelpers.TakePathOfLocations(game, pathRepairNPC, movementMode))
                {
                    var uniqueNPC = NPCHelpers.GetUniqueNPC(game, repairNPC);
                    if (uniqueNPC == null)
                    {
                        Log.Warning($"Client {game.Me.Name} Did not find {repairNPC} at {game.Me.Location}");
                        return false;
                    }

                    if (!NPCHelpers.RepairItemsAndBuyArrows(game, uniqueNPC))
                    {
                        Log.Warning($"Client {game.Me.Name} Selling items and refreshing potions to {repairNPC} failed at {game.Me.Location}");
                    }
                }
                else
                {
                    Log.Warning($"Client {game.Me.Name} {movementMode} to {repairNPC} failed at {game.Me.Location}");
                }
            }

            return true;
        }

        private async Task<bool> GambleItems(Client client, MovementMode movementMode)
        {
            bool shouldGamble = client.Game.Me.Attributes[D2NG.Core.D2GS.Players.Attribute.GoldInStash] > 7_000_000;
            if (shouldGamble && System.Threading.Interlocked.Exchange(ref isAnyClientGambling, 1) == 0)
            {
                var gambleNPC = NPCHelpers.GetGambleNPC(client.Game.Act);
                Log.Information($"Gambling items at {gambleNPC}");
                var pathToGamble = await _pathingService.GetPathToNPC(client.Game, gambleNPC, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToGamble, movementMode))
                {
                    Log.Warning($"{movementMode} to {gambleNPC} failed at {client.Game.Me.Location}");
                    return false;
                }

                var uniqueNPC = NPCHelpers.GetUniqueNPC(client.Game, gambleNPC);
                if (uniqueNPC == null)
                {
                    Log.Warning($"{gambleNPC} not found at {client.Game.Me.Location}");
                    return false;
                }

                NPCHelpers.GambleItems(client.Game, uniqueNPC);
                System.Threading.Interlocked.Exchange(ref isAnyClientGambling, 0);
            }

            return true;
        }
    }
}
