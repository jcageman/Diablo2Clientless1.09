using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.ItemData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace D2NG.Trace.Query;

/// <summary>
/// How the trace commands show an item: compact (name, quality, where) or with everything the item packet carried - its
/// stats as the bot logs them, sockets and what fills them, durability and the requirements the item data knows.
/// </summary>
public static class ItemView
{
    /// <summary>"GrimHelm (Rare) 0x100", or "Gold 0x85": an item in one line.</summary>
    public static string Short(Item item) => item == null ? "?" : Short(item.Name.ToString(), item.Quality, item.Id);

    public static string Short(string name, QualityType quality, uint id)
        => quality is QualityType.Normal or QualityType.NotApplicable ? $"{name} {Names.Id(id)}" : $"{name} ({quality}) {Names.Id(id)}";

    /// <summary>The cells an item takes in a container as the container counts them: the second stash page starts eight rows down.</summary>
    public static Point Cell(Item item) => item.Container == ContainerType.Stash2 && item.Location != null ? new Point(item.Location.X, (ushort)(item.Location.Y + 8)) : item.Location;

    public static string Where(Item item) => item.Container switch
    {
        ContainerType.Belt => $"belt slot {item.Location?.X}, row {item.Location?.Y}",
        ContainerType.Inventory => $"inventory at {Grid(item.Location)}",
        ContainerType.Cube => $"cube at {Grid(item.Location)}",
        ContainerType.Stash or ContainerType.Stash2 => $"stash at {Grid(Cell(item))}",
        ContainerType.ForTrade => $"own trade page at {Grid(item.Location)}",
        ContainerType.TradeOffer => $"other player's trade offer at {Grid(item.Location)}",
        _ => item.Container.ToString(),
    };

    /// <summary>A grid cell, where (0, 0) is a cell like any other.</summary>
    public static string Grid(Point cell) => cell == null ? "-" : $"({cell.X}, {cell.Y})";

    /// <summary>What `state` has always listed for an item.</summary>
    public static object Compact(Item item) => new
    {
        id = Names.Id(item.Id),
        name = item.Name.ToString(),
        quality = item.Quality.ToString(),
        container = item.Container.ToString(),
        at = Xy(item.Location),
        amount = item.Amount > 0 ? item.Amount : (uint?)null,
    };

    /// <summary>An equipped item: the body slot instead of a container.</summary>
    public static object Equipped(string slot, Item item, bool full) => full
        ? Details(item, slot)
        : new
        {
            slot,
            id = Names.Id(item.Id),
            name = item.Name.ToString(),
            quality = item.Quality.ToString(),
            amount = item.Amount > 0 ? item.Amount : (uint?)null,
        };

    public static object Of(Item item, bool full) => full ? Details(item, null) : Compact(item);

    /// <summary>Everything the item packet decoded: the compact fields, then ethereal, identified, level, sockets, durability, defense, requirements and stats.</summary>
    public static object Details(Item item, string slot)
    {
        var requirements = Requirements();
        var stats = Stats(item);
        return new
        {
            slot,
            id = Names.Id(item.Id),
            name = item.Name.ToString(),
            code = item.Type,
            quality = item.Quality.ToString(),
            container = slot == null ? item.Container.ToString() : null,
            at = slot == null ? Xy(item.Location) : null,
            amount = item.Amount > 0 ? item.Amount : (uint?)null,
            ethereal = item.Ethereal ? true : (bool?)null,
            identified = item.IsIdentified,
            level = item.Level > 0 ? item.Level : (uint?)null,
            sockets = item.Sockets > 0 || item.HasSockets ? $"{item.SocketedItems.Count}/{item.Sockets}" : null,
            socketed = item.SocketedItems.IsEmpty ? null : item.SocketedItems.Values.OrderBy(s => s.Location?.X ?? 0).ThenBy(s => s.Id).Select(s => s.Name.ToString()).ToArray(),
            runeword = item.IsRuneword ? true : (bool?)null,
            durability = item.IsIndestructible ? "indestructible" : item.MaximumDurability > 0 ? $"{item.Durability}/{item.MaximumDurability}" : null,
            defense = item.Defense > 0 ? item.Defense : (uint?)null,
            requires = requirements == null ? null : Requirement(requirements, item),
            stats = stats.Count > 0 ? stats : null,
        };
    }

    private static object Requirement(ItemRequirements requirements, Item item)
    {
        var reduced = item.GetValueOfStatType(StatType.ReducedRequirements);
        return Requirement(requirements.RequiredLevel(item), Reduced(requirements.RequiredStrength(item), reduced), Reduced(requirements.RequiredDexterity(item), reduced));
    }

    /// <summary>The requirements as `state --items full` shows them; null when the item needs nothing, but a level the item data cannot read is said even then.</summary>
    public static object Requirement(int? level, int strength, int dexterity)
    {
        if (level == 0 && strength == 0 && dexterity == 0)
        {
            return null;
        }

        return new
        {
            level = level is > 0 ? level : null,
            levelUnknown = level == null ? "depends on magic affixes the item data cannot read" : null,
            strength = strength > 0 ? strength : (int?)null,
            dexterity = dexterity > 0 ? dexterity : (int?)null,
        };
    }

    /// <summary>A strength or dexterity requirement after the item's "requirements -N%" (a negative percent).</summary>
    public static int Reduced(int requirement, int percent) => requirement <= 0 || percent == 0 ? requirement : Math.Max(0, requirement * (100 + percent) / 100);

    /// <summary>The item's stats one per line, worded as the bot's own item description words them.</summary>
    public static List<string> Stats(Item item)
    {
        if (item.Properties.Count == 0)
        {
            return [];
        }

        return [.. item.GetFullDescription()
            .Split('\n')
            .Skip(1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)];
    }

    private static readonly object Gate = new();
    private static (string Path, ItemRequirements Table) _requirements;

    /// <summary>The requirements table from the data folder's item-affixes.json, or null without one.</summary>
    private static ItemRequirements Requirements()
    {
        if (!GameDataLocation.TryResolve(GameDataLocation.ItemAffixesFile, out var path))
        {
            return null;
        }

        lock (Gate)
        {
            if (_requirements.Path != path)
            {
                try
                {
                    _requirements = (path, new ItemRequirements(ItemAffixTable.Load(path)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    _requirements = (path, null);
                }
            }

            return _requirements.Table;
        }
    }

    private static int[] Xy(Point point) => point == null ? null : [point.X, point.Y];
}
