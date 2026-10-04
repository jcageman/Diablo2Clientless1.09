using ConsoleBot.Bots.Types;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.TownManagement;

public class TownManagementOptions
{
    public TownManagementOptions(AccountConfig accountConfig, Act act)
    {
        Act = act;
        AccountConfig = accountConfig;
    }

    public Act Act { get; private init; }
    public AccountConfig AccountConfig { get; private init; }
    public Dictionary<ItemName, int> ItemsToBuy { get; set; }
    public long? HealthPotionsToBuy { get; set; }

    public long? ManaPotionsToBuy { get; set; }

    public bool IdentifyWithTome { get; set; }

    /// <summary>Rejuvenations to keep in the inventory; anything above is sold at the vendor. Null keeps them all.</summary>
    public int? RejuvenationsToKeep { get; set; }
}
