using D2NG.Core;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Mule.Packing;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace D2NG.Mule;

public sealed class FixtureItem
{
    public uint Id { get; init; }
    public string Name { get; init; }
    public string Quality { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Container { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public bool Identified { get; init; }
    public bool Touchable { get; init; }
}

/// <summary>
/// A character's stash, inventory and cube as one JSON document, written on every mule visit so that any real
/// situation can be replayed against the planners in a test.
/// </summary>
public sealed class MuleFixture
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Character { get; init; }
    public List<FixtureItem> Items { get; init; } = [];

    public static MuleFixture From(Game game, System.Func<Item, bool> touchable)
    {
        var items = game.Stash.Items.Concat(game.Inventory.Items).Concat(game.Cube.Items)
            .Select(i => new FixtureItem
            {
                Id = i.Id,
                Name = i.Name.ToString(),
                Quality = i.Quality.ToString(),
                Width = i.Width,
                Height = i.Height,
                Container = i.Container.ToString(),
                X = i.Location.X,
                Y = i.Location.Y,
                Identified = i.IsIdentified,
                Touchable = touchable(i)
            })
            .OrderBy(i => i.Container).ThenBy(i => i.Y).ThenBy(i => i.X)
            .ToList();
        return new MuleFixture { Character = game.Me.Name, Items = items };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public static MuleFixture Load(string path)
    {
        return JsonSerializer.Deserialize<MuleFixture>(File.ReadAllText(path), Options);
    }

    public OccupancyGrid InventoryGrid()
    {
        var grid = new OccupancyGrid(10, 8);
        foreach (var item in Items.Where(i => i.Container == nameof(ContainerType.Inventory)))
        {
            grid.Block(new Cell(item.X, item.Y), new Shape(item.Width, item.Height));
        }
        return grid;
    }

    public OccupancyGrid StashGrid()
    {
        var grid = new OccupancyGrid(10, 10);
        foreach (var item in StashItems())
        {
            grid.Block(item.At, item.Shape);
        }
        return grid;
    }

    public List<StashItem> StashItems()
    {
        return Items
            .Where(i => i.Container is nameof(ContainerType.Stash) or nameof(ContainerType.Stash2))
            .Select(i => new StashItem(i.Id, new Shape(i.Width, i.Height), new Cell(i.X, i.Container == nameof(ContainerType.Stash2) ? i.Y + 8 : i.Y)))
            .ToList();
    }

    /// <summary>
    /// The items a farmer would offer: identified, touchable, in stash or inventory.
    /// </summary>
    public List<Candidate> Candidates()
    {
        return Items
            .Where(i => i.Identified && i.Touchable)
            .Where(i => i.Container is nameof(ContainerType.Stash) or nameof(ContainerType.Stash2) or nameof(ContainerType.Inventory))
            .Select(i => new Candidate(i.Id, new Shape(i.Width, i.Height), i.Container == nameof(ContainerType.Inventory) ? Origin.FarmerInventory : Origin.FarmerStash))
            .ToList();
    }
}
