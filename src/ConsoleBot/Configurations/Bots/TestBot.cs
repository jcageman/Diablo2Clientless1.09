using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Configurations.Bots.Cows;
using ConsoleBot.Exceptions;
using ConsoleBot.Helpers;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
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

namespace ConsoleBot.Configurations.Bots
{
    public class TestBot : IBotConfiguration
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;

        public TestBot(BotConfiguration config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService, IMapApiService mapApiService)
        {
            _config = config;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _mapApiService = mapApiService;
        }

        public async Task<int> Run()
        {
            var client = new Client();
            if (!client.Connect(
    _config.Realm,
    _config.KeyOwner,
    _config.GameFolder))
            {
                return 1;
            }
            var characters = client.Login("tail", "tragic41");
            if (characters == null)
            {
                return 1;
            }
            var selectedCharacter = characters.Single(c =>
                c.Name.Equals("vamos", StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter == null)
            {
                throw new CharacterNotFoundException();
            }
            client.SelectCharacter(selectedCharacter);
            client.Chat.EnterChat();

            var joinGame = client.JoinGame($"{_config.GameNamePrefix}{1}", _config.GamePassword);
            if(!joinGame)
            {
                return 1;
            }

            while(client.Game.Players.Count(p => p.Id != client.Game.Me.Id) < 1)
            {
                Thread.Sleep(2000);
            }

            var cowManager = new CowManager(_pathingService, _mapApiService, new List<Client> { client });
            while(true)
            {
                Thread.Sleep(2000);
                var otherPlayer = client.Game.Players.Where(p => p.Id != client.Game.Me.Id).First();
                var inLineOfSight = await cowManager.IsInLineOfSight(client, otherPlayer.Location);
                Log.Information($"Player {otherPlayer.Name} is in sight: {inLineOfSight}");
            }

            return 0;
        }
    }
}
