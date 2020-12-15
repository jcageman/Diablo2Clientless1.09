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
            var toCasterSkills = item.GetValueOfStatType(StatType.SorceressSkills);
            toCasterSkills += item.GetValueOfStatType(StatType.NecromancerSkills);
            toCasterSkills += item.GetValueOfStatType(StatType.PaladinSkills);
            toCasterSkills += item.GetValueOfStatType(StatType.DruidSkills);

            var toMeleeSkills = item.GetValueOfStatType(StatType.BarbarianSkills);
            toMeleeSkills += item.GetValueOfStatType(StatType.AmazonSkills);
            toMeleeSkills += item.GetValueOfStatType(StatType.AssassinSkills);
            toMeleeSkills += item.GetValueOfStatType(StatType.DruidSkills);

            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetTotalResistFrLrCr() >= 20 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 70)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResistFrLrCr() >= 30 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 50)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResistFrLrCr() >= 40)
            {
                return true;
            }

            if (toCasterSkills == 2 && item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Necromancer) >= 30)
            {
                return true;
            }

            if (toCasterSkills == 2 && item.GetTotalResistFrLrCr() >= 45 && item.GetTotalLifeFromStats(CharacterClass.Necromancer) >= 70)
            {
                return true;
            }

            if (toCasterSkills >= 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResistFrLrCr() >= 30 && item.GetTotalLifeFromStats(CharacterClass.Necromancer) >= 50)
            {
                return true;
            }

            if (toCasterSkills >= 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= 10 && item.GetTotalResistFrLrCr() >= 70)
            {
                return true;
            }

            if (toMeleeSkills >= 2 && item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 50)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= 4
                && item.GetValueOfStatType(StatType.ExtraGold) >= 100
                && item.GetTotalResistFrLrCr() >= 80)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.BarbarianSkills) == 2
            && item.GetValueOfStatType(StatType.ExtraGold) >= 100
            && item.GetTotalResistFrLrCr() >= 60)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6
                && item.GetValueOfStatType(StatType.MinimumDamage) >= 7
                && item.GetValueOfStatType(StatType.Strength) + item.GetValueOfStatType(StatType.BarbarianSkills) * 4 >= 10)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.MinimumDamage) >= 7
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 100
                && item.GetTotalResistFrLrCr() >= 70)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.AmazonSkills) >= 1
                && item.GetValueOfStatType(StatType.MinimumDamage) >= 7
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 40
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

            if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 5
            && item.GetValueOfStatType(StatType.MinimumDamage) >= 7
            && item.GetValueOfStatType(StatType.Dexterity) >= 10)
            {
                return true;
            }

            return false;
        }
    }
}
