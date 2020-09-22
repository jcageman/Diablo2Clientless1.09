using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Exceptions;
using D2NG.Core;
using D2NG.Core.MCP.Exceptions;
using Serilog;
using System;
using System.Linq;
using System.Linq.Expressions;
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
                        break;
                    }

                    if(gameCount >= 100)
                    {
                        gameCount = 1;
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
                        client.Disconnect();
                        var connectCount = 0;
                        while (connectCount < 10)
                        {
                            try
                            {
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
                            throw new System.Exception("Reconnect tries of 10 reached, aborting");
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
            if(!string.IsNullOrEmpty(_config.ChannelToJoin))
            {
                client.Chat.JoinChannel(_config.ChannelToJoin);
            }
            
            return true;
        }
    }
}
