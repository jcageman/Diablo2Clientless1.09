using D2NG.Core;
using System.Threading.Tasks;

namespace ConsoleBot.Clients.ExternalMessagingClient;

public interface IExternalMessagingClient
{
    void RegisterClient(Client client);
    Task SendMessage(string message);
}
