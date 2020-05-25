using D2NG.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Weapons
    {
        private static readonly HashSet<string> desirableExceptionalWeapons = new HashSet<string> { "Martel de Fer", "Battle Hammer", "Ancient Axe", "Lance", "Executioner Sword", "Naga" };

        private static readonly HashSet<string> interestingExceptionalWeapons = new HashSet<string> { "Tabar", "Gothic Sword", "Bec-de-Corbin", "Grim Scythe" };

        private static readonly HashSet<string> desirableBows = new HashSet<string> { "Double Bow", "Rune Bow", "Gothic Bow" };

        public static bool ShouldPickupItem(Item item)
        {
            if (item.Quality == QualityType.Rare)
            {
                return true;
            }

            if (item.IsIdentified)
            {
                return ShouldKeepItem(item);
            }

            return false;
        }

        public static bool ShouldKeepItem(Item item)
        {
            var additionalDamage = 0;
            additionalDamage += item.GetValueOfStatType(StatType.EnhancedMaximumDamage);
            additionalDamage += (int)(item.GetValueOfStatType(StatType.MinimumDamage) * 1.5);
            additionalDamage += (int)(item.GetValueOfStatType(StatType.MaximumDamage) * 1.5);
            additionalDamage += (int)(item.GetValueOfStatType(StatType.SecondaryMinimumDamage) * 1.5);
            additionalDamage += (int)(item.GetValueOfStatType(StatType.SecondaryMaximumDamage) * 1.5);

            if(item.Classification == ClassificationType.Bow)
            {
                additionalDamage += item.GetValueOfStatType(StatType.Dexterity);
            }

            if (item.Quality == QualityType.Rare
            && desirableExceptionalWeapons.Contains(item.Name)
            && additionalDamage > 150)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && interestingExceptionalWeapons.Contains(item.Name)
            && additionalDamage > 170)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && desirableBows.Contains(item.Name)
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 10
            && additionalDamage > 130)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && (item.Name == "Large Siege Bow" || item.Name == "Razor Bow")
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 10
            && additionalDamage > 180)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && item.Name == "Flail"
            && item.GetValueOfStatType(StatType.PaladinSkills) >= 2
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 40)
            {
                return true;
            }

            

            return false;
        }
    }
}
