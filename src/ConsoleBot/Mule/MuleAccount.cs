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

        [Required]
        public List<MuleCharacter> Characters { get; set; }
    }
}
