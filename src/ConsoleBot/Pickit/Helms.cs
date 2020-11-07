using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Helms
    {
        private static readonly HashSet<ItemName> desirableHelms = new HashSet<ItemName> {
            ItemName.Cap, ItemName.SkullCap, ItemName.GreatHelm, ItemName.Crown, ItemName.Mask, ItemName.BoneHelm,
            ItemName.WarHat, ItemName.DeathMask, ItemName.GrimHelm };
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
            if (item.Name == ItemName.GrimHelm
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 40
                && item.GetTotalResistFrLrCr() >= 40)
            {
                return true;
            }

            if (desirableHelms.Contains(item.Name))
            {
                if (item.GetTotalResistFrLrCr() >= 50 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 50)
                {
                    return true;
                }

                if (item.GetTotalResistFrLrCr() >= 45 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 40 && item.GetValueOfStatType(StatType.MinimumDamage) == 2)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 30)
                {
                    return true;
                }
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 45)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 50)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 50 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 60)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique && item.Name == ItemName.SkullCap && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 45)
            {
                return true;
            }

            return false;
        }
    }
}
