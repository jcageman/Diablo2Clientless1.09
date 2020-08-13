using D2NG.Core;
using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;

namespace ConsoleBot.Clients.ExternalMessagingClient
{
    public class ExternalMessagingClient : IExternalMessagingClient, IDisposable
    {
        private readonly ExternalMessagingConfiguration _externalConfiguration;
        private readonly ITelegramBotClient _telegramBotClient;

        public ExternalMessagingClient(IOptions<ExternalMessagingConfiguration> externalConfiguration)
        {
            _externalConfiguration = externalConfiguration.Value ?? throw new ArgumentNullException(nameof(externalConfiguration), $"ExternalMessagingClient constructor fails due to {nameof(externalConfiguration)} being null");
            _telegramBotClient = new TelegramBotClient(_externalConfiguration.TelegramApiKey);
            _telegramBotClient.StartReceiving(Array.Empty<UpdateType>());
        }

        public void RegisterClient(Client client)
        {
            _telegramBotClient.OnMessage += (s, e) =>
            {
                OnTelegramMessageReceived(client, s, e);
            };

            client.OnReceivedPacketEvent(Sid.CHATEVENT, HandleChatEvent);
            client.OnReceivedPacketEvent(InComingPacket.ReceiveChat, (packet) => HandleChatMessageEvent(client, packet));
        }

        public async Task SendMessage(string message)
        {
            await _telegramBotClient.SendTextMessageAsync(new Telegram.Bot.Types.ChatId(_externalConfiguration.TelegramChatId), message);
        }

        private void HandleChatEvent(BncsPacket obj)
        {
            var packet = new ChatEventPacket(obj.Raw);
            if (packet.Eid != Eid.SHOWUSER)
            {
                Log.Information(packet.RenderText());
                if (packet.Eid == Eid.WHISPER)
                {
                    SendMessage(packet.RenderText()).Wait();
                }
            }
        }

        private void HandleChatMessageEvent(Client client, D2gsPacket obj)
        {
            var packet = new ChatPacket(obj);
            if (packet.ChatType != 0x04)
            {
                Log.Information(packet.RenderText());
                if (packet.CharacterName != client.Game.Me?.Name)
                {
                    SendMessage(packet.RenderText()).Wait();
                }
            }
        }

        private static void OnTelegramMessageReceived(Client client, object sender, MessageEventArgs messageEventArgs)
        {
            var message = messageEventArgs.Message;

            if (message == null || message.Type != MessageType.Text) return;

            Log.Information($"Text received: {message.Text}");
            if (message.Text.StartsWith("/w") || message.Text.StartsWith("/msg"))
            {
                client.Chat.Send(message.Text);
            }
            else if (client.Game.IsInGame())
            {
                client.Game.SendInGameMessage(message.Text);
            }
        }

        public void Dispose()
        {
            _telegramBotClient.StopReceiving();
        }
    }
}
