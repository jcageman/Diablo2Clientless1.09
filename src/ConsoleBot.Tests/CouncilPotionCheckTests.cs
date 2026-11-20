using ConsoleBot.Bots.Types.Travincal;
using Xunit;

namespace ConsoleBot.Tests;

public class CouncilPotionCheckTests
{
    [Theory]
    [InlineData(1, 1, 0, 0, CouncilPotionAction.Fight)]
    [InlineData(6, 6, 0, 4, CouncilPotionAction.Fight)]
    [InlineData(0, 6, 2, 0, CouncilPotionAction.RefillBelt)]
    [InlineData(12, 0, 0, 10, CouncilPotionAction.RefillBelt)]
    [InlineData(0, 0, 1, 1, CouncilPotionAction.RefillBelt)]
    [InlineData(0, 6, 0, 4, CouncilPotionAction.Town)]
    [InlineData(6, 0, 3, 0, CouncilPotionAction.Town)]
    [InlineData(0, 0, 3, 0, CouncilPotionAction.Town)]
    public void TheBeltNeedsAHealingAndAManaPotionAndTheInventoryIsTriedBeforeTown(int beltHealing, int beltMana, int inventoryHealing, int inventoryMana, CouncilPotionAction expected)
        => Assert.Equal(expected, CouncilPotionCheck.Decide(beltHealing, beltMana, inventoryHealing, inventoryMana));
}
