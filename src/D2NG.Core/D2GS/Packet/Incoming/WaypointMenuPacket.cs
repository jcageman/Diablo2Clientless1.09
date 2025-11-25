using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Generic;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class WaypointMenuPacket : D2gsPacket
{
    public WaypointMenuPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BitReader(packet.Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.WaypointMenu)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        WaypointId = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        foreach (Waypoint waypoint in Enum.GetValues(typeof(Waypoint)))
        {
            if (reader.ReadBit())
            {
                AllowedWaypoints.Add(waypoint);
            }

        }
    }

    public uint WaypointId { get; }
    public HashSet<Waypoint> AllowedWaypoints { get; } = [];
}