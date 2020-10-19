using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Shields
    {
        static HashSet<ItemName> DesirableShields = new HashSet<ItemName> { ItemName.BoneShield, ItemName.GrimShield, ItemName.SpikedShield, ItemName.BarbedShield };
        public static bool ShouldPickupItem(Item item)
        {
            /*
            if(item.Quality == QualityType.Unique && item.Name == "Gothic Shield")
            {
                return true;
            }
 
            if (item.Quality == QualityType.Unique && item.Name == "Bone Shield")
            {
                return true;
            }
                       */
            return item.Quality == QualityType.Rare;
        }

        public static bool ShouldKeepItem(Item item)
        {
            /*
            if (item.Quality == QualityType.Unique && item.Name == "Bone Shield")
            {
                return true;
            }
            
            if (item.Quality == QualityType.Unique && item.Name == "Gothic Shield" && item.GetTotalResist() > 155)
            {
                return true;
            }
            */

            if (DesirableShields.Contains(item.Name))
            {
                if(item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResist() > 50 && item.GetValueOfStatType(StatType.DamageToMana) >= 5)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.PaladinSkills) >= 2 && item.GetTotalResist() > 50)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResist() > 50)
                {
                    return true;
                }
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResist() > 70 && item.GetTotalLifeFromStats() > 30)
            {
                return true;
            }

            if (item.Name == ItemName.Pavise && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetValueOfStatType(StatType.EnhancedDefense) >= 90)
            {
                return true;
            }

            if ((item.Name == ItemName.GrimShield) && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50 && item.GetTotalResist() > 50)
            {
                return true;
            }

            if ((item.Name == ItemName.TowerShield) && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResist() > 50 && item.GetValueOfStatType(StatType.ReducedRequirements) >= 20)
            {
                return true;
            }

            return false;
        }
    }
}
