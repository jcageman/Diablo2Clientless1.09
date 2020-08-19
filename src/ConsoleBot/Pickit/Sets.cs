using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Sets
    {
        public static bool ShouldPickupItem(Item item)
        {
            if (item.Quality != QualityType.Set)
            {
                return false;
            }

            if (item.Name == "Crown" || item.Name == "Amulet" || item.Name == "Light Gauntlets" || item.Name == "Heavy Belt")
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.Quality != QualityType.Set)
            {
                return false;
            }

            // Iratha's set:
            if (item.Type == "Crown" && item.GetValueOfStatType(StatType.FireResistance) == 30)
            {
                return true;
            }

            if (item.Type == "Light Gauntlets" && item.GetValueOfStatType(StatType.ColdResistance) == 30)
            {
                return true;
            }

            if (item.Type == "Amulet" && item.GetValueOfStatType(StatType.PoisonResistance) == 30)
            {
                return true;
            }

            if (item.Type == "Heavy Belt")
            {
                return true;
            }

            return false;
        }
    }
}
