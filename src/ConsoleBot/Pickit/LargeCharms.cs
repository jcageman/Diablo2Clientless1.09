using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class LargeCharms
    {
        public static bool ShouldPickupItemExpansion(Item item)
        {
            return true;
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 5
                && item.GetValueOfStatType(StatType.MaximumDamage) >= 4)
            {
                return true;
            }

            if (item.GetTotalLifeFromStats(D2NG.Core.D2GS.Enums.CharacterClass.Sorceress) >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.AttackRating) >= 24
               && item.GetValueOfStatType(StatType.MaximumDamage) >= 4)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.ExtraGold) >= 21)
            {
                return true;
            }

            return false;
        }
    }
}
