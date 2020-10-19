using ConsoleBot.Clients.ExternalMessagingClient;
using Microsoft.Extensions.Options;
using System;
using ConsoleBot.Configurations.Bots;
using D2NG.Navigation.Services.Pathing;
using ConsoleBot.Configurations.Bots.Cows;
using D2NG.Navigation.Services.MapApi;

namespace ConsoleBot.Configurations
{
    public class BotConfigurationFactory : IBotConfigurationFactory
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;
        private readonly IMapApiService _mapApiService;

        public BotConfigurationFactory(IOptions<BotConfiguration> config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService, IMapApiService mapApiService)
        {
            _config = config.Value ?? throw new ArgumentNullException(nameof(config), $"BotConfigurationFactory constructor fails due to {nameof(config)} being null");
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
            _mapApiService = mapApiService;
        }
        public IBotConfiguration CreateConfiguration()
        {
            var botType = _config.BotType;
            switch (botType)
            {
                case "travincal":
                    return new TravincalBot(_config, _externalMessagingClient, _pathingService);
                case "mephisto":
                    return new MephistoBot(_config, _externalMessagingClient, _pathingService);
                case "cows":
                    return new CowBot(_config, _externalMessagingClient, _pathingService, _mapApiService);
                case "test":
                    return new TestBot(_config, _externalMessagingClient, _pathingService, _mapApiService);
                default:
                    throw new NotSupportedException($"{nameof(_config.BotType)} contains not supported type {botType}, it should be one of the following: travincal, mephisto or test");
            }
        }
    }
}
