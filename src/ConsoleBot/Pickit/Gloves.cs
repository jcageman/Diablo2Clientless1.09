using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit;

public static class Gloves
{
    public static bool ShouldPickupItemClassic(Item item)
    {
        if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
        {
            return true;
        }

        return false;
    }

    public static bool ShouldPickupItemExpansion(Item item)
    {
        if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
        {
            return true;
        }

        /*
        // Crafting blood gloves material
        if(item.Quality == QualityType.Magical
            && (item.Name == ItemName.HeavyGloves || item.Name == ItemName.SharkskinGloves || item.Name == ItemName.VampireboneGloves))
        {
            return true;
        }
        */

        return false;
    }

    public static bool ShouldKeepItemExpansion(Item item)
    {
        if (item.Quality == QualityType.Unique)
        {
            switch (item.Name)
#pragma warning disable CS1522 // Empty switch block
            {
#pragma warning restore CS1522 // Empty switch block
                          //case ItemName.LightGauntlets:
                          //    return true;
                          //case ItemName.DemonhideGloves:
                          //    return item.Ethereal;
            }
        }

        /*
        // Crafting blood gloves material
        if(item.Quality == QualityType.Magical
            && (item.Name == ItemName.HeavyGloves || item.Name == ItemName.SharkskinGloves || item.Name == ItemName.VampireboneGloves))
        {
            return true;
        }
        */

        /*
        if (item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 20
            && item.GetValueToSkillTab(SkillTab.AmazonBowAndCrossbowSkills) + item.GetValueToSkillTab(SkillTab.AmazonJavelinAndSpearSkills) == 2)
        {
            return true;
        }
        */
        if (item.GetValueToSkillTab(SkillTab.AmazonJavelinAndSpearSkills) == 2 && item.GetTotalResistFrLrCr() >= 30)
        {
            return true;
        }

        if (item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 20
            && item.Properties.TryGetValue(StatType.SkillOnHit, out var skillOnHit)
            && skillOnHit.Skill == D2NG.Core.D2GS.Players.Skill.AmplifyDamage)
        {
            return true;
        }

        return false;
    }

    public static bool ShouldKeepItemClassic(Item item)
    {
        if (item.Name == ItemName.WarGauntlets
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 60
            && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) > 0)
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
&& item.GetValueOfStatType(StatType.EnhancedDefense) >= 50
&& item.GetTotalResistFrLrCr() >= 30 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) + item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) * 10 >= 90)
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
