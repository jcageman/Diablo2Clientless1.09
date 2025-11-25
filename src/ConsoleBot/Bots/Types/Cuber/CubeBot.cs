using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types.Cuber
{
    public class CubeBot : SingleClientBotBase, IBotInstance
    {
        private readonly CubeConfiguration _cubeConfig;
        private readonly IPathingService _pathingService;
        private readonly ITownManagementService _townManagementService;

        private TaskCompletionSource<bool> ItemDropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> NextGame = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CubeBot(
            IOptions<BotConfiguration> config,
            IOptions<CubeConfiguration> cubeConfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMuleService muleService,
            ITownManagementService townManagementService) : base(config.Value, cubeConfig.Value, externalMessagingClient, muleService)
        {
            _cubeConfig = cubeConfig.Value;
            _pathingService = pathingService;
            _townManagementService = townManagementService;
        }

        public string GetName()
        {
            return "cube";
        }

        public async Task Run()
        {
            var client = new Client();
            _externalMessagingClient.RegisterClient(client);
            client.Game.OnWorldItemEvent(i => HandleItemDrop(i));
            client.OnReceivedPacketEvent(InComingPacket.ReceiveChat, (packet) =>
            {
                var chatPacket = new ChatPacket(packet);
                if (chatPacket.Message == "ng")
                {
                    NextGame.TrySetResult(true);
                }
            });
            await CreateGameLoop(client);
        }

        protected override async Task<bool> RunSingleGame(Client client)
        {
            var townManagementOptions = new TownManagementOptions(_accountConfig, Act.Act1);

            if (client.Game.Act != Act.Act1)
            {
                if (!await _townManagementService.SwitchAct(client, Act.Act1))
                {
                    return false;
                }

                var townArea = WayPointHelpers.MapTownArea(client.Game.Act);
                var pathStash = await _pathingService.GetPathToObject(client.Game.MapId, Difficulty.Normal, townArea, client.Game.Me.Location, EntityCode.Stash, MovementMode.Walking);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, pathStash, MovementMode.Walking))
                {
                    Log.Warning($"{MovementMode.Walking} failed at location {client.Game.Me.Location}");
                    return false;
                }
            }

            while(!NextGame.Task.IsCompleted)
            {
                if (client.Game.Cube.Items.Any())
                {
                    if (!InventoryHelpers.MoveCubeItemsToInventory(client.Game))
                    {
                        Log.Warning($"Couldn't move all items out of cube");
                        continue;
                    }
                }

                foreach (var item in client.Game.Inventory.Items.Where(i =>
                                        IsCubeResultItem(i) &&
                                        Pickit.Pickit.ShouldKeepItem(client.Game, i) &&
                                        Pickit.Pickit.CanTouchInventoryItem(client.Game, i)))
                {
                    if (Pickit.Pickit.ShouldKeepItem(client.Game, item))
                    {
                        Log.Information($"{client.Game.Me.Name}: Dropping {item.GetFullDescription()}");

                        InventoryHelpers.DropItemFromInventory(client.Game, item);
                    }
                }

                var recipeItems = new List<Item>();
                foreach (var requirement in _cubeConfig.RecipeRequirements)
                {
                    var matchingItems = new List<Item>();
                    do
                    {
                        matchingItems = GetCubeRequirementInInventory(client, requirement);
                        if (matchingItems.Count < requirement.Amount)
                        {
                            var requiredItemsOnGround = GetRequiredItemOnGround(client, requirement).Take(requirement.Amount - matchingItems.Count).ToList();
                            foreach (var item in requiredItemsOnGround)
                            {
                                await PickupItemOnGround(client, item);

                            }
                            var missingItems = requirement.Amount - matchingItems.Count - requiredItemsOnGround.Count;
                            if (missingItems > 0)
                            {
                                var itemType = (requirement.ItemNames != null ? string.Join(",", requirement.ItemNames) : null);
                                itemType ??= requirement.Classification?.ToString();
                                itemType ??= requirement.Quality.ToString();
                                Log.Information($"Missing {missingItems} of {itemType}");
                                await ItemDropped.Task;
                                ItemDropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            }
                        } 
                    } while (matchingItems.Count < requirement.Amount);

                    recipeItems.AddRange(matchingItems.Take(requirement.Amount));
                }

                TransMuteItems(client, recipeItems);
            }

            return true;
        }

        private static bool TransMuteItems(Client client, List<Item> itemsToTransmute)
        {

            Log.Information($"{client.Game.Me.Name}: Moving items to cube");
            foreach (var item in itemsToTransmute)
            {
                var inventoryItem = client.Game.Inventory.FindItemById(item.Id);
                if (inventoryItem == null)
                {
                    Log.Warning($"Item {item.GetFullDescription()} to be transmuted not found in inventory");
                    return false;
                }
                var freeSpace = client.Game.Cube.FindFreeSpace(inventoryItem);
                if (freeSpace == null)
                {
                    Log.Warning($"Item {item.GetFullDescription()} no space for it in cube");
                    return false;
                }

                if (InventoryHelpers.PutInventoryItemInCube(client.Game, inventoryItem, freeSpace) != MoveItemResult.Succes)
                {
                    Log.Warning($"Item {item.GetFullDescription()} moving to cube failed");
                    return false;
                }
            }

            Log.Information($"{client.Game.Me.Name}: Transmuting items");
            if (!InventoryHelpers.TransmuteItemsInCube(client.Game, true))
            {
                Log.Error($"Transmuting failed");
                return false;
            }
            Log.Information($"{client.Game.Me.Name}: Transmuting items done");
            var newCubeItems = client.Game.Cube.Items;
            foreach (var item in newCubeItems)
            {
                if (Pickit.Pickit.ShouldKeepItem(client.Game, item))
                {
                    Log.Information($"{client.Game.Me.Name}: Want to keep {item.GetFullDescription()}");
                }
                else
                {
                    Log.Information($"{client.Game.Me.Name}: Not keeping {item.GetFullDescription()}");
                }

                if (InventoryHelpers.PutCubeItemInInventory(client.Game, item) != MoveItemResult.Succes)
                {
                    Log.Error($"Couldn't move transmuted items out of cube");
                    continue;
                }
            }

            return true;
        }

        private static List<Item> GetRequiredItemOnGround(Client client, RecipeRequirement requirement)
        {
            return client.Game.Items.Values.Where(i => MatchesRequiredItem(i, requirement) && i.Ground && IsItemForCubing(client, i)).ToList();
        }

        private static List<Item> GetCubeRequirementInInventory(Client client, RecipeRequirement requirement)
        {
            return client.Game.Inventory.Items.Where(i =>
            {
                return MatchesRequiredItem(i, requirement) &&
                IsItemForCubing(client, i) &&
                Pickit.Pickit.CanTouchInventoryItem(client.Game, i);
            }).ToList();
        }

        private static bool IsItemForCubing(Client client, Item item)
        {
            return !Pickit.Pickit.ShouldKeepItem(client.Game, item)
                || item.Classification == ClassificationType.Gem
                || (item.Name == ItemName.Ring && item.Quality == QualityType.Unique);
        }

        private async Task<bool> PickupItemOnGround(Client client, Item item)
        {
            if(item.Location.Distance(client.Game.Me.Location) > 10)
            {
                var path = await _pathingService.GetPathToLocation(client.Game, item.Location, MovementMode.Walking);
                if (!await MovementHelpers.TakePathOfLocations(client.Game, path.ToList(), MovementMode.Walking))
                {
                    return false;
                }
            }

            if (await client.Game.MoveToAsync(item))
            {
                client.Game.PickupItem(item);
                if (!GeneralHelpers.TryWithTimeout((retryCount =>
                {
                    if (client.Game.Inventory.FindItemById(item.Id) == null)
                    {
                        return false;
                    }

                    return true;
                }), TimeSpan.FromSeconds(0.5)))
                {
                    return false;
                }
            }

            return true;
        }

        private Task HandleItemDrop(Item item)
        {
            if (item.Ground)
            {
                ItemDropped.TrySetResult(true);
            }

            return Task.CompletedTask;
        }

        private static bool MatchesRequiredItem(Item item, RecipeRequirement requirement)
        {
            return (requirement.ItemNames == null || requirement.ItemNames.Contains(item.Name))
                && (requirement.Classification == null || requirement.Classification == item.Classification)
                && (requirement.Quality == null || requirement.Quality == item.Quality);
        }

        private bool IsCubeResultItem(Item item)
        {
            return (_cubeConfig.RecipeResult.ItemNames == null || _cubeConfig.RecipeResult.ItemNames.Contains(item.Name))
            && (_cubeConfig.RecipeResult.Classification == null || _cubeConfig.RecipeResult.Classification == item.Classification)
            && (_cubeConfig.RecipeResult.Quality == null || _cubeConfig.RecipeResult.Quality == item.Quality);
        }
    }
}
