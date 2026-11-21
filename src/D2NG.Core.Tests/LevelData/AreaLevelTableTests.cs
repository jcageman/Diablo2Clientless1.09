using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.LevelData;
using System.IO;
using Xunit;

namespace D2NG.Core.Tests.LevelData;

public class AreaLevelTableTests
{
    private static AreaLevelTable Table() => AreaLevelTable.FromEntries(
        [new AreaLevels { Area = Area.BloodMoor, Classic = [1, 26, 51], Expansion = [1, 36, 67] }],
        new AreaLevelSource());

    [Fact]
    public void ClassicAndExpansionReadTheirOwnColumns()
    {
        var table = Table();

        Assert.Equal(26, table.MonsterLevel(Area.BloodMoor, Difficulty.Nightmare, expansion: false));
        Assert.Equal(36, table.MonsterLevel(Area.BloodMoor, Difficulty.Nightmare, expansion: true));
        Assert.Equal(67, table.MonsterLevel(Area.BloodMoor, Difficulty.Hell, expansion: true));
    }

    [Fact]
    public void AnAreaNotInTheTableHasNoLevel()
    {
        Assert.Null(Table().MonsterLevel(Area.ColdPlains, Difficulty.Normal, expansion: false));
    }

    [Fact]
    public void ASavedTableLoadsBackTheSame()
    {
        var path = Path.GetTempFileName();
        try
        {
            Table().Save(path);
            Assert.Equal(51, AreaLevelTable.Load(path).MonsterLevel(Area.BloodMoor, Difficulty.Hell, expansion: false));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
