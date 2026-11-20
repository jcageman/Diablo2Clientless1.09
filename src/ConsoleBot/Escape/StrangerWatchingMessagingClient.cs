using ConsoleBot.Clients.ExternalMessagingClient;
using D2NG.Core;
using System.Threading.Tasks;

namespace ConsoleBot.Escape;

/// <summary>
/// Every bot registers its clients with the messaging client, so this is the one place that sees
/// them all and can put them under <see cref="StrangerEscape"/>.
/// </summary>
public sealed class StrangerWatchingMessagingClient(IExternalMessagingClient inner) : IExternalMessagingClient
{
    public void RegisterClient(Client client)
    {
        StrangerEscape.Watch(client);
        inner.RegisterClient(client);
    }

    public Task SendMessage(string message) => inner.SendMessage(message);
}
