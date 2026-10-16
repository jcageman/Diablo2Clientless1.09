using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Items;

/// <summary>
/// The item table looked up by name instead of by code, for items known only by name - the mule
/// database keeps no item code.
/// </summary>
public static class ItemCatalog
{
    private static Dictionary<ItemName, (string Code, ushort Width, ushort Height)> s_byName;

    public static bool TryFind(ItemName name, out string code, out ushort width, out ushort height)
    {
        s_byName ??= DataManager.DataManager.Instance.ItemData.Items
            .GroupBy(e => e.Value.Name)
            .ToDictionary(g => g.Key, g => (g.First().Key, g.First().Value.Width, g.First().Value.Height));
        var found = s_byName.TryGetValue(name, out var entry);
        (code, width, height) = entry;
        return found;
    }
}
