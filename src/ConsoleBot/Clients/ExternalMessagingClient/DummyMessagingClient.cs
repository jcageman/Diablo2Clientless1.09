using D2NG.Core;
using System.Threading.Tasks;

namespace ConsoleBot.Clients.ExternalMessagingClient;

internal sealed class DummyMessagingClient : IExternalMessagingClient
{
    public void RegisterClient(Client client)
    {
    }

    public Task SendMessage(string message)
    {
        return Task.CompletedTask;
    }
}
