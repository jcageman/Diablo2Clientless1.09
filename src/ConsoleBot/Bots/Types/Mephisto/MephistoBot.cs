using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Mephisto;

public class MephistoBot : SingleClientBotBase, IBotInstance
{
    private readonly IPathingService _pathingService;
    private readonly ITownManagementService _townManagementService;
    private readonly MephistoConfiguration _mephistoConfig;

    public MephistoBot(
        IOptions<BotConfiguration> config,
        IOptions<MephistoConfiguration> mephconfig,
        IExternalMessagingClient externalMessagingClient,
        IPathingService pathingService,
        IMuleService muleService,
        ITownManagementService townManagementService) : base(config.Value, mephconfig.Value, externalMessagingClient, muleService)
    {
        _pathingService = pathingService;
        _townManagementService = townManagementService;
        _mephistoConfig = mephconfig.Value;
    }

    public string GetName()
    {
        return "mephisto";
    }

    public async Task Run()
    {
        var client = new Client();
        _externalMessagingClient.RegisterClient(client);
        await CreateGameLoop(client);
    }

    protected override async Task<bool> RunSingleGame(Client client)
    {
        if (client.Game.Me.Class != CharacterClass.Sorceress)
        {
            throw new NotSupportedException("Only sorceress is supported on Mephisto");
        }

        var townManagementOptions = new TownManagementOptions(_accountConfig, Act.Act3);

        var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
        if (townTaskResult.ShouldMule)
        {
            NeedsMule = true;
            return true;
        }

        // Every other bot stops here. Without it a character that never reached Ormus - no tome, no
        // potions - walked into Durance anyway and the run failed later, somewhere that had nothing
        // to do with the shopping.
        if (!townTaskResult.Succes)
        {
            Log.Warning($"Town tasks failed for {client.Game.Me.Name}, taking a new game");
            return false;
        }

        Log.Information("Taking DuranceOfHateLevel2 Waypoint");
        if (!await _townManagementService.TakeWaypoint(client, Waypoint.DuranceOfHateLevel2))
        {
            Log.Information("Taking DuranceOfHateLevel2 waypoint failed");
            return false;
        }

        if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
        {
            client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
        }

        var path2 = await _pathingService.GetPathFromWaypointToArea(client.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel2, Waypoint.DuranceOfHateLevel2, Area.DuranceOfHateLevel3, MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, path2, MovementMode.Teleport))
        {
            Log.Warning($"Teleporting to DuranceOfHateLevel3 warp failed at location {client.Game.Me.Location}");
            return false;
        }

        var warp = client.Game.GetNearestWarp();
        if (warp == null || warp.Location.Distance(client.Game.Me.Location) > 20)
        {
            Log.Warning($"Warp not close enough at location {warp?.Location} while at location {client.Game.Me.Location}");
            return false;
        }

        Log.Information($"Taking warp to Durance 3");
        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (warp.Location.Distance(client.Game.Me.Location) > 5 && !await client.Game.TeleportToLocationAsync(warp.Location))
            {
                Log.Debug($"Teleport to {warp.Location} failing retrying at location: {client.Game.Me.Location}");
                return false;
            }
            else
            {
                await client.Game.MoveToAsync(warp.Location);
            }

