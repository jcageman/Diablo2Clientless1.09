using D2NG.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class GoldItems
    {
        public static bool ShouldPickupItem(Item item)
        {
            if (item.Classification == ClassificationType.Gem
                            && (item.Name.Contains("Flawless") || item.Name.Contains("Perfect")))
            {
                return true;
            }

            var valuableMagicClassifications = new HashSet<ClassificationType> { ClassificationType.Staff, ClassificationType.Wand, ClassificationType.Scepter };
            if (item.Quality == QualityType.Magical && valuableMagicClassifications.Contains(item.Classification))
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

            //Exceptional armors are worth it
            if (item.Quality == QualityType.Magical
                && item.Classification == ClassificationType.Armor
                && item.Type.StartsWith("x"))
            {
                return true;
            }

            //Valuable shields
            var valuableShieldTypes = new HashSet<string> {
                "xit", "xow", "xts", "xsh", "xpk"};
            if (item.Quality == QualityType.Magical
                && item.Classification == ClassificationType.Shield
                && valuableShieldTypes.Contains(item.Type))
            {
                return true;
            }

            //Valuable Gloves
            var valuableGloveTypes = new HashSet<string> {
                "xtg", "xhg"};
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
