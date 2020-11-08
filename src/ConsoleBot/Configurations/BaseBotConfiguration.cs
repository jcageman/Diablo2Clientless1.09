using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using ConsoleBot.Exceptions;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Configurations
{
    public abstract class BaseBotConfiguration
    {
        protected readonly BotConfiguration _config;
        protected readonly IExternalMessagingClient _externalMessagingClient;
        protected bool NeedsMule = false;

        public BaseBotConfiguration(BotConfiguration config, IExternalMessagingClient externalMessagingClient)
        {
            _config = config;
            _externalMessagingClient = externalMessagingClient;
        }

        protected abstract Task<bool> RunSingleGame(Client client);

        protected async Task<int> CreateGameLoop(Client client)
        {
            try
            {
                if (!ConnectToRealm(client))
                {
                    throw new Exception("Could not connect to realm");
                }

                int totalCount = 0;
                int gameCount = 0;
                int successiveFailures = 0;
                int gameDescriptionIndex = 0;
                while (true)
                {
                    if(successiveFailures > 10 && totalCount > 15)
                    {
                        Log.Error($"bot stopping due to high successive failures: {successiveFailures} with run total {totalCount}");
                        await _externalMessagingClient.SendMessage($"bot stopping due to high successive failures: {successiveFailures} with run total {totalCount}");
                        client.Disconnect();
                        break;
                    }

                    if(gameCount >= 100)
                    {
                        gameCount = 1;
                    }

                    if(NeedsMule && await MakeMule(client, gameCount))
                    {
                        NeedsMule = false;
                    }

                    try
                    {
                        gameCount++;
                        totalCount++;
                        if (client.CreateGame(_config.Difficulty, $"{_config.GameNamePrefix}{gameCount}", _config.GamePassword, _config.GameDescriptions?.ElementAtOrDefault(gameDescriptionIndex)))
                        {
                            if(!await RunSingleGame(client))
                            {
                                successiveFailures += 1;
                            }
                            else
                            {
                                successiveFailures = 0;
                            }
                        }
                        else
                        {
                            Thread.Sleep(10000);
                        }

                        if (client.Game.IsInGame())
                        {
                            client.Game.LeaveGame();
                        }

                        if(!client.RejoinMCP())
                        {
                            throw new Exception("Rejoining MCP failed");
                        }
                    }
                    catch (Exception e)
                    {
                        gameDescriptionIndex++;
                        if(gameDescriptionIndex == _config.GameDescriptions?.Count)
                        {
                            gameDescriptionIndex = 0;
                        }

                        successiveFailures += 1;
                        Log.Warning($"Disconnecting client due to exception {e}, reconnecting to realm, game description is now: {_config.GameDescriptions?.ElementAtOrDefault(gameDescriptionIndex)}");
                        var connectCount = 0;
                        while (connectCount < 10)
                        {
                            try
                            {
                                client.Disconnect();
                                if (ConnectToRealm(client))
                                {
                                    break;
                                }
                            }
                            catch
                            {
                            }
                            
                            connectCount++;
                            Log.Warning($"Connecting to realm failed, doing re-attempt {connectCount} out of 10");
                            Thread.Sleep(10000);
                        }

                        if (connectCount == 10)
                        {
                            throw new Exception("Reconnect tries of 10 reached, aborting");
                        }

                        Log.Warning($"Sleeping for {5*successiveFailures} seconds");
                        Thread.Sleep(5000 * successiveFailures);
                    }
                }

                return 0;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unhandled Exception: {e}");
                await _externalMessagingClient.SendMessage($"bot crashed with exception: {e}");
                client.Disconnect();
                throw e;
            }
        }

        private bool ConnectToRealm(Client client)
        {
            var connect = client.Connect(
                _config.Realm,
                _config.KeyOwner,
                _config.GameFolder);
            if (!connect)
            {
                return false;
            }
            var characters = client.Login(_config.Username, _config.Password);
            if(characters == null)
            {
                return false;
            }

            var selectedCharacter = characters.Single(c =>
                c.Name.Equals(_config.Character, StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter == null)
            {
                throw new CharacterNotFoundException();
            }
            client.SelectCharacter(selectedCharacter);
            if(!string.IsNullOrEmpty(_config.ChannelToJoin))
            {
                client.Chat.EnterChat();
                client.Chat.JoinChannel(_config.ChannelToJoin);
            }
            
            return true;
        }

        private async Task<bool> MakeMule(Client client, int gameCount)
        {
            var muleGameName = $"{_config.GameNamePrefix}m{gameCount}";
            if (!client.CreateGame(Difficulty.Normal, muleGameName, _config.GamePassword, _config.GameDescriptions?.ElementAtOrDefault(0)))
            {
                return false;
            }

            foreach(var account in _config.MuleConfiguration.Accounts)
            {
                foreach (var character in account.Characters)
                {
                    if (!HasAnyItemsToMule(client))
                    {
                        break;
                    }

                    List<Item> muleItems = GetMuleItems(client, character);
                    if (!muleItems.Any())
                    {
                        continue;
                    }

                    var muleClient = new Client();
                    var connect = muleClient.Connect(
                    _config.Realm,
                    _config.KeyOwner,
                    _config.GameFolder);
                    if (!connect)
                    {
                        return false;
                    }

                    var characters = muleClient.Login(account.Username, account.Password);

                    var selectedCharacter = characters.Single(c =>
    c.Name.Equals(character.Name, StringComparison.CurrentCultureIgnoreCase));
                    if (selectedCharacter == null)
                    {
                        throw new CharacterNotFoundException();
                    }
                    muleClient.SelectCharacter(selectedCharacter);
                    if (!muleClient.JoinGame(muleGameName, _config.GamePassword))
                    {
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2));

                    InventoryHelpers.CleanupCursorItem(muleClient.Game);
                    MoveItemResult moveItemResult = MoveItemResult.Succes;
                    do
                    {
                        var movableInventoryItems = muleClient.Game.Inventory.Items.Where(i => Pickit.Pickit.CanTouchInventoryItem(client.Game, i)).ToList();
                        InventoryHelpers.StashItemsAndGold(muleClient.Game, movableInventoryItems, 0);
                        var stashItems = muleItems.Where(i => i.Container == ContainerType.Stash || i.Container == ContainerType.Stash2).ToList();
                        if (stashItems.Count > 0)
                        {
                            moveItemResult = InventoryHelpers.MoveStashItemsToInventory(client.Game, stashItems);
                        }
                        else
                        {
                            moveItemResult = MoveItemResult.Succes;
                        }

                        if (moveItemResult != MoveItemResult.Failed)
                        {
                            moveItemResult = await TradeInventoryItems(client, muleClient);
                        }
                        muleItems = GetMuleItems(client, character);
                        if (!muleItems.Any())
                        {
                            break;
                        }
                    } while (moveItemResult != MoveItemResult.NoSpace);
                    muleClient.Game.LeaveGame();
                    muleClient.Disconnect();
                }
            }

            client.Game.LeaveGame();

            if (!client.RejoinMCP())
            {
                throw new Exception("Rejoining MCP failed");
            }

            return true;
        }

        private static List<Item> GetMuleItems(Client client, MuleCharacter character)
        {
            var muleItems = client.Game.Items.Where(i => IsMuleItem(client, i));
            List<Item> items = new List<Item>();
            if (character.SojMule)
            {
                items = muleItems.Where(i => IsSojOrPs(i)).ToList();
            }
            else
            {
                items = muleItems.Where(i => !IsSojOrPs(i)).ToList();
            }

            return items;
        }

        private static bool HasAnyItemsToMule(Client client)
        {
            return client.Game.Items.Any(i => IsMuleItem(client, i));
        }

        private static bool IsMuleItem(Client client, Item item)
        {
            if(item.Name == ItemName.FlawlessSkull)
            {
                return false;
            }

            var rightContainer = item.Container == ContainerType.Stash || item.Container == ContainerType.Stash2 || item.Container == ContainerType.Inventory;
            
            return rightContainer && item.IsIdentified && Pickit.Pickit.ShouldKeepItem(client.Game, item) && Pickit.Pickit.CanTouchInventoryItem(client.Game, item);
        }

        private static bool IsSojOrPs(Item i)
        {
            return (i.Classification == ClassificationType.Ring && i.Quality == QualityType.Unique
            && i.GetValueOfStatType(StatType.AllSkills) == 1) || (i.Name == ItemName.PerfectSkull);
        }

        private static async Task<MoveItemResult> TradeInventoryItems(Client client, Client muleClient)
        {
            bool tradeAccepted = false;
            var clientActions = new HashSet<ButtonAction>();
            client.OnReceivedPacketEvent(InComingPacket.ButtonAction, (packet) => clientActions.Add(new ButtonActionPacket(packet).Action));
            client.OnReceivedPacketEvent(InComingPacket.TradeAccepted, (packet) => tradeAccepted = true);
            var muleClientActions = new HashSet<ButtonAction>();
            muleClient.OnReceivedPacketEvent(InComingPacket.ButtonAction, (packet) => muleClientActions.Add(new ButtonActionPacket(packet).Action));

            var entityPlayerClient = muleClient.Game.Players.First(p => p.Id == client.Game.Me.Id);

            var tries = 0;

            while (!clientActions.Contains(ButtonAction.APlayerWantsToTrade) && tries < 20)
            {
                muleClient.Game.InteractWithPlayer(entityPlayerClient);
                await Task.Delay(100);
                tries++;
            }

            if(tries > 20)
            {
                return MoveItemResult.Failed;
            }

            while (!tradeAccepted && tries < 20)
            {
                client.Game.ClickButton(ClickType.AcceptTradeRequest);
                await Task.Delay(100);
                tries++;
            }

            if (tries > 20)
            {
                return MoveItemResult.Failed;
            }

            var movedItems = MoveAllInventoryItemsToTradeScreenThatFit(client, muleClient.Game.Inventory);

            client.Game.ClickButton(ClickType.PressAcceptButton);
            muleClient.Game.ClickButton(ClickType.PressAcceptButton);

            while (!clientActions.Contains(ButtonAction.YouHaveTradedSomeItems) && tries < 20)
            {
                await Task.Delay(100);
                tries++;
            }

            return movedItems;
        }

        private static MoveItemResult MoveAllInventoryItemsToTradeScreenThatFit(Client client, Container inventory)
        {
            var temporaryInventory = new Inventory();
            foreach (var item in inventory.Items)
            {
                temporaryInventory.Add(item);
            }

            bool atLeastOneTraded = false;
            
            var tradeScreenClient = new Container(10, 4);
            foreach (var item in client.Game.Inventory.Items.Where(i => Pickit.Pickit.ShouldKeepItem(client.Game, i) && Pickit.Pickit.CanTouchInventoryItem(client.Game, i)))
            {
                var space = tradeScreenClient.FindFreeSpace(item);
                if (space == null)
                {
                    continue;
                }

                var freeSpaceInventory = temporaryInventory.FindFreeSpace(item);
                if(freeSpaceInventory == null)
                {
                    break;
                }

                client.Game.RemoveItemFromContainer(item);

                bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => client.Game.CursorItem?.Id == item.Id, TimeSpan.FromSeconds(3));

                if (!resultToBuffer)
                {
                    InventoryHelpers.CleanupCursorItem(client.Game);
                    Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                    continue;
                }

                client.Game.InsertItemIntoContainer(item, space, ItemContainer.Trade);

                var moveResult = GeneralHelpers.TryWithTimeout(
                    (retryCount) => client.Game.CursorItem == null && client.Game.Items.FirstOrDefault(i => i.Id == item.Id)?.Container == ContainerType.ForTrade,
                    TimeSpan.FromSeconds(3));
                if (!moveResult)
                {
                    InventoryHelpers.CleanupCursorItem(client.Game);
                    Log.Error($"Moving item {item.Id} - {item.Name} to trade failed");
                    continue;
                }

                var newItem = client.Game.Items.First(i => i.Id == item.Id);
                temporaryInventory.Block(freeSpaceInventory, newItem.Width, newItem.Height);
                tradeScreenClient.Add(newItem);
                atLeastOneTraded = true;
            }

            return atLeastOneTraded ? MoveItemResult.Succes : MoveItemResult.NoSpace;
        }
    }
}
