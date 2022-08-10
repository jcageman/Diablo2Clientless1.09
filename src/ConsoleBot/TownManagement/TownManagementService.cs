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
                Log.Information($"Client {client.Game.Me.Name} {movementMode} to {client.Game.Act} waypoint failed");
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
                Log.Error($"Client {client.Game.Me.Name} No waypoint found");
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
                if(!client.Game.TakeWaypoint(townWaypoint, waypoint))
                {
                    return false;
                }
                return GeneralHelpers.TryWithTimeout((retryCount) => {
                    if(retryCount % 5 == 0 && client.Game.Area != waypoint.ToArea())
                    {
                        client.Game.RequestUpdate(client.Game.Me.Id);
                    }
                    return client.Game.Area == waypoint.ToArea();
                }
                , TimeSpan.FromSeconds(2));
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
                Log.Error($"Client {client.Game.Me.Name} Checking whether moved to area failed");
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
            if(client.Game.Me.Location.Distance(portal.Location) > 10)
            {
                var pathToPortal = await _pathingService.GetPathToLocation(client.Game, portal.Location, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPortal, movementMode))
                {
                    Log.Warning($"Client {client.Game.Me.Name} Moving to {portal.Location} failed");
                    return false;
                }
            }

            var previousArea = client.Game.Area;
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                portal = client.Game.GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(t => t.TownPortalArea == area && t.TownPortalOwnerId == player.Id);
                if (portal == null)
                {
                    return false;
                }

                if(!await client.Game.MoveToAsync(portal))
                {
                    return false;
                }

                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }

                client.Game.InteractWithEntity(portal);
                return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(50);
                    return client.Game.Area != previousArea;
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
                else
                {
                    await Task.Delay(300);
                }

                return !await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, previousArea, client.Game.Me.Location);
            }, TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            return true;
        }

        public async Task<bool> CreateTownPortal(Client client)
        {
            if (!await GeneralHelpers.TryWithTimeout(async (_) =>
            {
                return await client.Game.CreateTownPortal();
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error($"Client {client.Game.Me.Name} failed to create town portal");
                return false;
            }

            return true;
        }

        public async Task<bool> TakeTownPortalToTown(Client client)
        {
            if (!await CreateTownPortal(client))
            {
                return false;
            }

            var previousArea = client.Game.Area;
            var townportal = client.Game.GetEntityByCode(EntityCode.TownPortal).First(t => t.TownPortalOwnerId == client.Game.Me.Id);
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if(!await client.Game.MoveToAsync(townportal))
                {
                    return false;
                }

                client.Game.InteractWithEntity(townportal);
                return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    await Task.Delay(50);
                    if(retryCount > 0 && retryCount % 5 == 0)
                    {
                        client.Game.RequestUpdate(client.Game.Me.Id);
                    }
                    
                    return client.Game.Area != previousArea;
                }, TimeSpan.FromSeconds(0.5));
            }, TimeSpan.FromSeconds(5.0)))
            {
                Log.Error($"Client {client.Game.Me.Name} Moving to town failed with area {client.Game.Area}");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (retryCount > 0 && retryCount % 5 == 0)
                {
                    client.Game.RequestUpdate(client.Game.Me.Id);
                }
                else
                {
                    await Task.Delay(100);
                }

                return !await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, previousArea, client.Game.Me.Location);
            }, TimeSpan.FromSeconds(3.5)))
            {
                Log.Error($"Client {client.Game.Me.Name} Checking whether in town failed");
                return false;
            }

            return true;
        }

        public async Task<bool> SwitchAct(Client client, Act act)
        {
            if(client.Game.Act == act)
            {
                return true;
            }

            if(!client.Game.IsInTown())
            {
                if(!await TakeTownPortalToTown(client))
                {
                    Log.Warning($"Client {client.Game.Me.Name} taking townportal to {WayPointHelpers.MapTownArea(client.Game.Act)} failed");
                    return false;
                }
            }
            else
            {
                var movementMode = GetMovementMode(client.Game);
                var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTownWayPoint, movementMode))
                {
                    Log.Warning($"Client {client.Game.Me.Name} moving to {client.Game.Act} waypoint failed");
                    return false;
                }
            }

            var targetTownArea = WayPointHelpers.MapTownArea(act);
            var townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).Single();
            Log.Information($"Client {client.Game.Me.Name} taking waypoint to {targetTownArea}");
            if (!GeneralHelpers.TryWithTimeout((_) =>
            {
                if(!client.Game.TakeWaypoint(townWaypoint, act.MapTownWayPoint()))
                {
                    return false;
                }
                return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == targetTownArea, TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5)))
            {
                Log.Information($"Client {client.Game.Me.Name} moving to {act} failed");
                return false;
            }

            return true;
        }

        public async Task<TownTaskResult> PerformTownTasks(Client client, TownManagementOptions options)
        {
            var result = new TownTaskResult { ShouldMule = false, Succes = false };
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
                return result;
            }

            if (client.Game.Act != options.Act)
            {
                if(!await SwitchAct(client, options.Act))
                {
                    return result;
                }
            }

            var townArea = WayPointHelpers.MapTownArea(game.Act);

            if (!await IdentifyItems(game, movementMode))
            {
                return result;
            }

            if (!await RefreshAndSellItems(game, movementMode, options))
            {
                return result;
            }

            if (!await RepairItems(game, movementMode))
            {
                return result;
            }

            if (options.ResurrectMerc && !await ResurrectMerc(game, movementMode))
            {
                return result;
            }

            if (InventoryHelpers.HasAnyItemsToStash(client.Game))
            {
                var pathStash = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, townArea, game.Me.Location, EntityCode.Stash, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(game, pathStash, movementMode))
                {
                    Log.Warning($"Client {client.Game.Me.Name} {movementMode} failed at location {game.Me.Location}");
                }

                var stashItemsResult = InventoryHelpers.StashItemsToKeep(game, _externalMessagingClient);
                if (stashItemsResult != Enums.MoveItemResult.Succes)
                {
                    Log.Warning($"Client {client.Game.Me.Name} Stashing items failed with result {stashItemsResult}");
                }

                if(stashItemsResult == Enums.MoveItemResult.NoSpace)
                {
                    result.ShouldMule = true;
                }
            }

            if (CubeHelpers.AnyGemsToTransmuteInStash(client.Game))
            {
                var pathStash = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, townArea, game.Me.Location, EntityCode.Stash, movementMode);
                if (!await MovementHelpers.TakePathOfLocations(game, pathStash, movementMode))
                {
                    Log.Warning($"Client {client.Game.Me.Name} {movementMode} failed at location {game.Me.Location}");
                }
                CubeHelpers.TransmuteGems(client.Game);
            }

            if (!await GambleItems(client, movementMode))
            {
                return result;
            }

            result.Succes = true;
            return result;
        }

        private static MovementMode GetMovementMode(Game game)
        {
            return game.Me.HasSkill(Skill.Teleport)
                && game.Me.MaxMana > 200
                && game.Me.Mana > 20
                ? MovementMode.Teleport : MovementMode.Walking;
        }

        private async Task<bool> IdentifyItems(Game game, MovementMode movementMode)
        {
            var unidentifiedItemCount = game.Inventory.Items.Count(i => !i.IsIdentified) +
        game.Cube.Items.Count(i => !i.IsIdentified);
            if (unidentifiedItemCount > 6)
            {
                Log.Information($"Client {game.Me.Name} Visiting Deckard Cain with {unidentifiedItemCount} unidentified items");
                var deckhardCainCode = NPCHelpers.GetDeckardCainForAct(game.Act);

                var deckardCain = NPCHelpers.GetUniqueNPC(game, deckhardCainCode);
                var pathDeckardCain = new List<Point>();
                if (deckardCain != null)
                {
                    pathDeckardCain = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, deckardCain.Location, movementMode);
                }
                else
                {
                    Log.Information($"Client {game.Me.Name} {movementMode} to deckard cain according to map {game.Me.Location}");
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
            var sellItemCount = game.Inventory.Items.Count(i => Pickit.Pickit.CanTouchInventoryItem(game, i) && !Pickit.Pickit.ShouldKeepItem(game, i)) + game.Cube.Items.Count(i => !Pickit.Pickit.ShouldKeepItem(game, i));
            if (NPCHelpers.ShouldRefreshCharacterAtNPC(game)
                || sellItemCount > 5
                || options.ItemsToBuy?.Count > 0
                || options.HealthPotionsToBuy > 0
                || options.ManaPotionsToBuy > 0)
            {
                var sellNpc = NPCHelpers.GetSellNPC(game.Act);
                Log.Information($"Client {game.Me.Name} moving to {sellNpc} for refresh and selling {sellItemCount} items");
                var uniqueNPC = NPCHelpers.GetUniqueNPC(game, sellNpc);
                List<Point> pathSellNPC;
                if (uniqueNPC != null)
                {
                    pathSellNPC = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, uniqueNPC.Location, movementMode);
                }
                else
                {
                    pathSellNPC = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, sellNpc, movementMode);
                }
                
                if (pathSellNPC.Count > 0 && await MovementHelpers.TakePathOfLocations(game, pathSellNPC, movementMode))
                {
                    uniqueNPC = NPCHelpers.GetUniqueNPC(game, sellNpc);
                    if (uniqueNPC == null)
                    {
                        Log.Warning($"Client {game.Me.Name} Did not find {sellNpc} at {game.Me.Location}");
                        return false;
                    }

                    if (!NPCHelpers.SellItemsAndRefreshPotionsAtNPC(game,
                                                                    uniqueNPC,
                                                                    options.ItemsToBuy,
                                                                    options.HealthPotionsToBuy,
                                                                    options.ManaPotionsToBuy))
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

        private async Task<bool> ResurrectMerc(Game game, MovementMode movementMode)
        {
            if (game.Me.MercId == null
                && game.ClientCharacter.IsExpansion
                && game.Me.Attributes.TryGetValue(D2NG.Core.D2GS.Players.Attribute.GoldInStash, out var goldInStash)
                && goldInStash > 50.000)
            {
                var mercNpc = NPCHelpers.GetMercNPCForAct(game.Act);
                Log.Information($"Client {game.Me.Name} moving to {mercNpc} for resurrecting merc");
                List<Point> pathRepairNPC = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, mercNpc, movementMode);
                if (pathRepairNPC.Count > 0 && await MovementHelpers.TakePathOfLocations(game, pathRepairNPC, movementMode))
                {
                    var uniqueNPC = NPCHelpers.GetUniqueNPC(game, mercNpc);
                    if (uniqueNPC == null)
                    {
                        Log.Warning($"Client {game.Me.Name} Did not find {mercNpc} at {game.Me.Location}");
                        return false;
                    }

                    if (!NPCHelpers.ResurrectMerc(game, uniqueNPC))
                    {
                        Log.Warning($"Client {game.Me.Name} Resurrecting merc at {mercNpc} failed at {game.Me.Location}");
                    }
                }
                else
                {
                    Log.Warning($"Client {game.Me.Name} {movementMode} to {mercNpc} failed at {game.Me.Location}");
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
                List<Point> pathRepairNPC;
                if(repairNPC == NPCCode.Hratli)
                {
                    pathRepairNPC = await _pathingService.GetPathToObject(game, EntityCode.Hratli, movementMode);
                }
                else if(repairNPC == NPCCode.Larzuk)
                {
                    pathRepairNPC = await _pathingService.GetPathToLocation(game, new Point(5143, 5038), movementMode);
                }
                else
                {
                    pathRepairNPC = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, repairNPC, movementMode);
                }  

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
            bool shouldGamble = client.Game.Me.Attributes.TryGetValue(D2NG.Core.D2GS.Players.Attribute.GoldInStash, out var goldInStash)
                && goldInStash > 7_000_000;
            if (shouldGamble && System.Threading.Interlocked.Exchange(ref isAnyClientGambling, 1) == 0)
            {
                var gambleNPC = NPCHelpers.GetGambleNPC(client.Game.Act);
                Log.Information($"Client {client.Game.Me.Name} Gambling items at {gambleNPC}");
                List<Point> pathToGamble;
                if (gambleNPC == NPCCode.Anya)
                {
                    pathToGamble = await _pathingService.GetPathToLocation(client.Game, new Point(5108, 5114), MovementMode.Teleport); 
                }
                else
                {
                    pathToGamble = await _pathingService.GetPathToNPC(client.Game, gambleNPC, movementMode);
                }
                
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToGamble, movementMode))
                {
                    Log.Warning($"Client {client.Game.Me.Name} {movementMode} to {gambleNPC} failed at {client.Game.Me.Location}");
                    return false;
                }

                var uniqueNPC = NPCHelpers.GetUniqueNPC(client.Game, gambleNPC);
                if (uniqueNPC == null)
                {
                    Log.Warning($"Client {client.Game.Me.Name} {gambleNPC} not found at {client.Game.Me.Location}");
                    return false;
                }

                NPCHelpers.GambleItems(client.Game, uniqueNPC);
                System.Threading.Interlocked.Exchange(ref isAnyClientGambling, 0);
            }

            return true;
        }
    }
}
