using System.ComponentModel.DataAnnotations;

namespace D2NG.MuleManager.Configuration
{
    public class MuleManagerAccount
    {
        [Required]
        public string Name { get; set; }
        [Required]
        public string Password { get; set; }
    }
}
