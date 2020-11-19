using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Gloves
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
            if (item.Name == ItemName.WarGauntlets
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 60
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) > 0)
            {
                return true;
            }

            if (item.Name == ItemName.WarGauntlets
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 100)
            {
                return true;
            }

            if (item.Name == ItemName.WarGauntlets
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) > 0
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 100)
            {
                return true;
            }

            if (item.Name == ItemName.WarGauntlets
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 60)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 30 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) + item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) * 10 >= 120)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 50 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) + item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) * 10 >= 100)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) + item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) * 10 >= 70)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique && item.Name == ItemName.ChainGloves && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 40)
            {
                return true;
            }

            /*
            // Magefists
            if (item.Quality == QualityType.Unique && item.Name == "Light Gauntlets")
            {
                return true;
            }
            */

            return false;
        }
    }
}
