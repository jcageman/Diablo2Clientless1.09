using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class TakeWaypointPacket : D2gsPacket
{
    public TakeWaypointPacket(uint originWaypointId, Waypoint waypoint) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.TakeWaypoint,
                BitConverter.GetBytes(originWaypointId),
                BitConverter.GetBytes((uint)waypoint)
            )
        )
    {
    }
}
