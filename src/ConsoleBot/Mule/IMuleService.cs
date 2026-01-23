using D2NG.Core;
using System.Threading.Tasks;

namespace ConsoleBot.Mule;

public interface IMuleService
{
    Task<bool> MuleItemsForClient(Client client);
}
