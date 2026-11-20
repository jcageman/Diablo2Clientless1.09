using ConsoleBot.Bots.Types.Leveling;
using static ConsoleBot.Bots.Types.Leveling.LevelingInventory;

namespace ConsoleBot.Tests;

/// <summary>A 10 by 8 bag, as this realm has: free cells scattered between potions and gear, no free two by four block.</summary>
public class BagHoleTests
{
    private const int BagWidth = 10;
    private const int BagHeight = 8;

    private static List<Placed> ScatteredBag()
    {
        var items = new List<Placed>();
        uint id = 1;
        // A one-cell item in every other column of every row: any two by four spot holds four of them.
        for (var y = 0; y < BagHeight; y++)
        {
            for (var x = 0; x < BagWidth; x += 2)
            {
                items.Add(new Placed(id++, x, y, 1, 1));
            }
        }

        return items;
    }

    [Fact]
    public void ClearsATwoByFourSpotWithTheFewestMoves()
    {
        var items = ScatteredBag();

        var hole = HoleFor(2, 4, BagWidth, BagHeight, items);

        Assert.NotNull(hole);
        var (at, moves) = hole.Value;
        Assert.Equal(4, moves.Count);
        var after = items.Select(item => moves.FirstOrDefault(move => move.Id == item.Id) is { Id: > 0 } move ? item with { X = move.To.X, Y = move.To.Y } : item).ToList();
        Assert.DoesNotContain(after, item => item.X < at.X + 2 && at.X < item.X + item.Width && item.Y < at.Y + 4 && at.Y < item.Y + item.Height);
        Assert.Equal(after.Count, after.Select(item => (item.X, item.Y)).Distinct().Count());
    }

    [Fact]
    public void NoneWhenTheBagIsTooFullToMoveAnythingAside()
    {
        var items = new List<Placed>();
        uint id = 1;
        for (var y = 0; y < BagHeight; y++)
        {
            for (var x = 0; x < BagWidth; x++)
            {
                if ((x, y) != (0, 0))
                {
                    items.Add(new Placed(id++, x, y, 1, 1));
                }
            }
        }

        Assert.Null(HoleFor(2, 4, BagWidth, BagHeight, items));
    }

    [Fact]
    public void NoMovesWhenTheSpotIsAlreadyFree()
    {
        var hole = HoleFor(2, 4, BagWidth, BagHeight, [new Placed(1, 0, 0, 1, 1)]);

        Assert.NotNull(hole);
        Assert.Empty(hole.Value.Moves);
    }
}
