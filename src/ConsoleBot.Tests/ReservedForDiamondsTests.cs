using ConsoleBot.Bots.Types.Leveling;
using D2NG.Core.D2GS.Items;

namespace ConsoleBot.Tests;

public class ReservedForDiamondsTests
{
    private static Item Shield(QualityType quality, uint sockets, uint used = 0, ItemName name = ItemName.LargeShield)
        => new() { Name = name, Classification = ClassificationType.Shield, Quality = quality, Sockets = sockets, UsedSockets = used, Properties = [] };

    [Fact]
    public void APlainThreeSocketShieldIsKeptForTheDiamonds()
    {
        Assert.True(LevelingGems.ReservedForDiamonds(Shield(QualityType.Normal, 3)));
        Assert.True(LevelingGems.ReservedForDiamonds(Shield(QualityType.Superior, 3, name: ItemName.KiteShield)));
    }

    [Fact]
    public void FewerSocketsOrAMagicShieldIsNot()
    {
        Assert.False(LevelingGems.ReservedForDiamonds(Shield(QualityType.Normal, 2)));
        Assert.False(LevelingGems.ReservedForDiamonds(Shield(QualityType.Magical, 3)));
    }

    [Fact]
    public void OnceSocketedItIsAnOrdinaryShield()
    {
        Assert.False(LevelingGems.ReservedForDiamonds(Shield(QualityType.Normal, 3, used: 3)));
    }

    [Fact]
    public void OnlyShields()
    {
        var armor = new Item { Name = ItemName.SplintMail, Classification = ClassificationType.Armor, Quality = QualityType.Normal, Sockets = 3, Properties = [] };

        Assert.False(LevelingGems.ReservedForDiamonds(armor));
    }
}
