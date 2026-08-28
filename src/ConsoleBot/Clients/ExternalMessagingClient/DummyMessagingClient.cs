using D2NG.Core;
using Serilog;
using System.Threading.Tasks;

namespace ConsoleBot.Clients.ExternalMessagingClient;

/// <summary>
/// Stand-in used when no <c>externalMessaging</c> section is configured. Messages still reach the
/// log file, so notifications the bot considers worth reporting are never lost silently.
/// </summary>
internal sealed class DummyMessagingClient : IExternalMessagingClient
{
    public void RegisterClient(Client client)
    {
    }

    public Task SendMessage(string message)
    {
        Log.Information($"External message (no external client configured): {message}");
        return Task.CompletedTask;
    }
}
