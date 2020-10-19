using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Boots
    {
        public static bool ShouldPickupItem(Item item)
        {
            if (item.Quality == QualityType.Rare)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResist() >= 60 && item.GetTotalLifeFromStats() >= 80)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() >= 60)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30 && item.GetTotalResist() >= 90)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 30
                && (item.GetValueOfStatType(StatType.FireResistance) + item.GetValueOfStatType(StatType.LightningResistance)) >= 80
                && item.GetValueOfStatType(StatType.ExtraGold) > 90)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 60 && item.GetTotalLifeFromStats() >= 70)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 20 && item.GetTotalResist() >= 90)
            {
                return true;
            }

            return false;
        }
    }
}
