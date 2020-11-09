using ConsoleBot.Bots;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Bots.Types.Cows;
using ConsoleBot.Enums;
using ConsoleBot.Exceptions;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Telegram.Bot.Types;
using Microsoft.Extensions.Options;

namespace ConsoleBot.Bots.Types
{
    public class TestBot : IBotInstance
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;

        public TestBot(IOptions<BotConfiguration> config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService, IMapApiService mapApiService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _mapApiService = mapApiService;
        }

        public string GetName()
        {
            return "test";
        }

        public async Task<int> Run()
        {
            var gameNumber = 2;
            var client1 = new Client();
            if (!client1.Connect(
    _config.Realm,
    _config.KeyOwner,
    _config.GameFolder))
            {
                return 1;
            }
            var selectedCharacter1 = client1.Login("taggert-mule-3", "tragic41")?.Single(c =>
                c.Name.Equals("tmulethree-two", StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter1 == null)
            {
                throw new CharacterNotFoundException();
            }
            client1.SelectCharacter(selectedCharacter1);
            client1.Chat.EnterChat();

            var createGame = client1.CreateGame(Difficulty.Normal, $"{_config.GameNamePrefix}{gameNumber}", _config.GamePassword, "gs2");
            if (!createGame)
            {
                return 1;
            }

            var client2 = new Client();
            if (!client2.Connect(
    _config.Realm,
    _config.KeyOwner,
    _config.GameFolder))
            {
                return 1;
            }
            var selectedCharacter2 = client2.Login("taggert-3", "tragic41")?.Single(c =>
                c.Name.Equals("far", StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter2 == null)
            {
                throw new CharacterNotFoundException();
            }
            client2.SelectCharacter(selectedCharacter2);
            client2.Chat.EnterChat();

            var joinGame = client2.JoinGame($"{_config.GameNamePrefix}{gameNumber}", _config.GamePassword);
            if (!joinGame)
            {
                return 1;
            }

            while (client2.Game.Players.Count(p => p.Id != client2.Game.Me.Id) < 1)
            {
                Thread.Sleep(2000);
            }

            bool tradeAccepted = false;
            var client1Actions = new HashSet<ButtonAction>();
            client1.OnReceivedPacketEvent(InComingPacket.ButtonAction, (packet) => client1Actions.Add(new ButtonActionPacket(packet).Action));
            client1.OnReceivedPacketEvent(InComingPacket.TradeAccepted, (packet) => tradeAccepted = true);
            var client2Actions = new HashSet<ButtonAction>();
            client2.OnReceivedPacketEvent(InComingPacket.ButtonAction, (packet) => client2Actions.Add(new ButtonActionPacket(packet).Action));

            var entityPlayerClient1 = client2.Game.Players.First(p => p.Id == client1.Game.Me.Id);

            while (!client1Actions.Contains(ButtonAction.APlayerWantsToTrade))
            {
                client2.Game.InteractWithPlayer(entityPlayerClient1);
                await Task.Delay(100);
            }

            while (!tradeAccepted)
            {
                client1.Game.ClickButton(ClickType.AcceptTradeRequest);
                await Task.Delay(100);
            }

            MoveAllInventoryItemsToTradeScreen(client1);
            MoveAllInventoryItemsToTradeScreen(client2);

            client1.Game.ClickButton(ClickType.PressAcceptButton);
            client2.Game.ClickButton(ClickType.PressAcceptButton);

            while (!client1Actions.Contains(ButtonAction.YouHaveTradedSomeItems))
            {
                await Task.Delay(100);
            }

            return 0;
        }

        private static void MoveAllInventoryItemsToTradeScreen(Client client)
        {
            var tradeScreenClient2 = new Container(10, 4);
            foreach (var item in client.Game.Inventory.Items)
            {
                var space = tradeScreenClient2.FindFreeSpace(item);
                if (space == null)
                {
                    continue;
                }

                client.Game.RemoveItemFromContainer(item);

                bool resultToBuffer = GeneralHelpers.TryWithTimeout((retryCount) => client.Game.CursorItem?.Id == item.Id, TimeSpan.FromSeconds(3));

                if (!resultToBuffer)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to buffer failed");
                    continue;
                }

                client.Game.InsertItemIntoContainer(item, space, ItemContainer.Trade);

                var moveResult = GeneralHelpers.TryWithTimeout(
                    (retryCount) => client.Game.CursorItem == null && client.Game.Items.FirstOrDefault(i => i.Id == item.Id).Container == ContainerType.ForTrade,
                    TimeSpan.FromSeconds(3));
                if (!moveResult)
                {
                    Log.Error($"Moving item {item.Id} - {item.Name} to trade failed");
                    continue;
                }

                var newItem = client.Game.Items.First(i => i.Id == item.Id);

                tradeScreenClient2.Add(newItem);
            }
        }
    }
}
