using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
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

namespace ConsoleBot.Bots.Types.Pindle
{
    public class PindleBot : SingleClientBotBase, IBotInstance
    {
        private readonly IPathingService _pathingService;
        private readonly ITownManagementService _townManagementService;

        public PindleBot(
            IOptions<BotConfiguration> config,
            IOptions<PindleConfiguration> pindleConfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMuleService muleService,
            ITownManagementService townManagementService) : base(config.Value, pindleConfig.Value, externalMessagingClient, muleService)
        {
            _pathingService = pathingService;
            _townManagementService = townManagementService;
        }

        public string GetName()
        {
            return "pindle";
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
                throw new NotSupportedException("Only sorceress is supported on Pindle");
            }

            if (client.Game.Act == Act.Act5)
            {
                var sellNpc = NPCHelpers.GetSellNPC(client.Game.Act);
                GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    var uniqueNPC = NPCHelpers.GetUniqueNPC(client.Game, sellNpc);
                    return uniqueNPC != null;
                }, TimeSpan.FromSeconds(2));
            }

            var townManagementOptions = new TownManagementOptions(_accountConfig, Act.Act5);

            var townTaskResult = await _townManagementService.PerformTownTasks(client, townManagementOptions);
            if(townTaskResult.ShouldMule)
            {
                NeedsMule = true;
                return true;
            }
            else if(!townTaskResult.Succes)
            {
                return false;
            }

            Log.Information($"Has merc with id {client.Game.Me.MercId}");

            if (client.Game.Area != Area.Harrogath)
            {
                Log.Information("Taking Harrogath Waypoint");
                if (!await _townManagementService.TakeWaypoint(client, Waypoint.Harrogath))
                {
                    Log.Information("Taking Harrogath waypoint failed");
                    return false;
                }
            }

            var pathToPortal = await _pathingService.GetPathToLocation(client.Game, new Point(5117, 5119), MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPortal, MovementMode.Teleport))
            {
                Log.Error($"Teleporting to {EntityCode.RedTownPortal}  failed");
                return false;
            }

            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                if (client.Game.Area == Area.NihlathaksTemple)
                {
                    return true;
                }

                var pindlePortal = client.Game.GetEntityByCode(EntityCode.RedTownPortal).FirstOrDefault(t => t.TownPortalArea == Area.NihlathaksTemple);
                if (pindlePortal == null)
                {
                    return false;
                }
                await client.Game.MoveToAsync(pindlePortal);

                client.Game.InteractWithEntity(pindlePortal);
                if (!GeneralHelpers.TryWithTimeout((retryCount) =>
                {
                    return client.Game.Area == Area.NihlathaksTemple;
                }, TimeSpan.FromSeconds(0.2)))
                {
                    return false;
                }

                return true;

            }, TimeSpan.FromSeconds(15)))
            {
                Log.Error($"Moving to {Area.NihlathaksTemple} failed");
                return false;
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Thunderstorm) && client.Game.Me.HasSkill(Skill.ThunderStorm))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ThunderStorm, client.Game.Me.Location);
            }

            if (!client.Game.Me.Effects.ContainsKey(EntityEffect.Shiverarmor) && client.Game.Me.HasSkill(Skill.ShiverArmor))
            {
                client.Game.UseRightHandSkillOnLocation(Skill.ShiverArmor, client.Game.Me.Location);
            }

            var pindleLocation = client.Game.Me.HasSkill(Skill.Nova) ? new Point(10058, 13213) : new Point(10061, 13234);
            var pathToPindle = await _pathingService.GetPathToLocation(client.Game, pindleLocation, MovementMode.Teleport);
            if (!await MovementHelpers.TakePathOfLocations(client.Game, pathToPindle, MovementMode.Teleport))
            {
                Log.Error($"Teleporting to Pindle failed");
                return false;
            }

            var pindle = client.Game.GetNPCsByCode(NPCCode.UnholyCorpse).First(u => u.MonsterEnchantments.Contains(MonsterEnchantment.IsSuperUnique));
            Log.Information($"Killing Pindle");
            var lastTeleportTime = DateTime.MinValue;
            if (!await GeneralHelpers.TryWithTimeout(async (retryCount) =>
                {
                    if (!client.Game.IsInGame())
                    {
                        return true;
                    }

                    if (!client.Game.Me.HasSkill(Skill.Nova) && await MoveToAlternativeLocationIfNeeded(client, pindle, lastTeleportTime))
                    {
                        lastTeleportTime = DateTime.Now;
                    }
                    else
                    {
                        Thread.Sleep(200);
                        if (retryCount % 3 == 0 && client.Game.Me.HasSkill(Skill.FrozenOrb))
                        {
                            client.Game.UseRightHandSkillOnEntity(Skill.FrozenOrb, pindle);
                        }
                        else if (client.Game.Me.HasSkill(Skill.StaticField) && pindle.Location.Distance(client.Game.Me.Location) < 30 && client.Game.GetNPCsByCode(NPCCode.UnholyCorpse).All(u => u.LifePercentage > 60))
                        {
                            client.Game.RepeatRightHandSkillOnLocation(Skill.StaticField, client.Game.Me.Location);
                        }
                        else if (client.Game.Me.HasSkill(Skill.Nova) && pindle.Location.Distance(client.Game.Me.Location) < 10)
                        {
                            client.Game.RepeatRightHandSkillOnLocation(Skill.Nova, client.Game.Me.Location);
                        }
                    }

                    return pindle.State == EntityState.Dead;
                },
                TimeSpan.FromSeconds(25)))
            {
                Log.Warning($"Killing Pindle failed at location {client.Game.Me.Location}");
                return false;
            }

            if (!PickupNearbyItems(client))
            {
                Log.Warning($"Failed to pickup items at location {client.Game.Me.Location}");
                return false;
            }

            return true;
        }

        private static async Task<bool> MoveToAlternativeLocationIfNeeded(Client client, WorldObject pindle, DateTime lastTeleportTime)
        {
            if (DateTime.Now.Subtract(lastTeleportTime) > TimeSpan.FromSeconds(4))
            {
                if (client.Game.Me.Life / (double)client.Game.Me.MaxLife < 0.8 || pindle.Location.Distance(client.Game.Me.Location) < 10)
                {
                    var startPoint = new Point(10061, 13234);
                    var alternativePoint = new Point(10059, 13219);
                    if (startPoint.Distance(client.Game.Me.Location) < alternativePoint.Distance(client.Game.Me.Location))
                    {
                        Log.Information($"Moving to alternative location due to low life or close pindle");
                        return await client.Game.TeleportToLocationAsync(alternativePoint);
                    }
                    else
                    {
                        Log.Information($"Moving back to start location due to low life or close pindle");
                        return await client.Game.TeleportToLocationAsync(startPoint);
                    }
                }
            }

            return false;
        }

        private bool PickupNearbyItems(Client client)
        {
            var pickupItems = client.Game.Items.Values.Where(i => i.Ground && Pickit.Pickit.ShouldPickupItem(client.Game, i, true)).OrderBy(n => n.Location.Distance(client.Game.Me.Location)).ToList();
            var revCountInInventory = client.Game.Inventory.Items.Count(i => i.Name == ItemName.FullRejuvenationPotion || i.Name == ItemName.RejuvenationPotion);
            if (revCountInInventory < 5)
            {
                var revs = client.Game.Items.Values.Where(i => i.Ground && (i.Name == ItemName.FullRejuvenationPotion || i.Name == ItemName.RejuvenationPotion)).OrderBy(n => n.Location.Distance(client.Game.Me.Location));
                Log.Information($"Killed Pindle, picking up {revs.Count()} rev potions since only {revCountInInventory} in inventory");
                pickupItems.AddRange(revs);
            }
            Log.Information($"Killed Pindle, picking up {pickupItems.Count()} items ");
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
                        client.Game.TeleportToLocation(item.Location);
                        return false;
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
