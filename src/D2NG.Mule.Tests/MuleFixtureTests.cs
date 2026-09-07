using D2NG.Mule.Packing;

namespace D2NG.Mule.Tests;

public class MuleFixtureTests
{
    [Fact]
    public void RoundTripsThroughJsonAndBuildsGrids()
    {
        var fixture = new MuleFixture
        {
            Character = "Tester",
            Items =
            [
                new FixtureItem { Id = 1, Name = "Ring", Width = 1, Height = 1, Container = "Inventory", X = 9, Y = 7, Identified = true, Touchable = true },
                new FixtureItem { Id = 2, Name = "Armor", Width = 2, Height = 3, Container = "Stash", X = 0, Y = 0, Identified = true, Touchable = true },
                new FixtureItem { Id = 3, Name = "Charm", Width = 1, Height = 1, Container = "Stash2", X = 3, Y = 1, Identified = true, Touchable = true },
                new FixtureItem { Id = 4, Name = "Cube", Width = 2, Height = 2, Container = "Inventory", X = 0, Y = 0, Identified = true, Touchable = false },
            ]
        };
        var path = Path.Combine(Path.GetTempPath(), $"mule-fixture-{Guid.NewGuid():N}.json");
        try
        {
            fixture.Save(path);
            var loaded = MuleFixture.Load(path);

            Assert.Equal("Tester", loaded.Character);
            Assert.Equal(80 - 5, loaded.InventoryGrid().FreeCells);
            Assert.Equal(100 - 7, loaded.StashGrid().FreeCells);
            Assert.Equal(new Cell(3, 9), loaded.StashItems().Single(i => i.Id == 3).At);
            var candidates = loaded.Candidates();
            Assert.Equal([1u, 2u, 3u], candidates.Select(c => c.Id).Order().ToList());
            Assert.Equal(Origin.FarmerInventory, candidates.Single(c => c.Id == 1).Origin);
            Assert.Equal(Origin.FarmerStash, candidates.Single(c => c.Id == 3).Origin);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
