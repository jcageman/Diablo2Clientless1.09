using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Armors
    {
        private static readonly HashSet<ItemName> casterArmors = new HashSet<ItemName> {
            ItemName.QuiltedArmor, ItemName.LeatherArmor, ItemName.HardLeatherArmor, ItemName.StuddedLeather, ItemName.RingMail, ItemName.ScaleMail,
            ItemName.ChainMail, ItemName.BreastPlate, ItemName.SplintMail, ItemName.LightPlate, ItemName.GhostArmor, ItemName.SerpentskinArmor,
            ItemName.DemonhideArmor, ItemName.TrellisedArmor, ItemName.LinkedMail, ItemName.MagePlate };

        private static readonly HashSet<ItemName> defArmors = new HashSet<ItemName> {
            ItemName.OrnatePlate, ItemName.ChaosArmor, ItemName.EmbossedPlate, ItemName.SharktoothArmor, ItemName.TemplarCoat, ItemName.MagePlate, ItemName.RussetArmor};

        private static readonly HashSet<ItemName> socketedLightArmors = new HashSet<ItemName> {
            ItemName.QuiltedArmor, ItemName.LeatherArmor, ItemName.HardLeatherArmor, ItemName.StuddedLeather, ItemName.RingMail, ItemName.LightPlate,
            ItemName.GhostArmor, ItemName.SerpentskinArmor, ItemName.DemonhideArmor, ItemName.TrellisedArmor, ItemName.LinkedMail, ItemName.MagePlate,
            ItemName.DuskShroud, ItemName.Wyrmhide, ItemName.ScarabHusk, ItemName.WireFleece, ItemName.DiamondMail, ItemName.ArchonPlate, ItemName.KrakenShell,
            ItemName.HellforgePlate, ItemName.LacqueredPlate, ItemName.ShadowPlate,ItemName.SacredArmor};
        public static bool ShouldPickupItemClassic(Item item)
        {
            if (item.Quality == QualityType.Rare || item.Quality == QualityType.Unique)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldPickupItemExpansion(Item item)
        {
            if (item.Quality == QualityType.Unique)
            {
                return true;
            }

            if (item.Quality == QualityType.Magical && socketedLightArmors.Contains(item.Name))
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            if(item.Quality == QualityType.Unique)
            {
                switch(item.Name)
                {
                    case ItemName.SerpentskinArmor:
                        return item.GetValueOfStatType(StatType.ColdResistance) >= 34 && item.GetValueOfStatType(StatType.AllSkills) > 0 && !item.Ethereal;
                    //case ItemName.Cuirass:
                    case ItemName.MeshArmor:
                        return item.Ethereal;
                    case ItemName.RussetArmor:
                        return item.Ethereal || item.GetValueOfStatType(StatType.Vitality) > 0;
                    //case ItemName.MagePlate:
                    //case ItemName.TemplarCoat:
                    //case ItemName.ChaosArmor:
                    case ItemName.WireFleece:
                    case ItemName.BalrogSkin:
                        return true;
                }
            }

            if (item.Quality == QualityType.Magical && socketedLightArmors.Contains(item.Name) && item.Sockets == 4 && item.GetValueOfStatType(StatType.Life) >= 40)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItemClassic(Item item)
        {
            if (item.Name == ItemName.OrnatePlate
                && item.Quality == QualityType.Rare
                && item.GetValueOfStatType(StatType.EnhancedDefense) >= 80
                && item.GetValueOfStatType(StatType.ReducedRequirements) <= -20
                && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 60)
            {
                return true;
            }

            if (item.Name == ItemName.OrnatePlate
            && item.Quality == QualityType.Rare
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 90
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 70)
            {
                return true;
            }

            if (defArmors.Contains(item.Name)
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 70
            && item.GetValueOfStatType(StatType.ReducedRequirements) <= -30
            && item.GetTotalResistFrLrCr() >= 40
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 50)
            {
                return true;
            }

            if (casterArmors.Contains(item.Name) && item.GetValueOfStatType(StatType.Life) >= 40)
            {
                if (item.GetTotalResistFrLrCr() >= 60 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 55)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 40 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 55)
                {
                    return true;
                }

                if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 40)
                {
                    return true;
                }
            }

            if (item.GetValueOfStatType(StatType.FasterHitRecovery) >= 10 && item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Sorceress) >= 55)
            {
                return true;
            }

            if (item.GetTotalResistFrLrCr() >= 70 && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 60)
            {
                return true;
            }

            if (item.Name == ItemName.MagePlate
            && item.GetValueOfStatType(StatType.EnhancedDefense) >= 60
            && item.GetTotalResistFrLrCr() >= 50
            && item.GetTotalLifeFromStats(CharacterClass.Barbarian) >= 30)
            {
                return true;
            }

            if(item.Sockets > 0)
            {
                if (item.Name == ItemName.QuiltedArmor && item.Quality == QualityType.Unique)
                {
                    return true;
                }

                if (item.Name == ItemName.LeatherArmor && item.Quality == QualityType.Unique)
                {
                    return true;
                }


                if (item.Name == ItemName.ScaleMail && item.Quality == QualityType.Unique)
                {
                    return true;
                }

                if (item.Name == ItemName.GothicPlate && item.Quality == QualityType.Unique)
                {
                    return true;
                }

                if (item.Name == ItemName.StuddedLeather && item.Quality == QualityType.Unique)
                {
                    return true;
                }

                if (item.Name == ItemName.FullPlateMail && item.Quality == QualityType.Unique)
                {
                    return true;
                }
            }
            
            return false;
        }
    }
}
