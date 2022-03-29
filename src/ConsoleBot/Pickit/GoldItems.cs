using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class GoldItems
    {
        static HashSet<ItemName> flawlessGems = new HashSet<ItemName> { ItemName.FlawlessAmethyst, ItemName.FlawlessDiamond, ItemName.FlawlessEmerald, ItemName.FlawlessRuby, ItemName.FlawlessSapphire, ItemName.FlawlessSkull, ItemName.FlawlessTopaz };
        static HashSet<ItemName> perfectGems = new HashSet<ItemName> { ItemName.PerfectAmethyst, ItemName.PerfectDiamond, ItemName.PerfectEmerald, ItemName.PerfectRuby, ItemName.PerfectSapphire, ItemName.PerfectSkull, ItemName.PerfectTopaz };
        public static bool ShouldPickupItem(Item item)
        {
            if (item.Classification == ClassificationType.Essence)
            {
                return true;
            }

            if (item.Classification == ClassificationType.Gem
                            && (flawlessGems.Contains(item.Name) || perfectGems.Contains(item.Name)))
            {
                return true;
            }

            var valuableClassifications = new HashSet<ClassificationType> { ClassificationType.Staff, ClassificationType.Wand, ClassificationType.Scepter };
            if (item.Quality != QualityType.Rare && valuableClassifications.Contains(item.Classification))
            {
                return true;
            }

            //Valuable normal armors to pickup
            var valuableNormalArmorTypes = new HashSet<string> {
                "plt", "fld", "gth", "ful", "aar", "aar" };
            if (item.Quality == QualityType.Magical
                && item.Classification == ClassificationType.Armor
                && valuableNormalArmorTypes.Contains(item.Type))
            {
                return true;
            }

            //Exceptional & elite armors are worth it
            if ((item.Quality == QualityType.Magical || item.Quality == QualityType.Normal)
                && item.Classification == ClassificationType.Armor
                && (item.Type.StartsWith("x") || item.Type.StartsWith("u")))
            {
                return true;
            }

            //Valuable shields
            var valuableShieldTypes = new HashSet<string> {
                "xit", "xow", "xts", "xsh", "xpk", "uit", "uow", "uts", "ush", "upk"};
            if (item.Quality == QualityType.Magical
                && item.Classification == ClassificationType.Shield
                && valuableShieldTypes.Contains(item.Type))
            {
                return true;
            }

            //Valuable Gloves
            var valuableGloveTypes = new HashSet<string> {
                "xtg", "xhg", "utg", "uhg"};
            if (item.Quality == QualityType.Magical
                && item.Classification == ClassificationType.Gloves
                && valuableGloveTypes.Contains(item.Type))
            {
                return true;
            }

            return false;
        }
    }
}
