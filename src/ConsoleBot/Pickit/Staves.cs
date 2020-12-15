using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;

namespace ConsoleBot.Pickit
{
    public static class Staves
    {
        public static bool ShouldPickupItem(Item item)
        {
            return true;
        }

        public static bool ShouldKeepItem(Item item)
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
