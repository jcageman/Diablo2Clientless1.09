using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using D2NG.Core;
using Serilog;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ConsoleBot.Bots
{
    public abstract class SingleClientBotBase
    {
        protected readonly AccountCharacter _accountCharacter;
        protected readonly BotConfiguration _config;
        protected readonly IExternalMessagingClient _externalMessagingClient;
        protected readonly IMuleService _muleService;
        protected bool NeedsMule = false;

        public SingleClientBotBase(
            BotConfiguration config,
            AccountCharacter accountCharacter,
            IExternalMessagingClient externalMessagingClient,
            IMuleService muleService)
        {
            _config = config;
            _accountCharacter = accountCharacter;
            _externalMessagingClient = externalMessagingClient;
            _muleService = muleService;
        }

        protected abstract Task<bool> RunSingleGame(Client client);

        protected async Task CreateGameLoop(Client client)
        {
            _accountCharacter.Validate();
            try
            {
                if (!RealmConnectHelpers.ConnectToRealm(client, _config, _accountCharacter))
                {
                    throw new Exception("Could not connect to realm");
                }

                int totalCount = 0;
                int gameCount = 0;
                int successiveFailures = 0;
                int gameDescriptionIndex = 0;
                while (true)
                {
                    if (successiveFailures > 0 && successiveFailures % 10 == 0)
                    {
                        gameDescriptionIndex++;
                        if (gameDescriptionIndex == _config.GameDescriptions?.Count)
                        {
                            gameDescriptionIndex = 0;
                        }
                        var reconnectMessage = $"Many successive failures, swithing GS to {_config.GameDescriptions?.ElementAtOrDefault(gameDescriptionIndex)}";
                        Log.Warning(reconnectMessage);
                        bool reconnectResult = await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _accountCharacter, 10);
                        if (!reconnectResult)
                        {
                            await _externalMessagingClient.SendMessage($"Reconnect tries of 10 reached, restarting bot");
                            return;
                        }
                    }

                    if (gameCount >= 100)
                    {
                        gameCount = 1;
                    }

                    if (NeedsMule)
                    {
                        await _externalMessagingClient.SendMessage($"{client.LoggedInUserName()}: needs mule, starting mule");
                        if (await _muleService.MuleItemsForClient(client))
                        {
                            NeedsMule = false;
                            await _externalMessagingClient.SendMessage($"{client.LoggedInUserName()}: finished mule");
                        }
                        else
                        {
                            await Task.Delay(Math.Pow(successiveFailures, 1.3) * TimeSpan.FromSeconds(5));
                            await _externalMessagingClient.SendMessage($"{client.LoggedInUserName()}: failed to mule all items, trying again");
                            if (!await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _accountCharacter, 10))
                            {
                                throw new Exception("Could not connect to realm");
                            }
                            successiveFailures++;
                            continue;
                        }
                    }

                    try
                    {
                        gameCount++;
                        totalCount++;
                        if (await client.CreateGame(_config.Difficulty, $"{_config.GameNamePrefix}{gameCount}", _config.GamePassword, _config.GameDescriptions?.ElementAtOrDefault(gameDescriptionIndex)))
                        {
                            if (!await RunSingleGame(client))
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
                            successiveFailures += 1;
                            await Task.Delay(Math.Pow(successiveFailures, 1.3) * TimeSpan.FromSeconds(5));
                        }

                        if (client.Game.IsInGame())
                        {
                            await client.Game.LeaveGame();
                        }

                        if (!client.RejoinMCP())
                        {
                            var reconnectMessage = $"Reconnecting to MCP failed, reconnecting to realm instead";
                            Log.Warning(reconnectMessage);
                            if (!await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _accountCharacter, 10))
                            {
                                throw new Exception("Could not connect to realm");
                            }
                        }
                    }
                    catch (HttpRequestException)
                    {
                        await _externalMessagingClient.SendMessage($"{client.LoggedInUserName() } Received http exception, map server is probably down, restarting bot");
                        return;
                    }
                    catch (Exception e)
                    {
                        gameDescriptionIndex++;
                        if (gameDescriptionIndex == _config.GameDescriptions?.Count)
                        {
                            gameDescriptionIndex = 0;
                        }

                        successiveFailures += 1;
                        Log.Warning($"Disconnecting client due to exception {e}, reconnecting to realm, game description is now: {_config.GameDescriptions?.ElementAtOrDefault(gameDescriptionIndex)}");
                        bool reconnectResult = await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _accountCharacter, 10);
                        if (!reconnectResult)
                        {
                            await _externalMessagingClient.SendMessage($"Reconnect tries of 10 reached, restarting bot");
                            return;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unhandled Exception: {e}");
                await _externalMessagingClient.SendMessage($"bot crashed with exception: {e}");
                throw e;
            }
            finally
            {
                if (client.Game.IsInGame())
                {
                    await client.Game.LeaveGame();
                }

                await client.Disconnect();
            }
        }
    }
}
