using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Enums;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Mule
{
    public class MuleService : IMuleService
    {
        static int GameCount = 0;

        private readonly BotConfiguration _botConfig;
        private readonly MuleConfiguration _muleConfig;
        private readonly IExternalMessagingClient _externalMessagingClient;

        public MuleService(IOptions<BotConfiguration> botConfig, IOptions<MuleConfiguration> muleConfig, IExternalMessagingClient externalMessagingClient)
        {
            _botConfig = botConfig.Value;
            _muleConfig = muleConfig.Value;
            _externalMessagingClient = externalMessagingClient;
        }
        public async Task<bool> MuleItemsForClient(Client client)
        {
            var muleGameName = $"{_botConfig.GameNamePrefix}m{GameCount++}";
            if (!await client.CreateGame(Difficulty.Normal, muleGameName, _botConfig.GamePassword, _botConfig.GameDescriptions?.ElementAtOrDefault(0)))
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return false;
            }

            if (!await WaitForInitialize(client))
            {
                Log.Error($"Fail to initialize client {client.LoggedInUserName()}");
                return false;
            }

            var failedToJoinCount = 0;

            foreach (var account in _muleConfig.Accounts)
            {
                List<Item> muleItems = GetMuleItems(client, account);
                if (!muleItems.Any())
                {
                    continue;
                }

                var accountCharacters = await GetAccountCharactersForMule(account);

                foreach (var character in accountCharacters)
                {
                    InventoryHelpers.CleanupCursorItem(client.Game);

                    if (!HasAnyItemsToMule(client))
                    {
                        break;
                    }

                    var muleClient = new Client();
                    var accountCharacter = new AccountConfig()
                    {
                        Username = account.Username,
                        Password = account.Password,
                        Character = character
                    };

                    if (!await RealmConnectHelpers.ConnectToRealm(
                        muleClient, _botConfig, accountCharacter))
                    {
                        Log.Error($"Fail to connect to realm with {account.Username} with character {character}");
                        return false;
                    }

                    if (!await muleClient.JoinGame(muleGameName, _botConfig.GamePassword))
                    {
                        Log.Error($"Fail to join game with {account.Username} with character {character}");
                        failedToJoinCount++;
                        await Task.Delay(TimeSpan.FromSeconds(5) * failedToJoinCount);
                        if (failedToJoinCount > 5)
                        {
                            await client.Game.LeaveGame();
                            await Task.Delay(TimeSpan.FromSeconds(2));
                            await client.RejoinMCP();
                            return false;
                        }
                        continue;
                    }

                    if(!await WaitForInitialize(muleClient))
                    {
                        Log.Error($"Fail to initialize client {muleClient.LoggedInUserName()}");
                        return false;
                    }

                    InventoryHelpers.CleanupCursorItem(muleClient.Game);

                    MoveItemResult moveItemResult = MoveItemResult.Succes;
                    do
                    {
                        var movableInventoryItems = muleClient.Game.Inventory.Items.Where(i => Pickit.Pickit.CanTouchInventoryItem(muleClient.Game, i)).ToList();
                        moveItemResult = InventoryHelpers.StashItemsAndGold(muleClient.Game, movableInventoryItems, 0);
                        if (moveItemResult == MoveItemResult.Failed)
                        {
                            break;
                        }

                        var itemsToTrade = GetItemsToTrade(muleClient.Game.Inventory, muleItems);
                        if (!itemsToTrade.Any())
                        {
                            break;
                        }

                        var stashItemsToTrade = itemsToTrade.Where(i => i.Container == ContainerType.Stash || i.Container == ContainerType.Stash2).ToList();
                        if (stashItemsToTrade.Count > 0)
                        {
                            moveItemResult = InventoryHelpers.MoveStashItemsToInventory(client.Game, stashItemsToTrade);
                            InventoryHelpers.CleanupCursorItem(client.Game);
                        }
                        else
                        {
                            moveItemResult = MoveItemResult.Succes;
                        }

                        var itemIdsToTrade = itemsToTrade.Select(i => i.Id).ToHashSet();
                        itemsToTrade = client.Game.Inventory.Items.Where(i => itemIdsToTrade.Contains(i.Id)).ToList();
                        if (!itemsToTrade.Any())
                        {
                            break;
                        }

                        if (moveItemResult != MoveItemResult.Failed)
                        {
                            moveItemResult = await TradeInventoryItems(client, muleClient, itemsToTrade);
                            await Task.Delay(TimeSpan.FromSeconds(5));
                        }
                        muleItems = GetMuleItems(client, account);
                        if (!muleItems.Any())
                        {
                            break;
                        }
                    } while (moveItemResult == MoveItemResult.Succes);

                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await muleClient.Game.LeaveGame();
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    muleClient.Disconnect();
                    if (moveItemResult == MoveItemResult.Failed)
                    {
                        await client.Game.LeaveGame();
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        await client.RejoinMCP();
                        return false;
                    }
                }
            }

            var stashInventoryItems = client.Game.Inventory.Items.Where(i => i.IsIdentified && Pickit.Pickit.ShouldKeepItem(client.Game, i) && Pickit.Pickit.CanTouchInventoryItem(client.Game, i)).ToList();
            InventoryHelpers.StashItemsAndGold(client.Game, stashInventoryItems, 0);
            await client.Game.LeaveGame();
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (!await client.RejoinMCP())
            {
                return false;
            }

            return true;
        }

        private static async Task<bool> WaitForInitialize(Client client)
        {
            var timer = new Stopwatch();
            timer.Start();
            while (client.Game.Me == null && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(100);
            }

            if (client.Game.Me == null)
            {
                Log.Error($"{client.Game.Me.Name} failed to initialize Me");
                return false;
            }

            client.Game.RequestUpdate(client.Game.Me.Id);
            if (!GeneralHelpers.TryWithTimeout(
                (_) => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0,
                TimeSpan.FromSeconds(5)))
            {
                Log.Error($"{client.Game.Me.Name} failed to initialize current location");
                return false;
            }

            return true;
        }

        private async Task<List<string>> GetAccountCharactersForMule(MuleAccount account)
        {
            var characterNames = account.IncludedCharacters.Select(c => c.ToLower()).ToList();
            if (!characterNames.Any())
            {
                var client = new Client();
                var connect = client.Connect(
                _botConfig.Realm,
                    _botConfig.KeyOwner,
                    _botConfig.GameFolder);
                if (!connect)
                {
                    return characterNames;
                }
                var characters = await client.Login(account.Username, account.Password);
                if (characters == null)
                {
                    return characterNames;
                }

                characterNames = characters.Select(c => c.Name.ToLower()).ToList();
                client.Disconnect();
            }

            characterNames = characterNames.Except(account.ExcludedCharacters.Select(c => c.ToLower())).ToList();
            return characterNames;
        }

        private static List<Item> GetMuleItems(Client client, MuleAccount muleAccount)
        {
            var muleItems = client.Game.Items.Values.Where(i => IsMuleItem(client, i));
            if (muleAccount.MatchesAny.Count == 0)
            {
                return muleItems.ToList();
            }

            return muleItems.Where(i => muleAccount.MatchesAny.Any(f => MatchesRule(i, f))).ToList();
        }

        private static bool MatchesRule(Item item, MuleRule muleRule)
        {
            return muleRule.MatchesAll.All(f => MatchesFilter(item, f));
        }

        private static bool MatchesFilter(Item item, MuleFilter filter)
        {
            bool isMatch = true;
            if (filter.ItemName != null)
            {
                isMatch &= item.Name == filter.ItemName;
            }

            if (filter.ClassificationType != null)
            {
                isMatch &= item.Classification == filter.ClassificationType;
            }

            if (filter.QualityType != null)
            {
                isMatch &= item.Quality == filter.QualityType;
            }

            if (filter.NotFilter.HasValue && filter.NotFilter.Value)
            {
                isMatch = !isMatch;
            }

            return isMatch;
        }

        private static bool HasAnyItemsToMule(Client client)
        {
            return client.Game.Items.Values.Any(i => IsMuleItem(client, i));
        }

        private static bool IsMuleItem(Client client, Item item)
        {
            if (item.Name == ItemName.FlawlessSkull
                || item.Name == ItemName.FlawlessAmethyst
                || item.Name == ItemName.FlawlessDiamond
                || item.Name == ItemName.FlawlessEmerald
                || item.Name == ItemName.FlawlessRuby
                || item.Name == ItemName.FlawlessSapphire
                || item.Name == ItemName.FlawlessTopaz)
            {
                return false;
            }

            var rightContainer = item.Container == ContainerType.Stash || item.Container == ContainerType.Stash2 || item.Container == ContainerType.Inventory;

            return rightContainer && item.IsIdentified && Pickit.Pickit.CanTouchInventoryItem(client.Game, item);
        }

        private async Task<MoveItemResult> TradeInventoryItems(Client client, Client muleClient, List<Item> tradeItems)
        {
            bool tradeAccepted = false;
            var clientActions = new HashSet<ButtonAction>();
            client.OnReceivedPacketEvent(InComingPacket.ButtonAction, (packet) => clientActions.Add(new ButtonActionPacket(packet).Action));
            client.OnReceivedPacketEvent(InComingPacket.TradeAccepted, (packet) => tradeAccepted = true);
            var muleClientActions = new HashSet<ButtonAction>();
            muleClient.OnReceivedPacketEvent(InComingPacket.ButtonAction, (packet) => muleClientActions.Add(new ButtonActionPacket(packet).Action));

            var entityPlayerClient = muleClient.Game.Players.First(p => p.Id == client.Game.Me.Id);

            var tries = 0;

            muleClient.Game.InteractWithPlayer(entityPlayerClient);

            while (!clientActions.Contains(ButtonAction.APlayerWantsToTrade) && tries < 20)
            {
                await Task.Delay(100);
                tries++;
            }

            if (tries >= 20)
            {
                return MoveItemResult.Failed;
            }

            client.Game.ClickButton(ClickType.AcceptTradeRequest);
            while (!tradeAccepted && tries < 20)
            {
                await Task.Delay(100);
                tries++;
            }

            if (tries >= 20)
            {
                return MoveItemResult.Failed;
            }

            await Task.Delay(500);
            var movedItems = await MoveAllInventoryItemsToTradeScreenThatFit(client, muleClient.Game.Inventory, tradeItems);

            client.Game.ClickButton(ClickType.PressAcceptButton);
            muleClient.Game.ClickButton(ClickType.PressAcceptButton);

            while (!clientActions.Contains(ButtonAction.YouHaveTradedSomeItems) && tries < 20)
            {
                await Task.Delay(100);
                tries++;
            }

            if (tries >= 20)
            {
                return MoveItemResult.Failed;
            }

            return movedItems;
        }

        private List<Item> GetItemsToTrade(Container muleInventory, List<Item> tradeableItems)
        {
            if (!muleInventory.HasAnyFreeSpace())
            {
                return new List<Item>();
            }

            var itemsToTrade = new List<Item>();
            var temporaryInventory = new Inventory();
            foreach (var item in muleInventory.Items)
            {
                temporaryInventory.Add(item);
            }

            var tradeScreenClient = new Container(10, 4);
            foreach (var item in tradeableItems)
            {
                var freeTradeScreenSpace = tradeScreenClient.FindFreeSpace(item);
                if (freeTradeScreenSpace == null)
                {
                    continue;
                }

                var freeSpaceInventory = temporaryInventory.FindFreeSpace(item);
                if (freeSpaceInventory == null)
                {
                    continue;
                }

                temporaryInventory.Block(freeSpaceInventory, item.Width, item.Height);
                tradeScreenClient.Block(freeTradeScreenSpace, item.Width, item.Height);
                itemsToTrade.Add(item);
            }

            return itemsToTrade;
        }

        private async Task<MoveItemResult> MoveAllInventoryItemsToTradeScreenThatFit(Client client, Container muleInventory, List<Item> tradeableItems)
        {
            var temporaryInventory = new Inventory();
            foreach (var item in muleInventory.Items)
            {
                temporaryInventory.Add(item);
            }

            bool atLeastOneTraded = false;

            var tradeScreenClient = new Container(10, 4);
            foreach (var item in tradeableItems)
            {
                var space = tradeScreenClient.FindFreeSpace(item);
                if (space == null)
                {
                    continue;
                }

                var freeSpaceInventory = temporaryInventory.FindFreeSpace(item);
                if (freeSpaceInventory == null)
                {
                    if (atLeastOneTraded)
                    {
                        break;
                    }
                    else
                    {
                        continue;
                    }
                }

                client.Game.RemoveItemFromContainer(item);

                bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => client.Game.CursorItem?.Id == item.Id, TimeSpan.FromSeconds(5));
                if (!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                    await _externalMessagingClient.SendMessage($"Moving item {item.Id} - {item.Name} to buffer failed");
                    return MoveItemResult.Failed;
                }

                await Task.Delay(100);
                client.Game.InsertItemIntoContainer(item, space, ItemContainer.Trade);
                Item newItem = null;
                var moveResult = GeneralHelpers.TryWithTimeout(
                    (retryCount) => client.Game.CursorItem == null && client.Game.Items.TryGetValue(item.Id, out newItem) && newItem.Container == ContainerType.ForTrade,
                    TimeSpan.FromSeconds(5));
                if (!moveResult)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to trade failed");
                    await _externalMessagingClient.SendMessage($"Moving item {item.Id} - {item.Name} to trade failed ");
                    return MoveItemResult.Failed;
                }

                await Task.Delay(100);
                temporaryInventory.Block(freeSpaceInventory, newItem.Width, newItem.Height);
                tradeScreenClient.Add(newItem);
                atLeastOneTraded = true;
            }

            return atLeastOneTraded ? MoveItemResult.Succes : MoveItemResult.NoSpace;
        }
    }
}
