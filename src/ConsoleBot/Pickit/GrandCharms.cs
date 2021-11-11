using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class GrandCharms
    {
        public static bool ShouldPickupItemExpansion(Item item)
        {
            return true;
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            if (item.HasSkillTab())
            {
                return true;
            }

            /*
            if (item.GetValueToSkillTab(SkillTab.SorceressLightningSpells) > 0
                || item.GetValueToSkillTab(SkillTab.SorceressFireSpells) > 0
                || item.GetValueToSkillTab(SkillTab.SorceressColdSpells) > 0
                || item.GetValueToSkillTab(SkillTab.AmazonJavelinAndSpearSkills) > 0
                || item.GetValueToSkillTab(SkillTab.PaladinCombatSkills) > 0
                || item.GetValueToSkillTab(SkillTab.PaladinOffensiveAuras) > 0
                || item.GetValueToSkillTab(SkillTab.NecromancerPoisonAndBoneSpells) > 0)
            {
                return true;
            }
            */
            if (item.GetValueOfStatType(StatType.AttackRating) >= 40
            && item.GetValueOfStatType(StatType.MaximumDamage) >= 7)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterRunWalk) >= 7
            && item.GetValueOfStatType(StatType.MaximumDamage) >= 7)
            {
                return true;
            }

            if (item.GetTotalLifeFromStats(D2NG.Core.D2GS.Enums.CharacterClass.Sorceress) >= 40)
            {
                return true;
            }

            if (item.GetTotalLifeFromStats(D2NG.Core.D2GS.Enums.CharacterClass.Sorceress) >= 20 && (
                    item.GetValueOfStatType(StatType.FasterHitRecovery) >= 12 || item.GetTotalResistFrLrCr() >= 30) )
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.ExtraGold) >= 30)
            {
                return true;
            }

            return false;
        }
    }
}
