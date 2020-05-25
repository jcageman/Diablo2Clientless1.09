using D2NG;
using D2NG.BNCS.Packet;
using D2NG.D2GS;
using D2NG.D2GS.Act;
using D2NG.D2GS.Items;
using D2NG.D2GS.Objects;
using D2NG.D2GS.Packet;
using D2NG.MCP.Packet;
using McMaster.Extensions.CommandLineUtils;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;

namespace ConsoleBot
{
    class Program
    {
        static int Main(string[] args) => CommandLineApplication.Execute<Program>(args);

        [Option(Description = "Config File", LongName = "config", ShortName = "c")]
        public string ConfigFile { get; }

        private static readonly Client Client = new Client();

        private static TelegramBotClient TelegramClient;

        private static bool FullInventoryReported = false;

        private static Config Config;

        private static async Task SendTelegramMessage(string message)
        {
            await TelegramClient.SendTextMessageAsync(new Telegram.Bot.Types.ChatId(Config.TelegramChatId), message);
        }

        private async Task OnExecute()
        {
            File.Delete("log.txt");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File("log.txt")
                .CreateLogger();

            if (ConfigFile != null)
            {
                Config = Config.FromFile(this.ConfigFile);
            }

            TelegramClient = new TelegramBotClient(Config.TelegramApiKey);

            var me = await TelegramClient.GetMeAsync();

            var cts = new CancellationTokenSource();

            TelegramClient.OnMessage += OnTelegramMessageReceived;

            TelegramClient.StartReceiving(Array.Empty<UpdateType>(), cts.Token);
            Client.OnReceivedPacketEvent(Sid.CHATEVENT, HandleChatEvent);
            Client.OnReceivedPacketEvent(InComingPacket.ReceiveChat, HandleChatMessageEvent);

            try
            {
                if(!ConnectToRealm())
                {
                    throw new Exception("Could not connect to realm");
                }

                Client.Chat.EnterChat();

                Client.Chat.JoinChannel("taggert");
                Thread.Sleep(2_000);

                int count = 1;
                int failureCount = 0;
                while (count < 200 && (((double)failureCount / count) < 0.98 || count < 5))
                {
                    try
                    {
                        if(!Client.CreateGame(Difficulty.Hell, $"go{count}", "gtd", "gs2"))
                        {
                            count++;
                            Thread.Sleep(5000);
                            continue;
                        }

                        failureCount += (await MainGameLoop()) ? 0 : 1;
                        Thread.Sleep(1000);
                    }
                    catch (CreateGameException)
                    {
                        Thread.Sleep(2_000);
                    }
                    
                    if (Client.Game.IsInGame())
                    {
                        Client.Game.LeaveGame();
                    }
                    if (!Client.RejoinMCP())
                    {
                        Log.Warning("Disconnecting client, reconnecting to realm");
                        Client.Disconnect();
                        var connectCount = 0;
                        while (connectCount < 10 && !ConnectToRealm())
                        {
                            connectCount++;
                            Log.Warning($"Connecting to realm failed, doing re-attempt {connectCount} out of 10 in 30 seconds");
                            Thread.Sleep(30000);
                        };

                        if(connectCount >= 10)
                        {
                            throw new Exception("Reconnect tries of 10 reached, aborting");
                        }
                        Thread.Sleep(3000);
                    }
                    
                    count++;
                }

                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unhandled Exception: {e}");
                await SendTelegramMessage($"bot crashed with exception: {e}");
            }
            finally
            {
                TelegramClient.StopReceiving();
                cts.Cancel();
            }
        }

        private static bool ConnectToRealm()
        {
            var connect = Client.Connect(
                Config.Realm,
                Config.KeyOwner,
                Config.GameFolder);
            if(!connect)
            {
                return false;
            }
            var characters = Client.Login(Config.Username, Config.Password);

            Client.SelectCharacter(characters.Where(c => c.Name == Config.Character).First());
            return true;
        }

        private async Task<bool> MainGameLoop()
        {
            Log.Information("In game");
            Client.Game.RequestUpdate(Client.Game.Me.Id);
            while (Client.Game.Me.Location.X == 0 && Client.Game.Me.Location.Y == 0)
            {
                Thread.Sleep(10);
            }

            /*
            while (Client.Game.Players.Count < 2)
            {
                Thread.Sleep(10);
            }
            */

            CleanupInventoryAndCursor();

            if (Client.Game.Act != Act.Act3 && Client.Game.Act != Act.Act4)
            {
                Log.Information("Starting location is not Act 3 or 4, not supported for now");
                return false;
            }

            Log.Information("Walking to wp");
            if (Client.Game.Act == Act.Act3 && !WalkToAct3WpFromStart())
            {
                Log.Information("Walk to waypoint failed");
                return false;
            }

            if(Client.Game.Act == Act.Act3)
            {
                TransmutePerfectSkulls();
            }

            Act4RepairAndGamble();

            Log.Information("Taking travincal wp");
            if (!MoveToWaypointViaNearestWaypoint(Waypoint.Travincal))
            {
                Log.Information("Taking trav waypoint failed");
                return false;
            }

            Client.Game.RequestUpdate(Client.Game.Me.Id);

            Log.Information("Doing bo");
            BarbBo();

            var initialLocation = Client.Game.Me.Location;

            Log.Information("Walking to council members");
            if (!WalkToCouncilMembers(initialLocation))
            {
                Log.Information("Walk to council members failed");
                return false;
            }

            Log.Information("Kill council members");
            if (!KillCouncilMembers(initialLocation))
            {
                Log.Information("Kill council members failed");
                return false;
            }

            Log.Information("Using find item");
            if (!UseFindItemOnCouncilMembers(initialLocation))
            {
                Log.Information("Finditem failed");
                return false;
            }

            Log.Information("Picking up left over items");
            if (!PickupNearbyItems(initialLocation, 200))
            {
                Log.Information("Pickup nearby items 1 failed");
            }

            if (!PickupNearbyItems(initialLocation, 200))
            {
                Log.Information("Pickup nearby items 2 failed");
            }

            Log.Information("Moving to town");
            if (!MoveToA3Town())
            {
                Log.Information("Move to town failed");
                return false;
            }

            Log.Information("Identifying items");
            if (!IdentifyItemsAtDeckardCain())
            {
                Log.Information("Identify items failed");
                return false;
            }

            Log.Information("Stashing items to keep");
            if (!await StashItemsToKeep())
            {
                Log.Information("Stashing items failed");
                return false;
            }

            Log.Information("Walking to ormus");
            if (!WalkToOrmus())
            {
                Log.Information("Walking to ormus failed");
                return false;
            }

            Log.Information("Selling items at ormus");
            if (!SellItemsAndRefreshPotionsAtOrmus())
            {
                Log.Information("Selling items failed");
                return false;
            }

            Log.Information("Successfully finished game");
            return true;
        }

