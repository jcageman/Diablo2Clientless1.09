using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Configurations
{
    public class MuleCharacter
    {
        [Required]
        public string Name { get; set; }

        [Required]
        public bool SojMule { get; set; }
    }
}
