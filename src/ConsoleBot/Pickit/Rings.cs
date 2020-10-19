using D2NG.Core.D2GS.Items;
using System;

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
            if (item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) > 37)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique
                && item.GetValueOfStatType(StatType.AllSkills) == 1)
            {
                return true;
            }

            // Faster cast rings
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResist() >= 55)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResist() >= 30 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) >= 60)
            {
                return true;
            }

            var totalMinDamage = Math.Max(item.GetValueOfStatType(StatType.MinimumDamage), item.GetValueOfStatType(StatType.SecondaryMinimumDamage));
            var totalMaxDamage = Math.Max(item.GetValueOfStatType(StatType.MaximumDamage), item.GetValueOfStatType(StatType.SecondaryMaximumDamage));
            var totalAddedDamage = totalMinDamage + totalMaxDamage;

            // BVB ring's
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
                && item.GetValueOfStatType(StatType.AttackRating) >= 40
                && item.GetTotalLifeFromStats() >= 70)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
                && totalAddedDamage >= 5
                && item.GetTotalLifeFromStats() >= 70)
            {
                return true;
            }

            // Leech ring's
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
            && totalAddedDamage >= 5
            && item.GetTotalLifeFromStats() >= 70)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 4 && item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 4
                && item.GetTotalLifeFromStats() >= 30 && item.GetTotalResist() >= 45)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && totalAddedDamage >= 5
                && item.GetTotalLifeFromStats() >= 70
                && item.GetTotalResist() >= 40)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && totalAddedDamage >= 5
                && (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit)) >= 4
                && item.GetTotalResist() >= 45)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && totalAddedDamage >= 5
                && (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit)) >= 4
                && item.GetTotalLifeFromStats() >= 70)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && totalAddedDamage >= 7
                && item.GetTotalResist() >= 70)
            {
                return true;
            }

            // Gold ring's
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