            return client.Game.TakeWarp(warp) && client.Game.Area == Area.DuranceOfHateLevel3;
        }, TimeSpan.FromSeconds(4)))
        {
            Log.Warning($"Teleport failed at location: {client.Game.Me.Location}");
            return false;
        }

        if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            client.Game.RequestUpdate(client.Game.Me.Id);
            var isValidPoint = await _pathingService.IsNavigatablePointInArea(client.Game.MapId, Difficulty.Normal, Area.DuranceOfHateLevel3, client.Game.Me.Location);
            return isValidPoint;
        }, TimeSpan.FromSeconds(3.5)))
        {
            Log.Error("Checking whether moved to area failed");
            return false;
        }

        Log.Information($"Teleporting to Mephisto");
        var path3 = await _pathingService.GetPathToLocation(client.Game, new Point(17566, 8070), MovementMode.Teleport);
        if (!await MovementHelpers.TakePathOfLocations(client.Game, path3, MovementMode.Teleport))
        {
            Log.Warning($"Teleporting to Mephisto failed at location {client.Game.Me.Location}");
            return false;
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

                if (mephisto.Location.Distance(client.Game.Me.Location) < 30 && (!client.Game.ClientCharacter.IsExpansion || mephisto.LifePercentage > 50))
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }

                if(mephisto.Location.Distance(client.Game.Me.Location) > 20)
                {
                    var teleportLocation = client.Game.Me.Location.GetPointBeforePointInSameDirection(mephisto.Location, 15);
                    client.Game.TeleportToLocation(teleportLocation);
                }

                Thread.Sleep(200);
                if (retryCount % 5 == 0)
                {
                    client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, mephisto);
                }

                return mephisto.LifePercentage < 30;
            },
            TimeSpan.FromSeconds(50)))
        {
            Log.Warning($"Killing Mephisto failed at location {client.Game.Me.Location}");
            return false;
        }

        // Splits the kill for the KPI. The two halves are quite different - the first spams Static
        // Field down to 30% while the second only casts Frozen Orb - and until they are timed apart
        // there is no way to tell which of them the kill seconds are actually in.
        var staticDuringOrb = _mephistoConfig.StaticDuringOrbPhase;
        Log.Information($"Mephisto at {mephisto.LifePercentage:F0}% life, finishing with Frozen Orb{(staticDuringOrb ? " and Static Field" : "")}");
        if (!GeneralHelpers.TryWithTimeout((_) =>
        {
            client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, mephisto);

            if (!client.Game.IsInGame())
            {
                return true;
            }

            return GeneralHelpers.TryWithTimeout((waitCount) =>
            {
                // Once per wait, not once per poll: the poll runs every 20ms and this is a repeat
                // cast, so the server keeps casting on its own until the next orb changes the hand.
                if (staticDuringOrb && waitCount == 0 && mephisto.Location.Distance(client.Game.Me.Location) < 30)
                {
                    client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                }

                return mephisto.State == EntityState.Dead || mephisto.State == EntityState.Dieing;
            }, TimeSpan.FromSeconds(0.7));
        }, TimeSpan.FromSeconds(50)))
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

    private static bool PickupNearbyItems(Client client)
    {
        PickitAudit.LogGroundItems(client.Game, "Mephisto", shouldPickupGoldItems: true);
        var pickupItems = client.Game.Items.Values
            .Where(i => i.Ground && D2NG.Pickit.Pickit.ShouldPickupItem(client.Game, i, true))
            .OrderBy(n => n.Location.Distance(client.Game.Me.Location))
            .ToList();
        Log.Information($"Killed Mephisto, picking up {pickupItems.Count} items ");
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

            // Only cube when the item genuinely will not fit. Cubing is two server round trips plus
            // opening the cube, and it was paid for every item picked up even though Mephisto drops a
            // handful into an inventory with fifty free cells. The full-inventory fallback below is
            // unchanged, so nothing that used to be picked up can now be missed.
            if (client.Game.Inventory.FindFreeSpace(item) == null)
            {
                InventoryHelpers.MoveInventoryItemsToCube(client.Game);
                if (client.Game.Inventory.FindFreeSpace(item) == null)
                {
                    Log.Warning($"Skipped {item.GetFullDescription()} since inventory is full");
                    continue;
                }
            }

            if (!GeneralHelpers.TryWithTimeout((retryCount =>
            {
                if (client.Game.Me.Location.Distance(item.Location) >= 5)
                {
                    // Re-sent on every 20ms retry, deliberately. Throttling this to one send per
                    // 400ms was measured and made pickup slower at every item count (+0.27s to
                    // +1.03s, worse the more items dropped) while every other phase stayed flat.
                    // The repeats are what get the character onto the item, not waste.
                    client.Game.TeleportToLocation(item.Location);
                    client.Game.MoveTo(item.Location);
                    return false;
                }
                else
                {
                    client.Game.MoveTo(item.Location);
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

        // Closes the pickup phase for the KPI. Without it the seconds between the last item and the
        // next game - leaving, rejoining the MCP and the fixed delay in the game loop - were being
        // billed to pickup, which made the pickup loop look about three seconds worse than it is.
        Log.Information($"Picked up {pickupItems.Count} items, leaving game");
        return true;
    }
}
