using ConsoleBot.Mule;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Tests;

public class MulePlacementTests
{
    private static readonly Dictionary<string, Dictionary<ClassificationType, int>> Held = new()
    {
        ["gloves"] = new() { [ClassificationType.Gloves] = 12, [ClassificationType.Amulet] = 1 },
        ["amulets"] = new() { [ClassificationType.Amulet] = 7 },
        ["empty"] = new(),
    };

    [Fact]
    public void GoesToTheCharacterWithTheMostOfTheSameClassification()
    {
        Assert.Equal("gloves", MulePlacement.BestCharacter(ClassificationType.Gloves, ["empty", "amulets", "gloves"], Held));
        Assert.Equal("amulets", MulePlacement.BestCharacter(ClassificationType.Amulet, ["empty", "amulets", "gloves"], Held));
    }

    [Fact]
    public void NoneHeldAnywhere_GoesToTheFirstCharacter()
    {
        Assert.Equal("empty", MulePlacement.BestCharacter(ClassificationType.Ring, ["empty", "amulets", "gloves"], Held));
    }

    [Fact]
    public void AVisitedCharacterIsNoLongerACandidate()
    {
        Assert.Equal("gloves", MulePlacement.BestCharacter(ClassificationType.Amulet, ["empty", "gloves"], Held));
    }

    [Fact]
    public void UnknownCharacterCountsAsEmpty()
    {
        Assert.Equal("new", MulePlacement.BestCharacter(ClassificationType.Belt, ["new", "gloves"], Held));
    }
}
