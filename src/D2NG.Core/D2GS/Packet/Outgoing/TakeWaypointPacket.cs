using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class TakeWaypointPacket : D2gsPacket
{
    public TakeWaypointPacket(uint originWaypointId, Waypoint waypoint) :
        this(originWaypointId, (uint)waypoint)
    {
    }

    /// <summary>Level 0 closes the waypoint menu without travelling.</summary>
    public TakeWaypointPacket(uint originWaypointId, uint levelId) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.TakeWaypoint,
                BitConverter.GetBytes(originWaypointId),
                BitConverter.GetBytes(levelId)
            )
        )
    {
    }
}
