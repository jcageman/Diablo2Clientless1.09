using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.MuleManager.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager
{
    public class MuleManagerService : IMuleManagerService
    {
        private readonly MuleManagerConfiguration _configuration;
        private readonly IMuleManagerRepository _muleManagerRepository;

        public MuleManagerService(IOptions<MuleManagerConfiguration> configuration, IMuleManagerRepository muleManagerRepository)
        {
            _configuration = configuration.Value;
            _muleManagerRepository = muleManagerRepository;
        }

        public async Task<bool> UpdateAllAccounts()
        {
            var updateTasks = _configuration.Accounts.Select(async a => await UpdateAccountMules(a)).ToList();
            await Task.WhenAll(updateTasks);
            return true;
        }

        private async Task<bool> UpdateAccountMules(MuleManagerAccount account)
        {
            var gameCount = new Random().Next(1, 100);
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
                await client.SelectCharacter(character);
                if (!await client.CreateGame(Core.D2GS.Enums.Difficulty.Normal, account.Name + "-" + gameCount++, "terx", "gs1"))
                {
                    Log.Error($"{account.Name}: Creating game failed");
                    if (!await ReconnectClient(client, account))
                    {
                        Log.Error($"{account.Name}: Reconnect failed");
                        return false;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    continue;
                }

                client.Game.RequestUpdate(client.Game.Me.Id);

                var stashes = client.Game.GetEntityByCode(EntityCode.Stash);
                if (!stashes.Any())
                {
                    Log.Error($"{client.Game.Me.Name}: No stash found");
                    if (!await ReconnectClient(client, account))
                    {
                        Log.Error($"{account.Name}: Reconnect failed");
                        return false;
                    }
                    continue;
                }

                var stash = stashes.Single();

                if (client.Game.Me.Location.Distance(stash.Location) >= 5)
                {
                    await client.Game.MoveToAsync(stash);
                }

                await Task.Delay(TimeSpan.FromSeconds(1));

                if (!client.Game.OpenStash(stash))
                {
                    Log.Error($"{account.Name}: Open stash failed");
                    if (!await ReconnectClient(client, account))
                    {
                        Log.Error($"{account.Name}: Reconnect failed");
                        return false;
                    }
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));

                client.Game.ClickButton(ClickType.CloseStash);
                await Task.Delay(TimeSpan.FromSeconds(0.1));
                client.Game.ClickButton(ClickType.CloseStash);

                var itemsOnAccount = client.Game.Stash.Items;
                itemsOnAccount.AddRange(client.Game.Inventory.Items);
                itemsOnAccount.AddRange(client.Game.Cube.Items);
                var itemsToUpdate = itemsOnAccount.Where(i => i.Classification != ClassificationType.Scroll).Select(i => i.MapToMuleItem(account, character)).ToList();

                await _muleManagerRepository.UpdateCharacter(account, character, itemsToUpdate);
                if (!await ReconnectClient(client, account))
                {
                    Log.Error($"{account.Name}: Reconnect failed");
                    return false;
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            await client.Disconnect();
            return true;
        }

        private async Task<bool> ReconnectClient(Client client, MuleManagerAccount account)
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }

            if (!await client.RejoinMCP())
            {
                await client.Disconnect();
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
    }
}
