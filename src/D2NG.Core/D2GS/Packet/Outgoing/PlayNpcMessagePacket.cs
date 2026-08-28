using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Asks an NPC to play one of its dialogue lines, by message id.
/// </summary>
/// <remarks>
/// Captured from a 1.09 client talking to Jerhyn in the palace courtyard after Duriel died:
/// <code>0x4D, 0xC9, 0x00</code>
/// One little endian word holding the message. The chat is opened with
/// <c>0x2F InitiateEntityChat</c> first and closed with <c>0x30 TerminateEntityChat</c> after.
/// <para>
/// This is not the same as <c>0x31 QuestMessage</c>, which the same session used on Fara.
/// </para>
/// </remarks>
internal class PlayNpcMessagePacket : D2gsPacket
{
    public PlayNpcMessagePacket(ushort messageId) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.PlayNPCMessage,
                BitConverter.GetBytes(messageId)
            )
        )
    {
    }
}
