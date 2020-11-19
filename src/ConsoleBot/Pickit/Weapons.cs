using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Weapons
    {
        private static readonly HashSet<ItemName> desirableExceptionalWeapons = new HashSet<ItemName> { ItemName.MarteldeFer, ItemName.BattleHammer, ItemName.AncientAxe, ItemName.Lance, ItemName.ExecutionerSword, ItemName.Naga };

        private static readonly HashSet<ItemName> interestingExceptionalWeapons = new HashSet<ItemName> { ItemName.Tabar, ItemName.GothicSword, ItemName.BecDeCorbin, ItemName.GrimScythe, ItemName.Zweihander };

        private static readonly HashSet<ItemName> desirableBows = new HashSet<ItemName> { ItemName.DoubleBow, ItemName.RuneBow };

        public static bool ShouldPickupItem(Item item)
        {
            if (item.Quality == QualityType.Rare)
            {
                return true;
            }

            if(item.Quality == QualityType.Unique && item.Name == ItemName.Blade)
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
            && additionalDamage > 180 && item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 170)
            {
                return true;
            }

            var desirablePvmWeapons = new HashSet<ItemName> { ItemName.MarteldeFer, ItemName.AncientAxe, ItemName.Lance, ItemName.ExecutionerSword };
            if (item.Quality == QualityType.Rare
            && desirablePvmWeapons.Contains(item.Name)
            && additionalDamage > 140 && item.GetValueOfStatType(StatType.ReducedRequirements) <= -20 && item.GetValueOfStatType(StatType.MinimumLifeStolenPerHit) >= 6)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && interestingExceptionalWeapons.Contains(item.Name)
            && (additionalDamage > 210 && item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 190))
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && item.Name == ItemName.GothicBow
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 10
            && item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 150
            && additionalDamage > 170)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && desirableBows.Contains(item.Name)
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 20
            && item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 150
            && additionalDamage > 170)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && (item.Name == ItemName.LargeSiegeBow || item.Name == ItemName.RazorBow)
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 20
            && item.GetValueOfStatType(StatType.EnhancedMaximumDamage) >= 170
            && additionalDamage > 180)
            {
                return true;
            }

            if (item.Quality == QualityType.Rare
            && item.Name == ItemName.Flail
            && item.GetValueOfStatType(StatType.PaladinSkills) >= 2
            && item.GetValueOfStatType(StatType.IncreasedAttackSpeed) >= 40
            && (item.GetTotalResistFrLrCr() > 10 || item.GetTotalLifeFromStats(CharacterClass.Paladin) > 10))
            {
                return true;
            }

            /*
            if (item.Quality == QualityType.Unique && item.Name == ItemName.Blade && item.GetValueOfStatType(StatType.FasterCastRate) == 50)
            {
                return true;
            }
            */
            return false;
        }
    }
}
