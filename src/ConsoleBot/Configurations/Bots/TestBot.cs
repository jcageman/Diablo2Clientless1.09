using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Exceptions;
using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Services.Pathing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Configurations.Bots
{
    public class TestBot : IBotConfiguration
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;

        public TestBot(BotConfiguration config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService)
        {
            _config = config;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
        }

        public Task<int> Run()
        {
            var clients = new List<Client>();
            List < Tuple<string, string, string> > clientLogins = new List<Tuple<string, string, string>>();
            foreach (var clientLogin in clientLogins)
            {
                var client = new Client();
                if (!client.Connect(
                    _config.Realm,
                    _config.KeyOwner,
                    _config.GameFolder))
                {
                    return Task.FromResult(1);
                }
                var characters = client.Login(clientLogin.Item1, clientLogin.Item2);
                var selectedCharacter = characters.Single(c =>
                    c.Name.Equals(clientLogin.Item3, StringComparison.CurrentCultureIgnoreCase));
                if (selectedCharacter == null)
                {
                    throw new CharacterNotFoundException();
                }
                client.SelectCharacter(selectedCharacter);
                client.Chat.EnterChat();
                clients.Add(client);
            }

            clients.First().CreateGame(Difficulty.Hell, $"y1", "x", "");
            foreach(var client in clients.Skip(1))
            {
                client.JoinGame("y1", "x");
            }

            while (true)
            {
                Thread.Sleep(100);
            }
        }
    }
}
