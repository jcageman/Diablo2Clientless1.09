using D2NG.Core.D2GS.Enums;
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
            if (item.GetTotalResistFrLrCr() >= 50 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 60)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 80)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 10 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 120)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique && item.Name == ItemName.HeavyBelt && item.GetValueOfStatType(StatType.ExtraGold) >= 80)
            {
                return true;
            }

            return false;
        }
    }
}
