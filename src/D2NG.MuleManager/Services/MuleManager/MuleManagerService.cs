using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.MCP;
using D2NG.MuleManager.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager
{
    public class MuleManagerService : IMuleManagerService
    {
        private readonly MuleManagerConfiguration _configuration;
        private readonly IMuleManagerRepository _muleManagerRepository;
        static int SeedCount = 0;

        public MuleManagerService(IOptions<MuleManagerConfiguration> configuration, IMuleManagerRepository muleManagerRepository)
        {
            _configuration = configuration.Value;
            _muleManagerRepository = muleManagerRepository;
        }

        public async Task<bool> UpdateAllAccounts()
        {
            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = 8
            };

            await Parallel.ForEachAsync(_configuration.Accounts, parallelOptions, async (a, token) => await UpdateAccountMules(a));
            return true;
        }

        private async ValueTask<bool> UpdateAccountMules(MuleManagerAccount account)
        {
            var random = new Random(GenerateSeed());
            var client = new Client();
            var connect = client.Connect(
                _configuration.Realm,
                _configuration.KeyOwner,
                _configuration.GameFolder);
            if (!connect)
            {
                Log.Error($"{account.Name}: Connect failed");
                return false;
            }

            var characters = await client.Login(account.Name, account.Password);
            if (characters == null)
            {
                Log.Error($"{account.Name}: Login failed");
                return false;
            }

            foreach (var character in characters)
            {
                if(!await CreateGameWithRetry(random, client, account, character))
                {
                    Log.Error($"{account.Name}-{character.Name}: Failed to create game");
                    continue;
                }

                client.Game.RequestUpdate(client.Game.Me.Id);
                Log.Information($"{client.Game.Me.Name}: In game");
                if (!TryWithTimeout(
                    (_) =>
                    {
                        return client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0;
                    }
                    ,
                    TimeSpan.FromSeconds(10)))
                {
                    Log.Error($"{client.Game.Me.Name}: Initialization failed");
                    continue;
                }

                await TryWithTimeout(
                async (_) =>
                {
                    await Task.Delay(100);
                    return client.Game.Stash.Items.Any();
                }, TimeSpan.FromSeconds(5));

                await Task.Delay(TimeSpan.FromSeconds(0.5));

                var itemsOnAccount = client.Game.Stash.Items;
                itemsOnAccount.AddRange(client.Game.Inventory.Items);
                itemsOnAccount.AddRange(client.Game.Cube.Items);
                var itemsToUpdate = itemsOnAccount.Where(i => i.Classification != ClassificationType.Scroll).Select(i => i.MapToMuleItem(account, character)).ToList();

                await _muleManagerRepository.UpdateCharacter(account, character, itemsToUpdate);
                if(client.Game.IsInGame())
                {
                    await client.Game.LeaveGame();
                }
                
                await Task.Delay(TimeSpan.FromSeconds(random.Next(2,4)));
            }
            client.Disconnect();
            return true;
        }

        static int GenerateSeed()
        {
            return (int)((DateTime.Now.Ticks << 4) +
                           (Interlocked.Increment(ref SeedCount)));
        }

        private static async Task<bool> TryWithTimeout(Func<int, Task<bool>> action, TimeSpan timeout)
        {
            bool success = false;
            TimeSpan elapsed = TimeSpan.Zero;
            int retryCount = 0;
            while ((!success) && (elapsed < timeout))
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                success = await action(retryCount);
                if (!success)
                {
                    await Task.Delay(20);
                }
                sw.Stop();
                elapsed += sw.Elapsed;
                retryCount++;
            }

            return success;
        }

        private static string RandomString(Random random, int length)
        {
            const string pool = "abcdefghijklmnopqrstuvwxyz0123456789";
            var builder = new StringBuilder();

            for (var i = 0; i < length; i++)
            {
                var c = pool[random.Next(0, pool.Length)];
                builder.Append(c);
            }

            return builder.ToString();
        }

        private static bool TryWithTimeout(Func<int, bool> action, TimeSpan timeout)
        {
            bool success = false;
            TimeSpan elapsed = TimeSpan.Zero;
            int retryCount = 0;
            while ((!success) && (elapsed < timeout))
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                success = action(retryCount);
                if (!success)
                {
                    Thread.Sleep(20);
                }
                sw.Stop();
                elapsed += sw.Elapsed;
                retryCount++;
            }

            return success;
        }

        private async Task<bool> ReconnectClient(Client client, MuleManagerAccount account)
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }

            if (!await client.RejoinMCP())
            {
                client.Disconnect();
                if (!client.Connect(
                    _configuration.Realm,
                    _configuration.KeyOwner,
                    _configuration.GameFolder))
                {
                    return false;
                }

                await client.Login(account.Name, account.Password);
            }

            return true;
        }

        private async Task<bool> ConnectToRealmWithRetry(
            Client client,
            MuleManagerAccount muleManagerAccount,
            int maxRetries)
        {
            var connectCount = 0;
            while (connectCount < maxRetries)
            {
                try
                {
                    client.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    if (await ReconnectClient(client, muleManagerAccount))
                    {
                        return true;
                    }
                }
                catch
                {
                }

                connectCount++;
                Log.Warning($"Connecting to realm failed for {muleManagerAccount.Name}, doing re-attempt {connectCount} out of 10");
                await Task.Delay(Math.Pow(connectCount, 1.5) * TimeSpan.FromSeconds(5));
            }

            return connectCount < maxRetries;
        }

        public async Task<bool> CreateGameWithRetry(
            Random random,
            Client client,
            MuleManagerAccount muleManagerAccount,
            Character character)
        {
            var createCount = 0;
            while (createCount < 20)
            {
                try
                {
                    if(await ReconnectClient(client, muleManagerAccount))
                    {
                        await client.SelectCharacter(character);
                        var randomServer = random.Next(1, 6);
                        var randomGameName = RandomString(random, 8);
                        if(await client.CreateGame(Difficulty.Normal, randomGameName, "terx", $"gs{randomServer}"))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                }

                createCount++;
                var retryDuration = Math.Pow(1 + createCount, 1.2) * TimeSpan.FromSeconds(3);
                Log.Information($"Creating game failed for {client.LoggedInUserName()} retrying in {retryDuration.TotalSeconds} seconds");
                await ConnectToRealmWithRetry(client, muleManagerAccount, 10);
                await Task.Delay(retryDuration);
            }

            return false;
        }
    }
}
