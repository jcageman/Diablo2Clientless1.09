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
        private readonly IPathingService _pathingService;
        private readonly ITownManagementService _townManagementService;

        private TaskCompletionSource<bool> ItemDropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> NextGame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        static HashSet<ItemName> PerfectGems = new HashSet<ItemName> { ItemName.PerfectAmethyst, ItemName.PerfectDiamond, ItemName.PerfectEmerald, ItemName.PerfectRuby, ItemName.PerfectSapphire, ItemName.PerfectSkull, ItemName.PerfectTopaz };

        public CubeBot(
            IOptions<BotConfiguration> config,
            IOptions<CubeConfiguration> cubeConfig,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMuleService muleService,
            ITownManagementService townManagementService) : base(config.Value, cubeConfig.Value, externalMessagingClient, muleService)
        {
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
            Log.Information("In game");
            client.Game.RequestUpdate(client.Game.Me.Id);
            if (!GeneralHelpers.TryWithTimeout(
                (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
                TimeSpan.FromSeconds(10)))
            {
                return false;
            }

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
                foreach(var item in client.Game.Inventory.Items.Where(i =>
                                        i.Name == ItemName.GrandCharm
                                        && Pickit.Pickit.ShouldKeepItem(client.Game, i)
                                        && Pickit.Pickit.CanTouchInventoryItem(client.Game, i)))
                {
                    if (Pickit.Pickit.ShouldKeepItem(client.Game, item))
                    {
                        Log.Information($"{client.Game.Me.Name}: Dropping {item.GetFullDescription()}");

                        InventoryHelpers.DropItemFromInventory(client.Game, item);
                    }
                }
                var grandCharms = GetGrandCharmsInInventory(client);
                if (grandCharms.Count <= 1)
                {
                    var grandCharmOnGround = GetGrandCharmsOnGround(client).FirstOrDefault();
                    if (grandCharmOnGround != null)
                    {
                        await PickupItemOnGround(client, grandCharmOnGround);
                        grandCharms = GetGrandCharmsInInventory(client);
                    }
                }

                var gems = GetGemsInInventory(client);
                if (gems.Count <= 3)
                {
                    var gemsOnGround = GetGemsOnGround(client).Take(3 - gems.Count).ToList();
                    foreach (var gem in gemsOnGround)
                    {
                        await PickupItemOnGround(client, gem);

                    }
                    gems = GetGemsInInventory(client);
                }

                if (grandCharms.Count < 1)
                {
                    ItemDropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Log.Information($"Missing 1 grandcharm");
                    await ItemDropped.Task;
                    continue;
                }

                if (gems.Count < 3)
                {
                    ItemDropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Log.Information($"Missing {3-gems.Count} perfect gems");
                    await ItemDropped.Task;
                    continue;
                }

                if (client.Game.Cube.Items.Any())
                {
                    if (!InventoryHelpers.MoveCubeItemsToInventory(client.Game))
                    {
                        Log.Warning($"Couldn't move all items out of cube");
                        continue;
                    }
                }

                var grandCharm = grandCharms.First();
                var itemsToTransmute = new List<Item>();
                itemsToTransmute.Add(grandCharms.First());
                itemsToTransmute.AddRange(gems.Take(3));
                TransMuteItems(client, itemsToTransmute);
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

        private static IEnumerable<Item> GetGemsOnGround(Client client)
        {
            return client.Game.Items.Values.Where(i => PerfectGems.Contains(i.Name) && i.Ground);
        }

        private static IEnumerable<Item> GetGrandCharmsOnGround(Client client)
        {
            return client.Game.Items.Values.Where(i => i.Name == ItemName.GrandCharm && i.Level >= 91 && i.Ground && !Pickit.Pickit.ShouldKeepItem(client.Game, i));
        }

        private static List<Item> GetGemsInInventory(Client client)
        {
            return client.Game.Inventory.Items.Where(i =>
                                PerfectGems.Contains(i.Name)
                                && Pickit.Pickit.CanTouchInventoryItem(client.Game, i)).ToList();
        }

        private static List<Item> GetGrandCharmsInInventory(Client client)
        {
            return client.Game.Inventory.Items.Where(i =>
                                        i.Name == ItemName.GrandCharm
                                        && !Pickit.Pickit.ShouldKeepItem(client.Game, i)
                                        && Pickit.Pickit.CanTouchInventoryItem(client.Game, i)).ToList();
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
    }
}
