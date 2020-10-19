using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Pickit
{
    public static class Gems
    {
        public static bool ShouldPickupItem(Item item)
        {
            return ShouldKeepItem(item);
        }

        public static bool ShouldKeepItem(Item item)
        {
            if(item.Name == ItemName.FlawlessSkull || item.Name == ItemName.PerfectSkull)
            {
                return true;
            }
            /*
            if (item.Name == "Flawless Amethyst" || item.Name == "Perfect Amethyst")
            {
                return true;
            }

            if (item.Name == "Flawless Emerald" || item.Name == "Perfect Emerald")
            {
                return true;
            }


            if (item.Name == "Flawless Ruby" || item.Name == "Perfect Ruby")
            {
                return true;
            }

            if (item.Name == "Flawless Diamond" || item.Name == "Perfect Diamond")
            {
                return true;
            }
            */

            return false;
        }
    }
}
