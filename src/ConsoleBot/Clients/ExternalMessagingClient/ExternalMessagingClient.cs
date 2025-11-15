using D2NG.Core;
using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ConsoleBot.Clients.ExternalMessagingClient
{
    public class ExternalMessagingClient : IExternalMessagingClient
    {
        private readonly ExternalMessagingConfiguration _externalConfiguration;
        private readonly ITelegramBotClient _telegramBotClient;
        private readonly List<Client> _clients = new List<Client>();

        public ExternalMessagingClient(IOptions<ExternalMessagingConfiguration> externalConfiguration)
        {
            _externalConfiguration = externalConfiguration.Value ?? throw new ArgumentNullException(nameof(externalConfiguration), $"ExternalMessagingClient constructor fails due to {nameof(externalConfiguration)} being null");
            _telegramBotClient = new TelegramBotClient(_externalConfiguration.TelegramApiKey);
            if(_externalConfiguration.ReceiveMessages)
            {
                var receiverOptions = new ReceiverOptions
                {
                };
                _telegramBotClient.StartReceiving(
                    (botClient, update, token) => HandleUpdateAsync(update),
                    (botClient, exception, token) => HandleExceptionAsync(exception),
                    receiverOptions
                );
            }
        }

        public void RegisterClient(Client client)
        {
            _clients.Add(client);
            client.OnReceivedPacketEvent(Sid.CHATEVENT, (packet) => HandleChatEvent(client, packet));
            client.OnReceivedPacketEvent(InComingPacket.ReceiveChat, (packet) => HandleChatMessageEvent(client, packet));
        }

        Task HandleUpdateAsync(Update update)
        {
            if (update.Message is Message message)
            {
                if (message == null || message.Type != MessageType.Text) return Task.CompletedTask;

                Log.Information($"Text received: {message.Text}");
                var client = _clients.FirstOrDefault(c => message.Text.StartsWith(c.LoggedInUserName() + " ", StringComparison.InvariantCultureIgnoreCase));
                if (client != null)
                {
                    var modifiedText = message.Text.Substring(client.LoggedInUserName().Length + 1);
                    if (modifiedText.StartsWith("/w") || modifiedText.StartsWith("/msg"))
                    {
                        client.Chat.Send(modifiedText);
                    }
                    else if (modifiedText.StartsWith("/chat"))
                    {
                        client.Chat.Send(modifiedText.Substring(5));
                    }
                    else if (client.Game.IsInGame())
                    {
                        client.Game.SendInGameMessage(modifiedText);
                    }
                }
            }
            return Task.CompletedTask;
        }

        Task HandleExceptionAsync(Exception exception)
        {
            Log.Information($"Exception received: {exception}");
            return Task.CompletedTask;
        }

        public async Task SendMessage(string message)
        {
            await _telegramBotClient.SendMessage(new ChatId(_externalConfiguration.TelegramChatId), message);
        }

        private void HandleChatEvent(Client client, BncsPacket obj)
        {
            var packet = new ChatEventPacket(obj.Raw);
            if (packet.Eid != Eid.SHOWUSER && packet.Eid != Eid.USERFLAGS && !packet.Username.Contains(client.LoggedInUserName()))
            {
                Log.Debug(packet.RenderText());
                if (packet.Eid == Eid.WHISPER || packet.Eid == Eid.TALK)
                {
                    SendMessage($"To {client.LoggedInUserName()} :" + packet.RenderText()).Wait();
                }
            }
        }

        private void HandleChatMessageEvent(Client client, D2gsPacket obj)
        {
            var packet = new ChatPacket(obj);
            if (packet.ChatType != 0x04)
            {
                Log.Debug(packet.RenderText());
                if (packet.CharacterName != client.Game.Me?.Name)
                {
                    SendMessage($"To {client.LoggedInUserName()} :" + packet.RenderText()).Wait();
                }
            }
        }
    }
}
