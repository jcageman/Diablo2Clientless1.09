using D2NG.Core.D2GS.Packet.Outgoing;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class PlayNpcMessagePacketTests
{
    [Fact]
    public void ReproducesTheCapturedJerhynMessage()
    {
        var packet = new PlayNpcMessagePacket(0x00C9);

        Assert.Equal<byte[]>([0x4D, 0xC9, 0x00], packet.Raw);
    }

    [Fact]
    public void KeepsTheMessageIdLittleEndian()
    {
        var packet = new PlayNpcMessagePacket(0x1234);

        Assert.Equal<byte[]>([0x4D, 0x34, 0x12], packet.Raw);
    }
}
