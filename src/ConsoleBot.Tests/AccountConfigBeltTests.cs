using ConsoleBot.Bots.Types;

namespace ConsoleBot.Tests;

public class AccountConfigBeltTests
{
    [Fact]
    public void WithoutACapTheWholeBeltIsFilled()
    {
        var account = new AccountConfig();

        Assert.Equal(6, account.HealthPotionTarget(3));
        Assert.Equal(6, account.ManaPotionTarget(3));
    }

    [Fact]
    public void TheCapLimitsBothTypesToTheRowsAsked()
    {
        var account = new AccountConfig { BeltRowsToFill = 2 };

        Assert.Equal(4, account.HealthPotionTarget(3));
        Assert.Equal(4, account.ManaPotionTarget(3));
    }

    [Fact]
    public void ACapAboveTheBeltWornDoesNotInventAnyRows()
    {
        var account = new AccountConfig { BeltRowsToFill = 4 };

        Assert.Equal(2, account.HealthPotionTarget(1));
    }

    [Fact]
    public void TheCapCountsTheColumnsEachTypeOwns()
    {
        var account = new AccountConfig
        {
            BeltRowsToFill = 2,
            HealthSlots = [0, 1, 2],
            ManaSlots = [3],
        };

        Assert.Equal(6, account.HealthPotionTarget(4));
        Assert.Equal(2, account.ManaPotionTarget(4));
    }
}
