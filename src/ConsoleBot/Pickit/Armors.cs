using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Armors
    {
        private static readonly HashSet<string> casterArmors = new HashSet<string> {
            "Quilted Armor", "Leather Armor", "Hard Leather Armor", "Studded Leather", "Ring Mail", "Scale Mail","Chain Mail", "Breast Plate", "Splint Mail", "Light Plate",
            "Ghost Armor", "Serpentskin Armor", "Demonhide Armor", "Trellised Armor", "Linked Mail", "Mage Plate" };

        private static readonly HashSet<string> defArmors = new HashSet<string> {
            "Ornate Plate", "Chaos Armor", "Embossed Plate", "Sharktooth Armor", "Templar Coat", "Mage Plate", "Russet Armor"};
        public static bool ShouldPickupItem(Item item)
        {
            if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
            {
                return true;
            }

            if (item.IsIdentified)
            {
                return ShouldKeepItem(item);
            }

            return false;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.Name == "Ornate Plate"
                && item.Quality == QualityType.Rare
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70
                && item.GetTotalLifeFromStats() >= 50)
            {
                return true;
            }

            if (item.Name == "Ornate Plate"
            && item.Quality == QualityType.Rare
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 90
            && item.GetTotalLifeFromStats() >= 30)
            {
                return true;
            }

            if (item.Name == "Ornate Plate"
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 60
            && item.GetValueOfStatType(StatType.ReducedRequirements) >= 20
            && item.GetTotalLifeFromStats() >= 60)
            {
                return true;
            }

            if (item.Name == "Ornate Plate"
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70
            && item.GetValueOfStatType(StatType.ReducedRequirements) >= 20
            && item.GetTotalLifeFromStats() >= 30)
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

            if (item.Name == "Mage Plate"
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 60
            && item.GetTotalResist() >= 50
            && item.GetTotalLifeFromStats() >= 30)
            {
                return true;
            }

            return false;
        }
    }
}
