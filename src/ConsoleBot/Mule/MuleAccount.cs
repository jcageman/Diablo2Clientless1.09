using ConsoleBot.Bots;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule
{
    public class MuleAccount
    {
        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }
        public List<MuleRule> MatchesAny { get; set; } = new List<MuleRule>();
        public List<string> IncludedCharacters { get; set; } = new List<string>();
        public List<string> ExcludedCharacters { get; set; } = new List<string>();
    }
}
