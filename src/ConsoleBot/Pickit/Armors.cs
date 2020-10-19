using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Armors
    {
        private static readonly HashSet<ItemName> casterArmors = new HashSet<ItemName> {
            ItemName.QuiltedArmor, ItemName.LeatherArmor, ItemName.HardLeatherArmor, ItemName.StuddedLeather, ItemName.RingMail, ItemName.ScaleMail,ItemName.ChainMail, ItemName.BreastPlate, ItemName.SplintMail, ItemName.LightPlate,
            ItemName.GhostArmor, ItemName.SerpentskinArmor, ItemName.DemonhideArmor, ItemName.TrellisedArmor, ItemName.LinkedMail, ItemName.MagePlate };

        private static readonly HashSet<ItemName> defArmors = new HashSet<ItemName> {
            ItemName.OrnatePlate, ItemName.ChaosArmor, ItemName.EmbossedPlate, ItemName.SharktoothArmor, ItemName.TemplarCoat, ItemName.MagePlate, ItemName.RussetArmor};
        public static bool ShouldPickupItem(Item item)
        {
            if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.Name == ItemName.OrnatePlate
                && item.Quality == QualityType.Rare
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 80
                && item.GetValueOfStatType(StatType.ReducedRequirements) >= 20
                && item.GetTotalLifeFromStats() >= 50)
            {
                return true;
            }

            if (item.Name == ItemName.OrnatePlate
            && item.Quality == QualityType.Rare
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 90
            && item.GetTotalLifeFromStats() >= 50)
            {
                return true;
            }

            if (defArmors.Contains(item.Name)
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 60
            && item.GetValueOfStatType(StatType.ReducedRequirements) >= 30
            && item.GetTotalResist() >= 30
            && item.GetTotalLifeFromStats() >= 30)
            {
                return true;
            }

            if (casterArmors.Contains(item.Name))
            {
                if (item.GetTotalResist() >= 60 && item.GetTotalLifeFromStats() >= 40)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 40 && item.GetTotalLifeFromStats() >= 30)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() >= 20)
                {
                    return true;
                }
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() >= 30)
            {
                return true;
            }

            if (item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() >= 60)
            {
                return true;
            }

            if (item.Name == ItemName.MagePlate
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 60
            && item.GetTotalResist() >= 50
            && item.GetTotalLifeFromStats() >= 30)
            {
                return true;
            }

            /*
            if(item.Name == "Studded Leather" && item.Quality == QualityType.Unique)
            {
                return true;
            }
            */
            return false;
        }
    }
}
