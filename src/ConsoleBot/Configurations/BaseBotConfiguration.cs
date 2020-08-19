using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Exceptions;
using D2NG.Core;
using D2NG.Core.MCP.Exceptions;
using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Configurations
{
    public abstract class BaseBotConfiguration
    {
        protected readonly BotConfiguration _config;
        protected readonly IExternalMessagingClient _externalMessagingClient;

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

                Thread.Sleep(2_000);

                int count = 1;
                int failureCount = 0;
                while ((((double)failureCount / count) < 0.98 || count < 5))
                {
                    if(count >= 100)
                    {
                        count = 1;
                    }

                    if (!client.CreateGame(_config.Difficulty, $"{_config.GameNamePrefix}{count}", _config.GamePassword, _config.GameDescription))
                    {
                        count++;
                        Thread.Sleep(5000);
                        continue;
                    }

                    failureCount += (await RunSingleGame(client)) ? 0 : 1;
                    Thread.Sleep(1000);

                    if (client.Game.IsInGame())
                    {
                        client.Game.LeaveGame();
                    }
                    if (!client.RejoinMCP())
                    {
                        Log.Warning("Disconnecting client, reconnecting to realm");
                        client.Disconnect();
                        var connectCount = 0;
                        while (connectCount < 10 && !ConnectToRealm(client))
                        {
                            connectCount++;
                            Log.Warning($"Connecting to realm failed, doing re-attempt {connectCount} out of 10 in 30 seconds");
                            Thread.Sleep(10000);
                        };

                        if (connectCount == 10)
                        {
                            throw new System.Exception("Reconnect tries of 10 reached, aborting");
                        }
                        Thread.Sleep(3000);
                    }

                    count++;
                }

                return 0;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unhandled Exception: {e}");
                await _externalMessagingClient.SendMessage($"bot crashed with exception: {e}");
                return 1;
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
            var selectedCharacter = characters.Single(c =>
                c.Name.Equals(_config.Character, StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter == null)
            {
                throw new CharacterNotFoundException();
            }
            client.SelectCharacter(selectedCharacter);
            client.Chat.EnterChat();
            client.Chat.JoinChannel(_config.ChannelToJoin);
            return true;
        }
    }
}
