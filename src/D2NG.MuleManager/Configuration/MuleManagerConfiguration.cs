using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace D2NG.MuleManager.Configuration;

public class MuleManagerConfiguration
{
    [Required]
    public string Realm { get; set; }

    [Required]
    public string GameFolder { get; set; }

    [Required]
    public string KeyOwner { get; set; }

    [Required]
    public List<MuleManagerAccount> Accounts { get; set; }
}
