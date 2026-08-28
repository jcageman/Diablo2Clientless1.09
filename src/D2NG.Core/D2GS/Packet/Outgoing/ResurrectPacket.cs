namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Resurrects a dead character in town. Captured from a real 1.09 client: a single command byte with
/// no payload.
/// </summary>
internal class ResurrectPacket : D2gsPacket
{
    public ResurrectPacket() :
        base([(byte)OutGoingPacket.Resurrect])
    {
    }
}
