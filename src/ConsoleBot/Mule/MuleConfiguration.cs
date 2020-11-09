using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule
{
    public class MuleConfiguration
    {
        [Required]
        public List<MuleAccount> Accounts { get; set; }
    }
}
