using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using System.Collections.Generic;

namespace ConsoleBot.Pickit;

public static class Helms
{
    private static int Min(int value) => PickitThresholdScaling.Min(PickitItemType.Helms, value);

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
                    return item.GetValueOfStatType(StatType.FasterCastRate) >= Min(30);
                case ItemName.DeathMask:
                    return item.Ethereal;
                case ItemName.GrandCrown:
                    return item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= Min(11) && item.GetValueOfStatType(StatType.ExtraGold) >= Min(95);
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
            && item.GetValueOfStatType(StatType.FasterCastRate) >= Min(20)
           && toCasterSkills + item.TotalToSkillTabs() >= Min(2) && item.GetTotalResistFrLrCr() >= Min(60) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(45))
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.BarbarianSkills) + item.GetValueToSkillTab(SkillTab.BarbarianWarcries) + item.GetValueToSkill(Skill.BattleOrders) >= 5)
        {
            return true;
        }

        if (item.Classification == ClassificationType.Circlet
            && item.Quality == QualityType.Magical
            && item.GetValueOfStatType(StatType.FasterCastRate) >= Min(20)
            && item.TotalToSkillTabs() >= Min(3))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldKeepItemClassic(Item item)
    {
        if (desirableHelms.Contains(item.Name) && item.GetValueOfStatType(StatType.Life) >= Min(30))
        {
            if (item.Name == ItemName.GrimHelm
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= Min(50)
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(50)
            && item.GetTotalResistFrLrCr() >= Min(40))
            {
                return true;
            }
            if (item.GetTotalResistFrLrCr() >= Min(50) && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(70))
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= Min(45) && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(40) && item.GetValueOfStatType(StatType.MinimumDamage) == 2)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= Min(20) && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= Min(90) && item.GetValueOfStatType(StatType.MinimumDamage) == 2)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(20) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(90))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(40) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(40))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(70) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(30))
            {
                return true;
            }
        }

        if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= Min(10) && item.GetTotalResistFrLrCr() >= Min(70) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(45))
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= Min(70) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(60))
        {
            return true;
        }

        if (item.GetTotalResistFrLrCr() >= Min(50) && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= Min(70))
        {
            return true;
        }

        if (item.Quality == QualityType.Unique && item.Name == ItemName.SkullCap && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= Min(50))
        {
            return true;
        }

        if (item.Quality == QualityType.Unique && item.Name == ItemName.SkullCap && item.Sockets == 1 && item.GetValueOfStatType(StatType.BetterChanceOfGettingMagicItem) >= Min(40))
        {
            return true;
        }

        return false;
    }
}
