using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class TileTests
{
    [Fact]
    public void testTileContainsPoint()
    {
        var tile = new Tile(1128, 1104, Area.RogueEncampment);
        var point = new Point(5653, 5523);
        Assert.True(tile.Contains(point));
    }
}
