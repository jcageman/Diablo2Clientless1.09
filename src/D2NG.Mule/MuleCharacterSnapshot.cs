using D2NG.Core;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Mule.Models;
using D2NG.Mule.Packing;
using System;
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

    /// <summary>
    /// Ids of the items the bots never move; see <see cref="MuleItemDb.Untouchable"/>.
    /// </summary>
    public HashSet<uint> UntouchableIds { get; init; } = [];

    /// <param name="canTouch">Whether the bots may move an item; usually <c>Pickit.CanTouchInventoryItem</c>.</param>
    public static MuleCharacterSnapshot Take(Game game, Func<Item, bool> canTouch)
    {
        var items = game.Stash.Items;
        items.AddRange(game.Inventory.Items);
        items.AddRange(game.Cube.Items);
        return new MuleCharacterSnapshot
        {
            Items = items.Where(i => i.Classification != ClassificationType.Scroll).ToList(),
            FreeCells = game.Stash.FreeCellCount() + game.Inventory.FreeCellCount(),
            FitProfile = Packing.FitProfile.Of(MuleGrids.Stash(game), MuleGrids.Inventory(game)),
            UntouchableIds = [.. items.Where(i => !canTouch(i)).Select(i => i.Id)]
        };
    }

    public List<MuleItemDb> MapItems(string accountName, string characterName)
    {
        return Items.Select(i =>
        {
            var muleItem = i.MapToMuleItem(accountName, characterName);
            muleItem.Untouchable = UntouchableIds.Contains(i.Id);
            return muleItem;
        }).ToList();
    }
}
