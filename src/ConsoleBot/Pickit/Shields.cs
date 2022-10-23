using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Shields
    {
        static HashSet<ItemName> DesirableShields = new HashSet<ItemName> { ItemName.BoneShield, ItemName.GrimShield, ItemName.SpikedShield, ItemName.BarbedShield };

        public static bool ShouldPickupItemExpansion(Item item)
        {
            if (item.Quality == QualityType.Unique)
            {
                switch (item.Name)
                {
                    case ItemName.Monarch:
                    //case ItemName.GrimShield:
                    //case ItemName.GildedShield:
                    case ItemName.Aegis:
                            return !item.Ethereal;
                }
            }

            if (item.Name == ItemName.Monarch && item.Quality == QualityType.Magical)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldPickupItemClassic(Item item)
        {
            if(item.Quality == QualityType.Unique && item.Name == ItemName.GothicShield && item.Level >= 90)
            {
                return true;
            }
 
            if (item.Quality == QualityType.Unique && item.Name == ItemName.BoneShield && item.Level >= 90)
            {
                return true;
            }

            return item.Quality == QualityType.Rare;
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            if(item.Name == ItemName.Monarch && item.Quality == QualityType.Magical && item.Sockets == 4 && item.GetValueOfStatType(StatType.IncreasedBlocking) > 0)
            {
                return true;
            }

            if (item.Quality == QualityType.Unique)
            {
                switch (item.Name)
                {
                    //case ItemName.GrimShield:
                    //    return item.GetValueOfStatType(StatType.AllSkills) > 0;
                    case ItemName.Monarch:
                    //case ItemName.GildedShield:
                        return !item.Ethereal;
                    case ItemName.Aegis:
                        return item.GetValueOfStatType(StatType.ColdResistance) >= 60;
                }
            }

            return false;
        }

        public static bool ShouldKeepItemClassic(Item item)
        {
            if(item.Sockets > 0)
            {
                if (item.Quality == QualityType.Unique && item.Name == ItemName.GothicShield && item.GetTotalResistFrLrCr() > 140)
                {
                    return true;
                }

                if (item.Quality == QualityType.Unique && item.Name == ItemName.BoneShield && item.Level >= 90)
                {
                    return true;
                }
            }

            if (DesirableShields.Contains(item.Name))
            {
                if(item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResistFrLrCr() > 50 && item.GetValueOfStatType(StatType.DamageToMana) >= 5)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.PaladinSkills) >= 2 && item.GetTotalResistFrLrCr() > 50 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.PaladinSkills) >= 2 && item.GetTotalResistFrLrCr() > 70 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResistFrLrCr() > 50)
                {
                    return true;
                }
            }

            if (item.Name == ItemName.GrimShield && item.GetValueOfStatType(StatType.PaladinSkills) >= 2 && item.GetTotalResistFrLrCr() > 50 && item.GetValueOfStatType(StatType.EnhancedDefense) >= 50)
            {
                return true;
            }

            if (item.Name == ItemName.BarbedShield && item.GetValueOfStatType(StatType.PaladinSkills) >= 2 && item.GetTotalResistFrLrCr() > 50)
            {
                return true;
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResistFrLrCr() > 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) > 30)
            {
                return true;
            }

            if (item.Name == ItemName.Pavise
                && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 90
                && (item.GetValueOfStatType(StatType.MaximumDamage) >= 4 || item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30))
            {
                return true;
            }

            if (item.Name == ItemName.GrimShield && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70 && item.GetTotalResistFrLrCr() > 50)
            {
                return true;
            }

            if (item.Name == ItemName.TowerShield && item.GetValueOfStatType(StatType.FasterBlockRate) >= 30 && item.GetTotalResistFrLrCr() > 50 && item.GetValueOfStatType(StatType.ReducedRequirements) <= -20)
            {
                return true;
            }

            return false;
        }
    }
}
