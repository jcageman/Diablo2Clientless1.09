using D2NG.Mule.Packing;

namespace D2NG.Mule.Tests;

public class OccupancyGridTests
{
    [Fact]
    public void ParsesDrawingAndCountsFreeCells()
    {
        var grid = OccupancyGrid.Parse("""
            ##..
            ....
            """);

        Assert.Equal(4, grid.Width);
        Assert.Equal(2, grid.Height);
        Assert.Equal(6, grid.FreeCells);
        Assert.True(grid.IsOccupied(0, 0));
        Assert.False(grid.IsOccupied(2, 0));
    }

    [Fact]
    public void FirstFitScansRowsLeftToRight()
    {
        var grid = OccupancyGrid.Parse("""
            ##.#
            ....
            """);

        Assert.Equal(new Cell(2, 0), grid.FirstFit(new Shape(1, 1)));
        Assert.Equal(new Cell(0, 1), grid.FirstFit(new Shape(2, 1)));
        Assert.Equal(new Cell(2, 0), grid.FirstFit(new Shape(1, 2)));
        Assert.Null(grid.FirstFit(new Shape(2, 2)));
    }

    [Fact]
    public void ServerPutsOneHighItemsInTheRightmostFreeColumn()
    {
        var grid = OccupancyGrid.Parse("""
            ....
            ....
            """);

        Assert.Equal(new Cell(3, 0), grid.ServerFreePosition(new Shape(1, 1)));
        Assert.Equal(new Cell(2, 0), grid.ServerFreePosition(new Shape(2, 1)));
    }

    [Fact]
    public void ServerScansTallerItemsColumnByColumnFromTheLeft()
    {
        var grid = OccupancyGrid.Parse("""
            #...
            ....
            ....
            """);

        // Row-major first fit would pick (1,0); the server walks column 0 first and finds (0,1).
        Assert.Equal(new Cell(0, 1), grid.ServerFreePosition(new Shape(1, 2)));
        Assert.Equal(new Cell(1, 0), grid.FirstFit(new Shape(1, 2)));
    }

    [Fact]
    public void BlockAndFreeRoundTrip()
    {
        var grid = new OccupancyGrid(3, 3);
        grid.Block(new Cell(1, 1), new Shape(2, 2));
        Assert.Equal(5, grid.FreeCells);
        Assert.False(grid.CanPlace(new Cell(2, 2), new Shape(1, 1)));
        grid.Free(new Cell(1, 1), new Shape(2, 2));
        Assert.Equal(9, grid.FreeCells);
    }
}
