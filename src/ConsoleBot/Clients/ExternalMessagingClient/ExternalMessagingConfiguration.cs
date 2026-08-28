using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Clients.ExternalMessagingClient;

/// <summary>
/// Telegram settings, bound from the optional <c>externalMessaging</c> section. When the section is
/// absent the bot uses a no-op messaging client instead and reports nothing.
/// </summary>
public class ExternalMessagingConfiguration
{
    /// <summary>
    /// Bot token issued by Telegram's BotFather, used to authenticate against the Telegram API.
    /// </summary>
    [Required]
    public string TelegramApiKey { get; set; }

    /// <summary>
    /// Identifier of the chat that status messages, found items and crash reports are sent to.
    /// </summary>
    [Required]
    public int TelegramChatId { get; set; }

    /// <summary>
    /// Whether to also listen for incoming Telegram messages, which lets the chat send commands
    /// back to the bot. Leave off for send-only reporting.
    /// </summary>
    public bool ReceiveMessages { get; set; }
}
