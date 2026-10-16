using ConsoleBot.Chicken;
using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Exceptions;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using D2NG.Core;
using Serilog;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ConsoleBot.Bots;

public abstract class SingleClientBotBase
{
    protected readonly AccountConfig _accountConfig;
    protected readonly BotConfiguration _config;
    protected readonly IExternalMessagingClient _externalMessagingClient;
    protected readonly IMuleService _muleService;
    protected bool NeedsMule;

    public SingleClientBotBase(
        BotConfiguration config,
        AccountConfig accountConfig,
        IExternalMessagingClient externalMessagingClient,
        IMuleService muleService)
    {
        _config = config;
        _accountConfig = accountConfig;
        _externalMessagingClient = externalMessagingClient;
        _muleService = muleService;
    }

    protected abstract Task<bool> RunSingleGame(Client client);

    protected async Task CreateGameLoop(Client client)
    {
        _accountConfig.Validate();
        ChickenService.Attach(client, _accountConfig.Chicken ?? _config.Chicken);
        try
        {
            if (!await RealmConnectHelpers.ConnectToRealm(client, _config, _accountConfig))
            {
                throw new Exception("Could not connect to realm");
            }

            int totalCount = 0;
            int gameCount = 0;
            int successiveFailures = 0;
            int muleFailuresInARow = 0;
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
                    bool reconnectResult = await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _accountConfig, 10);
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
                    Log.Information("{Account}: needs mule, starting mule", client.LoggedInUserName());
                    if (await _muleService.MuleItemsForClient(client))
                    {
                        NeedsMule = false;
                        muleFailuresInARow = 0;
                        Log.Information("{Account}: finished mule", client.LoggedInUserName());
                    }
                    else
                    {
                        // One failed mule pass usually succeeds on the retry; only a second one in a row
                        // is worth a message.
                        muleFailuresInARow++;
                        Log.Warning("{Account}: failed to mule all items ({Count} in a row), trying again", client.LoggedInUserName(), muleFailuresInARow);
                        if (muleFailuresInARow >= 2)
                        {
                            await _externalMessagingClient.SendMessage($"{client.LoggedInUserName()}: failed to mule all items {muleFailuresInARow} times in a row, trying again");
                        }

                        await Task.Delay(Math.Pow(successiveFailures, 1.3) * TimeSpan.FromSeconds(5));
                        if (!await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _accountConfig, 10))
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
                    if (!client.IsRealmConnected() && !await RealmConnectHelpers.RejoinRealm(client, _config, _accountConfig))
                    {
                        throw new Exception("Could not connect to realm");
                    }

                    if (_config.GameCreationJitterMs > 0)
                    {
                        await Task.Delay(Random.Shared.Next(_config.GameCreationJitterMs));
                    }

                    if (await client.CreateGame(_config.Difficulty, $"{_config.GameNamePrefix}{gameCount}", _config.GamePassword, _config.GameDescriptions?.ElementAtOrDefault(gameDescriptionIndex)))
                    {
                        Log.Information("In game");
                        client.Game.RequestUpdate(client.Game.Me.Id);
                        while (client.Game.Me.Location.X == 0 && client.Game.Me.Location.Y == 0)
                        {
                            await Task.Delay(10);
                        }

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

                    if (!await RealmConnectHelpers.RejoinRealm(client, _config, _accountConfig))
                    {
                        throw new Exception("Could not connect to realm");
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                }
                catch (HttpRequestException e)
                {
                    var mapServerMessage = $"{client.LoggedInUserName()} Received http exception, map server is probably down";
                    Log.Fatal(e, mapServerMessage);
                    await _externalMessagingClient.SendMessage(mapServerMessage);
                    throw new MapServerUnavailableException(mapServerMessage, e);
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
                    bool reconnectResult = await RealmConnectHelpers.ConnectToRealmWithRetry(client, _config, _accountConfig, 10);
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
            throw;
        }
        finally
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }

            client.Disconnect();
        }
    }
}
