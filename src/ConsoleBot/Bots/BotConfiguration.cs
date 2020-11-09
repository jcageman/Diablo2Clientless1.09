using ConsoleBot.Mule;
using D2NG.Core.D2GS.Enums;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots
{
    public class BotConfiguration
    {
        [Required]
        public string Realm { get; set; }

        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string Character { get; set; }

        public MuleConfiguration MuleConfiguration { get; set; }

        [Required]
        public string GameNamePrefix { get; set; }

        public string GamePassword { get; set; }

        [Required]
        public List<string> GameDescriptions { get; set; }

        [Required]
        public Difficulty Difficulty { get; set; }

        public string ChannelToJoin { get; set; }

        [Required]
        public string KeyOwner { get; set; }

        [Required]
        public string GameFolder { get; set; }

        [Required]
        public string BotType { get; set; }

        [Required]
        public string LogFile { get; set; }
    }
}
