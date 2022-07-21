using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Exceptions;
using ConsoleBot.Mule;
using D2NG.Core;
using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleBot.Bots.Types
{
    public class TestBot : IBotInstance
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;
        private readonly IMuleService _muleService;

        public TestBot(
            IOptions<BotConfiguration> config,
            IExternalMessagingClient externalMessagingClient,
            IPathingService pathingService,
            IMapApiService mapApiService,
            IMuleService muleService)
        {
            _config = config.Value;
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _mapApiService = mapApiService;
            _muleService = muleService;
        }

        public string GetName()
        {
            return "test";
        }

        public async Task Run()
        {
            var client1 = new Client();
            if (!client1.Connect(
    _config.Realm,
    _config.KeyOwner,
    _config.GameFolder))
            {
                return;
            }
            var selectedCharacter1 = (await client1.Login("test", "1234"))?.Single(c =>
                c.Name.Equals("testcharacter", StringComparison.CurrentCultureIgnoreCase));
            if (selectedCharacter1 == null)
            {
                throw new CharacterNotFoundException("testcharacter");
            }
            await client1.SelectCharacter(selectedCharacter1);
            client1.Chat.EnterChat();

            await _muleService.MuleItemsForClient(client1);
        }
    }
}
