using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace ConsoleBot.Pickit
{
    public static class Runes
    {
        private static readonly HashSet<ItemName> wantedRunes = new HashSet<ItemName> {
            ItemName.LemRune,
            ItemName.PulRune,
            ItemName.UmRune,
            ItemName.MalRune,
            ItemName.IstRune,
            ItemName.GulRune,
            ItemName.VexRune,
            ItemName.OhmRune,
            ItemName.LoRune,
            ItemName.SurRune,
            ItemName.BerRune,
            ItemName.JahRune,
            ItemName.ChamRune,
            ItemName.ZodRune};
        public static bool ShouldPickupItemExpansion(Item item)
        {
            return wantedRunes.Contains(item.Name);
        }

        public static bool ShouldKeepItemExpansion(Item item)
        {
            return wantedRunes.Contains(item.Name);
        }
    }
}
