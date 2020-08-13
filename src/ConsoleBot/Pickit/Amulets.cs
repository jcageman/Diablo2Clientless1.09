using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Amulets
    {
        public static bool ShouldPickupItem(Item item)
        {
            if (item.IsIdentified)
            {
                return ShouldKeepItem(item);
            }

            return item.Quality == QualityType.Rare;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.GetToClassSkills() == 2 && item.GetTotalResist() >= 40)
            {
                return true;
            }

            if (item.GetToClassSkills() == 1 && item.GetTotalResist() >= 80)
            {
                return true;
            }

            if (item.GetToClassSkills() == 1 && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResist() >= 70)
            {
                return true;
            }

            if ((item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit)) >= 4
                && item.GetValueOfStatType(StatType.ExtraGold) >= 30
                && item.GetTotalResist() >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.BarbarianSkills) >= 1
                && item.GetValueOfStatType(StatType.ExtraGold) >= 30
                && item.GetTotalResist() >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 4
                && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.MinimumDamage) * 2 >= 60)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumDamage) >= 5
                && item.GetTotalLifeFromStats() >= 40
                && item.GetTotalResist() >= 70)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.AmazonSkills) >= 1
                && item.GetValueOfStatType(StatType.MinimumDamage) >= 5
                && item.GetTotalLifeFromStats() >= 30
                && item.GetTotalResist() >= 60)
            {
                return true;
            }

            return false;
        }
    }
}
