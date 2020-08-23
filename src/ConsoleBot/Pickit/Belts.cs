using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Belts
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
            if (item.GetTotalResist() >= 50 && item.GetTotalLifeFromStats() >= 60)
            {
                return true;
            }

            if (item.GetTotalResist() >= 40 && item.GetTotalLifeFromStats() >= 80)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 40 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) >= 20)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 10 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) >= 120)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique && item.Name == "Heavy Belt" && item.GetValueOfStatType(StatType.ExtraGold) >= 80)
            {
                return true;
            }

            return false;
        }
    }
}
