using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Exceptions;
using D2NG.Core;
using D2NG.Core.D2GS;
using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers;

public static class RealmConnectHelpers
{
    /// <summary>
    /// A party joins in groups that fit the game server's connection window
    /// (<see cref="GameServerConnectionGate"/>), so a group never waits on the gate for the one before it.
    /// </summary>
    public const int ClientsPerConnectionWindow = GameServerConnectionGate.ConnectionsPerWindow;

    public static TimeSpan JoinDelayFor(int clientIndex) => GameServerConnectionGate.Window * (clientIndex / ClientsPerConnectionWindow);

    /// <summary>
    /// Gets a client that just left a game back to the character screen. When another bot logged on to
    /// the same account in the meantime the realm refuses that, so it reconnects right away: a fresh
    /// logon takes well under a second and takes the account back without disturbing the other bot's
    /// game.
    /// </summary>
    public static async Task<bool> RejoinRealm(Client client, BotConfiguration botConfiguration, AccountConfig accountCharacter)
    {
        try
        {
            if (await client.RejoinMCP())
            {
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Information("Rejoining the realm threw for {Account}: {Error}", accountCharacter.Username, e.Message);
        }

        Log.Information("{Account} lost its realm session, most likely logged on elsewhere; reconnecting", accountCharacter.Username);
        return await ConnectToRealmWithRetry(client, botConfiguration, accountCharacter, 10);
    }

    public static async Task<bool> ConnectToRealmWithRetry(
        Client client,
        BotConfiguration botConfiguration,
        AccountConfig accountCharacter,
        int maxRetries)
    {
        var connectCount = 0;
        while (connectCount < maxRetries)
        {
            try
            {
                client.Disconnect();
                if (await ConnectToRealm(client, botConfiguration, accountCharacter))
                {
                    return true;
                }
            }
            catch
            {
            }

            connectCount++;
            Log.Warning($"Connecting to realm failed for {accountCharacter.Username}, doing re-attempt {connectCount} out of 10");
            await Task.Delay(Math.Pow(connectCount, 1.5) * TimeSpan.FromSeconds(5));
        }

        return connectCount < maxRetries;
    }

    public static async Task<bool> ConnectToRealm(Client client,
        BotConfiguration botConfiguration,
        AccountConfig accountCharacter)
    {
        var connect = client.Connect(
            botConfiguration.Realm,
            botConfiguration.KeyOwner,
            botConfiguration.GameFolder);
        if (!connect)
        {
            return false;
        }
        var characters = await client.Login(accountCharacter.Username, accountCharacter.Password);
        if (characters == null)
        {
            return false;
        }

        var selectedCharacter = characters.FirstOrDefault(c =>
            c.Name.Equals(accountCharacter.Character, StringComparison.OrdinalIgnoreCase));
        if (selectedCharacter == null)
        {
            throw new CharacterNotFoundException(accountCharacter.Character);
        }
        await client.SelectCharacter(selectedCharacter);

        if (!string.IsNullOrEmpty(botConfiguration.ChannelToJoin))
        {
            client.Chat.EnterChat();
            client.Chat.JoinChannel(botConfiguration.ChannelToJoin);
        }

        return true;
    }

    public static async Task<Tuple<bool, int>> CreateGameWithRetry(
        int gameCount,
        Client client,
        BotConfiguration botConfiguration,
        AccountConfig account)
    {
        var newGameCount = gameCount;
        var result = await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            bool createGame = false;
            if (!client.IsRealmConnected())
            {
                await RejoinRealm(client, botConfiguration, account);
            }

            try
            {
                createGame = await client.CreateGame(botConfiguration.Difficulty, $"{botConfiguration.GameNamePrefix}{newGameCount}", botConfiguration.GamePassword, botConfiguration.GameDescriptions[0]);
            }
            catch
            {
            }

            if (!createGame)
            {
                newGameCount++;
                var retryDuration = Math.Pow(1 + retryCount, 1.2) * TimeSpan.FromSeconds(3);
                Log.Information($"Creating game failed for {client.LoggedInUserName()} retrying in {retryDuration.TotalSeconds} seconds");
                await ConnectToRealmWithRetry(client, botConfiguration, account, 10);
                await Task.Delay(retryDuration);
            }

            return createGame;
        }, TimeSpan.FromMinutes(5));
        return Tuple.Create(result, newGameCount);
    }

    public static async Task<bool> JoinGameWithRetry(int gameCount, Client client, BotConfiguration botConfiguration, AccountConfig cowAccount, Func<bool> giveUp = null)
    {
        var gaveUp = false;
        var joined = await GeneralHelpers.TryWithTimeout(async (retryCount) =>
        {
            if (giveUp?.Invoke() == true)
            {
                gaveUp = true;
                return true;
            }

            bool joinGame = false;
            if (!client.IsRealmConnected())
            {
                await RejoinRealm(client, botConfiguration, cowAccount);
            }

            try
            {
                joinGame = await client.JoinGame($"{botConfiguration.GameNamePrefix}{gameCount}", botConfiguration.GamePassword);
            }
            catch (Exception e)
            {
                Log.Warning("Joining game threw for {Account}: {Error}", client.LoggedInUserName(), e.Message);
            }

            if (!joinGame)
            {
                var retryDuration = Math.Pow(1 + retryCount, 1.2) * TimeSpan.FromSeconds(3);
                Log.Information($"Joining game failed for {client.LoggedInUserName()} retrying in {retryDuration.TotalSeconds} seconds");
                await ConnectToRealmWithRetry(client, botConfiguration, cowAccount, 10);
                var waited = Stopwatch.StartNew();
                while (waited.Elapsed < retryDuration && giveUp?.Invoke() != true)
                {
                    await Task.Delay(100);
                }
            }

            return joinGame;
        }, TimeSpan.FromSeconds(20));
        return joined && !gaveUp;
    }
}
