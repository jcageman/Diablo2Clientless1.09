using D2NG.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleBot.Clients.ExternalMessagingClient
{
    internal class DummyMessagingClient : IExternalMessagingClient
    {
        public void RegisterClient(Client client)
        {
        }

        public Task SendMessage(string message)
        {
            return Task.CompletedTask;
        }
    }
}
