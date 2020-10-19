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
                && item.GetTotalLifeFromStats() >= 20
                && item.GetTotalResist() >= 40)
            {
                return true;
            }

            if (desirableHelms.Contains(item.Name))
            {
                if (item.GetTotalResist() >= 50 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) / 2 >= 30)
                {
                    return true;
                }

                if (item.GetTotalResist() >= 45 && item.GetTotalLifeFromStats() >= 30 && item.GetValueOfStatType(StatType.MinimumDamage) == 2)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 40 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) / 2 >= 30)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) / 2 >= 20)
                {
                    return true;
                }
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) / 2 >= 30)
            {
                return true;
            }

            if (item.GetTotalResist() >= 70 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) / 2 >= 40)
            {
                return true;
            }

            if (item.GetTotalResist() >= 50 && item.GetTotalLifeFromStats() + item.GetValueOfStatType(StatType.Mana) / 2 >= 50)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique && item.Name == ItemName.SkullCap && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) == 50)
            {
                return true;
            }

            return false;
        }
    }
}
