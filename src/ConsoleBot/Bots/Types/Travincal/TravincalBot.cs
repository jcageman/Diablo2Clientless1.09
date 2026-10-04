using ConsoleBot.Attack;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using Attribute = D2NG.Core.D2GS.Players.Attribute;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Travincal;

public class TravincalBot : SingleClientBotBase, IBotInstance
{
    private readonly IPathingService _pathingService;
    private readonly ITownManagementService _townManagementService;
    private readonly IAttackService _attackService;
    private readonly IMapApiService _mapApiService;
    private readonly TravincalConfiguration _travConfig;

    public TravincalBot(IOptions<BotConfiguration> config, IOptions<TravincalConfiguration> travconfig, IExternalMessagingClient externalMessagingClient, IPathingService pathingService,
        IMuleService muleService, ITownManagementService townManagementService, IAttackService attackService, IMapApiService mapApiService)
    : base(config.Value, travconfig.Value, externalMessagingClient, muleService)
    {
        _travConfig = travconfig.Value;
        _pathingService = pathingService;
        _townManagementService = townManagementService;
        _attackService = attackService;
        _mapApiService = mapApiService;
    }

    public string GetName()
    {
        return "travincal";
    }

    public async Task Run()
    {
        if (_travConfig.IdentifyWithTome)
        {
            D2NG.Pickit.Pickit.ReserveInventoryItem(ItemName.TomeofIdentify);
        }

        var client = new Client();
        _externalMessagingClient.RegisterClient(client);
        await CreateGameLoop(client);
    }

    private static readonly TimeSpan GameDeadline = TimeSpan.FromMinutes(4);

    private int _itemsPickedUp;

    protected override Task<bool> RunSingleGame(Client client)
    {
        return GeneralHelpers.WithDeadline(() => RunTravincal(client), GameDeadline, "Travincal run");
    }

