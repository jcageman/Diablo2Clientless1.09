using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.TownManagement
{
    public class TownManagementOptions
    {
        public Act Act { get; set; }
        public Dictionary<ItemName, int> ItemsToBuy { get; set; }
        public bool ResurrectMerc { get; set; } = true;

        public long? HealthPotionsToBuy { get; set; }

        public long? ManaPotionsToBuy { get; set; }
    }
}
