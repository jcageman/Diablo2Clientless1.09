using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Mule
{
    public class MuleRule
    {
        [Required]
        public List<MuleFilter> MatchesAll { get; set; } = new List<MuleFilter>();
    }
}
