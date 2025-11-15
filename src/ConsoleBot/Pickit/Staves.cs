using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using System;

namespace ConsoleBot.Pickit
{
    public static class Staves
    {
        public static bool ShouldPickupItemClassic(Item item)
        {
            return true;
        }

        public static bool ShouldPickupItemExpansion(Item item)
        {
            if (item.Quality == QualityType.Rare && item.Classification == ClassificationType.SorceressOrb)
            {
                return true;
            }

            if (item.Name == ItemName.SwirlingCrystal && item.Quality == QualityType.Unique)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            /*
            if(item.Name == ItemName.SwirlingCrystal && item.Quality == QualityType.Unique && !item.Ethereal && item.GetValueOfStatType(StatType.SorceressSkills) >= 3)
            {
                return true;
            }
            */

            if (item.Classification == ClassificationType.SorceressOrb
            && item.GetValueToSkillTab(SkillTab.SorceressLightningSpells)
            + item.GetValueToSkill(Skill.EnergyShield) >= 6)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItemClassic(Item item)
        {
            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2
            && item.GetValueToSkill(Skill.EnergyShield) + item.GetValueToSkill(Skill.ShiverArmor) >= 4)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2
            && item.GetValueToSkill(Skill.EnergyShield) + item.GetValueToSkill(Skill.ChillingArmor) >= 4)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
            && item.GetValueToSkill(Skill.BlessedHammer) >= 2
            && item.GetValueToSkill(Skill.Concentration) >= 2)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
            && (item.GetTotalResistFrLrCr() >= 30 && item.GetTotalLifeFromStats(CharacterClass.Paladin) >= 50))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
             && item.GetValueOfStatType(StatType.FasterCastRate) >= 10
             && (item.GetValueToSkill(Skill.BlessedHammer) + item.GetValueToSkill(Skill.Concentration) >= 3))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
            && item.GetValueOfStatType(StatType.FasterCastRate) >= 20
            && item.GetValueToSkill(Skill.BlessedHammer) + item.GetValueToSkill(Skill.Concentration) >= 2)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.NecromancerSkills) == 2
             && item.GetValueOfStatType(StatType.FasterCastRate) == 20
             && item.GetTotalResistFrLrCr() >= 40
             && item.GetValueOfStatType(StatType.SingleSkill1) + item.GetValueOfStatType(StatType.SingleSkill2) + item.GetValueOfStatType(StatType.SingleSkill3) >= 3)
            {
                return true;
            }

            return false;
        }
    }
}
