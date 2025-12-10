using D2NG.Core;
using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ConsoleBot.Clients.ExternalMessagingClient;

public class ExternalMessagingClient : IExternalMessagingClient
{
    private readonly ExternalMessagingConfiguration _externalConfiguration;
    private readonly ITelegramBotClient _telegramBotClient;
    private readonly List<Client> _clients = [];
    private readonly ILogger<ExternalMessagingClient> _logger;

    public ExternalMessagingClient(IOptions<ExternalMessagingConfiguration> externalConfiguration, ILogger<ExternalMessagingClient> logger)
    {
        _externalConfiguration = externalConfiguration.Value ?? throw new ArgumentNullException(nameof(externalConfiguration), $"ExternalMessagingClient constructor fails due to {nameof(externalConfiguration)} being null");
        _telegramBotClient = new TelegramBotClient(_externalConfiguration.TelegramApiKey);
        _logger = logger;
        if(_externalConfiguration.ReceiveMessages)
        {
            var receiverOptions = new ReceiverOptions
            {
            };
            _telegramBotClient.StartReceiving(
                (botClient, update, token) => HandleUpdateAsync(update),
                (botClient, exception, token) => HandleExceptionAsync(exception, _logger),
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

    private Task HandleUpdateAsync(Update update)
    {
        if (update.Message is Message message)
        {
            if (message == null || message.Type != MessageType.Text) return Task.CompletedTask;

            _logger.LogInformation("Text received: {Text}", message.Text);
            var client = _clients.FirstOrDefault(c => message.Text.StartsWith(c.LoggedInUserName() + " ", StringComparison.InvariantCultureIgnoreCase));
            if (client != null)
            {
                var modifiedText = message.Text[(client.LoggedInUserName().Length + 1)..];
                if (modifiedText.StartsWith("/w", StringComparison.OrdinalIgnoreCase) || modifiedText.StartsWith("/msg", StringComparison.OrdinalIgnoreCase))
                {
                    client.Chat.Send(modifiedText);
                }
                else if (modifiedText.StartsWith("/chat", StringComparison.OrdinalIgnoreCase))
                {
                    client.Chat.Send(modifiedText[5..]);
                }
                else if (client.Game.IsInGame())
                {
                    client.Game.SendInGameMessage(modifiedText);
                }
            }
        }
        return Task.CompletedTask;
    }

    private static Task HandleExceptionAsync(Exception exception, ILogger logger)
    {
        logger.LogInformation("Exception received: {Exception}", exception);
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
            _logger.LogDebug("Chat event: {Text}", packet.RenderText());
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
            _logger.LogDebug("Chat message: {Text}", packet.RenderText());
            if (packet.CharacterName != client.Game.Me?.Name)
            {
                SendMessage($"To {client.LoggedInUserName()} :" + packet.RenderText()).Wait();
            }
        }
    }
}
