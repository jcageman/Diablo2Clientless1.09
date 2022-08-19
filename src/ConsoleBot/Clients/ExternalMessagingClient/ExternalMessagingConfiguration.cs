using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Clients.ExternalMessagingClient
{
    public class ExternalMessagingConfiguration
    {
        [Required]
        public string TelegramApiKey { get; set; }

        [Required]
        public int TelegramChatId { get; set; }

        public bool ReceiveMessages { get; set; } = false;
    }
}
