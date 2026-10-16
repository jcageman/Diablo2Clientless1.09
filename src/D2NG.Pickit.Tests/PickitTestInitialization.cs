using System.IO;
using System.Runtime.CompilerServices;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Pickit;

namespace D2NG.Pickit.Tests;

internal static class PickitTestInitialization
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Pickit.Configure(new PickitConfiguration
        {
            NipDirectory = Path.Combine(AppContext.BaseDirectory, "Nips", "Expansion"),
            Gamble = new GambleConfiguration
            {
                Rules =
                [
                    new() { ItemNames = [ItemName.Amulet], MinimumCharacterLevel = 90 },
                    new() { ItemNames = [ItemName.Boots, ItemName.HeavyBoots], MinimumCharacterLevel = 0 }
                ]
            }
        });
    }
}
