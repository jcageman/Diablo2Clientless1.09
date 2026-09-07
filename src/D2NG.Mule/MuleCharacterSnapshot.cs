using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Mule.Models;
using D2NG.Mule.Packing;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule;

/// <summary>
/// The items a mule character holds and the room it has left, read from a client that is in a
/// game with that character.
/// </summary>
public class MuleCharacterSnapshot
{
    public List<Item> Items { get; init; }
    public int FreeCells { get; init; }

    /// <summary>
    /// Per item height, the widest item that still has a spot. See <see cref="Packing.FitProfile"/>.
    /// </summary>
    public int[] FitProfile { get; init; }

    public static MuleCharacterSnapshot Take(Game game)
    {
        var items = game.Stash.Items;
        items.AddRange(game.Inventory.Items);
        items.AddRange(game.Cube.Items);
        return new MuleCharacterSnapshot
        {
            Items = items.Where(i => i.Classification != ClassificationType.Scroll).ToList(),
            FreeCells = game.Stash.FreeCellCount() + game.Inventory.FreeCellCount(),
            FitProfile = Packing.FitProfile.Of(MuleGrids.Stash(game), MuleGrids.Inventory(game))
        };
    }

    public List<MuleItemDb> MapItems(string accountName, string characterName)
    {
        return Items.Select(i => i.MapToMuleItem(accountName, characterName)).ToList();
    }
}
