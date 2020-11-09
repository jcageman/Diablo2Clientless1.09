using ConsoleBot.Bots;
using D2NG.Core;
using System.Threading.Tasks;

namespace ConsoleBot.Mule
{
    public interface IMuleService
    {
        public Task<bool> MuleItemsForClient(Client client, BotConfiguration configuration);
    }
}
