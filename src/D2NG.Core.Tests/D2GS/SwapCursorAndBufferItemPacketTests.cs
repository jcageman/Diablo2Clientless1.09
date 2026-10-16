using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Packet.Outgoing;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

/// <summary>
/// The byte fixture is a 1.09 client capture of a stash gem put down on an inventory gem at (2,2):
/// the held item, the item it lands on and that item's cell. There is no container field - the item
/// it lands on decides where it goes.
/// </summary>
public class SwapCursorAndBufferItemPacketTests
{
    [Fact]
    public void MatchesCapturedBytes()
    {
        var held = new Item { Id = 0x7C };
        var target = new Item { Id = 0x7E, Location = new Point(2, 2) };

        var packet = new SwapCursorAndBufferItemPacket(held, target);

        Assert.Equal(
            new byte[] { 0x1F, 0x7C, 0x00, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00 },
            packet.Raw);
    }
}
