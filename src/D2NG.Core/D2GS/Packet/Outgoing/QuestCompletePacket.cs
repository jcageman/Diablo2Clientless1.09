using System;
using D2NG.Core.D2GS.Quest;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Acknowledges the client-side completion sequence for a quest. End-of-act quests use this after
/// their special quest event; without it a clientless character can retain kill credit without
/// advancing its character-list progression.
/// </summary>
internal class QuestCompletePacket : D2gsPacket
{
    public QuestCompletePacket(QuestId quest) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.QuestComplete,
                BitConverter.GetBytes((ushort)quest)
            )
        )
    {
    }
}
