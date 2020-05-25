using D2NG.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Belts
    {
        public static bool ShouldPickupItem(Item item)
        {
            if(item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
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
            if (item.Name == "War Belt"
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70
                && item.GetTotalLifeFromStats() >= 50)
            {
                return true;
            }

            if (item.GetTotalResist() >= 50 && item.GetTotalLifeFromStats() >= 40)
            {
                return true;
            }

            if (item.GetTotalResist() >= 30 && item.GetTotalLifeFromStats() >= 80)
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

            if (item.Quality == QualityType.Unique && item.Name == "Heavy Belt" && item.GetValueOfStatType(StatType.ExtraGold) >= 75)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.ExtraGold) > 100
             && (item.GetValueOfStatType(StatType.FireResistance) + item.GetValueOfStatType(StatType.LightningResistance)) >= 25
             && item.GetTotalLifeFromStats() >= 40)
            {
                return true;
            }

            return false;
        }
    }
}
