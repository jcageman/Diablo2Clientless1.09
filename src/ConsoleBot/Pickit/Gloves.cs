using D2NG.D2GS.Items;

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

            if (item.IsIdentified)
            {
                return ShouldKeepItem(item);
            }

            return false;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.Name == "War Gauntlets"
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
                && item.GetTotalLifeFromStats() >= 60
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) > 0)
            {
                return true;
            }

            if (item.Name == "War Gauntlets"
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

            if (item.Quality == QualityType.Unique && item.Name == "Chain Gloves" && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 38)
            {
                return true;
            }

            return false;
        }
    }
}
