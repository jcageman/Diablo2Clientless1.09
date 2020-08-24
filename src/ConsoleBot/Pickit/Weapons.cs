using D2NG.Core.D2GS.Items;
using System;
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

            if(item.Quality == QualityType.Unique && item.Name == "Blade")
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItem(Item item)
        {
            var additionalDamage = item.GetValueOfStatType(StatType.EnhancedMaximumDamage);
            additionalDamage += (int)1.5 * Math.Max(item.GetValueOfStatType(StatType.MinimumDamage), item.GetValueOfStatType(StatType.SecondaryMinimumDamage));
            additionalDamage += (int)1.5 * Math.Max(item.GetValueOfStatType(StatType.MaximumDamage), item.GetValueOfStatType(StatType.SecondaryMaximumDamage));

            if (item.Classification == ClassificationType.Bow)
            {
                additionalDamage += item.GetValueOfStatType(StatType.Dexterity);
                additionalDamage += (item.GetValueOfStatType(StatType.AmazonSkills) * 5);
            }
            else
            {
                additionalDamage += (item.GetValueOfStatType(StatType.BarbarianSkills) * 5);
            }

            if (item.Quality == QualityType.Rare
            && desirableExceptionalWeapons.Contains(item.Name)
            && additionalDamage > 160)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && interestingExceptionalWeapons.Contains(item.Name)
            && (additionalDamage > 200 || item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 190))
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && desirableBows.Contains(item.Name)
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 10
            && additionalDamage > 150)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && desirableBows.Contains(item.Name)
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 10
            && item.GetValueOfStatType(StatType.ReducedRequirements) >= 30
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

            if (item.Quality == QualityType.Rare
            && item.GetValueOfStatType(StatType.PaladinSkills) >= 2
            && item.GetTotalResist() >= 30 && item.IsWeapon)
            {
                return true;
            }

            /*
            if (item.Quality == QualityType.Unique && item.Name == "Blade" && item.GetValueOfStatType(StatType.FasterCastRate) == 50)
            {
                return true;
            }
            */
            return false;
        }
    }
}
