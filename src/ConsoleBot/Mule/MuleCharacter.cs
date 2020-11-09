using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule
{
    public class MuleCharacter
    {
        [Required]
        public string Name { get; set; }

        [Required]
        public bool SojMule { get; set; }
    }
}
