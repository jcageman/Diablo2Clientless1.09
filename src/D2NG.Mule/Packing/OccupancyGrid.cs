using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using D2NG.Core.D2GS.Items.Containers;

namespace D2NG.Mule.Packing;

/// <summary>
/// A grid of cells that are either taken or free. Mutable so planners can simulate placements on a copy.
/// </summary>
public sealed class OccupancyGrid
{
    private readonly bool[,] _cells;

    public int Width { get; }
    public int Height { get; }

    public OccupancyGrid(int width, int height)
    {
        Width = width;
        Height = height;
        _cells = new bool[height, width];
    }

    public static OccupancyGrid FromContainer(Container container)
    {
        var grid = new OccupancyGrid((int)container.Width, (int)container.Height);
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                grid._cells[y, x] = container.IsOccupied(x, y);
            }
        }
        return grid;
    }

    /// <summary>
    /// Rows of '#' (taken) and '.' (free), for tests and logs.
    /// </summary>
    public static OccupancyGrid Parse(string drawing)
    {
        var rows = drawing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();
        var grid = new OccupancyGrid(rows[0].Length, rows.Count);
        for (var y = 0; y < rows.Count; y++)
        {
            if (rows[y].Length != grid.Width)
            {
                throw new ArgumentException($"row {y} has width {rows[y].Length}, expected {grid.Width}");
            }
            for (var x = 0; x < grid.Width; x++)
            {
                grid._cells[y, x] = rows[y][x] != '.';
            }
        }
        return grid;
    }

    public OccupancyGrid Clone()
    {
        var copy = new OccupancyGrid(Width, Height);
        Array.Copy(_cells, copy._cells, _cells.Length);
        return copy;
    }

    public bool IsOccupied(int x, int y) => _cells[y, x];

    public int FreeCells
    {
        get
        {
            var free = 0;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    if (!_cells[y, x])
                    {
                        free++;
                    }
                }
            }
            return free;
        }
    }

    public bool CanPlace(Cell at, Shape shape)
    {
        if (at.X < 0 || at.Y < 0 || at.X + shape.Width > Width || at.Y + shape.Height > Height)
        {
            return false;
        }
        for (var y = at.Y; y < at.Y + shape.Height; y++)
        {
            for (var x = at.X; x < at.X + shape.Width; x++)
            {
                if (_cells[y, x])
                {
                    return false;
                }
            }
        }
        return true;
    }

    public void Block(Cell at, Shape shape) => Set(at, shape, true);

    public void Free(Cell at, Shape shape) => Set(at, shape, false);

    private void Set(Cell at, Shape shape, bool value)
    {
        for (var y = at.Y; y < at.Y + shape.Height; y++)
        {
            for (var x = at.X; x < at.X + shape.Width; x++)
            {
                _cells[y, x] = value;
            }
        }
    }

    /// <summary>
    /// The bot's own placement policy: rows top to bottom, each row left to right. Matches Container.FindFreeSpace.
    /// </summary>
    public Cell? FirstFit(Shape shape)
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = new Cell(x, y);
                if (CanPlace(cell, shape))
                {
                    return cell;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The spot the D2 server picks when it checks whether a traded item has room (D2Common INVENTORY_GetFreePosition
    /// on an ownerless grid): one-high items scan columns right to left, anything else columns left to right, each
    /// column top to bottom.
    /// </summary>
    public Cell? ServerFreePosition(Shape shape)
    {
        if (shape.Height == 1)
        {
            for (var x = Width - 1; x >= 0; x--)
            {
                for (var y = 0; y < Height; y++)
                {
                    var cell = new Cell(x, y);
                    if (CanPlace(cell, shape))
                    {
                        return cell;
                    }
                }
            }
            return null;
        }

        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                var cell = new Cell(x, y);
                if (CanPlace(cell, shape))
                {
                    return cell;
                }
            }
        }
        return null;
    }

    public bool Fits(Shape shape) => FirstFit(shape) != null;

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                sb.Append(_cells[y, x] ? '#' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
