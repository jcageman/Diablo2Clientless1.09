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
            var gameCount = new Random().Next(1, 100);
            foreach (var account in _configuration.Accounts)
            {
                var client = new Client();
                var connect = client.Connect(
                    _configuration.Realm,
                    _configuration.KeyOwner,
                    _configuration.GameFolder);
                if (!connect)
                {
                    return false;
                }

                var characters = client.Login(account.Name, account.Password);
                if (characters == null)
                {
                    return false;
                }

                foreach (var character in characters)
                {
                    client.SelectCharacter(character);
                    if (!await client.CreateGame(Core.D2GS.Enums.Difficulty.Normal, "m" + gameCount++, "mulemanager", "gs2"))
                    {
                        return false;
                    }

                    client.Game.RequestUpdate(client.Game.Me.Id);

                    var stashes = client.Game.GetEntityByCode(EntityCode.Stash);
                    if (!stashes.Any())
                    {
                        Log.Error($"{client.Game.Me.Name}: No stash found");
                        if (!await ReconnectClient(client, account))
                        {
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
                        Log.Error($"{client.Game.Me.Name}: Open stash failed");
                        if (!await ReconnectClient(client, account))
                        {
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
                        return false;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
                await client.Disconnect();
            }

            return true;
        }

        private async Task<bool> ReconnectClient(Client client, MuleManagerAccount account)
        {
            if (client.Game.IsInGame())
            {
                await client.Game.LeaveGame();
            }

            if (!client.RejoinMCP())
            {
                await client.Disconnect();
                if (!client.Connect(
                    _configuration.Realm,
                    _configuration.KeyOwner,
                    _configuration.GameFolder))
                {
                    return false;
                }

                client.Login(account.Name, account.Password);
            }

            return true;
        }
    }
}
