namespace D2NG.Core.D2GS.Packet.Outgoing;

/// <summary>
/// Tells the server a cinematic has finished. Completing an act sets the player busy and sends 0x61 to
/// play the act's cinematic, and the player stays busy - refused by every portal and waypoint - until
/// the client sends this (D2MOO PlrMsg.cpp, Rcv0x48_TurnOffBusyState). A single command byte.
/// </summary>
internal class TurnOffBusyPacket : D2gsPacket
{
    public TurnOffBusyPacket() :
        base([(byte)OutGoingPacket.TurnOffBusy])
    {
    }
}