        private bool WalkToOrmus()
        {
            var points = new List<Point>()
                {
                    new Point(5151, 5068),
                    new Point(5147, 5085),
                    new Point(5138, 5096),
                    new Point(5126, 5094),
                };
            return WalkPathOfLocations(points);
        }

        private bool Act4RepairAndGamble()
        {
            bool shouldRepair = Client.Game.Items.Where(i => i.Action == D2NG.D2GS.Items.Action.Equip && i.MaximumDurability > 0 && ((double)i.Durability / i.MaximumDurability) < 0.2).Any();
            bool shouldGamble = Client.Game.Me.Attributes[D2NG.D2GS.Attribute.GoldInStash] > 7_000_000;
            if (!shouldRepair && !shouldGamble)
            {
                return true;
            }

            if(!MoveToA4Npcs())
            {
                return false;
            }

            if(shouldRepair)
            {
                var halbu = GetNPC(NPCCode.Halbu);
                if(halbu == null)
                {
                    return false;
                }
                RepairItemsAtNPC(halbu);
            }

            if (shouldGamble)
            {
                var jamella = GetNPC(NPCCode.JamellaAct4);
                if (jamella == null)
                {
                    return false;
                }
                
                GambleItemsAtNPC(jamella);
            }

            if(!MoveToA4Waypoint())
            {
                return false;
            }

            if (!MoveToWaypointViaNearestWaypoint(Waypoint.KurastDocks))
            {
                Log.Debug("Taking kurast docks waypoint failed");
                return false;
            }

            return true;
        }

        private WorldObject GetNPC(NPCCode npcCode)
        {
            return Client.Game.GetNPCsByCode(npcCode).Single();
        }

        private void RepairItemsAtNPC(WorldObject npc)
        {
            Log.Information($"Repairing items");
            TryWithTimeout((retryCount) =>
            {
                Client.Game.MoveTo(npc);
                if (Client.Game.Me.Location.Distance(npc.Location) < 5)
                {
                    return Client.Game.InteractWithNPC(npc);
                }
                return false;
            }, TimeSpan.FromSeconds(3));

            Thread.Sleep(50);
            Client.Game.InitiateEntityChat(npc);

            Client.Game.TownFolkAction(npc, TownFolkActionType.Trade);

            Client.Game.RepairItems(npc);

            Thread.Sleep(50);
            Client.Game.TerminateEntityChat(npc);
            Thread.Sleep(50);
        }

