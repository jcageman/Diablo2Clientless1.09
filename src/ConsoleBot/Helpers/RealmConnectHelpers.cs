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
        public static async Task<bool> ConnectToRealmWithRetry(Client client, string realm,
            string keyOwner,
            string gameFolder,
            string username,
            string password,
            string charactername,
            int maxRetries)
        {
            var connectCount = 0;
            while (connectCount < maxRetries)
            {
                try
                {
                    client.Disconnect();
                    if (ConnectToRealm(client, realm, keyOwner, gameFolder, username, password, charactername))
                    {
                        return true;
                    }
                }
                catch
                {
                }

                connectCount++;
                Log.Warning($"Connecting to realm failed for {username}, doing re-attempt {connectCount} out of 10");
                await Task.Delay(Math.Pow(connectCount, 1.5) * TimeSpan.FromSeconds(5));
            }

            return connectCount < maxRetries;
        }

        public static bool ConnectToRealm(Client client, 
            string realm,
            string keyOwner,
            string gameFolder,
            string username,
            string password,
            string charactername)
        {
            var connect = client.Connect(
                realm,
                keyOwner,
                gameFolder);
            if (!connect)
            {
                return false;
            }
            var characters = client.Login(username, password);
            if (characters == null)
            {
                return false;
            }

            var selectedCharacter = characters.Single(c =>
                c.Name.Equals(charactername, StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter == null)
            {
                throw new CharacterNotFoundException(charactername);
            }
            client.SelectCharacter(selectedCharacter);

            return true;
        }
    }
}
