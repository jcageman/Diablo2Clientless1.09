using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Rings
    {
        public static bool ShouldPickupItem(Item item)
        {
            return true;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.Quality == QualityType.Magical
            && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) > 35)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique
                && item.GetValueOfStatType(StatType.AllSkills) == 1)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResist() >= 55)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResist() >= 20 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) >= 60)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetTotalLifeFromStats() >= 60 && item.GetTotalResist() >= 50)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 1
                && item.GetTotalLifeFromStats() >= 80)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 1
                && item.GetTotalLifeFromStats() >= 60
                && item.GetTotalResist() >= 50)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
                && item.GetTotalLifeFromStats() >= 30
                && item.GetTotalResist() >= 70)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 4 && item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 4
                && (item.GetTotalLifeFromStats() >= 30 || item.GetTotalResist() >= 30))
            {
                return true;
            }

            // BVB ring
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
                && item.GetValueOfStatType(StatType.AttackRating) >= 40
                && item.GetTotalLifeFromStats() >= 50)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 4 && item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 4
                && item.GetTotalLifeFromStats() >= 20 && item.GetTotalResist() >= 20)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 4 && item.GetTotalResist() >= 70)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumDamage) >= 5
                && item.GetTotalLifeFromStats() >= 40
                && item.GetTotalResist() >= 20)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumDamage) >= 5
                && (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit)) >= 4
                && item.GetTotalResist() >= 45)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumDamage) >= 5
                && (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit)) >= 4
                && item.GetTotalLifeFromStats() >= 45)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumDamage) >= 7
                && item.GetTotalResist() >= 70)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.ExtraGold) > 30
             && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) > 4
             && item.GetTotalResist() >= 40)
            {
                return true;
            }

            return false;
        }
    }
}
