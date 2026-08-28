using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;

namespace D2NG.Pickit.Tests;

public class InventoryReservationTests
{
    // The real inventory is 10 wide by 8 tall, and the default reserves the bottom 4 rows.
    private const int InventoryHeight = 8;

    private static Item InInventoryAt(ClassificationType classification, ItemName name, ushort row)
    {
        var item = TestItemFactory.Create(classification, name, QualityType.Normal);
        item.Container = ContainerType.Inventory;
        item.Location = new Point(0, row);
        return item;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void CharmsInTheReservedBottomRowsAreNotTouched(ushort row)
    {
        var charm = InInventoryAt(ClassificationType.SmallCharm, ItemName.SmallCharm, row);

        Assert.False(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, charm, InventoryHeight));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CharmsAboveTheReservedRowsAreTouched(ushort row)
    {
        var charm = InInventoryAt(ClassificationType.SmallCharm, ItemName.SmallCharm, row);

        Assert.True(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, charm, InventoryHeight));
    }

    [Fact]
    public void ReservedRowsAreCountedFromTheBottomWhateverTheHeight()
    {
        // Row 4 is reserved in an 8 row inventory but well clear of the bottom 4 of a 12 row one.
        var charm = InInventoryAt(ClassificationType.SmallCharm, ItemName.SmallCharm, 4);

        Assert.False(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, charm, inventoryHeight: 8));
        Assert.True(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, charm, inventoryHeight: 12));
    }

    [Fact]
    public void NonCharmsInTheReservedRowsAreStillTouched()
    {
        var ring = InInventoryAt(ClassificationType.Ring, ItemName.Ring, 7);

        Assert.True(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, ring, InventoryHeight));
    }

    [Theory]
    [InlineData(ItemName.TomeOfTownPortal)]
    [InlineData(ItemName.HoradricCube)]
    public void ToolsTheBotNeedsAreNeverTouched(ItemName name)
    {
        var item = InInventoryAt(ClassificationType.BodyPart, name, 0);

        Assert.False(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, item, InventoryHeight));
    }

    // Bots identify at Deckard Cain and nothing reads a tome of identify, so carrying one only costs the
    // inventory space it sits in. It is sold like any other item.
    [Fact]
    public void TheIdentifyTomeIsSold()
    {
        var tome = InInventoryAt(ClassificationType.BodyPart, ItemName.TomeofIdentify, 0);

        Assert.True(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, tome, InventoryHeight));
    }

    // Wirt's leg used to be reserved for every bot, so a mephisto or travincal character carried one
    // forever. Only the cow portal character reserves it now, via Pickit.ReserveInventoryItem.
    [Fact]
    public void WirtsLegIsTouchedUnlessABotReservesIt()
    {
        var leg = InInventoryAt(ClassificationType.BodyPart, ItemName.WirtsLeg, 0);

        Assert.True(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, leg, InventoryHeight));
    }

    [Fact]
    public void AnAmazonKeepsHerAmmunition()
    {
        var arrows = InInventoryAt(ClassificationType.Arrows, ItemName.Arrows, 0);
        var javelins = InInventoryAt(ClassificationType.AmazonJavelin, ItemName.Javelin, 0);

        Assert.False(Pickit.CanTouchInventoryItem(CharacterClass.Amazon, arrows, InventoryHeight));
        Assert.False(Pickit.CanTouchInventoryItem(CharacterClass.Amazon, javelins, InventoryHeight));
        Assert.True(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, arrows, InventoryHeight));
    }

    [Fact]
    public void ItemsOutsideTheInventoryAreAlwaysTouched()
    {
        var charm = InInventoryAt(ClassificationType.SmallCharm, ItemName.SmallCharm, 7);
        charm.Container = ContainerType.Stash;

        Assert.True(Pickit.CanTouchInventoryItem(CharacterClass.Sorceress, charm, InventoryHeight));
    }
}
