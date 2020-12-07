using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Sets
    {
        public static bool ShouldPickupItem(Item item)
        {
            return ShouldKeepItem(item);
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.Quality != QualityType.Set)
            {
                return false;
            }

            // Death's
            /*
            if (item.Name == "Sash" || item.Name == "Leather Gloves")
            {
                return true;
            }
            */

            // Iratha's set:
            /*
            if (item.Name == "Crown" && item.GetValueOfStatType(StatType.FireResistance) == 30)
            {
                return true;
            }

            if (item.Name == "Light Gauntlets" && item.GetValueOfStatType(StatType.ColdResistance) == 30)
            {
                return true;
            }

            if (item.Name == "Amulet" && item.GetValueOfStatType(StatType.PoisonResistance) == 30)
            {
                return true;
            }

            if (item.Name == "Heavy Belt" && item.GetValueOfStatType(StatType.MinimumDamage) == 5)
            {
                return true;
            }

            // hsarus
            if (item.Name == ItemName.Belt)
            {
                return true;
            }

            if (item.Name == ItemName.ChainBoots)
            {
                return true;
            }

            //sigons
            if (item.Name == ItemName.GreatHelm)
            {
                return true;
            }

            if (item.Name == ItemName.TowerShield)
            {
                return true;
            }            */

            return false;
        }
    }
}
