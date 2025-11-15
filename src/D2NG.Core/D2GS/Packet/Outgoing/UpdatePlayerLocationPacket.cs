using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class UpdatePlayerLocationPacket : D2gsPacket
{
    public UpdatePlayerLocationPacket(Point point) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.UpdatePlayerLocation,
                BitConverter.GetBytes(point.X),
                BitConverter.GetBytes(point.Y)
            )
        )
    {
    }
}
