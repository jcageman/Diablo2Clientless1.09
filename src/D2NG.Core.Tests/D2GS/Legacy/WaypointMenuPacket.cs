using D2NG.Core.D2GS.Exceptions;

using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Generic;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

internal class WaypointMenuPacket : D2gsPacket
{
    public WaypointMenuPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new LegacyBitReader(packet.Raw);
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
