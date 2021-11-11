using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class SmallCharms
    {
        public static bool ShouldPickupItemExpansion(Item item)
        {
            return true;
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            if (item.Level >= 95)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 5
                && item.Properties.Count > 1)
            {
                return true;
            }

            if (item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 20)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 5)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.ExtraGold) >= 10)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 15)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.ColdResistance) >= 10
                || item.GetValueOfStatType(StatType.FireResistance) >= 10
                || item.GetValueOfStatType(StatType.LightningResistance) >= 10
                || item.GetValueOfStatType(StatType.PoisonResistance) >= 11)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumPoisonDamage) >= 200)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MaximumDamage) >= 2
                && item.GetValueOfStatType(StatType.AttackRating) >= 10
                && item.GetValueOfStatType(StatType.Life) >= 10)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MaximumDamage) >= 2
                && item.GetValueOfStatType(StatType.AttackRating) >= 20
                && item.GetValueOfStatType(StatType.FasterRunWalk) >= 3)
            {
                return true;
            }

            return false;
        }
    }
}
