using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using System.Collections.Generic;

namespace ConsoleBot.Pickit;

public static class Helms
{
    private static readonly HashSet<ItemName> desirableHelms = [
        ItemName.Cap, ItemName.SkullCap, ItemName.GreatHelm, ItemName.Crown, ItemName.Mask, ItemName.BoneHelm,
        ItemName.WarHat, ItemName.DeathMask, ItemName.GrimHelm ];
    public static bool ShouldPickupItemExpansion(Item item)
    {
        if (item.Quality == QualityType.Unique)
        {
            switch (item.Name)
            {
                case ItemName.SlayerGuard:
                    return !item.Ethereal;
                //case ItemName.TotemicMask:
                //case ItemName.WarHat:
                case ItemName.WingedHelm:
                    return !item.Ethereal;
                case ItemName.DeathMask:
                    return item.Ethereal;
                case ItemName.GrandCrown:
                    return true;
                case ItemName.GrimHelm:
                    return true;
                case ItemName.Shako:
                    return !item.Ethereal;
            }
        }

        if (item.Classification == ClassificationType.BarbarianHelm && (item.Quality == QualityType.Magical || item.Quality == QualityType.Rare))
        {
            return true;
        }

        if (item.Classification == ClassificationType.Circlet && (item.Quality == QualityType.Magical || item.Quality == QualityType.Rare))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldPickupItemClassic(Item item)
    {
        if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
        {
            return true;
        }

        return false;
    }

    public static bool ShouldKeepItemExpansion(Item item)
    {
        if (item.Quality == QualityType.Unique)
        {
            switch (item.Name)
            {
                case ItemName.SlayerGuard:
                    return !item.Ethereal && item.GetValueToSkillTab(SkillTab.BarbarianCombatSkills) == 1;
                //case ItemName.TotemicMask:
                //case ItemName.WarHat:
                case ItemName.WingedHelm:
                    return item.GetValueOfStatType(StatType.FasterCastRate) >= 30;
                case ItemName.DeathMask:
                    return item.Ethereal;
                case ItemName.GrandCrown:
                    return item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 11 && item.GetValueOfStatType(StatType.ExtraGold) >= 95;
                case ItemName.GrimHelm:
                    return true;
                case ItemName.Shako:
                    return !item.Ethereal;
            }
        }

        var toCasterSkills = item.GetValueOfStatType(StatType.SorceressSkills);
        toCasterSkills += item.GetValueOfStatType(StatType.NecromancerSkills);
        toCasterSkills += item.GetValueOfStatType(StatType.PaladinSkills);
        toCasterSkills += item.GetValueOfStatType(StatType.DruidSkills);

        if (item.Quality == QualityType.Rare
            && item.GetValueOfStatType(StatType.FasterCastRate) >= 20
           && toCasterSkills + item.TotalToSkillTabs() >= 2 && item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 45)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.BarbarianSkills) + item.GetValueToSkillTab(SkillTab.BarbarianWarcries) + item.GetValueToSkill(Skill.BattleOrders) >= 5)
        {
            return true;
        }

        if (item.Classification == ClassificationType.Circlet
            && item.Quality == QualityType.Magical
            && item.GetValueOfStatType(StatType.FasterCastRate) >= 20
            && item.TotalToSkillTabs() >= 3)
        {
            return true;
        }

        return false;
    }

    public static bool ShouldKeepItemClassic(Item item)
    {
        if (desirableHelms.Contains(item.Name) && item.GetValueOfStatType(StatType.Life) >= 30)
        {
            if (item.Name == ItemName.GrimHelm
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 50
            && item.GetTotalResistFrLrCr() >= 40)
            {
                return true;
            }
            if (item.GetTotalResistFrLrCr() >= 50 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 70)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 45 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 40 && item.GetValueOfStatType(StatType.MinimumDamage) == 2)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 20 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 90 && item.GetValueOfStatType(StatType.MinimumDamage) == 2)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 20 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 90)
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

        if (item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 60)
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= 50 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 70)
        {
            return true;
        }

        if (item.Quality == QualityType.Unique && item.Name == ItemName.SkullCap && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 50)
        {
            return true;
        }

        if (item.Quality == QualityType.Unique && item.Name == ItemName.SkullCap && item.Sockets == 1 && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= 40)
        {
            return true;
        }

        return false;
    }
}
