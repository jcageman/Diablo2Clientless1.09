namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Asks the server to push the quest state again, answered with a 0x9C for this character and a 0x9D
/// for the game. Captured from a real 1.09 client: a single command byte with no payload.
/// </summary>
internal class RequestQuestDataPacket : D2gsPacket
{
    public RequestQuestDataPacket() :
        base([(byte)OutGoingPacket.RequestQuestData])
    {
    }
}
