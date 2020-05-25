using D2NG.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Boots
    {
        public static bool ShouldPickupItem(Item item)
        {
            if(item.Quality == QualityType.Rare)
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
            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResist() >= 40 && item.GetTotalLifeFromStats() >= 60)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResist() >= 60 && item.GetTotalLifeFromStats() >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResist() >= 70)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30
                && (item.GetValueOfStatType(StatType.FireResistance) + item.GetValueOfStatType(StatType.LightningResistance)) >= 40
                && item.GetValueOfStatType(StatType.ExtraGold) > 50)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 60 && item.GetTotalLifeFromStats() >= 30)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 20 && item.GetTotalResist() >= 80)
            {
                return true;
            }

            return false;
        }
    }
}
