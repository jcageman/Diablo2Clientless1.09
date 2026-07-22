using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit;

public static class Amulets
{
    private static int Min(int value) => PickitThresholdScaling.Min(PickitItemType.Amulets, value);

    public static bool ShouldPickupItemClassic(Item item)
    {
        return item.Quality == QualityType.Rare;
    }

    public static bool ShouldPickupItemExpansion(Item item)
    {
        return item.Quality == QualityType.Rare || item.Quality == QualityType.Unique || item.Quality == QualityType.Crafted;
    }

    public static bool ShouldKeepItemExpansion(Item item)
    {
        if(item.Quality == QualityType.Unique)
        {
            // Cats eye
            if(item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= Min(20) && item.GetValueOfStatType(StatType.FasterRunWalk) >= Min(30))
            {
                return true;
            }

            // Atma's Scarab
            if (item.GetValueOfStatType(StatType.PoisonResistance) >= Min(75))
            {
                return true;
            }

            // Highlord's Wrath
            if (item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= Min(20) && item.GetValueOfStatType(StatType.AmazonSkills) == 1)
            {
                return true;
            }

            // Mara's Kaleidescope
            if (item.GetTotalResistFrLrCr() >= Min(60) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(20) && item.GetValueOfStatType(StatType.AmazonSkills) == 2)
            {
                return true;
            }
        }

        if (item.GetValueToSkillTab(SkillTab.BarbarianWarcries) >= Min(3))
        {
            return true;
        }

        return ShouldKeepItemClassic(item);
    }
        public static bool ShouldKeepItemClassic(Item item)
    {
        var toCasterSkills = item.GetValueOfStatType(StatType.SorceressSkills);
        toCasterSkills += item.GetValueOfStatType(StatType.NecromancerSkills);
        toCasterSkills += item.GetValueOfStatType(StatType.PaladinSkills);
        toCasterSkills += item.GetValueOfStatType(StatType.DruidSkills);
        toCasterSkills += item.GetValueToSkillTab(SkillTab.SorceressLightningSpells);
        toCasterSkills += item.GetValueToSkillTab(SkillTab.SorceressColdSpells);

        var toMeleeSkills = item.GetValueOfStatType(StatType.BarbarianSkills);
        toMeleeSkills += item.GetValueOfStatType(StatType.AmazonSkills);
        toMeleeSkills += item.GetValueOfStatType(StatType.AssassinSkills);
        toMeleeSkills += item.GetValueOfStatType(StatType.DruidSkills);
        toMeleeSkills += item.GetValueToSkillTab(SkillTab.AssasinTraps);
        toMeleeSkills += item.GetValueToSkillTab(SkillTab.DruidElemental);
        toMeleeSkills += item.GetValueToSkillTab(SkillTab.BarbarianCombatSkills);

        if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetTotalResistFrLrCr() >= Min(40) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(40))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetTotalResistFrLrCr() >= Min(20) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(70))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(30) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(50))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.SorceressSkills) == 2 && item.GetValueOfStatType(StatType.FasterCastRate) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(40))
        {
            return true;
        }

        if (toCasterSkills >= Min(2) && item.GetTotalResistFrLrCr() >= Min(60) && item.GetTotalLifeFromStats(CharacterClass.Necromancer) >= Min(30))
        {
            return true;
        }

        if (toCasterSkills >= Min(2) && item.GetTotalResistFrLrCr() >= Min(45) && item.GetTotalLifeFromStats(CharacterClass.Necromancer) >= Min(70))
        {
            return true;
        }

        if (toCasterSkills >= Min(2) && item.GetValueOfStatType(StatType.FasterCastRate) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(30) && item.GetTotalLifeFromStats(CharacterClass.Necromancer) >= Min(50))
        {
            return true;
        }

        if (toCasterSkills >= Min(2) && item.GetValueOfStatType(StatType.FasterCastRate) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(70))
        {
            return true;
        }

        if (toMeleeSkills >= Min(2) && item.GetTotalResistFrLrCr() >= Min(60) && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(50))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= Min(4)
            && item.GetValueOfStatType(StatType.ExtraGold) >= Min(80)
            && item.GetTotalResistFrLrCr() >= Min(80))
        {
            return true;
        }
        if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= Min(4)
            && item.GetValueOfStatType(StatType.ExtraGold) >= Min(80)
            && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= Min(30)
            && item.GetTotalResistFrLrCr() >= Min(40))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.BarbarianSkills) == 2
        && item.GetValueOfStatType(StatType.ExtraGold) >= Min(80)
        && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= Min(4)
        && item.GetTotalResistFrLrCr() >= Min(40))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= Min(6)
            && item.GetValueOfStatType(StatType.MinimumDamage) >= Min(7)
            && item.GetValueOfStatType(StatType.Strength) + item.GetValueOfStatType(StatType.BarbarianSkills) * 4 >= Min(10))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.MinimumDamage) >= Min(7)
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(100)
            && item.GetTotalResistFrLrCr() >= Min(70))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.AmazonSkills) >= 1
            && item.GetValueOfStatType(StatType.MinimumDamage) >= Min(7)
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(40)
            && item.GetTotalResistFrLrCr() >= Min(60))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.AmazonSkills) >= 1
            && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) + item.GetValueOfStatType(StatType.MinimumManaStolenPerHit) >= Min(4)
            && item.GetValueOfStatType(StatType.MinimumDamage) >= Min(5)
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(30)
            && item.GetTotalResistFrLrCr() >= Min(60))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= Min(5)
        && item.GetValueOfStatType(StatType.MinimumDamage) >= Min(7)
        && item.GetValueOfStatType(StatType.Dexterity) >= Min(10))
        {
            return true;
        }

        return false;
    }
}
