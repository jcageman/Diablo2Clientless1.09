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
        // Panel order, not numeric order. The values are level ids and do not ascend the way the
        // bitfield is packed, so Enum.GetValues here read act 1 correctly and scrambled every act after
        // it. See WaypointExtensions.BitOrder.
        foreach (var waypoint in WaypointExtensions.BitOrder)
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