        private bool GambleItemsAtNPC(WorldObject npc)
        {
            Log.Information($"Gambling items");
            bool moveResult = TryWithTimeout((retryCount) =>
            {
                if (Client.Game.Me.Location.Distance(npc.Location) > 5)
                {
                    Client.Game.MoveTo(npc);
                    return false;
                }
                return true;
            }, TimeSpan.FromSeconds(3));

            if (!moveResult)
            {
                Log.Debug("Moving to npc for gamble failed");
                return false;
            }

            while (Client.Game.Me.Attributes[D2NG.D2GS.Attribute.GoldInStash] > 200000)
            {
                bool result = TryWithTimeout((retryCount) =>
                {
                    if (Client.Game.Me.Location.Distance(npc.Location) < 5)
                    {
                        return Client.Game.InteractWithNPC(npc);
                    }
                    else
                    {
                        Client.Game.MoveTo(npc);
                    }
                    return false;
                }, TimeSpan.FromSeconds(3));

                if (!result)
                {
                    Log.Debug("Interacting with npc for gamble failed");
                    break;
                }

                Thread.Sleep(50);
                Client.Game.InitiateEntityChat(npc);

                var oldItems = Client.Game.Items.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();

                Client.Game.TownFolkAction(npc, TownFolkActionType.Gamble);
                
                var itemsResult = TryWithTimeout((retryCount) =>
                {
                    var newItems = Client.Game.Items.Where(i => i.Container == ContainerType.ArmorTab).Select(i => i.Id).ToHashSet();
                    return newItems.Except(oldItems).Any();
                }, TimeSpan.FromSeconds(1));

                if (!itemsResult)
                {
                    Log.Debug("Waiting for items failed");
                    continue;
                }

                Thread.Sleep(10);
                Log.Debug("Trying to find gamble items and sell previous onces");

                var inventoryItemsToSell = Client.Game.Inventory.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(i)).ToList();
                foreach (Item item in inventoryItemsToSell)
                {
                    if(item.Quality == QualityType.Rare)
                    {
                        Log.Information($"Selling item {item.GetFullDescription()}");
                    }
                    
                    Client.Game.SellItem(npc, item);
                }

                var inventoryFull = false;

                foreach (var gambleItem in Client.Game.Items.Where(i => i.Container == ContainerType.ArmorTab && Pickit.Pickit.ShouldGamble(i)))
                {
                    if(Client.Game.Inventory.FindFreeSpace(gambleItem) == null)
                    {
                        Log.Information($"Inventory full, not gambling anymore");
                        inventoryFull = true;
                        break;
                    }

                    Log.Debug($"Gambling item {gambleItem.GetFullDescription()}");

                    var oldUnidentifiedItems = Client.Game.Inventory.Items.Where(i => !i.IsIdentified).ToHashSet();

                    Client.Game.GambleItem(npc, gambleItem);
                    var identifiedItems = new HashSet<uint>();
                    bool identifyResult = TryWithTimeout((retryCount) =>
                    {
                        var newUnidentifiedItems = Client.Game.Inventory.Items.Where(i => !i.IsIdentified).ToHashSet();
                        var deltaItems = newUnidentifiedItems.Except(oldUnidentifiedItems);
                        if (deltaItems.Count() > 0)
                        {
                            var gambledItem = deltaItems.First();
                            if(!identifiedItems.Contains(gambledItem.Id))
                            {
                                identifiedItems.Add(gambledItem.Id);
                                Client.Game.IdentifyGambleItem(gambledItem);
                            }
                        }

                        if(Client.Game.Inventory.Items.Any(i => i.IsIdentified && identifiedItems.Contains(i.Id)))
                        {
                            return true;
                        }

                        return false;
                    }, TimeSpan.FromSeconds(2));

                    if (!identifyResult)
                    {
                        Log.Debug($"Identify item {gambleItem.GetFullDescription()} for gamble failed");
                        break;
                    }
                }

                Thread.Sleep(50);
                Client.Game.TerminateEntityChat(npc);
                Thread.Sleep(50);

                if(inventoryFull)
                {
                    break;
                }
            }

            return true;
        }

        private bool MoveToA4Waypoint()
        {
            var points = new List<Point>()
                    {
                        new Point(5087, 5044),
                        new Point(5078, 5042),
                        new Point(5061, 5040),
                        new Point(5046, 5037),
                        new Point(5043, 5018),
                        
                    };

            return WalkPathOfLocations(points);
        }

        private bool MoveToA4Npcs()
        {
            if (!MoveToWaypointViaNearestWaypoint(Waypoint.ThePandemoniumFortress))
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

            return WalkPathOfLocations(points);
        }

        private bool SellItemsAndRefreshPotionsAtOrmus()
        {
            var ormus = GetNPC(NPCCode.Ormus);
            if (ormus == null)
            {
                return false;
            }

            TryWithTimeout((retryCount) =>
            {
                Client.Game.MoveTo(ormus);
                if (Client.Game.Me.Location.Distance(ormus.Location) < 5)
                {
                    return Client.Game.InteractWithNPC(ormus);
                }
                return false;
            }, TimeSpan.FromSeconds(3));

            Thread.Sleep(50);
            Client.Game.InitiateEntityChat(ormus);

            Client.Game.TownFolkAction(ormus, TownFolkActionType.Trade);

            Thread.Sleep(300);

            var healingPotion = Client.Game.Items.Where(i => i.Container == ContainerType.MiscTab && i.Type == "hp5").FirstOrDefault()
            ?? Client.Game.Items.Where(i => i.Container == ContainerType.MiscTab && i.Type.StartsWith("hp")).FirstOrDefault();
            var manaPotion = Client.Game.Items.Where(i => i.Container == ContainerType.MiscTab && i.Type == "mp5").FirstOrDefault()
            ?? Client.Game.Items.Where(i => i.Container == ContainerType.MiscTab && i.Type.StartsWith("mp")).FirstOrDefault();
            if(healingPotion == null || manaPotion == null)
            {
                Client.Game.TerminateEntityChat(ormus);
                return false;
            }

            var inventoryItemsToSell = Client.Game.Inventory.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(i) && Pickit.Pickit.CanTouchInventoryItem(i)).ToList();
            var cubeItemsToSell = Client.Game.Cube.Items.Where(i => !Pickit.Pickit.ShouldKeepItem(i)).ToList();
            Log.Information($"Selling {inventoryItemsToSell.Count} inventory items and {cubeItemsToSell.Count} cube items");

            foreach (Item item in inventoryItemsToSell)
            {
                Log.Information($"Selling inventory item {item.GetFullDescription()}");
                Client.Game.SellItem(ormus, item);
            }

            foreach (Item item in cubeItemsToSell)
            {
                Log.Information($"Selling cube item {item.GetFullDescription()}");
                Client.Game.SellItem(ormus, item);
            }

            var tomeOfTownPortal = Client.Game.Inventory.Items.FirstOrDefault(i => i.Name == "Tome of Town Portal");
            var scrollOfTownPortal = Client.Game.Items.FirstOrDefault(i => i.Container == ContainerType.MiscTab && i.Name == "Scroll of Town Portal");
            if (tomeOfTownPortal != null && scrollOfTownPortal != null && tomeOfTownPortal.Amount < 100)
            {
                Client.Game.BuyItem(ormus, scrollOfTownPortal, true);
            }

            var numberOfHealthPotions = Client.Game.Belt.NumOfHealthPotions();
            while (numberOfHealthPotions < 6)
            {
                Client.Game.BuyItem(ormus, healingPotion, false);
                numberOfHealthPotions += 1;
            }

