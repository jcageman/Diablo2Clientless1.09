using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Shields
    {
        public static bool ShouldPickupItem(Item item)
        {
            if (item.IsIdentified)
            {
                return ShouldKeepItem(item);
            }

            if(item.Quality == QualityType.Unique && item.Name == "Gothic Shield")
            {
                return true;
            }

            return item.Quality == QualityType.Rare;
        }

        public static bool ShouldKeepItem(Item item)
        {
            if (item.Quality == QualityType.Unique && item.Name == "Gothic Shield" && item.GetTotalResist() > 155)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResist() > 70)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResist() > 50 && item.GetValueOfStatType(StatType.DamageToMana) >= 5)
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

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResist() > 80)
            {
                return true;
            }

            if (item.Name == "Pavise" && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70)
            {
                return true;
            }

            if (item.Name == "Grim Shield" && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetValueOfStatType(StatType.EnhancedDefense) >= 90)
            {
                return true;
            }

            if ((item.Name == "Grim Shield" || item.Name == "Tower Shield") && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetValueOfStatType(StatType.EnhancedDefense) >= 30 && item.GetTotalResist() > 50)
            {
                return true;
            }

            return false;
        }
    }
}
