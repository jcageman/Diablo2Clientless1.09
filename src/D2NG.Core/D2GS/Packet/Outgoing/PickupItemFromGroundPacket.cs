using D2NG.Core.D2GS.Items;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class PickupItemFromGroundPacket : D2gsPacket
{
    public PickupItemFromGroundPacket(Item item) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.PickItem,
                BitConverter.GetBytes((uint)0x04),
                BitConverter.GetBytes(item.Id),
                BitConverter.GetBytes((uint)0x00)
            )
        )
    {
    }
}