            var numberOfManaPotions = Client.Game.Belt.NumOfManaPotions();
            while (numberOfManaPotions < 6)
            {
                Client.Game.BuyItem(ormus, manaPotion, false);
                numberOfManaPotions += 1;
            }

            Thread.Sleep(50);
            Client.Game.TerminateEntityChat(ormus);
            Thread.Sleep(50);
            Client.Game.TerminateEntityChat(ormus);
            return true;
        }

        private void CleanupInventoryAndCursor()
        {
            if (Client.Game.CursorItem != null)
            {
                var freeSpaceCube = Client.Game.Cube.FindFreeSpace(Client.Game.CursorItem);
                var freeSpaceInventory = Client.Game.Inventory.FindFreeSpace(Client.Game.CursorItem);
                if (freeSpaceCube != null)
                {
                    Client.Game.InsertItemIntoContainer(Client.Game.CursorItem, freeSpaceCube, ItemContainer.Cube);
                    Thread.Sleep(100);
                }
                else if (freeSpaceInventory != null)
                {
                    Client.Game.InsertItemIntoContainer(Client.Game.CursorItem, freeSpaceInventory, ItemContainer.Inventory);
                    Thread.Sleep(100);
                }
            }

            MoveInventoryItemsToCube();

            CleanupPotionsInBelt();
        }

        private void CleanupPotionsInBelt()
        {
            var manaPotionsInWrongSlot = Client.Game.Belt.GetManaPotionsInSlots(new List<int>() { 0, 1 });
            foreach (var manaPotion in manaPotionsInWrongSlot)
            {
                Client.Game.UseBeltItem(manaPotion);
            }

            var healthPotionsInWrongSlot = Client.Game.Belt.GetHealthPotionsInSlots(new List<int>() { 2, 3 });
            foreach (var healthPotion in healthPotionsInWrongSlot)
            {
                Client.Game.UseBeltItem(healthPotion);
            }
        }

        private bool IdentifyItemsAtDeckardCain()
        {
            if(!Client.Game.Inventory.Items.Where(i => !i.IsIdentified).Any()
                && !Client.Game.Cube.Items.Where(i => !i.IsIdentified).Any())
            {
                return true;
            }

            var result1 = TryWithTimeout((retryCount) =>
            {
                return Client.Game.GetNPCsByCode(NPCCode.DeckardCainAct3).Any();
            }, TimeSpan.FromSeconds(2));

            if(!result1)
            {
                return false;
            }

            var deckardCain = GetNPC(NPCCode.DeckardCainAct3);
            if (deckardCain == null)
            {
                return false;
            }

            Log.Information($"Identifying items at Cain");
            Thread.Sleep(500);
            var result2 = TryWithTimeout((retryCount) =>
            {
                Client.Game.MoveTo(deckardCain);
                if (Client.Game.Me.Location.Distance(deckardCain.Location) < 5)
                {
                    Thread.Sleep(100);
                    return Client.Game.InteractWithNPC(deckardCain);
                }
                return false;
            }, TimeSpan.FromSeconds(4));

            if(!result2)
            {
                Log.Error($"Failed to interact with Cain");
                return false;
            }
                
            Thread.Sleep(50);
            Client.Game.InitiateEntityChat(deckardCain);
            Thread.Sleep(50);
            Client.Game.IdentifyItems(deckardCain);
            Thread.Sleep(50);
            Client.Game.TerminateEntityChat(deckardCain);
            Thread.Sleep(50);
            return true;
        }

        private bool PickupNearbyItems(Point initialLocation, double distance)
        {
            var pickupItems = Client.Game.Items.Where(i =>
            {
                return i.Ground && PenalizedWalkingDistance(initialLocation, i.Location) < distance && Pickit.Pickit.ShouldPickupItem(i);
            }).OrderBy(n => PenalizedWalkingDistance(initialLocation, n.Location));

            foreach (var item in pickupItems)
            {
                if(!Client.Game.IsInGame())
                {
                    return false;
                }

                MoveInventoryItemsToCube();
                if (Client.Game.Inventory.FindFreeSpace(item) == null)
                {
                    continue;
                }

                if(!MoveToCorrectPlaceInTravBuilding(initialLocation, item.Location))
                {
                    return false;
                }

                TryWithTimeout((retryCount =>
                {
                    if (Client.Game.Me.Location.Distance(item.Location) >= 5)
                    {
                        Client.Game.MoveTo(item.Location);
                    }
                    
                    return Client.Game.Me.Location.Distance(item.Location) < 5;
                }), TimeSpan.FromSeconds(3));

                if (Client.Game.Me.Location.Distance(item.Location) < 5)
                {
                    Client.Game.PickupItem(item);
                }
            }

            MoveInventoryItemsToCube();
            return true;
        }

        public void MoveInventoryItemsToCube()
        {
            foreach(var item in Client.Game.Inventory.Items)
            {
                if(Pickit.Pickit.CanTouchInventoryItem(item))
                {
                    var freeSpace = Client.Game.Cube.FindFreeSpace(item);
                    if(freeSpace != null)
                    {
                        PutInventoryItemInCube(Client.Game, item, freeSpace);
                    }
                }
            }
        }

        public void PutInventoryItemInCube(Game game, Item item, Point point)
        {
            var cube = game.Inventory.FindItemByName("Horadric Cube");
            if (cube != null)
            {
                if(!game.ActivateBufferItem(cube))
                {
                    Log.Error($"Opening cube for {item.Id} - {item.Name} failed");
                    return;
                }

                game.RemoveItemFromContainer(item);

                bool resultToBuffer = TryWithTimeout((retryCount) =>
                {
                    return game.CursorItem?.Id == item.Id;
                }, TimeSpan.FromSeconds(1));

                if(!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                    return;
                }

                game.InsertItemIntoContainer(item, point, ItemContainer.Cube);

                bool resultMove = TryWithTimeout((retryCount) =>
                {
                    return Client.Game.CursorItem == null && Client.Game.Cube.FindItemById(item.Id) != null;
                }, TimeSpan.FromSeconds(1));

                if (!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to cube failed");
                }

                game.ClickButton(ClickType.CloseHoradricCube);
            }
        }

        public void PutCubeItemInInventory(Game game, Item item, Point point)
        {
            var cube = game.Inventory.FindItemByName("Horadric Cube");
            if (cube != null)
            {
                if (!game.ActivateBufferItem(cube))
                {
                    Log.Error($"Opening cube for {item.Id} - {item.Name} failed");
                    return;
                }

                game.RemoveItemFromContainer(item);

                bool resultToBuffer = TryWithTimeout((retryCount) =>
                {
                    return game.CursorItem?.Id == item.Id;
                }, TimeSpan.FromSeconds(1));

                if (!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                    return;
                }

                game.InsertItemIntoContainer(item, point, ItemContainer.Inventory);

                bool resultMove = TryWithTimeout((retryCount) =>
                {
                    return Client.Game.CursorItem == null && Client.Game.Inventory.FindItemById(item.Id) != null;
                }, TimeSpan.FromSeconds(1));

                if (!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to cube failed");
                }

                game.ClickButton(ClickType.CloseHoradricCube);
            }
        }

        public bool TransmuteItemsInCube(Game game)
        {
            var cube = game.Inventory.FindItemByName("Horadric Cube");
            if (cube != null)
            {
                if(!game.ActivateBufferItem(cube))
                {
                    return false;
                }
                var oldItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
                game.ClickButton(ClickType.TransmuteItems);

                bool transmuteResult = TryWithTimeout((retryCount) =>
                {
                    var newItems = game.Cube.Items.Select(i => i.Id).ToHashSet();
                    return !newItems.Intersect(oldItems).Any() && newItems.Count > 0;
                }, TimeSpan.FromSeconds(2));

                if (!transmuteResult)
                {
                    Log.Error($"Transmuting items failed");
                    return false;
                }

                game.ClickButton(ClickType.CloseHoradricCube);
                return true;
            }

            return false;
        }

        private bool UseFindItemOnCouncilMembers(Point initialLocation)
        {
            List<WorldObject> councilMembers = GetCouncilMembers();
            var nearestMembers = councilMembers.OrderBy(n => PenalizedWalkingDistance(initialLocation, n.Location));

            foreach (var nearestMember in nearestMembers)
            {
                PickupNearbyItems(initialLocation, 5);
                bool result = TryWithTimeout((retryCount) =>
                {
                    if (!Client.Game.IsInGame())
                    {
                        return false;
                    }

                    if (retryCount % 4 == 0)
                    {
                        Log.Debug($"Requesting update find item, since % 4th attempt");
                        Client.Game.RequestUpdate(Client.Game.Me.Id);
                    }

                    MoveToCorrectPlaceInTravBuilding(initialLocation, nearestMember.Location);
                    return TryWithTimeout((retryCount) =>
                    {
                        if (!Client.Game.IsInGame())
                        {
                            return false;
                        }

                        if (nearestMember.Location.Distance(Client.Game.Me.Location) > 5)
                        { 
                            Client.Game.MoveTo(nearestMember);
                        }

                        if (nearestMember.Location.Distance(Client.Game.Me.Location) <= 5)
                        {
                            Client.Game.UseRightHandSkillOnEntity(Skill.FindItem, nearestMember);
                            Thread.Sleep(500);
                            return true;
                        }

                        return false;

                    }, TimeSpan.FromSeconds(2));
                }, TimeSpan.FromSeconds(4));

                if (!Client.Game.IsInGame())
                {
                    return false;
                }
            }

            return true;
        }



        private bool KillCouncilMembers(Point initialLocation)
        {
            var startTime = DateTime.Now;
            var aliveMembers = new List<WorldObject>();
            do
            {
                List<WorldObject> councilMembers = GetCouncilMembers();
                aliveMembers = councilMembers
                    .Where(n => n.State != EntityState.Dead)
                    .OrderBy(n => PenalizedWalkingDistance(initialLocation, n.Location))
                    .ToList();

                var nearest = aliveMembers.FirstOrDefault();
                if (nearest != null)
                {
                    if (!Client.Game.IsInGame())
                    {
                        return false;
                    }

                    if(DateTime.Now.Subtract(startTime) > TimeSpan.FromMinutes(2))
                    {
                        Log.Debug("Passed maximum elapsed time for killing council members");
                        return false;
                    }
                    
                    if(!MoveToCorrectPlaceInTravBuilding(initialLocation, nearest.Location))
                    {
                        Log.Information("Couldn't move to right location in trav building");
                        continue;
                    }

                    var distanceToNearest = nearest.Location.Distance(Client.Game.Me.Location);
                    if (nearest.Location.Distance(Client.Game.Me.Location) > 15)
                    {
                        Client.Game.MoveTo(nearest);
                    }
                    else
                    {
                        
                        var wwDirection = Client.Game.Me.Location.GetPointPastPointInSameDirection(nearest.Location, 6);
                        if(Client.Game.Me.Location.Equals(nearest.Location))
                        {
                            if(Client.Game.Me.Location.X - initialLocation.X > 100)
                            {
                                //Log.Information($"same location, wwing to left");
                                wwDirection = new Point((ushort)(Client.Game.Me.Location.X - 6), Client.Game.Me.Location.Y);
                            }
                            else
                            {
                                //Log.Information($"same location, wwing to right");
                                wwDirection = new Point((ushort)(Client.Game.Me.Location.X + 6), Client.Game.Me.Location.Y);
                            }
                        }
                        //Log.Information($"player loc: {Client.Game.Me.Location}, nearest: {nearest.Location} ww destination: {wwDirection}  ");
                        Client.Game.RepeatRightHandSkillOnLocation(Skill.Whirlwind, wwDirection);
                        Thread.Sleep((int)(distanceToNearest * 80 + 400));
                    }
                }
            } while (aliveMembers.Any());

            return true;
        }

        private double PenalizedWalkingDistance(Point initialLocation, Point location)
        {
            var distance = location.Distance(Client.Game.Me.Location);
            var MeDeltaY = Client.Game.Me.Location.Y - initialLocation.Y;
            var NearestDeltaY = Client.Game.Me.Location.Y - initialLocation.Y;
            var MeDeltaX = Client.Game.Me.Location.X - initialLocation.X;
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

        private bool MoveToCorrectPlaceInTravBuilding(Point initialLocation, Point targetLocation)
        {
            return TryWithTimeout((retryCount) =>
            {
                if (!Client.Game.IsInGame())
                {
                    return false;
                }

                var MeDeltaY = Client.Game.Me.Location.Y - initialLocation.Y;
                var NearestDeltaY = targetLocation.Y - initialLocation.Y;
                Point insideBuilding = new Point((ushort)(initialLocation.X + 100), (ushort)(initialLocation.Y - 85));
                Point outsideBuilding = new Point((ushort)(initialLocation.X + 100), (ushort)(initialLocation.Y - 75));
                if (MeDeltaY < -78 && NearestDeltaY > -80)
                {
                    //Log.Information($"Moving outside building");
                    Client.Game.MoveTo(insideBuilding);
                    Client.Game.MoveTo(outsideBuilding);
                    return Client.Game.Me.Location.Distance(outsideBuilding) < 5;
                }
                else if (MeDeltaY > -80 && NearestDeltaY < -78)
                {
                    //Log.Information($"Moving inside building");
                    Client.Game.MoveTo(outsideBuilding);
                    Client.Game.MoveTo(insideBuilding);
                    return Client.Game.Me.Location.Distance(insideBuilding) < 5;
                }

                return true;

            }, TimeSpan.FromSeconds(4));
        }

        private List<WorldObject> GetCouncilMembers()
        {
            var councilMembers = Client.Game.GetNPCsByCode(NPCCode.CouncilMember1);
            councilMembers.AddRange(Client.Game.GetNPCsByCode(NPCCode.CouncilMember2));
            return councilMembers;
        }

        private bool MoveToA3Town()
        {
            var existingTownPortals = Client.Game.GetEntityByCode(EntityCode.TownPortal).ToHashSet();
            Client.Game.CreateTownPortal();
            var newTownPortals = Client.Game.GetEntityByCode(EntityCode.TownPortal).Where(t => !existingTownPortals.Contains(t)).ToList();
            if(!newTownPortals.Any())
            {
                return false;
            }

            var townportal = newTownPortals.First();
            return TryWithTimeout((retryCount) =>
            {
                Client.Game.MoveTo(townportal);

                Client.Game.InteractWithObject(townportal);
                return TryWithTimeout((retryCount) =>
                {
                    return Client.Game.Area == Area.KurastDocks;
                }, TimeSpan.FromSeconds(1));
            }, TimeSpan.FromSeconds(3.5));
        }

        private void BarbBo()
        {
            Client.Game.UseRightHandSkillOnLocation(Skill.BattleCommand, Client.Game.Me.Location);
            Thread.Sleep(500);
            Client.Game.UseRightHandSkillOnLocation(Skill.BattleOrders, Client.Game.Me.Location);
            Thread.Sleep(500);
            Client.Game.UseRightHandSkillOnLocation(Skill.Shout, Client.Game.Me.Location);
            Client.Game.UseHealthPotion();
            Thread.Sleep(300);
        }

        private bool WalkToCouncilMembers(Point initialLocation)
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
            return WalkPathOfLocations(travpoints);
        }

        private bool WalkToAct3WpFromStart()
        {
            var points = new List<Point>()
                    {
                        new Point(5131, 5163),
                        new Point(5133, 5145),
                        new Point(5133, 5125),
                        new Point(5132, 5106),
                        new Point(5133, 5092),
                    };

            var result = WalkPathOfLocations(points);
            if(!result)
            {
                return false;
            }
            
            var healingPotionsInBelt = Client.Game.Belt.NumOfHealthPotions();
            var manaPotionsInBelt = Client.Game.Belt.NumOfManaPotions();
            if(healingPotionsInBelt < 6
                || manaPotionsInBelt < 6
                || Client.Game.Inventory.Items.FirstOrDefault(i => i.Name == "Tome of Town Portal")?.Amount < 5
                || Client.Game.Me.Life < 500)
            {
                if(!SellItemsAndRefreshPotionsAtOrmus())
                {
                    return false;
                }

                WalkPathOfLocations(new List<Point> { new Point(5138, 5096) });
            }

            var points2 = new List<Point>()
                    {
                        new Point(5148, 5090),
                        new Point(5149, 5087),
                        new Point(5154, 5072),
                        new Point(5159, 5059)
                    };
            return WalkPathOfLocations(points2);
        }

        private bool WalkPathOfLocations(List<Point> points)
        {
            foreach (var point in points)
            {
                var result = TryWithTimeout((retryCount) => {
                    if (retryCount > 0)
                    {
                        Log.Debug($"Retrying");
                        Client.Game.RequestUpdate(Client.Game.Me.Id);
                    }
                    
                    if(retryCount > 1 && !Client.Game.IsInTown() && Client.Game.Me.Class == CharacterClass.Barbarian && Client.Game.Me.Skills.ContainsKey(Skill.Whirlwind))
                    {
                        Log.Debug($"Seems stuck, whirlwinding to point {point}");
                        Client.Game.UseRightHandSkillOnLocation(Skill.Whirlwind, point);
                        Thread.Sleep((int)(Client.Game.Me.Location.Distance(point) * 80 + 400));
                    }
                    else
                    {
                        Log.Debug($"Running to point {point}");
                        Client.Game.MoveTo(point);
                    }
                    
                    return Client.Game.Me.Location.Distance(point) < 10;
                }, TimeSpan.FromSeconds(4));

                if(!result)
                {
                    return false;
                }
            }

            return true;
        }

        private bool MoveToWaypointViaNearestWaypoint(Waypoint waypoint)
        {
            WorldObject nearestWaypoint = null;
            TryWithTimeout((retryCount) =>
            {
                nearestWaypoint = GetNearestWaypoint();
                return nearestWaypoint != null;
            }, TimeSpan.FromSeconds(2));

            if(nearestWaypoint == null)
            {
                Log.Error("No waypoint found");
                return false;
            }

            Log.Debug("Walking to waypoint");
            return TryWithTimeout((retryCount) =>
            {
                while (Client.Game.Me.Location.Distance(nearestWaypoint.Location) > 5)
                {
                    Client.Game.MoveTo(nearestWaypoint);
                }
                Log.Debug("Taking waypoint");
                Client.Game.TakeWaypoint(nearestWaypoint, waypoint);
                return TryWithTimeout((retryCount) => Client.Game.Area == waypoint.ToArea(), TimeSpan.FromSeconds(2));
            }, TimeSpan.FromSeconds(5));
        }
        private WorldObject GetNearestWaypoint()
        {
            var waypoints = new List<WorldObject>();
            foreach (var waypointEntityCode in EntityConstants.WayPointEntityCodes)
            {
                waypoints.AddRange(Client.Game.GetEntityByCode(waypointEntityCode));
            }

            return waypoints.SingleOrDefault();
        }

        private async Task<bool> StashItemsToKeep()
        {
            var inventoryItemsToKeep = Client.Game.Inventory.Items.Where(i => Pickit.Pickit.ShouldKeepItem(i) && Pickit.Pickit.CanTouchInventoryItem(i)).ToList();
            var cubeItemsToKeep = Client.Game.Cube.Items.Where(i => Pickit.Pickit.ShouldKeepItem(i)).ToList();
            var goldOnPerson = Client.Game.Me.Attributes.GetValueOrDefault(D2NG.D2GS.Attribute.GoldOnPerson, 0);
            if (inventoryItemsToKeep.Count == 0 && cubeItemsToKeep.Count == 0 && goldOnPerson < 200000)
            {
                return true;
            }

            Log.Information($"Stashing {inventoryItemsToKeep.Count + cubeItemsToKeep.Count } items and {goldOnPerson} gold");

            var stashes = Client.Game.GetEntityByCode(EntityCode.Stash);
            if (!stashes.Any())
            {
                Log.Error($"No stash found");
                return false;
            }

            var stash = stashes.Single();

            bool result = TryWithTimeout((retryCount) =>
            {
                if(Client.Game.Me.Location.Distance(stash.Location) >= 5)
                {
                    Client.Game.MoveTo(stash);
                }
                else
                {
                    return Client.Game.OpenStash(stash);
                }
                
                return false;
            }, TimeSpan.FromSeconds(4));

            if(!result)
            {
                Log.Error($"Failed to open stash");
                return false;
            }

            if (goldOnPerson > 0)
            {
                Client.Game.MoveGoldToStash(goldOnPerson);
            }

            Thread.Sleep(100);
            foreach (Item item in inventoryItemsToKeep)
            {
                Log.Information($"Want to keep {item.GetFullDescription()}");
                if(!FullInventoryReported && Client.Game.Stash.FindFreeSpace(item) == null)
                {
                    
                    await SendTelegramMessage($"bot inventory is full");
                    FullInventoryReported = true;
                    break;
                }

                if (!MoveItemToStash(item))
                {
                    return false;
                };
            }

            foreach (Item item in cubeItemsToKeep)
            {
                if (!FullInventoryReported && Client.Game.Stash.FindFreeSpace(item) == null)
                {

                    await SendTelegramMessage($"bot inventory is full");
                    FullInventoryReported = true;
                    break;
                }

                Log.Information($"Want to keep {item.GetFullDescription()}");
                if (!MoveItemToStash(item))
                {
                    return false;
                };
            }

            Thread.Sleep(300);
            Client.Game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            Client.Game.ClickButton(ClickType.CloseStash);
            return true;
        }

        private void TransmutePerfectSkulls()
        {
            var flawlessSkulls = Client.Game.Stash.Items.Where(i => i.Name == "Flawless Skull").ToList();
            if (flawlessSkulls.Count < 3)
            {
                return;
            }

            if(Client.Game.Cube.Items.Any())
            {
                return;
            }

            var stashes = Client.Game.GetEntityByCode(EntityCode.Stash);
            if (!stashes.Any())
            {
                return;
            }

            var stash = stashes.Single();


            bool result = TryWithTimeout((retryCount) =>
            {
                if (Client.Game.Me.Location.Distance(stash.Location) >= 5)
                {
                    Client.Game.MoveTo(stash);
                }
                else
                {
                    return Client.Game.OpenStash(stash);
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
                if(!MoveItemFromStashToInventory(skull))
                {
                    break;
                }
            }

            Thread.Sleep(300);
            Client.Game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            Client.Game.ClickButton(ClickType.CloseStash);


            Log.Information($"Moved skulls to inventory for transmuting");

            var remainingSkulls = flawlessSkulls.AsEnumerable();
            while(remainingSkulls.Count() > 2)
            {
                Log.Information($"Transmuting 3 flawless skulls to perfect skull");
                var skullsToTransmute = remainingSkulls.Take(3);
                remainingSkulls = remainingSkulls.Skip(3);
                foreach (var skull in skullsToTransmute)
                {
                    var inventoryItem = Client.Game.Inventory.FindItemById(skull.Id);
                    if(inventoryItem == null)
                    {
                        Log.Error($"Skull to be transmuted not found in inventory");
                        return;
                    }
                    var freeSpace = Client.Game.Cube.FindFreeSpace(inventoryItem);
                    if (freeSpace != null)
                    {
                        PutInventoryItemInCube(Client.Game, inventoryItem, freeSpace);
                    }
                }

                if(!TransmuteItemsInCube(Client.Game))
                {
                    Log.Error($"Transmuting items failed");
                    return;
                }

                var newCubeItems = Client.Game.Cube.Items;
                foreach (var item in newCubeItems)
                {
                    var freeSpace = Client.Game.Inventory.FindFreeSpace(item);
                    if (freeSpace != null)
                    {
                        PutCubeItemInInventory(Client.Game, item, freeSpace);
                    }
                }
            }

            if(!Client.Game.OpenStash(stash))
            {
                Log.Error($"Opening stash failed");
                return;
            }

            var inventoryItemsToKeep = Client.Game.Inventory.Items.Where(i => Pickit.Pickit.ShouldKeepItem(i) && Pickit.Pickit.CanTouchInventoryItem(i)).ToList();
            foreach (Item item in inventoryItemsToKeep)
            {
                if (!MoveItemToStash(item))
                {
                    return;
                };
            }

            Thread.Sleep(300);
            Client.Game.ClickButton(ClickType.CloseStash);
            Thread.Sleep(100);
            Client.Game.ClickButton(ClickType.CloseStash);

            Log.Information($"Transmuting items succeeded");
        }

        private bool MoveItemFromStashToInventory(Item item)
        {
            Point location = Client.Game.Inventory.FindFreeSpace(item);
            if (location != null)
            {
                Client.Game.RemoveItemFromContainer(item);
                bool resultToBuffer = TryWithTimeout((retryCount) =>
                {
                    return Client.Game.CursorItem?.Id == item.Id;
                }, TimeSpan.FromSeconds(3));

                if (!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                    return false;
                }
                Client.Game.InsertItemIntoContainer(item, location, ItemContainer.Inventory);

                return TryWithTimeout((retryCount) =>
                {
                    return Client.Game.CursorItem == null && Client.Game.Inventory.FindItemById(item.Id) != null;
                }, TimeSpan.FromSeconds(3));
            }

            return false;
        }

        private bool MoveItemToStash(Item item)
        {
            Point location = Client.Game.Stash.FindFreeSpace(item);
            if (location != null)
            {
                Client.Game.RemoveItemFromContainer(item);
                bool resultToBuffer = TryWithTimeout((retryCount) =>
                {
                    return Client.Game.CursorItem?.Id == item.Id;
                }, TimeSpan.FromSeconds(3));

                if (!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                    return false;
                }

                Client.Game.InsertItemIntoContainer(item, location, ItemContainer.Stash);

                return TryWithTimeout((retryCount) =>
                {
                    return Client.Game.CursorItem == null && Client.Game.Stash.FindItemById(item.Id) != null;
                }, TimeSpan.FromSeconds(3));
            }

            return false;
        }

        private static void HandleChatEvent(BncsPacket obj)
        {
            var packet = new ChatEventPacket(obj.Raw);
            if (packet.Eid != Eid.SHOWUSER)
            {
                Log.Information(packet.RenderText());
                if(packet.Eid == Eid.WHISPER)
                {
                    SendTelegramMessage(packet.RenderText()).Wait();
                }
            }
        }

        private static void HandleChatMessageEvent(D2gsPacket obj)
        {
            var packet = new ChatPacket(obj);
            if(packet.ChatType != 0x04)
            {
                Log.Information(packet.RenderText());
                if(packet.CharacterName != Config.Character)
                {
                    SendTelegramMessage(packet.RenderText()).Wait();
                }
            }
        }

        private static void OnTelegramMessageReceived(object sender, MessageEventArgs messageEventArgs)
        {
            var message = messageEventArgs.Message;

            if (message == null || message.Type != MessageType.Text) return;

            Log.Information($"Text received: {message.Text}");
            if(message.Text.StartsWith("/w") || message.Text.StartsWith("/msg"))
            {
                Client.Chat.Send(message.Text);
            }
            else if(Client.Game.IsInGame())
            {
                Client.Game.SendInGameMessage(message.Text);
            }
        }

        public bool TryWithTimeout(Func<int, bool> action, TimeSpan timeout)
        {
            bool success = false;
            TimeSpan elapsed = TimeSpan.Zero;
            int retryCount = 0;
            while ((!success) && (elapsed < timeout))
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                success = action(retryCount);
                sw.Stop();
                elapsed += sw.Elapsed;
                retryCount++;
            }

            return success;
        }
    }
}
