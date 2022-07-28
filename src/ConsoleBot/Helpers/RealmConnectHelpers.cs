using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Exceptions;
using D2NG.Core;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers
{
    public static class RealmConnectHelpers
    {
        public static async Task<bool> ConnectToRealmWithRetry(
            Client client,
            BotConfiguration botConfiguration,
            AccountCharacter accountCharacter,
            int maxRetries)
        {
            var connectCount = 0;
            while (connectCount < maxRetries)
            {
                try
                {
                    client.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(2));
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
            AccountCharacter accountCharacter)
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
                c.Name.Equals(accountCharacter.Character, StringComparison.CurrentCultureIgnoreCase));
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
            AccountCharacter account)
        {
            var newGameCount = gameCount;
            var result = await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                bool createGame = false;
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
                    await RealmConnectHelpers.ConnectToRealmWithRetry(client, botConfiguration, account, 10);
                    await Task.Delay(retryDuration);
                }

                return createGame;
            }, TimeSpan.FromSeconds(15));
            return Tuple.Create(result, newGameCount);
        }

        public static async Task<bool> JoinGameWithRetry(int gameCount, Client client, BotConfiguration botConfiguration, AccountCharacter cowAccount)
        {
            return await GeneralHelpers.TryWithTimeout(async (retryCount) =>
            {
                bool joinGame = false;
                try
                {
                    joinGame = await client.JoinGame($"{botConfiguration.GameNamePrefix}{gameCount}", botConfiguration.GamePassword);
                }
                catch
                {
                }

                if (!joinGame)
                {
                    var retryDuration = Math.Pow(1 + retryCount, 1.2) * TimeSpan.FromSeconds(3);
                    Log.Information($"Joining game failed for {client.LoggedInUserName()} retrying in {retryDuration.TotalSeconds} seconds");
                    await ConnectToRealmWithRetry(client, botConfiguration, cowAccount, 10);
                    await Task.Delay(retryDuration);
                }

                return joinGame;
            }, TimeSpan.FromSeconds(20));
        }
    }
}
