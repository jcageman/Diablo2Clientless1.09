using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Says one of the messages an NPC is offering, which is how a quest reward is claimed. Captured from a
/// real 1.09 client claiming the den of evil skill point from Akara: the entity id followed by the
/// message id the server had just advertised.
/// </summary>
internal class QuestMessagePacket : D2gsPacket
{
    public QuestMessagePacket(uint entityId, uint messageId) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.QuestMessage,
                BitConverter.GetBytes(entityId),
                BitConverter.GetBytes(messageId)
            )
        )
    {
    }
}