    private async Task<bool> RunTravincal(Client client)
    {
        _itemsPickedUp = 0;
        client.Game.MovementSpeedFactor = _travConfig.MovementSpeedFactor;
        if (client.Game.Me.Class != CharacterClass.Barbarian)
        {
            throw new NotSupportedException("Only barbarian is supported on travincal");
        }

        var townManagementOptions = new TownManagementOptions(_accountConfig, Act.Act4) { IdentifyWithTome = _travConfig.IdentifyWithTome };

        var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
        if (townTaskResult.ShouldMule)
        {
            NeedsMule = true;
            return true;
        }

        if (!townTaskResult.Succes)
        {
            Log.Warning($"Town tasks failed for {client.Game.Me.Name}, taking a new game");
            return false;
        }

        Log.Information("Taking travincal wp");
        if (!await _townManagementService.TakeWaypoint(client, Waypoint.Travincal))
        {
            Log.Information("Taking trav waypoint failed");
            return false;
        }

        Log.Information("Doing bo");
        if(!BarbBo(client.Game))
        {
            return false;
        }

        Log.Information("Walking to council members");
        
        var pathToCouncil = await _pathingService.GetPathToObjectWithOffset(client.Game, EntityCode.CompellingOrb, 23, 25, MovementMode.Walking);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToCouncil, MovementMode.Walking))
        {
            Log.Information($"Walking to councile members failed");
            return false;
        }

        Log.Information("Kill council members");
        if (!await KillCouncilMembers(client))
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

        if (_travConfig.IdentifyWithTome)
        {
            InventoryHelpers.SendIdentifyRequests(client.Game);
        }

        Log.Information("Moving to town");
        if (!await _townManagementService.TakeTownPortalToTown(client))
        {
            Log.Information("Move to town failed");
            return false;
        }

        townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
        if (townTaskResult.ShouldMule)
        {
            NeedsMule = true;
        }

        if (!townTaskResult.Succes)
        {
            Log.Warning($"Town tasks after the run failed for {client.Game.Me.Name}");
        }

        var onPerson = client.Game.Me.Attributes.GetValueOrDefault(Attribute.GoldOnPerson, 0);
        var inStash = client.Game.Me.Attributes.GetValueOrDefault(Attribute.GoldInStash, 0);
        Log.Information("Run yield: {Items} items picked up, gold {OnPerson} on person, {InStash} in stash, {Total} total",
            _itemsPickedUp, onPerson, inStash, onPerson + inStash);

        Log.Information("Successfully finished game");
        return true;
    }

    private async Task<bool> PickupNearbyItems(Game game, double distance)
    {
        PickitAudit.LogGroundItems(game, "Travincal", shouldPickupGoldItems: true);
        var pickupItems = game.Items.Values.Where(i =>
        {
            return i.Ground && game.Me.Location.Distance(i.Location) < distance && D2NG.Pickit.Pickit.ShouldPickupItem(game, i, true);
        }).OrderBy(n => game.Me.Location.Distance(n.Location));

        foreach (var item in pickupItems)
        {
            if (!game.IsInGame())
            {
                return false;
            }

            InventoryHelpers.MoveInventoryItemsToCube(game);

            if (!item.IsGold && game.Inventory.FindFreeSpace(item) == null)
            {
                continue;
            }

            await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (game.Me.Location.Distance(item.Location) >= 5)
                {
                    var pathNearest = await _pathingService.GetPathToLocation(game.MapId, Difficulty.Normal, Area.Travincal, game.Me.Location, item.Location, MovementMode.Walking);
                    await MovementHelpers.TakePathOfLocations(game, pathNearest, MovementMode.Walking);
                    return false;
                }

                return true;
            }, TimeSpan.FromSeconds(3));

            if (game.Me.Location.Distance(item.Location) < 5)
            {
                game.PickupItem(item);
                _itemsPickedUp++;
            }
        }

        InventoryHelpers.MoveInventoryItemsToCube(game);
        return true;
    }

    private async Task<bool> UseFindItemOnCouncilMembers(Game game)
    {
        var remaining = GetCouncilMembers(game)
            .Where(m => m.State == EntityState.Dead || m.State == EntityState.Dieing)
            .ToList();

        while (remaining.Count > 0)
        {
            if (!game.IsInGame())
            {
                return false;
            }

            var nearest = remaining.MinBy(m => game.Me.Location.Distance(m.Location));
            remaining.Remove(nearest);

            if (nearest.Effects.Contains(EntityEffect.CorpseNoDraw))
            {
                continue;
            }

            if (!await ClassHelpers.FindItemOnDeadEnemy(game, _pathingService, _mapApiService, nearest))
            {
                Log.Warning("Failed to do find item on corpse");
            }

            await PickupNearbyItems(game, 10);
        }

        return true;
    }

    private async Task<bool> KillCouncilMembers(Client client)
    {
        var startTime = DateTime.Now;
        List<WorldObject> aliveMembers;
        do
        {
            List<WorldObject> councilMembers = GetCouncilMembers(client.Game);
            aliveMembers = councilMembers
                .Where(n => n.State != EntityState.Dead && n.State != EntityState.Dieing)
                .OrderBy(n => client.Game.Me.Location.Distance(n.Location))
                .ToList();

            var nearest = aliveMembers.FirstOrDefault();
            if (nearest != null)
            {
                if (!client.Game.IsInGame())
                {
                    return false;
                }

                if (DateTime.Now.Subtract(startTime) > TimeSpan.FromMinutes(2))
                {
                    Log.Information("Passed maximum elapsed time for killing council members");
                    return false;
                }

                if (client.Game.Me.Location.Distance(nearest.Location) > 10)
                {
                    var nearestFindItemMember = councilMembers
                    .Where(n => n.State == EntityState.Dead && !n.Effects.Contains(EntityEffect.CorpseNoDraw))
                    .OrderBy(n => client.Game.Me.Location.Distance(n.Location))
                    .FirstOrDefault();
                    if (nearestFindItemMember != null && nearestFindItemMember.Location.Distance(client.Game.Me.Location) <= 10)
                    {
                        await ClassHelpers.FindItemOnDeadEnemy(client.Game, _pathingService, _mapApiService, nearestFindItemMember);
                    }

                    var pathNearest = await _pathingService.GetPathToLocation(client.Game.MapId, Difficulty.Normal, Area.Travincal, client.Game.Me.Location, nearest.Location, MovementMode.Walking);
                    pathNearest = pathNearest.SkipLast(2).ToList();
                    if (pathNearest.Count > 0 && !await MovementHelpers.TakePathOfLocations(client.Game, pathNearest, MovementMode.Walking))
                    {
                        Log.Warning($"Walking to Council Member from {client.Game.Me.Location} to {pathNearest.Last()} failed at {client.Game.Me.Location}");
                    }
                }

                await _attackService.AssistPlayer(client, client.Game.Me);
            }
        } while (aliveMembers.Count != 0);

        return true;
    }

    private static List<WorldObject> GetCouncilMembers(Game game)
    {
        var councilMembers = game.GetNPCsByCode(NPCCode.CouncilMember1);
        councilMembers.AddRange(game.GetNPCsByCode(NPCCode.CouncilMember2));
        return councilMembers;
    }

    private static bool BarbBo(Game game)
    {
        if(!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            game.UseRightHandSkillOnLocation(Skill.BattleCommand, game.Me.Location);
            Thread.Sleep(200);
            return game.Me.Effects.ContainsKey(EntityEffect.Battlecommand);
        }, TimeSpan.FromSeconds(3)))
        {
            Log.Warning("Battle command failed");
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            game.UseRightHandSkillOnLocation(Skill.BattleOrders, game.Me.Location);
            Thread.Sleep(200);
            return game.Me.Effects.ContainsKey(EntityEffect.BattleOrders);
        }, TimeSpan.FromSeconds(3)))
        {
            Log.Warning("Battle orders failed");
            return false;
        }

        if (!GeneralHelpers.TryWithTimeout((retryCount) =>
        {
            game.UseRightHandSkillOnLocation(Skill.Shout, game.Me.Location);
            Thread.Sleep(200);
            return game.Me.Effects.ContainsKey(EntityEffect.Shout);
        }, TimeSpan.FromSeconds(3)))
        {
            Log.Warning("Shout failed");
            return false;
        }

        // The buffs raise maximum life and mana without raising the current values on 1.09. The
        // chicken service sees that maximum change and drinks one potion to lift the bar, so there
        // is nothing to do here.
        return true;
    }
}
