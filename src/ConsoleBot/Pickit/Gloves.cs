using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Gloves
    {
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
            if (item.Name == ItemName.WarGauntlets
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
                && item.GetTotalLifeFromStats() >= 60
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) > 0)
            {
                return true;
            }

            if (item.Name == ItemName.WarGauntlets
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
                && item.GetTotalLifeFromStats() >= 80)
            {
                return true;
            }

            if (item.Name == ItemName.WarGauntlets
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) > 0
                && item.GetTotalLifeFromStats() >= 100)
            {
                return true;
            }

            if (item.Name == ItemName.WarGauntlets
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70
            && item.GetTotalLifeFromStats() >= 20
            && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) > 0)
            {
                return true;
            }

            if (item.GetTotalResist() >= 50 && item.GetTotalLifeFromStats() >= 60)
            {
                return true;
            }

            if (item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() >= 40)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique && item.Name == ItemName.ChainGloves && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 40)
            {
                return true;
            }

            /*
            // Magefists
            if (item.Quality == QualityType.Unique && item.Name == "Light Gauntlets")
            {
                return true;
            }
            */

            return false;
        }
    }
}
