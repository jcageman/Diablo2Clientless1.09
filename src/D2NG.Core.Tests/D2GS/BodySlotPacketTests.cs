using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet.Outgoing;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

/// <summary>
/// Moving an item on or off the character needs its own packets. The packet that moves items inside
/// containers addresses them by id and does nothing at all to a worn item - sending it for an equipped
/// amulet left the item on the character and the bot waiting for a cursor that never filled.
/// </summary>
public class BodySlotPacketTests
{
    /// <summary>
    /// Every slot, captured from a 1.09 client in one session by taking each piece off in turn.
    /// </summary>
    [Theory]
    [InlineData(DirectoryType.Helm, 0x01)]
    [InlineData(DirectoryType.Amulet, 0x02)]
    [InlineData(DirectoryType.Armor, 0x03)]
    [InlineData(DirectoryType.RightHand, 0x04)]
    [InlineData(DirectoryType.LeftHand, 0x05)]
    [InlineData(DirectoryType.RightHandRing, 0x06)]
    [InlineData(DirectoryType.LeftHandRing, 0x07)]
    [InlineData(DirectoryType.Belt, 0x08)]
    [InlineData(DirectoryType.Boots, 0x09)]
    [InlineData(DirectoryType.Gloves, 0x0A)]
    public void RemoveBodyItemMatchesTheCapturedSlot(DirectoryType location, byte slot)
    {
        var packet = new RemoveBodyItemPacket(location);

        Assert.Equal<byte[]>([0x1C, slot, 0x00], packet.Raw);
    }

    /// <summary>
    /// The four equips that were captured, each into a different slot, which is what shows the second word
    /// is the slot and not a constant.
    /// </summary>
    [Theory]
    [InlineData(0x06, DirectoryType.Helm, 0x01)]
    [InlineData(0x1E, DirectoryType.RightHand, 0x04)]
    [InlineData(0x07, DirectoryType.Belt, 0x08)]
    [InlineData(0x0A, DirectoryType.Boots, 0x09)]
    public void EquipItemMatchesTheCapturedItemAndSlot(uint itemId, DirectoryType location, byte slot)
    {
        var packet = new EquipItemPacket(itemId, location);

        Assert.Equal<byte[]>(
            [0x1A, (byte)itemId, 0x00, 0x00, 0x00, slot, 0x00, 0x00, 0x00],
            packet.Raw);
    }

    [Fact]
    public void EquipItemKeepsTheItemIdLittleEndian()
    {
        var packet = new EquipItemPacket(0x11223344, DirectoryType.Armor);

        Assert.Equal<byte[]>([0x1A, 0x44, 0x33, 0x22, 0x11, 0x03, 0x00, 0x00, 0x00], packet.Raw);
    }
}
