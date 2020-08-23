using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Helms
    {
        private static readonly HashSet<string> desirableHelms = new HashSet<string> {
            "Cap", "Skull Cap", "Great Helm", "Crown", "Mask", "Bone Helm",
            "War Hat", "Death Mask", "Grim Helm" };
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
            if (item.Name == "Grim Helm"
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70
                && item.GetTotalLifeFromStats() >= 30)
            {
                return true;
            }

            if (item.Name == "Grim Helm"
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
                && item.GetTotalLifeFromStats() >= 20
                && item.GetTotalResist() >= 40)
            {
                return true;
            }

            if (desirableHelms.Contains(item.Name))
            {
                if (item.GetTotalResist() >= 50 && item.GetTotalLifeFromStats() >= 30)
                {
                    return true;
                }

                if (item.GetTotalResist() >= 45 && item.GetTotalLifeFromStats() >= 30 && item.GetValueOfStatType(StatType.MinimumDamage) == 2)
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

            if (item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() >= 40)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique && item.Name == "Skull Cap" && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 48)
            {
                return true;
            }

            return false;
        }
    }
}
