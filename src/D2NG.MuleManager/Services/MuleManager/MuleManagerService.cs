using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.MCP;
using D2NG.Mule;
using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.Refresh;
using D2NG.Pickit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager;

public class MuleManagerService : IMuleManagerService
{
    private readonly MuleManagerConfiguration _configuration;
    private readonly IMuleRepository _muleRepository;
    private static int SeedCount;
    private static readonly Lock PickitLock = new();
    private static bool PickitConfigured;

    public MuleManagerService(IOptions<MuleManagerConfiguration> configuration, IMuleRepository muleRepository)
    {
        _configuration = configuration.Value;
        _muleRepository = muleRepository;
        lock (PickitLock)
        {
            if (!PickitConfigured)
            {
                Pickit.Pickit.Configure(new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(_configuration.PickitConfigFile)).Build().GetSection("pickit").Get<PickitConfiguration>());
                PickitConfigured = true;
            }
        }
    }

    public async Task RefreshAccount(MuleManagerAccount account, RefreshKind kind, bool force, string onlyCharacter, AccountProgress progress, CancellationToken token)
    {
        progress.State = RefreshState.Running;
        var random = new Random(GenerateSeed());
        var client = new Client();
        try
        {
            if (!client.Connect(_configuration.Realm, _configuration.KeyOwner, _configuration.GameFolder))
            {
                Fail(progress, "could not connect to the realm");
                return;
            }

            var characters = await client.Login(account.Name, account.Password);
            if (characters == null)
            {
                Fail(progress, "login failed");
                return;
            }

            var visiting = characters.Where(c => onlyCharacter == null || c.Name.Equals(onlyCharacter, StringComparison.OrdinalIgnoreCase)).ToList();
            progress.Characters = visiting.Count;
            if (visiting.Count == 0)
            {
                Fail(progress, onlyCharacter == null ? "the account has no characters" : $"no character {onlyCharacter} on the account");
                return;
            }

            foreach (var character in visiting)
            {
                token.ThrowIfCancellationRequested();
                progress.Current = character.Name;
                var lastSeen = kind == RefreshKind.Mules && !force ? await _muleRepository.GetCharacter(account.Name, character.Name) : null;
                if (lastSeen?.Expansion != null && MuleSkipRule.ShouldSkip(lastSeen, DateTimeOffset.UtcNow))
                {
                    Log.Information("{Account}-{Character}: full when last seen, skipping", account.Name, character.Name);
                    progress.Skipped++;
                    continue;
                }

                if (await Visit(random, client, account, character, kind))
                {
                    progress.Recorded++;
                }
                else
                {
                    progress.Failed++;
                    progress.Problem = $"{character.Name}: could not enter a game or read the character";
                }

                await Task.Delay(TimeSpan.FromSeconds(random.Next(2, 4)), token);
            }

            progress.State = progress.Failed > 0 && progress.Recorded == 0 ? RefreshState.Failed : RefreshState.Done;
        }
        catch (OperationCanceledException)
        {
            Fail(progress, "stopped");
            throw;
        }
        catch (Exception e)
        {
            Log.Error(e, "{Account}: refresh failed", account.Name);
            Fail(progress, e.Message);
        }
        finally
        {
            progress.Current = null;
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }

            client.Disconnect();
        }
    }

    private static void Fail(AccountProgress progress, string problem)
    {
        progress.State = RefreshState.Failed;
        progress.Problem = problem;
    }

    private async Task<bool> Visit(Random random, Client client, MuleManagerAccount account, Character character, RefreshKind kind)
    {
        if (!await CreateGameWithRetry(random, client, account, character))
        {
            Log.Error("{Account}-{Character}: failed to create a game", account.Name, character.Name);
            return false;
        }

        try
        {
            client.Game.RequestUpdate(client.Game.Me.Id);
            if (!TryWithTimeout(_ => client.Game.Me.Location.X != 0 && client.Game.Me.Location.Y != 0, TimeSpan.FromSeconds(10)))
            {
                Log.Error("{Account}-{Character}: initialization failed", account.Name, character.Name);
                return false;
            }

            await TryWithTimeout(async _ =>
            {
                await Task.Delay(100);
                return client.Game.Stash.Items.Count != 0 || !client.Game.Me.Equipment.IsEmpty;
            }, TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(0.5));

            if (kind == RefreshKind.Gear)
            {
                var gear = GearSnapshot.Take(client.Game, account.Name, character.Name);
                if (gear == null)
                {
                    return false;
                }

                await _muleRepository.UpdateGear(gear);
                Log.Information("{Account}-{Character}: recorded the gear, {Items} worn", account.Name, character.Name, gear.Equipped.Count);
            }
            else
            {
                var snapshot = MuleCharacterSnapshot.Take(client.Game, i => Pickit.Pickit.CanTouchMuleItem(client.Game, i));
                await _muleRepository.UpdateCharacter(account.Name, character.Name, snapshot);
                Log.Information("{Account}-{Character}: recorded {Items} items", account.Name, character.Name, snapshot.Items.Count);
            }

            return true;
        }
        finally
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }
        }
    }

    private static int GenerateSeed()
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
            Stopwatch sw = new();
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
            Stopwatch sw = new();
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

    /// <summary>
    /// Creates a game at the character's highest difficulty. A game in a lower difficulty would
    /// send a played character's next game back to act 1.
    /// </summary>
    public async Task<bool> CreateGameWithRetry(
        Random random,
        Client client,
        MuleManagerAccount muleManagerAccount,
        Character character)
    {
        var difficulty = CharacterProgression.TryCreate(character, out var progression) ? progression.CurrentDifficulty : Difficulty.Normal;
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
                    if(await client.CreateGame(difficulty, randomGameName, "terx", $"gs{randomServer}"))
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("Creating a game for {Account}-{Character} threw: {Message}", muleManagerAccount.Name, character.Name, e.Message);
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
