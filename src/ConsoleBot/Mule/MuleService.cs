using ConsoleBot.Bots;
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
using System.Threading.Tasks;

namespace ConsoleBot.Mule
{
    public class MuleService : IMuleService
    {
        static int GameCount = 0;
        public async Task<bool> MuleItemsForClient(Client client, BotConfiguration configuration)
        {
            var muleGameName = $"{configuration.GameNamePrefix}m{GameCount++}";
            if (!client.CreateGame(Difficulty.Normal, muleGameName, configuration.GamePassword, configuration.GameDescriptions?.ElementAtOrDefault(0)))
            {
                return false;
            }

            foreach (var account in configuration.MuleConfiguration.Accounts)
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
                    configuration.Realm,
                    configuration.KeyOwner,
                    configuration.GameFolder);
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
                    if (!muleClient.JoinGame(muleGameName, configuration.GamePassword))
                    {
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2));

                    MoveItemResult moveItemResult = MoveItemResult.Succes;
                    do
                    {
                        InventoryHelpers.CleanupCursorItem(muleClient.Game);
                        InventoryHelpers.CleanupCursorItem(client.Game);
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
            if (item.Name == ItemName.FlawlessSkull)
            {
                return false;
            }

            var rightContainer = item.Container == ContainerType.Stash || item.Container == ContainerType.Stash2 || item.Container == ContainerType.Inventory;

            return rightContainer && item.IsIdentified && Pickit.Pickit.CanTouchInventoryItem(client.Game, item);
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

            if (tries > 20)
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
            foreach (var item in client.Game.Inventory.Items.Where(i => IsMuleItem(client, i)))
            {
                var space = tradeScreenClient.FindFreeSpace(item);
                if (space == null)
                {
                    continue;
                }

                var freeSpaceInventory = temporaryInventory.FindFreeSpace(item);
                if (freeSpaceInventory == null)
                {
                    if(atLeastOneTraded)
                    {
                        break;
                    }
                    else
                    {
                        continue;
                    }
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
