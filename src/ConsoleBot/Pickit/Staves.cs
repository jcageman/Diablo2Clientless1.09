using D2NG.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Staves
    {
        public static bool ShouldPickupItem(Item item)
        {
            if (item.IsIdentified)
            {
                return ShouldKeepItem(item);
            }

            return true;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2
            && item.GetValueToSkill(D2NG.D2GS.Skill.EnergyShield) == 3
            && item.GetValueToSkill(D2NG.D2GS.Skill.ThunderStorm) == 3)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.SorceressSkills) == 2
            && item.GetValueToSkill(D2NG.D2GS.Skill.EnergyShield) == 3
            && item.GetValueToSkill(D2NG.D2GS.Skill.ShiverArmor) == 3)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
            && item.GetValueToSkill(D2NG.D2GS.Skill.BlessedHammer) >= 2
            && item.GetValueToSkill(D2NG.D2GS.Skill.Concentration) >= 2)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.PaladinSkills) == 2
             && item.GetValueOfStatType(StatType.FasterCastRate) >= 10
             && (item.GetValueToSkill(D2NG.D2GS.Skill.BlessedHammer) >= 1 || item.GetValueToSkill(D2NG.D2GS.Skill.Concentration) >= 1))
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
