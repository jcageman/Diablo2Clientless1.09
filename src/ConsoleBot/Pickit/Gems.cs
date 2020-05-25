using D2NG.D2GS.Items;

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
            return item.Name == "Flawless Skull" || item.Name == "Perfect Skull";
        }
    }
}
