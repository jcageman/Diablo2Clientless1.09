using System.Collections.Generic;

namespace D2NG.Mule.Models;

public class MuleItemDb
{
    public string Id { get; set; }
    public string AccountName { get; set; }
    public string CharacterName { get; set; }
    public string ItemName { get; set; }

    /// <summary>The base's code from the item packet, which keys the game tables. Null on records written before it was kept.</summary>
    public string BaseCode { get; set; }

    /// <summary>Which unique or set item it is, for the level it requires; 0 when the quality has neither.</summary>
    public uint UniqueCode { get; set; }

    public uint SetCode { get; set; }

    /// <summary>The game mode of the character holding it; null on records written before it was kept.</summary>
    public bool? Expansion { get; set; }

    /// <summary>Where on the character it lies: Stash, Inventory or Cube, with its top left cell.</summary>
    public string Container { get; set; }

    public int X { get; set; }

    public int Y { get; set; }
    public string QualityType { get; set; }
    public string ClassificationType { get; set; }
    public bool Ethereal { get; set; }
    public uint Level { get; set; }
    public uint Sockets { get; set; }
    public List<StatDb> Stats { get; set; }
    public List<string> StatTypes { get; set; }

    /// <summary>
    /// An item the bots never move, such as a charm in the inventory rows kept for charms. Planners
    /// that work from the database skip it: no trade can hand it over.
    /// </summary>
    public bool Untouchable { get; set; }
}
