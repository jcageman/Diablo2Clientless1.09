using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace ConsoleBot.Clients.ExternalMessagingClient
{
    public class ExternalMessagingConfiguration
    {
        [Required]
        public string TelegramApiKey { get; set; }

        [Required]
        public int TelegramChatId { get; set; }
    }
}
