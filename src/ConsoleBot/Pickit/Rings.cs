using D2NG.Core.D2GS.Enums;
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
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.FasterCastRate) >= 10
                && item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 30)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.FasterCastRate) >= 10
            && item.GetTotalResistFrLrCr() >= 45 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 30
            && item.GetValueOfStatType(StatType.ReplenishLife) >= 6)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.FasterCastRate) >= 10
                && item.GetTotalResistFrLrCr() >= 50 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 60)
            {
                return true;
            }

            var totalMinDamage = Math.Max(item.GetValueOfStatType(StatType.MinimumDamage), item.GetValueOfStatType(StatType.SecondaryMinimumDamage));
            var totalMaxDamage = Math.Max(item.GetValueOfStatType(StatType.MaximumDamage), item.GetValueOfStatType(StatType.SecondaryMaximumDamage));
            var totalAddedDamage = totalMinDamage + totalMaxDamage;

            // BVB / ZvZ ring's
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
                && totalAddedDamage >= 7
                && item.GetValueOfStatType(StatType.Dexterity) + item.GetValueOfStatType(StatType.Strength) >= 10)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
                && item.GetValueOfStatType(StatType.AttackRating) >= 70
                && totalAddedDamage >= 3
                && item.GetValueOfStatType(StatType.Strength) >= 10)
            {
                return true;
            }

            // BVA non-leech rings
            if (item.Quality == QualityType.Rare
                && (item.GetValueOfStatType(StatType.AttackRating) >= 70 || totalAddedDamage >= 5)
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 80
                && item.GetTotalResistFrLrCr() >= 50)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && (item.GetValueOfStatType(StatType.AttackRating) >= 30 || totalAddedDamage >= 5)
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 100
            && item.GetTotalResistFrLrCr() >= 45)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && (item.GetValueOfStatType(StatType.AttackRating) >= 30 || totalAddedDamage >= 5)
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 80
            && item.GetTotalResistFrLrCr() >= 55)
            {
                return true;
            }

            // Leech ring's
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 4 && item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 4
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30 && item.GetTotalResistFrLrCr() >= 45)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && totalAddedDamage >= 5
                 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30
                && (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit)) >= 4
                && item.GetTotalResistFrLrCr() >= 45)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && totalAddedDamage >= 7
                && (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit)) >= 4
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 80)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare && totalAddedDamage >= 7
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30
                && item.GetTotalResistFrLrCr() >= 40)
            {
                return true;
            }

            // Gold ring's
            if (item.Quality == QualityType.Rare && item.GetValueOfStatType(StatType.ExtraGold) > 40
             && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) > 4
             && item.GetTotalResistFrLrCr() >= 40)
            {
                return true;
            }

            return false;
        }
    }
}
