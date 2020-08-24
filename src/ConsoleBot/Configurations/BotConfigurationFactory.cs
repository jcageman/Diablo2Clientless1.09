using ConsoleBot.Clients.ExternalMessagingClient;
using Microsoft.Extensions.Options;
using System;
using ConsoleBot.Configurations.Bots;
using D2NG.Navigation.Services.Pathing;

namespace ConsoleBot.Configurations
{
    public class BotConfigurationFactory : IBotConfigurationFactory
    {
        private readonly BotConfiguration _config;
        private readonly IExternalMessagingClient _externalMessagingClient;
        private readonly IPathingService _pathingService;

        public BotConfigurationFactory(IOptions<BotConfiguration> config, IExternalMessagingClient externalMessagingClient, IPathingService pathingService)
        {
            _config = config.Value ?? throw new ArgumentNullException(nameof(config), $"BotConfigurationFactory constructor fails due to {nameof(config)} being null");
            _externalMessagingClient = externalMessagingClient;
            _pathingService = pathingService;
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
                case "test":
                    return new TestBot(_config, _pathingService);
                default:
                    throw new NotSupportedException($"{nameof(_config.BotType)} contains not supported type {botType}, it should be one of the following: travincal, mephisto or test");
            }
        }
    }
}
