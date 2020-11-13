using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Amulets
    {
        public static bool ShouldPickupItem(Item item)
        {
            return item.Quality == QualityType.Rare;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.GetToClassSkills() == 2 && item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40)
            {
                return true;
            }

            if (item.GetToClassSkills() == 2 && item.GetTotalResistFrLrCr() >= 20 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 70)
            {
                return true;
            }

            if (item.GetToClassSkills() >= 2 && item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 50)
            {
                return true;
            }

            if (item.GetToClassSkills() >= 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResistFrLrCr() >= 30 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 50)
            {
                return true;
            }

            if (item.GetToClassSkills() >= 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResistFrLrCr() >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 4
                && item.GetValueOfStatType(StatType.ExtraGold) >= 100
                && item.GetTotalResistFrLrCr() >= 80)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 4
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) + item.GetValueOfStatType(StatType.MinimumDamage) * 4 + item.GetValueOfStatType(StatType.BarbarianSkills) * 20 >= 100)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumDamage) >= 7
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 120
                && item.GetTotalResistFrLrCr() >= 70)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.AmazonSkills) >= 1
                && item.GetValueOfStatType(StatType.MinimumDamage) >= 7
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 60
                && item.GetTotalResistFrLrCr() >= 60)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.AmazonSkills) >= 1
                && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 4
                && item.GetValueOfStatType(StatType.MinimumDamage) >= 5
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30
                && item.GetTotalResistFrLrCr() >= 60)
            {
                return true;
            }

            return false;
        }
    }
}
