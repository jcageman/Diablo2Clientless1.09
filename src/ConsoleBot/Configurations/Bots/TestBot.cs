using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using D2NG.Navigation.Services.Pathing;
using System;
using System.Threading.Tasks;

namespace ConsoleBot.Configurations.Bots
{
    public class TestBot : IBotConfiguration
    {
        private readonly BotConfiguration config;
        private readonly IPathingService _pathingService;

        public TestBot(BotConfiguration config,IPathingService pathingService)
        {
            this.config = config;
            _pathingService = pathingService;
        }

        public async Task<int> Run()
        {
            var pathToCouncil = await _pathingService.GetPathToObjectWithOffset(1575833078, Difficulty.Normal, Area.Travincal, new Point(4738, 1583), EntityCode.CompellingOrb, 23, -25, MovementMode.Walking);
            Console.WriteLine(string.Join(" --> ", pathToCouncil));
            return 0;
        }
    }
}
