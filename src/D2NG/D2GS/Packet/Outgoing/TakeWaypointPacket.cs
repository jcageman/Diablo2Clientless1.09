using D2NG.D2GS.Objects;
using System;

namespace D2NG.D2GS.Packet
{
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
}
