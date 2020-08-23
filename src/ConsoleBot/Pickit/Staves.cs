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
            && item.GetValueToSkill(Skill.EnergyShield) == 3
            && item.GetValueToSkill(Skill.ThunderStorm) == 3)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2
            && item.GetValueToSkill(Skill.EnergyShield) == 3
            && item.GetValueToSkill(Skill.ShiverArmor) == 3)
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
            && (item.GetTotalResist() >= 30 || item.GetTotalLifeFromStats() >= 50))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
             && item.GetValueOfStatType(StatType.FasterCastRate) >= 10
             && (item.GetValueToSkill(Skill.BlessedHammer) >= 1 || item.GetValueToSkill(Skill.Concentration) >= 1))
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
            && item.GetValueOfStatType(StatType.FasterCastRate) >= 20)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.NecromancerSkills) == 2
             && item.GetValueOfStatType(StatType.FasterCastRate) == 20
             && item.GetTotalResist() >= 30)
            {
                return true;
            }

            return false;
        }
    }
}
