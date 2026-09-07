using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.D2GS.Quest;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class QuestCompletePacketTests
{
    [Fact]
    public void SerializesQuestIdAsLittleEndianWord()
    {
        var packet = new QuestCompletePacket(QuestId.TerrorsEnd);

        Assert.Equal([0x58, 0x1A, 0x00], packet.Raw);
    }
}
