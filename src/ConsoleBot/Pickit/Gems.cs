using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Gems
    {
        public static bool ShouldPickupItemClassic(Item item)
        {
            return ShouldKeepItemClassic(item);
        }

        public static bool ShouldPickupItemExpansion(Item item)
        {
            return ShouldKeepItemExpansion(item);
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            if (item.Name == ItemName.ChippedSkull || item.Name == ItemName.FlawlessSkull || item.Name == ItemName.PerfectSkull)
            {
                return true;
            }

            if (item.Name == ItemName.ChippedAmethyst || item.Name == ItemName.FlawlessAmethyst || item.Name == ItemName.PerfectAmethyst)
            {
                return true;
            }

            if (item.Name == ItemName.ChippedEmerald || item.Name == ItemName.FlawlessEmerald || item.Name == ItemName.PerfectEmerald)
            {
                return true;
            }

            if (item.Name == ItemName.ChippedRuby || item.Name == ItemName.FlawlessRuby || item.Name == ItemName.PerfectRuby)
            {
                return true;
            }

            if (item.Name == ItemName.ChippedDiamond || item.Name == ItemName.FlawlessDiamond || item.Name == ItemName.PerfectDiamond)
            {
                return true;
            }

            if (item.Name == ItemName.ChippedTopaz || item.Name == ItemName.FlawlessTopaz || item.Name == ItemName.PerfectTopaz)
            {
                return true;
            }

            if (item.Name == ItemName.ChippedSapphire || item.Name == ItemName.FlawlessSapphire || item.Name == ItemName.PerfectSapphire)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldKeepItemClassic(Item item)
        {
            if(item.Name == ItemName.FlawlessSkull || item.Name == ItemName.PerfectSkull)
            {
                return true;
            }
            /*

            if (item.Name == ItemName.FlawlessAmethyst || item.Name == ItemName.PerfectAmethyst)
            {
                return true;
            }

            if (item.Name == ItemName.FlawlessEmerald || item.Name == ItemName.PerfectEmerald)
            {
                return true;
            }

            if (item.Name == ItemName.FlawlessRuby || item.Name == ItemName.PerfectRuby)
            {
                return true;
            }

            if (item.Name == ItemName.FlawlessDiamond || item.Name == ItemName.PerfectDiamond)
            {
                return true;
            }
            */

            return false;
        }
    }
}
