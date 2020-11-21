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
using ConsoleBot.Mule;

namespace ConsoleBot.Bots.Types
{
    public class TestBot : IBotInstance
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;
        private readonly IMuleService _muleService;

        public TestBot(
            IOptions<BotConfiguration> config,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMapApiService mapApiService,
            IMuleService muleService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _mapApiService = mapApiService;
            _muleService = muleService;
        }

        public string GetName()
        {
            return "test";
        }

        public async Task Run()
        {
            var client1 = new Client();
            if (!client1.Connect(
    _config.Realm,
    _config.KeyOwner,
    _config.GameFolder))
            {
                return;
            }
            var selectedCharacter1 = client1.Login(_config.Username, _config.Password)?.Single(c =>
                c.Name.Equals(_config.Character, StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter1 == null)
            {
                throw new CharacterNotFoundException(_config.Character);
            }
            client1.SelectCharacter(selectedCharacter1);
            client1.Chat.EnterChat();

            await _muleService.MuleItemsForClient(client1);
        }
    }
}
