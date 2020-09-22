using D2NG.Core;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net;

namespace ConsoleBot
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
