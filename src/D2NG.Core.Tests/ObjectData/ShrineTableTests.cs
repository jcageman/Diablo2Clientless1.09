using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.ObjectData;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.ObjectData;

/// <summary>
/// The map api reports preset objects by class id and position. The table turns those into shrines
/// the bot can hunt, and says up front which of them are fixed health or mana shrines.
/// </summary>
public class ShrineTableTests
{
    private static ShrineTable Table()
        => ShrineTable.FromEntries(
            [
                new ShrineEntry { ClassId = 2, Name = "Shrine", Kind = ShrineKind.Random },
                new ShrineEntry { ClassId = 84, Name = "Shrine", Kind = ShrineKind.Health },
                new ShrineEntry { ClassId = 164, Name = "Shrine", Kind = ShrineKind.Mana }
            ],
            null);

    [Fact]
    public void LocationsSkipObjectsThatAreNotShrines()
    {
        var objects = new Dictionary<int, List<Point>>
        {
            [(int)EntityCode.WaypointAct1] = [new Point(4000, 5000)],
            [84] = [new Point(4100, 5100), new Point(4200, 5200)],
            [2] = [new Point(4300, 5300)]
        };

        var shrines = Table().Locations(objects);

        Assert.Equal(3, shrines.Count);
        Assert.Equal(2, shrines.Count(s => s.Kind == ShrineKind.Health && s.Code == (EntityCode)84));
        var rolled = Assert.Single(shrines, s => s.Kind == ShrineKind.Random);
        Assert.Equal(new Point(4300, 5300), rolled.Location);
    }

    [Fact]
    public void ANullObjectDictionaryYieldsNoShrines()
    {
        Assert.Empty(Table().Locations(null));
    }

    [Fact]
    public void UnknownCodesAreNotShrines()
    {
        Assert.False(Table().IsShrine(EntityCode.Stash));
        Assert.False(Table().TryGetKind(EntityCode.Stash, out _));
        Assert.Empty(ShrineTable.Empty.Locations(new Dictionary<int, List<Point>> { [84] = [new Point(1, 1)] }));
    }

    [Fact]
    public void TheTableSurvivesARoundTripThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shrines-{Path.GetRandomFileName()}.json");
        try
        {
            Table().Save(path);
            var loaded = ShrineTable.Load(path);

            Assert.Equal(3, loaded.Count);
            Assert.True(loaded.TryGetKind((EntityCode)164, out var kind));
            Assert.Equal(ShrineKind.Mana, kind);
            Assert.Contains("\"Mana\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
