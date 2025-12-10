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
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.TownManagement;

public class TownManagementService : ITownManagementService
{
    private readonly IPathingService _pathingService;
    private readonly IExternalMessagingClient _externalMessagingClient;
    private readonly ILogger<TownManagementService> _logger;
    private static int isAnyClientGambling;

    public TownManagementService(IPathingService pathingService, IExternalMessagingClient externalMessagingClient, ILogger<TownManagementService> logger)
    {
        _pathingService = pathingService;
        _externalMessagingClient = externalMessagingClient;
        _logger = logger;
    }

    public async Task<bool> TakeWaypoint(Client client, Waypoint waypoint)
    {
        var movementMode = GetMovementMode(client.Game);
        var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, movementMode);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTownWayPoint, movementMode))
        {
            _logger.LogDebug("Client {ClientName} {MovementMode} to {Act} waypoint failed", client.Game.Me.Name, movementMode, client.Game.Act);
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
            _logger.LogDebug("Client {ClientName} No waypoint found", client.Game.Me.Name);
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (client.Game.Me.HasSkill(Skill.Teleport))
            {
                await client.Game.TeleportToLocationAsync(townWaypoint.Location);
            }
            else
            {
                await client.Game.MoveToAsync(townWaypoint);
            }

            if (!client.Game.TakeWaypoint(townWaypoint, waypoint))
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
            _logger.LogError("Client {ClientName} Checking whether moved to area failed", client.Game.Me.Name);
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
                _logger.LogWarning("Client {ClientName} Moving to {PortalLocation} failed", client.Game.Me.Name, portal.Location);
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
            _logger.LogError("Client {ClientName} failed to create town portal", client.Game.Me.Name);
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
            _logger.LogError("Client {ClientName} Moving to town failed with area {Area}", client.Game.Me.Name, client.Game.Area);
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
            _logger.LogError("Client {ClientName} Checking whether in town failed", client.Game.Me.Name);
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
                _logger.LogWarning("Client {ClientName} taking townportal to {TownArea} failed", client.Game.Me.Name, WayPointHelpers.MapTownArea(client.Game.Act));
                return false;
            }
        }
        else
        {
            var movementMode = GetMovementMode(client.Game);
            var pathToTownWayPoint = await _pathingService.ToTownWayPoint(client.Game, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToTownWayPoint, movementMode))
            {
                _logger.LogDebug("Client {ClientName} moving to {Act} waypoint failed", client.Game.Me.Name, client.Game.Act);
                return false;
            }
        }

        var targetTownArea = WayPointHelpers.MapTownArea(act);
        var townWaypoint = client.Game.GetEntityByCode(client.Game.Act.MapTownWayPointCode()).Single();
        _logger.LogDebug("Client {ClientName} taking waypoint to {TargetTownArea}", client.Game.Me.Name, targetTownArea);
        if (!GeneralHelpers.TryWithTimeout((_) =>
        {
            if(!client.Game.TakeWaypoint(townWaypoint, act.MapTownWayPoint()))
            {
                return false;
            }
            return GeneralHelpers.TryWithTimeout((_) => client.Game.Area == targetTownArea, TimeSpan.FromSeconds(2));
        }, TimeSpan.FromSeconds(5)))
        {
            _logger.LogDebug("Client {ClientName} moving to {Act} failed", client.Game.Me.Name, act);
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
        InventoryHelpers.CleanupPotionsInBelt(game, options.AccountConfig);

        if(client.Game.Me.Class == CharacterClass.Paladin && client.Game.Me.HasSkill(Skill.Vigor))
        {
            client.Game.ChangeSkill(Skill.Vigor, Hand.Right);
        }

        if (!await GeneralHelpers.PickupCorpseIfExists(client, _pathingService))
        {
            _logger.LogError("{ClientName} failed to pickup corpse", client.Game.Me.Name);
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

        if (options.AccountConfig.ResurrectMerc && !await ResurrectMerc(game, movementMode))
        {
            return result;
        }

        if (InventoryHelpers.ShouldStashItems(client.Game))
        {
            var pathStash = await _pathingService.GetPathToObject(game.MapId, Difficulty.Normal, townArea, game.Me.Location, EntityCode.Stash, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(game, pathStash, movementMode))
            {
                _logger.LogWarning("Client {ClientName} {MovementMode} failed at location {Location}", client.Game.Me.Name, movementMode, game.Me.Location);
            }

            var stashItemsResult = InventoryHelpers.StashItemsToKeep(game, _externalMessagingClient);
            if (stashItemsResult != Enums.MoveItemResult.Succes)
            {
                _logger.LogWarning("Client {ClientName} Stashing items failed with result {Result}", client.Game.Me.Name, stashItemsResult);
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
                _logger.LogWarning("Client {ClientName} {MovementMode} failed at location {Location}", client.Game.Me.Name, movementMode, game.Me.Location);
            }
            CubeHelpers.TransmuteGems(client.Game, _logger);
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
            _logger.LogDebug("Client {ClientName} Visiting Deckard Cain with {UnidCount} unidentified items", game.Me.Name, unidentifiedItemCount);
            var deckhardCainCode = NPCHelpers.GetDeckardCainForAct(game.Act);

            var deckardCain = NPCHelpers.GetUniqueNPC(game, deckhardCainCode);
            var pathDeckardCain = new List<Point>();
            if (deckardCain != null)
            {
                pathDeckardCain = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, deckardCain.Location, movementMode);
            }
            else
            {
                _logger.LogDebug("Client {ClientName} {MovementMode} to deckard cain according to map {Location}", game.Me.Name, movementMode, game.Me.Location);
                pathDeckardCain = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, deckhardCainCode, movementMode);
            }

            if (!await MovementHelpers.TakePathOfLocations(game, pathDeckardCain, movementMode))
            {
                _logger.LogWarning("Client {ClientName} {MovementMode} to deckard cain failed at {Location}", game.Me.Name, movementMode, game.Me.Location);
                return false;
            }

            deckardCain = NPCHelpers.GetUniqueNPC(game, deckhardCainCode);
            if(deckardCain == null)
            {
                _logger.LogError("Client {ClientName} could not find deckard cain failed at {Location}", game.Me.Name, game.Me.Location);
                return false;
            }

            pathDeckardCain = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, deckardCain.Location, movementMode);
            if (!await MovementHelpers.TakePathOfLocations(game, pathDeckardCain, movementMode))
            {
                _logger.LogWarning("Client {ClientName} {MovementMode} to real deckard cain failed at {Location}", game.Me.Name, movementMode, game.Me.Location);
                return false;
            }

            return NPCHelpers.IdentifyItemsAtDeckardCain(game);
        }

        return true;
    }

    private async Task<bool> RefreshAndSellItems(Game game, MovementMode movementMode, TownManagementOptions options)
    {
        var sellItemCount = game.Inventory.Items.Count(i => Pickit.Pickit.CanTouchInventoryItem(game, i) && !Pickit.Pickit.ShouldKeepItem(game, i)) + game.Cube.Items.Count(i => !Pickit.Pickit.ShouldKeepItem(game, i));
        if (NPCHelpers.ShouldRefreshCharacterAtNPC(game, options)
            || sellItemCount > 5
            || options.ItemsToBuy?.Count > 0
            || options.HealthPotionsToBuy > 0
            || options.ManaPotionsToBuy > 0)
        {
            var sellNpc = NPCHelpers.GetSellNPC(game.Act);
            _logger.LogDebug("Client {ClientName} moving to {SellNpc} for refresh and selling {SellCount} items", game.Me.Name, sellNpc, sellItemCount);
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
                    _logger.LogWarning("Client {ClientName} Did not find {SellNpc} at {Location}", game.Me.Name, sellNpc, game.Me.Location);
                    return false;
                }

                if (!NPCHelpers.SellItemsAndRefreshPotionsAtNPC(game, uniqueNPC, options))
                {
                    _logger.LogWarning("Client {ClientName} Selling items and refreshing potions failed at {Location}", game.Me.Name, game.Me.Location);
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("Client {ClientName} {MovementMode} to {SellNpc} failed at {Location}", game.Me.Name, movementMode, sellNpc, game.Me.Location);
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
            _logger.LogDebug("Client {ClientName} moving to {MercNpc} for resurrecting merc", game.Me.Name, mercNpc);
            List<Point> pathRepairNPC = await _pathingService.GetPathToNPC(game.MapId, Difficulty.Normal, WayPointHelpers.MapTownArea(game.Act), game.Me.Location, mercNpc, movementMode);
            if (pathRepairNPC.Count > 0 && await MovementHelpers.TakePathOfLocations(game, pathRepairNPC, movementMode))
            {
                var uniqueNPC = NPCHelpers.GetUniqueNPC(game, mercNpc);
                if (uniqueNPC == null)
                {
                    _logger.LogDebug("Client {ClientName} Did not find {MercNpc} at {Location}", game.Me.Name, mercNpc, game.Me.Location);
                    return false;
                }

                if (!NPCHelpers.ResurrectMerc(game, uniqueNPC))
                {
                    _logger.LogDebug("Client {ClientName} Resurrecting merc at {MercNpc} failed at {Location}", game.Me.Name, mercNpc, game.Me.Location);
                }
            }
            else
            {
                _logger.LogDebug("Client {ClientName} {MovementMode} to {MercNpc} failed at {Location}", game.Me.Name, movementMode, mercNpc, game.Me.Location);
            }
        }

        return true;
    }

    private async Task<bool> RepairItems(Game game, MovementMode movementMode)
    {
        if (NPCHelpers.ShouldGoToRepairNPC(game))
        {
            var repairNPC = NPCHelpers.GetRepairNPC(game.Act);
            _logger.LogDebug("Client {ClientName} moving to {RepairNpc} for repair/arrows", game.Me.Name, repairNPC);
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
                    _logger.LogWarning("Client {ClientName} Did not find {RepairNpc} at {Location}", game.Me.Name, repairNPC, game.Me.Location);
                    return false;
                }

                if (!NPCHelpers.RepairItemsAndBuyArrows(game, uniqueNPC))
                {
                    _logger.LogWarning("Client {ClientName} Selling items and refreshing potions to {RepairNpc} failed at {Location}", game.Me.Name, repairNPC, game.Me.Location);
                }
            }
            else
            {
                _logger.LogWarning("Client {ClientName} {MovementMode} to {RepairRpc} failed at {Location}", game.Me.Name, movementMode, repairNPC, game.Me.Location);
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
            _logger.LogDebug("Client {ClientName} Gambling items at {GambleNpc}", client.Game.Me.Name, gambleNPC);
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
                _logger.LogDebug("Client {ClientName} {MovementMode} to {GambleNpc} failed at {Location}", client.Game.Me.Name, movementMode, gambleNPC, client.Game.Me.Location);
                return false;
            }

            var uniqueNPC = NPCHelpers.GetUniqueNPC(client.Game, gambleNPC);
            if (uniqueNPC == null)
            {
                _logger.LogWarning("Client {ClientName} {GambleNpc} not found at {Location}", client.Game.Me.Name, gambleNPC, client.Game.Me.Location);
                return false;
            }

            NPCHelpers.GambleItems(client.Game, uniqueNPC);
            System.Threading.Interlocked.Exchange(ref isAnyClientGambling, 0);
        }

        return true;
    }
}
