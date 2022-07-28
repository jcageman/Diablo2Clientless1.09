using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using System;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Pickit
    {
        private static readonly Dictionary<ClassificationType, (Func<Item, bool> classicFunction, Func<Item, bool> expansionFunction)> PickupRules
    = new Dictionary<ClassificationType, (Func<Item, bool> classicFunction, Func<Item, bool> expansionFunction)>
    {
       {ClassificationType.AmazonBow, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.AmazonJavelin, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.AmazonSpear, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Amulet, (Amulets.ShouldPickupItemClassic, Amulets.ShouldPickupItemExpansion) },
        {ClassificationType.Armor, (Armors.ShouldPickupItemClassic, Armors.ShouldPickupItemExpansion) },
        {ClassificationType.AssassinKatar, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Axe, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.BarbarianHelm, (Helms.ShouldPickupItemClassic, Helms.ShouldPickupItemExpansion) },
        {ClassificationType.Belt, (Belts.ShouldPickupItemClassic, Belts.ShouldPickupItemExpansion) },
        {ClassificationType.Boots, (Boots.ShouldPickupItemClassic, Boots.ShouldPickupItemExpansion) },
        {ClassificationType.Bow, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Circlet, (Helms.ShouldPickupItemClassic, Helms.ShouldPickupItemExpansion) },
        {ClassificationType.Club, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Crossbow, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Dagger, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.DruidPelt, (Helms.ShouldPickupItemClassic, Helms.ShouldPickupItemExpansion) },
        {ClassificationType.Gem, (Gems.ShouldPickupItemClassic, Gems.ShouldPickupItemExpansion) },
        {ClassificationType.Gloves, (Gloves.ShouldPickupItemClassic, Gloves.ShouldPickupItemExpansion) },
        {ClassificationType.GrandCharm, (GrandCharms.ShouldPickupItemExpansion, GrandCharms.ShouldPickupItemExpansion) },
        {ClassificationType.Hammer, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Helm, (Helms.ShouldPickupItemClassic, Helms.ShouldPickupItemExpansion) },
        {ClassificationType.Javelin, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Jewel, (Jewels.ShouldPickupItemExpansion, Jewels.ShouldPickupItemExpansion) },
        {ClassificationType.LargeCharm, (LargeCharms.ShouldPickupItemExpansion, LargeCharms.ShouldPickupItemExpansion) },
        {ClassificationType.Mace, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.NecromancerShrunkenHead, (Shields.ShouldPickupItemClassic, Shields.ShouldPickupItemExpansion) },
        {ClassificationType.PaladinShield, (Shields.ShouldPickupItemClassic, Shields.ShouldPickupItemExpansion) },
        {ClassificationType.Polearm, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Ring, (Rings.ShouldPickupItemClassic, Rings.ShouldPickupItemExpansion) },
        {ClassificationType.Rune, (Runes.ShouldPickupItemExpansion, Runes.ShouldPickupItemExpansion) },
        {ClassificationType.Scepter, (Staves.ShouldPickupItemClassic, Staves.ShouldPickupItemExpansion) },
        {ClassificationType.Shield, (Shields.ShouldPickupItemClassic, Shields.ShouldPickupItemExpansion) },
        {ClassificationType.SmallCharm, (SmallCharms.ShouldPickupItemExpansion, SmallCharms.ShouldPickupItemExpansion) },
        {ClassificationType.SorceressOrb, (Staves.ShouldPickupItemClassic, Staves.ShouldPickupItemExpansion) },
        {ClassificationType.Spear, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Staff, (Staves.ShouldPickupItemClassic, Staves.ShouldPickupItemExpansion) },
        {ClassificationType.Sword, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.ThrowingAxe, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.ThrowingKnife, (Weapons.ShouldPickupItemClassic, Weapons.ShouldPickupItemExpansion) },
        {ClassificationType.Wand, (Staves.ShouldPickupItemClassic, Staves.ShouldPickupItemExpansion) }
    };

        private static readonly Dictionary<ClassificationType, (Func<Item, bool> classicFunction, Func<Item, bool> expansionFunction)> KeepRules
    = new Dictionary<ClassificationType, (Func<Item, bool> classicFunction, Func<Item, bool> expansionFunction)>
    {
        {ClassificationType.AmazonBow, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.AmazonJavelin, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.AmazonSpear, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Amulet, (Amulets.ShouldKeepItemClassic, Amulets.ShouldKeepItemExpansion) },
        {ClassificationType.Armor, (Armors.ShouldKeepItemClassic, Armors.ShouldKeepItemExpansion) },
        {ClassificationType.AssassinKatar, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Axe, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.BarbarianHelm, (Helms.ShouldKeepItemClassic, Helms.ShouldKeepItemExpansion) },
        {ClassificationType.Belt, (Belts.ShouldKeepItemClassic, Belts.ShouldKeepItemExpansion) },
        {ClassificationType.Boots, (Boots.ShouldKeepItemClassic, Boots.ShouldKeepItemExpansion) },
        {ClassificationType.Bow, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Circlet, (Helms.ShouldKeepItemClassic, Helms.ShouldKeepItemExpansion) },
        {ClassificationType.Club, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Crossbow, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Dagger, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.DruidPelt, (Helms.ShouldKeepItemClassic, Helms.ShouldKeepItemExpansion) },
        {ClassificationType.Gem, (Gems.ShouldKeepItemClassic, Gems.ShouldKeepItemExpansion) },
        {ClassificationType.Gloves, (Gloves.ShouldKeepItemClassic, Gloves.ShouldKeepItemExpansion) },
        {ClassificationType.GrandCharm, (GrandCharms.ShouldKeepItemExpansion, GrandCharms.ShouldKeepItemExpansion) },
        {ClassificationType.Hammer, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Helm, (Helms.ShouldKeepItemClassic, Helms.ShouldKeepItemExpansion) },
        {ClassificationType.Javelin, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Jewel, (Jewels.ShouldKeepItemExpansion, Jewels.ShouldKeepItemExpansion) },
        {ClassificationType.LargeCharm, (LargeCharms.ShouldKeepItemExpansion, LargeCharms.ShouldKeepItemExpansion) },
        {ClassificationType.Mace, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.NecromancerShrunkenHead, (Shields.ShouldKeepItemClassic, Shields.ShouldKeepItemExpansion) },
        {ClassificationType.PaladinShield, (Shields.ShouldKeepItemClassic, Shields.ShouldKeepItemExpansion) },
        {ClassificationType.Polearm, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Ring, (Rings.ShouldKeepItemClassic, Rings.ShouldKeepItemExpansion) },
        {ClassificationType.Rune, (Runes.ShouldKeepItemExpansion, Runes.ShouldKeepItemExpansion) },
        {ClassificationType.Scepter, (Staves.ShouldKeepItemClassic, Staves.ShouldKeepItemExpansion) },
        {ClassificationType.Shield, (Shields.ShouldKeepItemClassic, Shields.ShouldKeepItemExpansion) },
        {ClassificationType.SmallCharm, (SmallCharms.ShouldKeepItemExpansion, SmallCharms.ShouldKeepItemExpansion) },
        {ClassificationType.SorceressOrb, (Staves.ShouldKeepItemClassic, Staves.ShouldKeepItemExpansion) },
        {ClassificationType.Spear, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Staff, (Staves.ShouldKeepItemClassic, Staves.ShouldKeepItemExpansion) },
        {ClassificationType.Sword, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.ThrowingAxe, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.ThrowingKnife, (Weapons.ShouldKeepItemClassic, Weapons.ShouldKeepItemExpansion) },
        {ClassificationType.Wand, (Staves.ShouldKeepItemClassic, Staves.ShouldKeepItemExpansion) }
    };

        public static bool ShouldPickupItem(Game game, Item item, bool shouldPickupGoldItems)
        {
            return ShouldPickupItem(game.ClientCharacter.IsExpansion, game.Me.Class, shouldPickupGoldItems, item);
        }

        public static bool ShouldPickupItem(bool isExpansion, CharacterClass characterClass, bool shouldPickupGoldItems, Item item)
        {
            if (shouldPickupGoldItems && GoldItems.ShouldPickupItem(item))
            {
                return true;
            }

            if (item.IsGold && item.Amount > 5000)
            {
                return true;
            }

            if (item.Name == ItemName.EssenceOfAnguish
            || item.Name == ItemName.EssenceOfPain
            || item.Name == ItemName.EssenceOfSuffering
            || item.Name == ItemName.EssenceOfHatred
            || item.Name == ItemName.EssenceOfTerror
            || item.Name == ItemName.StandardOfHeroes
            || item.Name == ItemName.TokenOfAbsolution)
            {
                return true;
            }

            if (item.IsIdentified)
            {
                return ShouldKeepItem(isExpansion, characterClass, item);
            }

            if (Sets.ShouldPickupItem(item))
            {
                return true;
            }

            if (PickupRules.TryGetValue(item.Classification, out var checkMethods))
            {
                return CheckItem(isExpansion, item, checkMethods.classicFunction, checkMethods.expansionFunction);
            }

            return false;
        }
        public static bool ShouldKeepItem(Game game, Item item)
        {
            return ShouldKeepItem(game.ClientCharacter.IsExpansion, game.Me.Class, item);
        }

        public static bool ShouldKeepItem(bool isExpansion, CharacterClass characterClass, Item item)
        {
            if (!item.IsIdentified)
            {
                return true;
            }

            if (!CanTouchInventoryItem(isExpansion, characterClass, item))
            {
                return true;
            }

            if (Sets.ShouldKeepItem(item))
            {
                return true;
            }

            if (item.Name == ItemName.EssenceOfAnguish
                || item.Name == ItemName.EssenceOfPain
                || item.Name == ItemName.EssenceOfSuffering
                || item.Name == ItemName.StandardOfHeroes
                || item.Name == ItemName.TokenOfAbsolution)
            {
                return true;
            }

            if (KeepRules.TryGetValue(item.Classification, out var checkMethods))
            {
                return CheckItem(isExpansion, item, checkMethods.classicFunction, checkMethods.expansionFunction);
            }

            return false;
        }

        private static bool CheckItem(bool isExpansion, Item item, Func<Item, bool> classicFunction, Func<Item, bool> expansionFunction)
        {
            if (isExpansion)
            {
                return expansionFunction(item);
            }
            else
            {
                return classicFunction(item);
            }
        }

        public static bool ShouldGamble(Self self, Item item)
        {
            if (item.IsIdentified)
            {
                return false;
            }
            
            if(self.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] >= 90)
            {
                return item.Name == ItemName.Amulet;
            }

            if (item.Name == ItemName.BoneHelm && self.Attributes[D2NG.Core.D2GS.Players.Attribute.Level] >= 86)
            {
                return true;
            }

            if (item.Name == ItemName.Boots || item.Name == ItemName.HeavyBoots)
            {
                return true;
            }
            /*
            if (item.Classification == ClassificationType.Ring)
            {
                return true;
            }
             */
            /*
            if (item.Name == ItemName.BoneShield)
            {
                return true;
            }

            if (item.Name == ItemName.SplintMail)
            {
                return true;
            }

            if (item.Name == ItemName.JaredsStone)
            {
                return true;
            }
            if (item.Name == ItemName.Cap)
            {
                return true;
            }

            if (item.Name == ItemName.SplintMail)
            {
                return true;
            }

            if (item.Name == ItemName.LongWarBow)
            {
                return true;
            }
            */
            return false;
        }

        public static bool CanTouchInventoryItem(Game game, Item item)
        {
            return CanTouchInventoryItem(game.ClientCharacter.IsExpansion, game.Me.Class, item);
        }

        public static bool CanTouchInventoryItem(bool isExpansion, CharacterClass characterClass, Item item)
        {
            if (item.Container != ContainerType.Inventory)
            {
                return true;
            }

            var defaultInventoryItems = new List<ItemName>() { ItemName.TomeOfTownPortal, ItemName.TomeofIdentify, ItemName.HoradricCube, ItemName.WirtsLeg };
            if (defaultInventoryItems.Contains(item.Name))
            {
                return false;
            }

            var defaultClassifications = new List<ClassificationType>() { ClassificationType.HealthPotion, ClassificationType.ManaPotion, ClassificationType.RejuvenationPotion };
            if (defaultClassifications.Contains(item.Classification))
            {
                return false;
            }

            if (characterClass == CharacterClass.Amazon && item.Name == ItemName.Arrows)
            {
                return false;
            }

            if (characterClass == CharacterClass.Amazon && item.Name == ItemName.Javelin)
            {
                return false;
            }

            if (isExpansion
                && item.Location.Y >= 4
                && (item.Classification == ClassificationType.SmallCharm || item.Classification == ClassificationType.LargeCharm|| item.Classification == ClassificationType.GrandCharm))
            {
                return false;
            }

            return true;
        }

        public static bool SendItemToKeepToExternalClient(Item item)
        {
            if(item.Name == ItemName.Ring && item.Quality == QualityType.Unique)
            {
                return false;
            }

            if(item.Classification == ClassificationType.Gem)
            {
                return false;
            }

            if (item.Name == ItemName.SolRune || item.Name == ItemName.NefRune)
            {
                return false;
            }

            // Crafting blood gloves material
            if (item.Quality == QualityType.Magical
                && (item.Name == ItemName.HeavyGloves || item.Name == ItemName.SharkskinGloves || item.Name == ItemName.VampireboneGloves))
            {
                return false;
            }

            return true;
        }
    }
}
